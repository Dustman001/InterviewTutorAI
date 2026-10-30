using System.Security.Claims;
using System.Text.Json;
using TutorAI.Api.Auth;
using TutorAI.Api.Data;
using TutorAI.Api.Prep;

namespace TutorAI.Api.Endpoints;

public sealed record PrepActionDto(
    long Id, string Tool, PrepActionStatus Status, JsonElement Args, JsonElement? Result, DateTime CreatedAt, DateTime? DecidedAt);

public sealed record DrillDto(Guid Id, Guid? RunId, DateTime ScheduledFor, DrillStatus Status, Scenario Scenario, Guid? SessionId);

/// <param name="StopReason">Why a Stopped or Failed run ended early.</param>
public sealed record PrepRunDto(
    Guid Id, PrepRunStatus Status, string? StopReason, string? Summary, string JobDescription, string? Role, string? Seniority,
    IReadOnlyList<PlannedQuestion> Questions, IReadOnlyList<PrepActionDto> Actions, IReadOnlyList<DrillDto> Drills,
    string? Model, int Steps, int InputTokens, int OutputTokens, DateTime CreatedAt, DateTime? CompletedAt);

public sealed record PrepRunSummaryDto(
    Guid Id, PrepRunStatus Status, string? Role, string? Seniority, string? Summary, DateTime CreatedAt);

public static class PrepEndpoints
{
    /// <summary>Rate-limit policy for starting runs, partitioned by user (see Program.cs).</summary>
    public const string RunsPolicy = "prep-runs";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int MaxListedRuns = 20;
    /// <summary>Started and cancelled drills stay on the list this long.</summary>
    public const int RecentDrillDays = 7;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void MapPrepEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/prep");

        // Runs the agent to the end (bounded by Prep:RunTimeoutSeconds) and returns what it did.
        group.MapPost("/runs", async (
            PrepRunRequest request, HttpRequest http, ClaimsPrincipal user, PrepService service, IPrepRepository prep,
            CancellationToken ct) =>
        {
            var (run, created, problem) = await service.StartRunAsync(
                user.UserId(), user.IsAdmin(), request, http.Headers[IdempotencyHeader].FirstOrDefault(), ct);
            if (problem is not null)
            {
                return Results.Json(new { error = problem.Error }, statusCode: problem.StatusCode);
            }

            var dto = await ToDtoAsync(run!, prep, ct);
            return created ? Results.Created($"/api/prep/runs/{run!.Id}", dto) : Results.Ok(dto);
        }).RequireRateLimiting(RunsPolicy);

        group.MapGet("/runs", async (ClaimsPrincipal user, IPrepRepository prep, CancellationToken ct) =>
            Results.Ok((await prep.ListRunsAsync(user.UserId(), MaxListedRuns, ct))
                .Select(r => new PrepRunSummaryDto(r.Id, r.Status, r.Role, r.Seniority, r.Summary, Utc(r.CreatedAt)))));

        group.MapGet("/runs/{id:guid}", async (Guid id, ClaimsPrincipal user, IPrepRepository prep, CancellationToken ct) =>
            await prep.GetRunAsync(id, user.UserId(), ct) is { } run
                ? Results.Ok(await ToDtoAsync(run, prep, ct))
                : Results.NotFound());

        // The confirmation step: an irreversible action proposed by the agent runs only from here.
        group.MapPost("/runs/{id:guid}/actions/{actionId:long}/confirm", async (
            Guid id, long actionId, ClaimsPrincipal user, PrepService service, CancellationToken ct) =>
            ActionResult(await service.ConfirmAsync(user.UserId(), id, actionId, ct)));

        group.MapPost("/runs/{id:guid}/actions/{actionId:long}/reject", async (
            Guid id, long actionId, ClaimsPrincipal user, PrepService service, CancellationToken ct) =>
            ActionResult(await service.RejectAsync(user.UserId(), id, actionId, ct)));

        group.MapGet("/drills", async (ClaimsPrincipal user, IPrepRepository prep, TimeProvider clock, CancellationToken ct) =>
            Results.Ok((await prep.ListDrillsAsync(user.UserId(), clock.GetUtcNow().UtcDateTime, RecentDrillDays, ct)).Select(ToDto)));

        group.MapPost("/drills/{id:guid}/start", async (Guid id, ClaimsPrincipal user, PrepService service, CancellationToken ct) =>
        {
            var (sessionId, created, problem) = await service.StartDrillAsync(user.UserId(), id, ct);
            return problem is not null
                ? Results.Json(new { error = problem.Error }, statusCode: problem.StatusCode)
                : created
                    ? Results.Created($"/api/sessions/{sessionId}", new { sessionId })
                    : Results.Ok(new { sessionId });
        });

        group.MapPost("/drills/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, PrepService service, CancellationToken ct) =>
            await service.CancelDrillAsync(user.UserId(), id, ct) is { } problem
                ? Results.Json(new { error = problem.Error }, statusCode: problem.StatusCode)
                : Results.NoContent());
    }

    private static IResult ActionResult((PrepActionRow? Action, PrepProblem? Problem) outcome) =>
        outcome.Problem is { } problem
            ? Results.Json(new { error = problem.Error }, statusCode: problem.StatusCode)
            : Results.Ok(ToDto(outcome.Action!));

    private static async Task<PrepRunDto> ToDtoAsync(PrepRunRow run, IPrepRepository prep, CancellationToken ct)
    {
        var actions = await prep.ListActionsAsync(run.Id, ct);
        var drills = await prep.ListDrillsForRunAsync(run.Id, ct);
        var questions = run.QuestionsJson is null ? [] : JsonSerializer.Deserialize<List<PlannedQuestion>>(run.QuestionsJson, Json) ?? [];
        return new PrepRunDto(
            run.Id, run.Status, run.StopReason, run.Summary, run.JobDescription, run.Role, run.Seniority, questions,
            [.. actions.Select(ToDto)], [.. drills.Select(ToDto)], run.Model, run.Steps, run.InputTokens, run.OutputTokens,
            Utc(run.CreatedAt), run.CompletedAt is { } done ? Utc(done) : null);
    }

    private static PrepActionDto ToDto(PrepActionRow action) =>
        new(action.Id, action.Tool, action.Status, JsonSerializer.Deserialize<JsonElement>(action.ArgsJson),
            action.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(action.ResultJson),
            Utc(action.CreatedAt), action.DecidedAt is { } decided ? Utc(decided) : null);

    private static DrillDto ToDto(DrillRow drill) =>
        new(drill.Id, drill.RunId, Utc(drill.ScheduledFor), drill.Status,
            JsonSerializer.Deserialize<Scenario>(drill.ScenarioJson, Json) ?? new Scenario(), drill.SessionId);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
