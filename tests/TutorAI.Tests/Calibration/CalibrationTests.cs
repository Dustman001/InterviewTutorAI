using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Tests.Integration;
using Xunit.Abstractions;

namespace TutorAI.Tests.Calibration;

/// <summary>
/// Grades the labelled samples with the real grader and compares them with the raters' levels. Costly (one
/// Opus call per sample per repeat), so it runs only with TUTORAI_CALIBRATION=1.
///
/// TUTORAI_CALIBRATION_DIR: the samples folder (default: Calibration/samples in this project).
/// TUTORAI_CALIBRATION_REPEATS: gradings per sample (default 3).
/// TUTORAI_CALIBRATION_BAR: the within-one-step agreement each criterion needs (default 0.85).
/// </summary>
[Trait("Category", "Calibration")]
public sealed class CalibrationTests(ITestOutputHelper output)
{
    [CalibrationFact]
    public async Task Grader_agrees_with_human_raters()
    {
        var folder = Environment.GetEnvironmentVariable("TUTORAI_CALIBRATION_DIR") ?? DefaultSamplesFolder();
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Order().ToList() : [];
        Assert.True(files.Count > 0, $"No calibration samples (*.json) in {folder}.");
        var samples = files.Select(CalibrationSample.Load).ToList();
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("TUTORAI_CALIBRATION_REPEATS"), out var r) && r > 0 ? r : 3;
        var bar = double.TryParse(Environment.GetEnvironmentVariable("TUTORAI_CALIBRATION_BAR"), System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0.85;

        var apiKey = E2E.Config["Anthropic:ApiKey"];
        var grader = new CefrGrader(
            string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey },
            Options.Create(new AnthropicOptions()),
            // Gradings run back to back, so the system prompt is read from the cache after the first.
            Options.Create(new AssessmentOptions { CacheGraderPrompt = true }));

        // One grading first to write the cache, then the rest a few at a time.
        var jobs = samples.SelectMany(s => Enumerable.Range(0, repeats).Select(_ => s)).ToList();
        var usage = new System.Collections.Concurrent.ConcurrentBag<ClaudeUsage>();
        var results = new List<(CalibrationSample Sample, GradingResult Result)> { (jobs[0], await GradeAsync(grader, jobs[0], usage)) };
        var rest = new System.Collections.Concurrent.ConcurrentBag<(CalibrationSample, GradingResult)>();
        await Parallel.ForEachAsync(jobs.Skip(1), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (sample, ct) =>
            rest.Add((sample, await GradeAsync(grader, sample, usage, ct))));
        results.AddRange(rest);
        output.WriteLine(
            $"Tokens: {usage.Sum(u => u.InputTokens)} input, {usage.Sum(u => u.CacheReadTokens)} read from cache, " +
            $"{usage.Sum(u => u.CacheWriteTokens)} written to cache, {usage.Sum(u => u.OutputTokens)} output.");

        var runs = samples
            .Select(s => new CalibrationRun(s, [.. results.Where(x => ReferenceEquals(x.Sample, s)).Select(x => x.Result)]))
            .ToList();
        var criteria = CalibrationReport.Criteria(runs);
        var report = CalibrationReport.Markdown(
            runs, criteria, CalibrationReport.Pronunciation(samples), bar, grader.Model, grader.PromptVersion);

        var outFolder = Path.Combine(AppContext.BaseDirectory, "TestResults", "calibration");
        Directory.CreateDirectory(outFolder);
        var path = Path.Combine(outFolder, $"calibration-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, report);
        output.WriteLine(report);
        output.WriteLine($"Report written to {path}");

        Assert.All(criteria, c => Assert.True(
            c.WithinOne >= bar, $"{c.Criterion}: within one step in {c.WithinOne:P0} of gradings, below the {bar:P0} bar."));
    }

    private static Task<GradingResult> GradeAsync(
        CefrGrader grader, CalibrationSample sample, System.Collections.Concurrent.ConcurrentBag<ClaudeUsage> usage,
        CancellationToken ct = default)
    {
        var session = new SessionRow
        {
            Id = Guid.NewGuid(),
            Mode = StringValue.Parse<SessionMode>(sample.Mode),
            ScenarioJson = sample.Scenario?.GetRawText() ?? "{}",
            Status = SessionStatus.Ended,
        };
        var turns = sample.Turns
            .Select((t, i) => new TurnRow
            {
                Seq = i + 1,
                Role = t.Role == "learner" ? TurnRole.User : TurnRole.Tutor,
                Text = t.Text,
                WordCount = t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            })
            .ToList();
        // Without measured timings the grader is told they're missing, as for a typed session.
        var fluency = sample.Fluency ?? FluencyMetrics.Compute(
            turns.Where(t => t.Role == TurnRole.User).Select(t => (t.WordCount, (IReadOnlyList<AssessedPhrase>)[])));
        var callUsage = new ClaudeUsage();
        usage.Add(callUsage);
        return grader.GradeAsync(session, turns, fluency, callUsage, ct);
    }

    private static string DefaultSamplesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Calibration", "samples");
            if (File.Exists(Path.Combine(dir.FullName, "TutorAI.Tests.csproj")))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Calibration", "samples");
    }
}

/// <summary>Skipped unless TUTORAI_CALIBRATION=1 and Anthropic credentials are present.</summary>
public sealed class CalibrationFactAttribute : FactAttribute
{
    public CalibrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TUTORAI_CALIBRATION") != "1")
        {
            Skip = "Set TUTORAI_CALIBRATION=1 to grade the calibration samples (costly: one Opus call per sample per repeat).";
        }
        else if (!E2E.HasClaude)
        {
            Skip = "Needs Anthropic credentials (user-secret Anthropic:ApiKey or ANTHROPIC_API_KEY).";
        }
    }
}
