using System.Collections.Concurrent;
using System.Globalization;
using Anthropic;
using TutorAI.Api;
using TutorAI.Api.Practice;
using TutorAI.Api.Prep;
using TutorAI.Tests.Integration;
using TutorAI.Tests.Services;
using Xunit.Abstractions;

namespace TutorAI.Tests.AgentEvals;

/// <summary>
/// Runs the interview-prep agent against Claude on every case in AgentEvals/cases and scores the runs. The
/// learner's data is in memory (no database), the model is real. Costly (one agent run per case per repeat,
/// roughly $0.05-0.10 each with Sonnet), so it runs only with TUTORAI_AGENT_EVALS=1.
///
/// TUTORAI_AGENT_EVALS_DIR: the cases folder (default: AgentEvals/cases in this project).
/// TUTORAI_AGENT_EVALS_REPEATS: runs per case (default 1).
/// TUTORAI_AGENT_EVALS_BAR: the share of checks that must pass (default 0.9).
/// TUTORAI_AGENT_EVALS_FILTER: run only cases whose id contains this text.
/// </summary>
[Trait("Category", "AgentEval")]
public sealed class AgentEvalTests(ITestOutputHelper output)
{
    [AgentEvalFact]
    public async Task Prep_agent_meets_the_eval_bar()
    {
        var folder = Environment.GetEnvironmentVariable("TUTORAI_AGENT_EVALS_DIR") ?? DefaultCasesFolder();
        var filter = Environment.GetEnvironmentVariable("TUTORAI_AGENT_EVALS_FILTER");
        var cases = Directory.GetFiles(folder, "*.json").Order().Select(AgentEvalCase.Load)
            .Where(c => string.IsNullOrEmpty(filter) || c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(cases.Count > 0, $"No eval cases (*.json) in {folder}.");
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("TUTORAI_AGENT_EVALS_REPEATS"), out var r) && r > 0 ? r : 1;
        var bar = double.TryParse(Environment.GetEnvironmentVariable("TUTORAI_AGENT_EVALS_BAR"), CultureInfo.InvariantCulture, out var b) ? b : 0.9;

        var apiKey = E2E.Config["Anthropic:ApiKey"];
        var chat = new AnthropicPrepChatClientFactory(
            string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey });
        var options = new PrepOptions();

        var results = new ConcurrentBag<(int Order, AgentEvalRun Run, IReadOnlyList<EvalCheck> Checks)>();
        var jobs = cases.SelectMany(c => Enumerable.Range(0, repeats).Select(_ => c)).Select((c, i) => (Case: c, Order: i)).ToList();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (job, ct) =>
        {
            var run = await RunCaseAsync(job.Case, chat, options, ct);
            results.Add((job.Order, run, AgentEvalChecks.Evaluate(run)));
        });

        var ordered = results.OrderBy(x => x.Order).Select(x => (x.Run, x.Checks)).ToList();
        var report = AgentEvalChecks.Report(ordered, bar, options.Model);
        var outFolder = Path.Combine(AppContext.BaseDirectory, "TestResults", "agent-evals");
        Directory.CreateDirectory(outFolder);
        var path = Path.Combine(outFolder, $"agent-evals-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, report);
        output.WriteLine(report);
        output.WriteLine($"Report written to {path}");

        var rate = AgentEvalChecks.PassRate(ordered.Select(x => x.Checks));
        Assert.True(rate >= bar, $"{rate:P1} of checks passed, below the {bar:P0} bar. See {path}.");
    }

    internal static async Task<AgentEvalRun> RunCaseAsync(
        AgentEvalCase evalCase, IPrepChatClientFactory chat, PrepOptions options, CancellationToken ct)
    {
        var harness = new PrepHarness(chat, options);
        evalCase.Seed(harness);
        var phrasesBefore = harness.Practice.Items.Count(i => i.Kind == PracticeKind.Phrase);
        var run = harness.NewRun(evalCase.JobDescription, evalCase.Role, evalCase.Seniority);
        var result = await harness.Agent.RunAsync(run, ct);
        return new AgentEvalRun(
            evalCase, result, [.. harness.Prep.Actions], [.. harness.Prep.Drills],
            harness.Practice.Items.Count(i => i.Kind == PracticeKind.Phrase) - phrasesBefore,
            harness.Clock.GetUtcNow().UtcDateTime, options.MaxSteps);
    }

    private static string DefaultCasesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TutorAI.Tests.csproj")))
            {
                return Path.Combine(dir.FullName, "AgentEvals", "cases");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "AgentEvals", "cases");
    }
}

/// <summary>Skipped unless TUTORAI_AGENT_EVALS=1 and Anthropic credentials are present.</summary>
public sealed class AgentEvalFactAttribute : FactAttribute
{
    public AgentEvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TUTORAI_AGENT_EVALS") != "1")
        {
            Skip = "Set TUTORAI_AGENT_EVALS=1 to run the prep agent evals (costly: one agent run per case).";
        }
        else if (!E2E.HasClaude)
        {
            Skip = "Needs Anthropic credentials (user-secret Anthropic:ApiKey or ANTHROPIC_API_KEY).";
        }
    }
}
