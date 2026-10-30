using System.Text.Json;
using System.Text.RegularExpressions;
using TutorAI.Api.Practice;
using TutorAI.Api.Prep;
using TutorAI.Tests.Services;

namespace TutorAI.Tests.AgentEvals;

/// <summary>One eval case: a job description, the learner's state, and what a good run does. See AgentEvals/cases/*.json.</summary>
public sealed record AgentEvalCase(
    string Id,
    string Description,
    string JobDescription,
    string? Role,
    string? Seniority,
    EvalLearner? Learner,
    EvalExpect Expect)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public static AgentEvalCase Load(string path) =>
        JsonSerializer.Deserialize<AgentEvalCase>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is not an eval case.");

    /// <summary>Puts the learner's sessions and practice items into the harness's in-memory stores.</summary>
    internal void Seed(PrepHarness harness)
    {
        var now = harness.Clock.GetUtcNow().UtcDateTime;
        foreach (var s in Learner?.Sessions ?? [])
        {
            harness.AddGradedSession(
                now.AddDays(-s.DaysAgo), s.Overall, s.Interaction ?? s.Overall, s.Grammar ?? s.Overall, s.Advice ?? "",
                [.. (s.Corrections ?? []).Select(c => (c[0], c[1]))]);
        }

        foreach (var p in Learner?.Practice ?? [])
        {
            harness.Practice.Add(
                PrepHarness.UserId, StringValueOf(p.Kind), p.Target, (byte)p.Step, p.Mastered, p.LastScore);
        }
    }

    private static PracticeKind StringValueOf(string kind) =>
        PracticeKind.All.FirstOrDefault(k => k.Value == kind) is { Value: not null } found
            ? found
            : throw new InvalidDataException($"Unknown practice kind {kind}.");
}

public sealed record EvalLearner(IReadOnlyList<EvalSession>? Sessions, IReadOnlyList<EvalPracticeItem>? Practice);

public sealed record EvalSession(
    int DaysAgo, string Overall, string? Interaction, string? Grammar, string? Advice, IReadOnlyList<string[]>? Corrections);

public sealed record EvalPracticeItem(string Kind, string Target, int Step, bool Mastered, double? LastScore);

/// <summary>What the run must do. Every field is optional; the invariants in <see cref="AgentEvalChecks"/> always apply.</summary>
public sealed record EvalExpect(
    /// <summary>Tools that must have succeeded at least once.</summary>
    IReadOnlyList<string>? Calls,
    /// <summary>Tools that must not have succeeded.</summary>
    IReadOnlyList<string>? NotCalled,
    int? QuestionsMin,
    int? QuestionsMax,
    /// <summary>Regexes (case-insensitive); each must match at least one planned question.</summary>
    IReadOnlyList<string>? QuestionsMention,
    int? PhrasesMin,
    int? PhrasesMax,
    /// <summary>true: exactly one drill; false: none.</summary>
    bool? Drill,
    string? DrillType,
    /// <summary>The target of the one item a restart must be proposed for.</summary>
    string? RestartTarget,
    /// <summary>No write tool may succeed (the input isn't a job description).</summary>
    bool? NoWrites,
    /// <summary>Regexes (case-insensitive) the closing summary must match.</summary>
    IReadOnlyList<string>? SummaryMentions,
    /// <summary>Regexes (case-insensitive) the closing summary must not match.</summary>
    IReadOnlyList<string>? SummaryMustNot);

/// <summary>What the eval checks look at after a run.</summary>
public sealed record AgentEvalRun(
    AgentEvalCase Case,
    PrepRunResult Result,
    IReadOnlyList<PrepActionRow> Actions,
    IReadOnlyList<DrillRow> Drills,
    int PhrasesAdded,
    DateTime Now,
    int MaxSteps);

public sealed record EvalCheck(string Name, bool Passed, string? Detail = null);

/// <summary>Scores one run against its case. Pure: no model, no I/O.</summary>
public static class AgentEvalChecks
{
    private static readonly string[] WriteTools =
        [PrepTools.AddPracticeItemsTool, PrepTools.ScheduleDrillTool, PrepTools.RestartPracticeItemTool];

    public static IReadOnlyList<EvalCheck> Evaluate(AgentEvalRun run)
    {
        var checks = new List<EvalCheck>();
        var expect = run.Case.Expect;
        var result = run.Result;
        var succeeded = result.Calls.Where(c => !c.Error).Select(c => c.Tool).ToHashSet();

        // Invariants, for every case.
        checks.Add(new("completed", result.Outcome.Status == PrepRunStatus.Completed,
            $"{result.Outcome.Status.Value}{(result.Outcome.StopReason is { } why ? ": " + why : "")}"));
        checks.Add(new("within step limit", result.Outcome.Steps <= run.MaxSteps, $"{result.Outcome.Steps} steps"));
        var drillCalls = run.Actions.Count(a => a.Tool == PrepTools.ScheduleDrillTool);
        checks.Add(new("at most one drill", drillCalls <= 1, $"{drillCalls} drills"));
        var prompt = new[] { "Work in this order", "Rules:", "You prepare one learner" };
        checks.Add(new("prompt not leaked", !prompt.Any(p => (result.Outcome.Summary ?? "").Contains(p, StringComparison.OrdinalIgnoreCase))));

        foreach (var tool in expect.Calls ?? [])
        {
            checks.Add(new($"called {tool}", succeeded.Contains(tool)));
        }

        foreach (var tool in expect.NotCalled ?? [])
        {
            var call = result.Calls.FirstOrDefault(c => c.Tool == tool && !c.Error);
            checks.Add(new($"did not call {tool}", call is null, call?.ArgsJson));
        }

        if (expect.QuestionsMin is { } qMin)
        {
            checks.Add(new($"at least {qMin} questions", result.Questions.Count >= qMin, $"{result.Questions.Count}"));
        }

        if (expect.QuestionsMax is { } qMax)
        {
            checks.Add(new($"at most {qMax} questions", result.Questions.Count <= qMax, $"{result.Questions.Count}"));
        }

        foreach (var pattern in expect.QuestionsMention ?? [])
        {
            checks.Add(new($"a question mentions /{pattern}/",
                result.Questions.Any(q => Regex.IsMatch(q.Question, pattern, RegexOptions.IgnoreCase))));
        }

        if (expect.PhrasesMin is { } pMin)
        {
            checks.Add(new($"at least {pMin} phrases added", run.PhrasesAdded >= pMin, $"{run.PhrasesAdded}"));
        }

        if (expect.PhrasesMax is { } pMax)
        {
            checks.Add(new($"at most {pMax} phrases added", run.PhrasesAdded <= pMax, $"{run.PhrasesAdded}"));
        }

        if (expect.Drill is { } wantDrill)
        {
            checks.Add(new(wantDrill ? "one drill scheduled" : "no drill scheduled", run.Drills.Count == (wantDrill ? 1 : 0),
                $"{run.Drills.Count}"));
            if (wantDrill && run.Drills.Count == 1)
            {
                var hours = (run.Drills[0].ScheduledFor - run.Now).TotalHours;
                checks.Add(new("drill within 2 days", hours is > 0 and <= 48, $"{hours:0.#} h ahead"));
            }
        }

        if (expect.DrillType is { } type)
        {
            var actual = run.Drills.Count == 1
                ? JsonSerializer.Deserialize<TutorAI.Api.Data.Scenario>(run.Drills[0].ScenarioJson, JsonSerializerOptions.Web)?.InterviewType
                : null;
            checks.Add(new($"drill type {type}", string.Equals(actual, type, StringComparison.OrdinalIgnoreCase), actual));
        }

        if (expect.RestartTarget is { } target)
        {
            var proposed = run.Actions
                .Where(a => a.Tool == PrepTools.RestartPracticeItemTool && a.Status == PrepActionStatus.PendingConfirmation)
                .Select(a => JsonSerializer.Deserialize<JsonElement>(a.ArgsJson).GetProperty("target").GetString())
                .ToList();
            checks.Add(new($"restart proposed for \"{target}\"", proposed.Contains(target), string.Join("; ", proposed)));
            checks.Add(new("restart left pending", run.Actions.All(a => a.Tool != PrepTools.RestartPracticeItemTool || a.Status == PrepActionStatus.PendingConfirmation)));
        }

        if (expect.NoWrites is true)
        {
            var writes = WriteTools.Where(succeeded.Contains).ToList();
            checks.Add(new("no writes", writes.Count == 0, string.Join(", ", writes)));
        }

        foreach (var pattern in expect.SummaryMentions ?? [])
        {
            checks.Add(new($"summary mentions /{pattern}/", Regex.IsMatch(result.Outcome.Summary ?? "", pattern, RegexOptions.IgnoreCase)));
        }

        foreach (var pattern in expect.SummaryMustNot ?? [])
        {
            checks.Add(new($"summary avoids /{pattern}/", !Regex.IsMatch(result.Outcome.Summary ?? "", pattern, RegexOptions.IgnoreCase)));
        }

        return checks;
    }

    /// <summary>A Markdown report: totals, then each case with its failed checks.</summary>
    public static string Report(IReadOnlyList<(AgentEvalRun Run, IReadOnlyList<EvalCheck> Checks)> results, double bar, string model)
    {
        var total = results.Sum(r => r.Checks.Count);
        var passed = results.Sum(r => r.Checks.Count(c => c.Passed));
        var rate = total == 0 ? 0 : (double)passed / total;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Interview-prep agent evals").AppendLine();
        sb.AppendLine($"Model `{model}`, {results.Count} cases, {passed}/{total} checks passed ({rate:P1}); bar {bar:P0}.");
        sb.AppendLine($"Cases with every check passed: {results.Count(r => r.Checks.All(c => c.Passed))}/{results.Count}.");
        sb.AppendLine($"Tokens: {results.Sum(r => r.Run.Result.Outcome.InputTokens)} input, {results.Sum(r => r.Run.Result.Outcome.OutputTokens)} output.");
        sb.AppendLine().AppendLine("| Case | Checks | Steps | Tokens | Failed |").AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var (run, checks) in results)
        {
            var failed = checks.Where(c => !c.Passed).Select(c => c.Detail is null ? c.Name : $"{c.Name} ({c.Detail})");
            sb.AppendLine(
                $"| {run.Case.Id} | {checks.Count(c => c.Passed)}/{checks.Count} | {run.Result.Outcome.Steps} | " +
                $"{run.Result.Outcome.InputTokens + run.Result.Outcome.OutputTokens} | {Escape(string.Join("; ", failed))} |");
        }

        return sb.ToString();
    }

    public static double PassRate(IEnumerable<IReadOnlyList<EvalCheck>> checks)
    {
        var all = checks.SelectMany(c => c).ToList();
        return all.Count == 0 ? 0 : (double)all.Count(c => c.Passed) / all.Count;
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
