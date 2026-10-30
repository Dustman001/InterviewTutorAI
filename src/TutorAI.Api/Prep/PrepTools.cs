using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;
using TutorAI.Api.Practice;

namespace TutorAI.Api.Prep;

/// <summary>A phrase for the learner to rehearse, as the agent proposes it.</summary>
public sealed record PhraseInput(
    [property: Description("The phrase or sentence to read aloud, 3 to 300 characters, as the learner could say it in this interview.")]
    string Text,
    [property: Description("One sentence on why it helps, addressed to the learner.")]
    string Explanation);

/// <summary>One tool call in a run, as the evals and logs see it.</summary>
public sealed record PrepToolCall(string Tool, string ArgsJson, string ResultJson, bool Error);

/// <summary>
/// The agent's tools for one run. The model decides what to call; everything here checks the arguments, scopes
/// every read and write to the run's user, records each write once (<see cref="OnceAsync"/>), and turns the
/// irreversible tool into a proposal for the learner to confirm. Errors go back to the model as tool results,
/// so it can correct itself.
/// </summary>
public sealed class PrepTools(
    PrepRunRow run,
    IPrepRepository prep,
    IPracticeRepository practice,
    ISessionRepository sessions,
    IUnitOfWorkFactory database,
    TimeProvider clock,
    PrepOptions options,
    SessionLimitsOptions limits)
{
    public const string GetWeakAreasTool = "get_weak_areas";
    public const string PlanQuestionsTool = "plan_questions";
    public const string AddPracticeItemsTool = "add_practice_items";
    public const string ScheduleDrillTool = "schedule_drill";
    public const string RestartPracticeItemTool = "restart_practice_item";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private readonly List<PrepToolCall> _calls = [];
    private IReadOnlyList<PlannedQuestion> _questions = [];
    private int _phrasesAdded;
    private int _drillsScheduled;

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<PrepToolCall> Calls => _calls;

    public IReadOnlyList<PlannedQuestion> Questions => _questions;

    public IList<AITool> All =>
    [
        AIFunctionFactory.Create(GetWeakAreasAsync, GetWeakAreasTool,
            "Reads the learner's recent graded sessions (CEFR levels, the grader's advice and corrections) and their practice items " +
            "(ids, targets, step 0-4, last score). Call it once, first."),
        AIFunctionFactory.Create(PlanQuestionsAsync, PlanQuestionsTool,
            "Records the interview questions for this job, in the order an interviewer would ask them. A later call replaces the list."),
        AIFunctionFactory.Create(AddPracticeItemsAsync, AddPracticeItemsTool,
            "Adds phrases to the learner's practice list. Phrases the learner already has are skipped and reported with their ids. " +
            "The learner can delete added items."),
        AIFunctionFactory.Create(ScheduleDrillAsync, ScheduleDrillTool,
            "Schedules a spoken mock-interview drill that asks the planned questions. Needs plan_questions first. The learner can cancel it."),
        AIFunctionFactory.Create(RestartPracticeItemAsync, RestartPracticeItemTool,
            "Proposes sending an existing practice item back to the first step, which discards its progress. This cannot be undone, " +
            "so it is NOT done now: the learner is asked to confirm after the run. Use only for an item that is mastered or well " +
            "along (step 2 or more) and directly relevant to this job."),
    ];

    [Description("Reads the learner's weak areas.")]
    private async Task<string> GetWeakAreasAsync(CancellationToken ct)
    {
        var progress = await sessions.GetProgressAsync(run.UserId, null, options.WeakAreaSessions, ct);
        var recent = new List<object>();
        foreach (var point in progress.Reverse())
        {
            var assessment = await sessions.GetAssessmentAsync(point.SessionId, ct);
            var detail = Detail(assessment);
            recent.Add(new
            {
                date = point.StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                mode = point.Mode.Value,
                levels = new { point.Overall, point.Fluency, point.Grammar, point.Vocabulary, point.Interaction, point.Pronunciation },
                advice = detail is null ? null : new
                {
                    fluency = detail.Fluency?.Advice,
                    grammar = detail.Grammar?.Advice,
                    vocabulary = detail.Vocabulary?.Advice,
                    interaction = detail.Interaction?.Advice,
                },
                corrections = detail?.TopCorrections?.Take(4).Select(c => new { c.Original, c.Corrected }),
            });
        }

        var items = await practice.ListAsync(run.UserId, includeMastered: true, limit: 40, ct);
        var result = Serialize(new
        {
            gradedSessions = recent,
            practiceItems = items.Select(i => new
            {
                i.Id,
                kind = i.Kind.Value,
                i.Target,
                step = (int)i.Box,
                mastered = i.DueAt is null,
                i.Attempts,
                i.LastScore,
            }),
            note = recent.Count == 0 ? "No graded sessions yet: plan for the job alone and keep the practice phrases general." : null,
        });
        return Record(GetWeakAreasTool, "{}", result);
    }

    private async Task<string> PlanQuestionsAsync(
        [Description("The questions, in order. Each: the question as the interviewer would say it, its focus (e.g. behavioural, technical, motivation), and why it suits this job and learner.")]
        PlannedQuestion[] questions,
        CancellationToken ct)
    {
        var args = Serialize(new { questions });
        var cleaned = (questions ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q?.Question))
            .Select(q => q with
            {
                Question = q.Question.Trim(),
                Focus = (q.Focus ?? "").Trim(),
                Why = (q.Why ?? "").Trim(),
            })
            .ToList();
        if (cleaned.Count < options.MinQuestions || cleaned.Count > options.MaxQuestions)
        {
            return Fail(PlanQuestionsTool, args, $"Plan {options.MinQuestions} to {options.MaxQuestions} questions; got {cleaned.Count}.");
        }

        if (cleaned.FirstOrDefault(q => q.Question.Length > limits.MaxQuestionChars) is { } tooLong)
        {
            return Fail(PlanQuestionsTool, args, $"Each question must be at most {limits.MaxQuestionChars} characters: \"{tooLong.Question[..40]}...\" is longer.");
        }

        _questions = cleaned;
        await prep.SetQuestionsAsync(run.Id, Serialize(cleaned), ct);
        return Record(PlanQuestionsTool, args, Serialize(new { planned = cleaned.Count }));
    }

    private async Task<string> AddPracticeItemsAsync(
        [Description("The phrases to add, at most 10 per run in all.")] PhraseInput[] phrases,
        CancellationToken ct)
    {
        var args = Serialize(new { phrases });
        var wanted = (phrases ?? [])
            .Where(p => p is not null)
            .Select(p => (Text: (p.Text ?? "").Trim(), Explanation: (p.Explanation ?? "").Trim()))
            .Where(p => p.Text.Length > 0)
            .DistinctBy(p => PracticeSeeds.Key(p.Text))
            .ToList();
        if (wanted.Count == 0)
        {
            return Fail(AddPracticeItemsTool, args, "Give at least one phrase.");
        }

        if (wanted.FirstOrDefault(p => p.Text.Length is < 3 or > PracticeSeeds.MaxTargetChars) is { Text: { } bad })
        {
            return Fail(AddPracticeItemsTool, args, $"Each phrase must have 3 to {PracticeSeeds.MaxTargetChars} characters: \"{Shorten(bad)}\" doesn't.");
        }

        var keys = wanted.Select(p => PracticeSeeds.Key(p.Text)).Order(StringComparer.Ordinal).ToList();
        return await OnceAsync(AddPracticeItemsTool, keys, args, PrepActionStatus.Applied, async (work, ct2) =>
        {
            var remaining = options.MaxPhrasesPerRun - _phrasesAdded;
            if (wanted.Count > remaining)
            {
                throw new ToolRefusedException($"This run can add {remaining} more phrase(s); {wanted.Count} were given.");
            }

            var existing = await practice.FindByTargetKeysAsync(run.UserId, keys, ct2);
            var existingKeys = existing.Select(e => e.TargetKey).ToHashSet(StringComparer.Ordinal);
            var seeds = wanted
                .Where(p => !existingKeys.Contains(PracticeSeeds.Key(p.Text)))
                .Select(p => new PracticeSeed(PracticeKind.Phrase, p.Text, PracticeSeeds.Key(p.Text), null,
                    p.Explanation.Length > 1000 ? p.Explanation[..1000] : p.Explanation))
                .ToList();
            var added = seeds.Count == 0
                ? []
                : await practice.InsertNewAsync(new PracticeBatch(run.UserId, null, null, Now, seeds), work, ct2);
            _phrasesAdded += added.Count;
            return new
            {
                added = added.Select(a => new { a.Id, a.Target }),
                skippedAlreadyPresent = existing.Select(e => new { e.Id, e.Target, step = (int)e.Box, mastered = e.DueAt is null }),
            };
        }, ct);
    }

    private async Task<string> ScheduleDrillAsync(
        [Description("When, as an ISO 8601 date and time with offset, e.g. 2026-11-02T18:00:00Z. Not in the past, at most 30 days ahead.")]
        string scheduledFor,
        [Description("Length in minutes, 5 to 30.")] int durationMinutes,
        [Description("behavioral, technical or mixed.")] string interviewType,
        CancellationToken ct)
    {
        var args = Serialize(new { scheduledFor, durationMinutes, interviewType });
        if (!DateTimeOffset.TryParse(scheduledFor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            return Fail(ScheduleDrillTool, args, "scheduledFor must be an ISO 8601 date and time, e.g. 2026-11-02T18:00:00Z.");
        }

        var at = when.UtcDateTime;
        if (at < Now.AddMinutes(-5) || at > Now.AddDays(options.MaxDrillDaysAhead))
        {
            return Fail(ScheduleDrillTool, args, $"Schedule between now ({Now:yyyy-MM-ddTHH:mm}Z) and {options.MaxDrillDaysAhead} days ahead.");
        }

        var maxMinutes = Math.Min(30, limits.MaxDurationMinutes);
        if (durationMinutes < limits.MinDurationMinutes || durationMinutes > maxMinutes)
        {
            return Fail(ScheduleDrillTool, args, $"durationMinutes must be {limits.MinDurationMinutes} to {maxMinutes}.");
        }

        var type = interviewType?.Trim().ToLowerInvariant() is "behavioral" or "technical" or "mixed"
            ? interviewType.Trim().ToLowerInvariant()
            : "mixed";
        if (_questions.Count == 0)
        {
            return Fail(ScheduleDrillTool, args, "Call plan_questions first: the drill asks the planned questions.");
        }

        var minute = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var questions = _questions.Select(q => q.Question).ToList();
        return await OnceAsync(ScheduleDrillTool, new { minute, durationMinutes, type, questions }, args, PrepActionStatus.Applied, async (work, ct2) =>
        {
            if (_drillsScheduled >= options.MaxDrillsPerRun)
            {
                throw new ToolRefusedException($"A run can schedule at most {options.MaxDrillsPerRun} drills.");
            }

            var jobDescription = run.JobDescription.Length > limits.MaxJobDescriptionChars
                ? run.JobDescription[..limits.MaxJobDescriptionChars]
                : run.JobDescription;
            var scenario = new Scenario(
                Role: run.Role, Seniority: run.Seniority, InterviewType: type, JobDescription: jobDescription,
                DurationMinutes: durationMinutes, Questions: questions);
            var drill = await prep.AddDrillAsync(new DrillRow
            {
                Id = Guid.NewGuid(),
                UserId = run.UserId,
                RunId = run.Id,
                ScheduledFor = minute,
                ScenarioJson = JsonSerializer.Serialize(scenario, Json),
                Status = DrillStatus.Scheduled,
                CreatedAt = Now,
                UpdatedAt = Now,
            }, work, ct2);
            _drillsScheduled++;
            return new { drillId = drill.Id, scheduledFor = minute, durationMinutes, interviewType = type, questions = questions.Count };
        }, ct);
    }

    private async Task<string> RestartPracticeItemAsync(
        [Description("The practice item's id, from get_weak_areas or add_practice_items.")] long itemId,
        [Description("One sentence for the learner on why this item is worth practising again for this job.")] string reason,
        CancellationToken ct)
    {
        var args = Serialize(new { itemId, reason });
        if (await practice.GetAsync(itemId, run.UserId, ct) is not { } item)
        {
            return Fail(RestartPracticeItemTool, args, $"The learner has no practice item {itemId}.");
        }

        if (item.Box == 0 && item.DueAt is not null)
        {
            return Fail(RestartPracticeItemTool, args, "That item is already at the first step; there is nothing to restart.");
        }

        var proposal = new
        {
            itemId = item.Id,
            target = item.Target,
            step = (int)item.Box,
            mastered = item.DueAt is null,
            reason = (reason ?? "").Trim() is { Length: > 400 } r ? r[..400] : (reason ?? "").Trim(),
        };
        return await OnceAsync(RestartPracticeItemTool, new { itemId }, Serialize(proposal), PrepActionStatus.PendingConfirmation,
            write: null, ct);
    }

    /// <summary>
    /// Runs a write at most once per run: the key is a hash of the tool and its normalised arguments, unique per
    /// run in dbo.PrepActions. A repeated call gets the first result back without writing again. With no write,
    /// the action is recorded as pending, for the learner to confirm.
    /// </summary>
    private async Task<string> OnceAsync(
        string tool, object keyArgs, string args, PrepActionStatus status, Func<IUnitOfWork, CancellationToken, Task<object>>? write,
        CancellationToken ct)
    {
        var key = IdempotencyKey(tool, keyArgs);
        if (await prep.GetActionByKeyAsync(run.Id, key, ct) is { } done)
        {
            return Record(tool, args, Serialize(new
            {
                alreadyDone = true,
                note = "This exact call was already made in this run; nothing was written again.",
                status = done.Status.Value,
                actionId = done.Id,
                result = done.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(done.ResultJson),
            }));
        }

        object? result;
        await using (var work = await database.BeginAsync(ct))
        {
            try
            {
                result = write is null ? null : await write(work, ct);
            }
            catch (ToolRefusedException refused)
            {
                return Fail(tool, args, refused.Message);
            }

            var resultJson = result is null ? null : Serialize(result);
            var action = await prep.AddActionAsync(new PrepActionRow
            {
                RunId = run.Id,
                IdempotencyKey = key,
                Tool = tool,
                ArgsJson = args,
                Status = status,
                ResultJson = resultJson,
                CreatedAt = Now,
            }, work, ct);
            await work.CommitAsync(ct);

            return Record(tool, args, status == PrepActionStatus.PendingConfirmation
                ? Serialize(new
                {
                    actionId = action.Id,
                    status = status.Value,
                    note = "Not done yet: the learner will be asked to confirm this after the run. Mention it in your summary; don't call it again.",
                })
                : Serialize(new { actionId = action.Id, status = status.Value, result }));
        }
    }

    internal static string IdempotencyKey(string tool, object keyArgs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + JsonSerializer.Serialize(keyArgs, Json))));

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static GradingResult? Detail(AssessmentRow? assessment)
    {
        if (assessment?.Overall is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GradingResult>(assessment.DetailJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Record(string tool, string args, string result)
    {
        lock (_calls)
        {
            _calls.Add(new PrepToolCall(tool, args, result, Error: false));
        }

        return result;
    }

    private string Fail(string tool, string args, string error)
    {
        var result = Serialize(new { error });
        lock (_calls)
        {
            _calls.Add(new PrepToolCall(tool, args, result, Error: true));
        }

        return result;
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "..." : text;

    /// <summary>A limit the write checked inside its transaction; goes back to the model as an error, nothing is recorded.</summary>
    private sealed class ToolRefusedException(string message) : Exception(message);
}
