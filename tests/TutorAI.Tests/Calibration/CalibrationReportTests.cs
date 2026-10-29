using TutorAI.Api.Assessment;

namespace TutorAI.Tests.Calibration;

public class CalibrationReportTests
{
    private static CalibrationSample Sample(string id, string level, double? composite = null, string? pronunciation = null) =>
        new(id, "test rater", "Conversation", null, [new CalibrationTurn("learner", "Hello.")], null, composite,
            new HumanLevels(level, level, level, level, pronunciation));

    private static GradingResult Graded(string level)
    {
        var grade = new DimensionGrade(level, "medium", [], "");
        return new GradingResult(grade, grade, grade, grade, "", []);
    }

    [Fact]
    public void Criteria_report_agreement_bias_and_consistency()
    {
        // Rated B1. Repeats: B1 (exact), B1+ (one up), B2 (two up). Rated B2 twice exactly.
        var runs = new List<CalibrationRun>
        {
            new(Sample("a", "B1"), [Graded("B1"), Graded("B1+"), Graded("B2")]),
            new(Sample("b", "B2"), [Graded("B2"), Graded("B2")]),
        };

        var grammar = CalibrationReport.Criteria(runs).Single(c => c.Criterion == "grammar");

        Assert.Equal(5, grammar.Gradings);
        Assert.Equal(3 / 5d, grammar.Exact, 3);
        Assert.Equal(4 / 5d, grammar.WithinOne, 3);
        Assert.Equal(3 / 5d, grammar.Bias, 3);
        Assert.Equal(0.5, grammar.Consistent, 3);
    }

    [Fact]
    public void Pronunciation_fit_suggests_midpoints_between_level_medians()
    {
        var samples = new[]
        {
            Sample("1", "B1", 66, "B1"), Sample("2", "B1", 70, "B1"),
            Sample("3", "B2", 80, "B2"), Sample("4", "B2", 84, "B2"),
            Sample("5", "B2"), // no composite: ignored
        };

        var fit = CalibrationReport.Pronunciation(samples);

        Assert.Equal(4, fit.Samples);
        Assert.Equal([("B1", 2, 68.0), ("B2", 2, 82.0)], fit.Medians);
        Assert.Equal([("B2", 75.0)], fit.SuggestedThresholds);
        // Current thresholds: 66 -> A2+ (one off), 70 -> B1, 80 -> B2, 84 -> B2.
        Assert.Equal(0.75, fit.CurrentExact);
        Assert.Equal(1.0, fit.CurrentWithinOne);
    }

    [Fact]
    public void Markdown_marks_criteria_below_the_bar_and_lists_big_misses()
    {
        var runs = new List<CalibrationRun> { new(Sample("travel-01", "A2"), [Graded("B2")]) };

        var report = CalibrationReport.Markdown(
            runs, CalibrationReport.Criteria(runs), CalibrationReport.Pronunciation(runs.Select(r => r.Sample)), 0.85, "m", "v1");

        Assert.Contains("| grammar | 1 | 0% | 0% | +4.00 | 100% | **fail** |", report);
        Assert.Contains("- `travel-01` grammar: rater A2, grader B2", report);
        Assert.Contains("No sample has both", report);
    }

    [Fact]
    public void Load_rejects_samples_without_a_rater_or_with_unknown_levels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tutorai-cal-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // comments are allowed
              "id": "x", "rater": "", "mode": "Conversation",
              "turns": [{ "role": "learner", "text": "Hi." }],
              "levels": { "fluency": "B1", "grammar": "B3", "vocabulary": "B1", "interaction": "B1" },
            }
            """);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => CalibrationSample.Load(path));
            Assert.Contains("rater is missing", error.Message);
            Assert.Contains("levels.grammar 'B3'", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
