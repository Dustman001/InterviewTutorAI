using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TutorAI.Api.Assessment;

namespace TutorAI.Tests.Calibration;

/// <summary>
/// One labelled session for calibrating the grader (samples/*.json). The levels are a qualified rater's, not
/// the app's. See the ReadMe's "Grading calibration" section for the format.
/// </summary>
public sealed record CalibrationSample(
    string Id,
    string Rater,
    string Mode,
    JsonElement? Scenario,
    List<CalibrationTurn> Turns,
    FluencyStats? Fluency,
    double? PronunciationComposite,
    HumanLevels Levels)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CalibrationSample Load(string path)
    {
        var sample = JsonSerializer.Deserialize<CalibrationSample>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(sample.Rater)) problems.Add("rater is missing");
        if (sample.Turns is not { Count: > 0 }) problems.Add("turns are missing");
        else if (sample.Turns.Any(t => t.Role is not ("learner" or "tutor"))) problems.Add("a turn's role is not learner or tutor");
        foreach (var (criterion, level) in sample.Levels.Graded())
        {
            if (!CefrMapper.IsValid(level)) problems.Add($"levels.{criterion} '{level}' is not a CEFR level");
        }

        if (sample.Levels.Pronunciation is { } p && !CefrMapper.IsValid(p)) problems.Add($"levels.pronunciation '{p}' is not a CEFR level");
        return problems.Count == 0 ? sample : throw new InvalidDataException($"{Path.GetFileName(path)}: {string.Join("; ", problems)}.");
    }
}

/// <param name="Role">learner or tutor.</param>
public sealed record CalibrationTurn(string Role, string Text);

/// <param name="Pronunciation">Optional: compared with the pronunciation composite to refit the score thresholds.</param>
public sealed record HumanLevels(string Fluency, string Grammar, string Vocabulary, string Interaction, string? Pronunciation = null)
{
    public IEnumerable<(string Criterion, string Level)> Graded() =>
        [("fluency", Fluency), ("grammar", Grammar), ("vocabulary", Vocabulary), ("interaction", Interaction)];
}

/// <summary>The grader's repeats for one sample.</summary>
public sealed record CalibrationRun(CalibrationSample Sample, IReadOnlyList<GradingResult> Repeats);

public sealed record CriterionStats(string Criterion, int Gradings, double Exact, double WithinOne, double Bias, double Consistent);

/// <param name="SuggestedThresholds">Lowest composite for each level: the midpoint between neighbouring levels' medians.</param>
public sealed record PronunciationFit(
    int Samples, double? CurrentExact, double? CurrentWithinOne,
    IReadOnlyList<(string Level, int Count, double Median)> Medians,
    IReadOnlyList<(string Level, double Min)> SuggestedThresholds);

/// <summary>Agreement between the grader and human raters, and the Markdown report of it.</summary>
public static class CalibrationReport
{
    public static IReadOnlyList<CriterionStats> Criteria(IReadOnlyList<CalibrationRun> runs)
    {
        var result = new List<CriterionStats>();
        foreach (var criterion in new[] { "fluency", "grammar", "vocabulary", "interaction" })
        {
            int gradings = 0, exact = 0, withinOne = 0, consistent = 0;
            double bias = 0;
            foreach (var run in runs)
            {
                var human = Step(run.Sample.Levels.Graded().Single(g => g.Criterion == criterion).Level);
                var steps = run.Repeats.Select(r => Step(Pick(r, criterion).Level)).ToList();
                foreach (var step in steps)
                {
                    gradings++;
                    exact += step == human ? 1 : 0;
                    withinOne += Math.Abs(step - human) <= 1 ? 1 : 0;
                    bias += step - human;
                }

                consistent += steps.Distinct().Count() == 1 ? 1 : 0;
            }

            result.Add(new CriterionStats(
                criterion, gradings,
                Ratio(exact, gradings), Ratio(withinOne, gradings),
                gradings == 0 ? 0 : bias / gradings, Ratio(consistent, runs.Count)));
        }

        return result;
    }

    public static PronunciationFit Pronunciation(IEnumerable<CalibrationSample> samples)
    {
        var labelled = samples
            .Where(s => s.PronunciationComposite is not null && s.Levels.Pronunciation is not null)
            .Select(s => (Composite: s.PronunciationComposite!.Value, Human: Step(s.Levels.Pronunciation!)))
            .ToList();
        if (labelled.Count == 0)
        {
            return new PronunciationFit(0, null, null, [], []);
        }

        var current = labelled.Select(l => Step(CefrMapper.FromPronunciationScore(l.Composite)) - l.Human).ToList();
        var medians = labelled
            .GroupBy(l => l.Human)
            .OrderBy(g => g.Key)
            .Select(g => (Level: CefrMapper.Levels[g.Key], Count: g.Count(), Median: Median([.. g.Select(l => l.Composite)])))
            .ToList();
        var thresholds = medians
            .Zip(medians.Skip(1), (lower, upper) => (upper.Level, Min: Math.Round((lower.Median + upper.Median) / 2, 1)))
            .ToList();

        return new PronunciationFit(
            labelled.Count, Ratio(current.Count(d => d == 0), current.Count), Ratio(current.Count(d => Math.Abs(d) <= 1), current.Count),
            medians, thresholds);
    }

    public static string Markdown(
        IReadOnlyList<CalibrationRun> runs, IReadOnlyList<CriterionStats> criteria, PronunciationFit pronunciation,
        double bar, string model, string promptVersion)
    {
        var sb = new StringBuilder();
        var raters = string.Join(", ", runs.Select(r => r.Sample.Rater).Distinct());
        var repeats = runs.Count == 0 ? 0 : runs.Max(r => r.Repeats.Count);
        sb.AppendLine("# Grading calibration report").AppendLine();
        sb.AppendLine(Invariant($"{runs.Count} samples, {repeats} gradings each, by `{model}` with prompt version `{promptVersion}`. Raters: {raters}."));
        sb.AppendLine(Invariant($"The bar: every criterion within one step of the rater in at least {Pct(bar)} of gradings.")).AppendLine();

        sb.AppendLine("| Criterion | Gradings | Exact | Within one step | Bias (steps) | Same level every repeat | Bar |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var c in criteria)
        {
            sb.AppendLine(Invariant(
                $"| {c.Criterion} | {c.Gradings} | {Pct(c.Exact)} | {Pct(c.WithinOne)} | {c.Bias:+0.00;-0.00;0.00} | {Pct(c.Consistent)} | {(c.WithinOne >= bar ? "pass" : "**fail**")} |"));
        }

        sb.AppendLine().AppendLine("A positive bias means the grader rates higher than the rater. One step is one level on A1, A2, A2+, B1, B1+, B2, B2+, C1, C2.");
        sb.AppendLine().AppendLine("## Pronunciation thresholds").AppendLine();
        if (pronunciation.Samples == 0)
        {
            sb.AppendLine("No sample has both a pronunciation composite and a rated pronunciation level.");
        }
        else
        {
            sb.AppendLine(Invariant(
                $"{pronunciation.Samples} samples. The current thresholds agree exactly in {Pct(pronunciation.CurrentExact)} and within one step in {Pct(pronunciation.CurrentWithinOne)}.")).AppendLine();
            sb.AppendLine("| Rated level | Samples | Median composite | Suggested minimum |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var (level, count, median) in pronunciation.Medians)
            {
                var min = pronunciation.SuggestedThresholds.FirstOrDefault(t => t.Level == level);
                sb.AppendLine(Invariant($"| {level} | {count} | {median:0.0} | {(min.Level is null ? "–" : min.Min.ToString("0.0", CultureInfo.InvariantCulture))} |"));
            }

            sb.AppendLine().AppendLine("Levels with few samples give unreliable medians; refit `CefrMapper` only from well-populated levels.");
        }

        sb.AppendLine().AppendLine("## Disagreements of two steps or more").AppendLine();
        var misses = runs
            .SelectMany(run => run.Sample.Levels.Graded().Select(g => (run, g.Criterion, Human: g.Level,
                Grader: run.Repeats.Select(r => Pick(r, g.Criterion).Level).ToList())))
            .Where(x => x.Grader.Any(l => Math.Abs(Step(l) - Step(x.Human)) >= 2))
            .ToList();
        if (misses.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var (run, criterion, human, grader) in misses)
        {
            sb.AppendLine($"- `{run.Sample.Id}` {criterion}: rater {human}, grader {string.Join(" / ", grader)}");
        }

        return sb.ToString();
    }

    public static int Step(string level)
    {
        for (var i = 0; i < CefrMapper.Levels.Count; i++)
        {
            if (CefrMapper.Levels[i] == level) return i;
        }

        throw new ArgumentException($"'{level}' is not a CEFR level.", nameof(level));
    }

    private static DimensionGrade Pick(GradingResult result, string criterion) => criterion switch
    {
        "fluency" => result.Fluency,
        "grammar" => result.Grammar,
        "vocabulary" => result.Vocabulary,
        "interaction" => result.Interaction,
        _ => throw new ArgumentOutOfRangeException(nameof(criterion)),
    };

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : part / (double)whole;

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    // The invariant culture's percent format puts a space before the sign.
    private static string Pct(double? ratio) => ratio is { } r ? $"{Math.Round(r * 100):0}%" : "–";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
