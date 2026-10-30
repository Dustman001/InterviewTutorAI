using System.Text.Json;
using Microsoft.Extensions.Options;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Practice;

namespace TutorAI.Api.Prep;

public sealed record PrepRunRequest(string? JobDescription, string? Role, string? Seniority);

/// <summary>A problem the endpoint turns into a status code and an error message for the learner.</summary>
public sealed record PrepProblem(int StatusCode, string Error);

/// <summary>Starts prep runs, decides pending actions, and starts or cancels drills. All of it without a model call except the run itself.</summary>
public sealed class PrepService(
    PrepAgent agent,
    IPrepRepository prep,
    IPracticeRepository practice,
    ISessionRepository sessions,
    IUnitOfWorkFactory database,
    TimeProvider clock,
    IOptions<PrepOptions> options,
    IOptions<SessionLimitsOptions> limits)
{
    public const int MinJobDescriptionChars = 30;
    public const int MaxIdempotencyKeyChars = 100;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static PrepProblem? Validate(PrepRunRequest request, string? idempotencyKey, SessionLimitsOptions limits)
    {
        var jd = request.JobDescription?.Trim() ?? "";
        if (jd.Length < MinJobDescriptionChars)
        {
            return new(StatusCodes.Status400BadRequest, $"Paste the job description (at least {MinJobDescriptionChars} characters).");
        }

        if (jd.Length > limits.MaxJobDescriptionChars)
        {
            return new(StatusCodes.Status400BadRequest, $"The job description can be at most {limits.MaxJobDescriptionChars} characters.");
        }

        if (request.Role?.Length > limits.MaxScenarioFieldChars || request.Seniority?.Length > limits.MaxScenarioFieldChars)
        {
            return new(StatusCodes.Status400BadRequest, $"Role and seniority can be at most {limits.MaxScenarioFieldChars} characters.");
        }

        return idempotencyKey?.Length > MaxIdempotencyKeyChars
            ? new(StatusCodes.Status400BadRequest, $"Idempotency-Key can be at most {MaxIdempotencyKeyChars} characters.")
            : null;
    }

    /// <returns>The run (new, or the earlier one with the same idempotency key), or why none was started.</returns>
    public async Task<(PrepRunRow? Run, bool Created, PrepProblem? Problem)> StartRunAsync(
        int userId, bool isAdmin, PrepRunRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (Validate(request, idempotencyKey, limits.Value) is { } invalid)
        {
            return (null, false, invalid);
        }

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is not null && await prep.FindRunByKeyAsync(userId, key, ct) is { } replay)
        {
            return (replay, false, null);
        }

        // After the replay lookup: retrying a request that already ran doesn't count against the cap.
        var now = clock.GetUtcNow().UtcDateTime;
        if (!isAdmin && await prep.CountRunsSinceAsync(userId, now.AddDays(-1), ct) >= options.Value.RunsPerDay)
        {
            return (null, false, new(StatusCodes.Status429TooManyRequests,
                $"You can run interview prep {options.Value.RunsPerDay} times a day. Try again tomorrow."));
        }

        var (run, created) = await prep.CreateRunAsync(new PrepRunRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IdempotencyKey = key,
            JobDescription = request.JobDescription!.Trim(),
            Role = Blank(request.Role),
            Seniority = Blank(request.Seniority),
            Status = PrepRunStatus.Running,
            Model = options.Value.Model,
            CreatedAt = now,
        }, ct);
        if (!created)
        {
            // The same request raced in and owns the run.
            return (run, false, null);
        }

        await agent.RunAsync(run, ct);
        return (await prep.GetRunAsync(run.Id, userId, ct), true, null);
    }

    /// <summary>Applies a pending action once. Confirming an action already confirmed returns it unchanged.</summary>
    public async Task<(PrepActionRow? Action, PrepProblem? Problem)> ConfirmAsync(int userId, Guid runId, long actionId, CancellationToken ct)
    {
        if (await prep.GetActionAsync(actionId, runId, userId, ct) is not { } action)
        {
            return (null, new(StatusCodes.Status404NotFound, "No such action."));
        }

        if (action.Status == PrepActionStatus.Confirmed)
        {
            return (action, null);
        }

        if (action.Status != PrepActionStatus.PendingConfirmation)
        {
            return (null, new(StatusCodes.Status409Conflict, $"This action is {action.Status.Value} and can't be confirmed."));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using (var work = await database.BeginAsync(ct))
        {
            object result = action.Tool switch
            {
                PrepTools.RestartPracticeItemTool => await RestartAsync(work),
                _ => throw new InvalidOperationException($"No confirmation handler for {action.Tool}."),
            };

            if (!await prep.DecideActionAsync(action.Id, PrepActionStatus.Confirmed, JsonSerializer.Serialize(result, Json), now, work, ct))
            {
                // Decided meanwhile (a double click): roll back, report what's stored.
                return await prep.GetActionAsync(actionId, runId, userId, ct) is { Status: var status } current && status == PrepActionStatus.Confirmed
                    ? (current, null)
                    : (null, new(StatusCodes.Status409Conflict, "This action was already decided."));
            }

            await work.CommitAsync(ct);
        }

        return (await prep.GetActionAsync(actionId, runId, userId, ct), null);

        async Task<object> RestartAsync(IUnitOfWork work)
        {
            var itemId = JsonSerializer.Deserialize<JsonElement>(action.ArgsJson).GetProperty("itemId").GetInt64();
            var item = await practice.RestartAsync(itemId, userId, now, work, ct);
            return item is null
                ? new { restarted = false, note = "The practice item no longer exists." }
                : new { restarted = true, itemId = item.Id, item.Target };
        }
    }

    public async Task<(PrepActionRow? Action, PrepProblem? Problem)> RejectAsync(int userId, Guid runId, long actionId, CancellationToken ct)
    {
        if (await prep.GetActionAsync(actionId, runId, userId, ct) is not { } action)
        {
            return (null, new(StatusCodes.Status404NotFound, "No such action."));
        }

        if (action.Status == PrepActionStatus.Rejected)
        {
            return (action, null);
        }

        if (action.Status != PrepActionStatus.PendingConfirmation)
        {
            return (null, new(StatusCodes.Status409Conflict, $"This action is {action.Status.Value} and can't be rejected."));
        }

        await using (var work = await database.BeginAsync(ct))
        {
            if (!await prep.DecideActionAsync(action.Id, PrepActionStatus.Rejected, null, clock.GetUtcNow().UtcDateTime, work, ct))
            {
                return (null, new(StatusCodes.Status409Conflict, "This action was already decided."));
            }

            await work.CommitAsync(ct);
        }

        return (await prep.GetActionAsync(actionId, runId, userId, ct), null);
    }

    /// <summary>Starts the drill as an interview session (counts toward the guest's session limit). Starting it again returns the same session.</summary>
    public async Task<(Guid? SessionId, bool Created, PrepProblem? Problem)> StartDrillAsync(int userId, Guid drillId, CancellationToken ct)
    {
        if (await prep.GetDrillAsync(drillId, userId, ct) is not { } drill)
        {
            return (null, false, new(StatusCodes.Status404NotFound, "No such drill."));
        }

        if (drill.Status == DrillStatus.Started && drill.SessionId is { } existing)
        {
            return (existing, false, null);
        }

        if (drill.Status != DrillStatus.Scheduled)
        {
            return (null, false, new(StatusCodes.Status409Conflict, $"This drill is {drill.Status.Value}."));
        }

        var scenario = JsonSerializer.Deserialize<Scenario>(drill.ScenarioJson, Json) ?? new Scenario();
        if (SessionRules.ScenarioProblem(scenario, limits.Value) is { } problem)
        {
            return (null, false, new(StatusCodes.Status409Conflict, $"This drill can't start: {problem}"));
        }

        if (await sessions.CreateSessionAsync(userId, SessionMode.Interview, drill.ScenarioJson, ct) is not { } session)
        {
            return (null, false, new(StatusCodes.Status403Forbidden, "This account has used all its sessions."));
        }

        if (!await prep.UpdateDrillAsync(drill.Id, userId, DrillStatus.Started, session.Id, clock.GetUtcNow().UtcDateTime, ct))
        {
            // Started from another tab at the same moment; that one's session wins.
            var current = await prep.GetDrillAsync(drillId, userId, ct);
            return current?.SessionId is { } other
                ? (other, false, null)
                : (null, false, new(StatusCodes.Status409Conflict, "This drill was cancelled."));
        }

        return (session.Id, true, null);
    }

    public async Task<PrepProblem?> CancelDrillAsync(int userId, Guid drillId, CancellationToken ct)
    {
        if (await prep.GetDrillAsync(drillId, userId, ct) is not { } drill)
        {
            return new(StatusCodes.Status404NotFound, "No such drill.");
        }

        if (drill.Status == DrillStatus.Cancelled)
        {
            return null;
        }

        return drill.Status == DrillStatus.Scheduled
               && await prep.UpdateDrillAsync(drill.Id, userId, DrillStatus.Cancelled, null, clock.GetUtcNow().UtcDateTime, ct)
            ? null
            : new(StatusCodes.Status409Conflict, "This drill has already started.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
