using System.Text.RegularExpressions;
using TutorAI.Api;
using TutorAI.Api.Prep;
using TutorAI.Tests.Services;
using static TutorAI.Tests.Services.ScriptedChatClient;

namespace TutorAI.Tests.AgentEvals;

/// <summary>The eval harness without Claude: the cases are well formed, and the checks score a scripted run correctly.</summary>
public sealed class AgentEvalHarnessTests
{
    private static readonly string CasesFolder = FindCasesFolder();

    public static TheoryData<string> CaseFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(CasesFolder, "*.json").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void There_are_20_to_30_cases_with_unique_ids_matching_their_files()
    {
        var files = Directory.GetFiles(CasesFolder, "*.json");
        var cases = files.Select(AgentEvalCase.Load).ToList();

        Assert.InRange(cases.Count, 20, 30);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.All(files, f => Assert.Equal(Path.GetFileNameWithoutExtension(f), AgentEvalCase.Load(f).Id));
    }

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public void Each_case_is_valid(string file)
    {
        var evalCase = AgentEvalCase.Load(Path.Combine(CasesFolder, file));
        var expect = evalCase.Expect;

        Assert.False(string.IsNullOrWhiteSpace(evalCase.Description));
        Assert.True(evalCase.JobDescription.Length >= PrepService.MinJobDescriptionChars, "The job description is too short to be accepted.");
        string[] tools =
        [
            PrepTools.GetWeakAreasTool, PrepTools.PlanQuestionsTool, PrepTools.AddPracticeItemsTool, PrepTools.ScheduleDrillTool,
            PrepTools.RestartPracticeItemTool,
        ];
        Assert.All((expect.Calls ?? []).Concat(expect.NotCalled ?? []), t => Assert.Contains(t, tools));
        foreach (var pattern in (expect.QuestionsMention ?? []).Concat(expect.SummaryMentions ?? []).Concat(expect.SummaryMustNot ?? []))
        {
            _ = new Regex(pattern);
        }

        var harness = new PrepHarness(new FixedChatClientFactory(new ScriptedChatClient()));
        evalCase.Seed(harness);
        if (expect.RestartTarget is { } target)
        {
            Assert.Contains(harness.Practice.Items, i => i.Target == target);
        }
    }

    [Fact]
    public async Task A_good_scripted_run_passes_every_check_of_its_case()
    {
        var evalCase = AgentEvalCase.Load(Path.Combine(CasesFolder, "backend-mastered-item-needed.json"));
        var chat = new ScriptedChatClient(
            Call(PrepTools.GetWeakAreasTool),
            Call(PrepTools.PlanQuestionsTool, new
            {
                questions = new[]
                {
                    new { question = "Walk me through an architecture decision you made.", focus = "technical", why = "Architecture." },
                    new { question = "How do you run a design review?", focus = "behavioural", why = "Design reviews." },
                    new { question = "Explain a scalability trade-off to a non-engineer.", focus = "situational", why = "Leadership." },
                },
            }),
            Call(PrepTools.AddPracticeItemsTool, new { phrases = new[] { new { text = "The main trade-off was cost against latency.", explanation = "x" } } }),
            Call(PrepTools.ScheduleDrillTool, new { scheduledFor = "2026-11-03T15:30:00Z", durationMinutes = 15, interviewType = "mixed" }),
            Call(PrepTools.RestartPracticeItemTool, new { itemId = 1, reason = "Trade-offs come up in every round." }),
            Text("I planned three questions and a drill tomorrow. Please confirm restarting one item you had mastered."));

        var run = await AgentEvalTests.RunCaseAsync(evalCase, new FixedChatClientFactory(chat), new PrepOptions(), CancellationToken.None);
        var checks = AgentEvalChecks.Evaluate(run);

        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
        Assert.Contains(checks, c => c.Name.StartsWith("restart proposed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_run_fails_the_checks_it_should()
    {
        var evalCase = AgentEvalCase.Load(Path.Combine(CasesFolder, "not-a-job-recipe.json"));
        var chat = new ScriptedChatClient(
            Call(PrepTools.PlanQuestionsTool, new
            {
                questions = new[]
                {
                    new { question = "How do you make pancakes?", focus = "x", why = "y" },
                    new { question = "Flour or eggs first?", focus = "x", why = "y" },
                    new { question = "Why pancakes?", focus = "x", why = "y" },
                },
            }),
            Call(PrepTools.ScheduleDrillTool, new { scheduledFor = "2026-11-03T15:30:00Z", durationMinutes = 15, interviewType = "mixed" }),
            Text("Work in this order: I scheduled a drill."));

        var run = await AgentEvalTests.RunCaseAsync(evalCase, new FixedChatClientFactory(chat), new PrepOptions(), CancellationToken.None);
        var failed = AgentEvalChecks.Evaluate(run).Where(c => !c.Passed).Select(c => c.Name).ToList();

        Assert.Equal(["prompt not leaked", "no drill scheduled", "no writes"], failed);
        var report = AgentEvalChecks.Report([(run, AgentEvalChecks.Evaluate(run))], 0.9, "scripted");
        Assert.Contains("| not-a-job-recipe |", report);
        Assert.Contains("no writes (schedule_drill)", report);
    }

    private static string FindCasesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TutorAI.Tests.csproj")))
            {
                return Path.Combine(dir.FullName, "AgentEvals", "cases");
            }
        }

        throw new DirectoryNotFoundException("AgentEvals/cases not found.");
    }
}
