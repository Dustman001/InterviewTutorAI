using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using TutorAI.Api.Assessment;
using TutorAI.Api.Auth;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Speech;

namespace TutorAI.Api.Endpoints;

/// <param name="Mode">Conversation or Interview.</param>
public sealed record CreateSessionRequest(string Mode, Scenario? Scenario);

/// <param name="Phrases">Raw Azure Speech detailed-result JSON, one object per recognized phrase of the turn.</param>
public sealed record TurnRequest(string? Text, int? DurationMs, JsonElement[]? Phrases);

public sealed record SessionDto(
    Guid Id, SessionMode Mode, Scenario Scenario, DateTime StartedAt, DateTime? EndedAt, SessionStatus Status);

public sealed record SessionSummaryDto(
    Guid Id, SessionMode Mode, Scenario Scenario, DateTime StartedAt, DateTime? EndedAt, SessionStatus Status,
    AssessmentLevels? Levels, GradingState? GradingState);

/// <param name="NextCursor">Pass as <c>before</c> to get the next page; null on the last page.</param>
public sealed record SessionPageDto(IReadOnlyList<SessionSummaryDto> Items, string? NextCursor);

public sealed record TurnDto(
    long Id, int Seq, TurnRole Role, string Text, DateTime StartedAt, int? DurationMs, int WordCount,
    double? AccuracyScore, double? FluencyScore, double? ProsodyScore);

/// <param name="State">Pending, Running, Done or Failed; null when the session was never queued for grading.</param>
public sealed record GradingDto(GradingState? State, string? Error);

public sealed record SessionDetailDto(
    SessionDto Session, IReadOnlyList<TurnDto> Turns, AssessmentResponse? Assessment, GradingDto Grading);

public static class SessionEndpoints
{
    /// <summary>Rate-limit policy for turn requests, partitioned by user (see Program.cs).</summary>
    public const string TurnsPolicy = "turns";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/sessions").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionEndpoints));
            var scope = new Dictionary<string, object?> { ["UserId"] = http.User.FindFirst(TokenService.UserIdClaim)?.Value };
            if (http.Request.RouteValues.TryGetValue("id", out var id))
            {
                scope["SessionId"] = id;
            }

            using (logger.BeginScope(scope))
            {
                return await next(context);
            }
        });

        sessions.MapPost("/", async (
            CreateSessionRequest request, ClaimsPrincipal user, ISessionRepository repository, IUserRepository users,
            IOptions<SessionLimitsOptions> limits, IOptions<AzureSpeechOptions> speech, CancellationToken ct) =>
        {
            if (!StringValue.TryParse<SessionMode>(request.Mode, out var mode))
            {
                return Results.BadRequest(new { error = $"Mode must be {StringValue.Describe<SessionMode>()}." });
            }

            var scenario = request.Scenario ?? new Scenario();
            if (SessionRules.ScenarioProblem(scenario, limits.Value) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            if (scenario.Accent is not null)
            {
                if (AccentCatalog.Find(speech.Value, scenario.Accent) is not { } accent)
                {
                    var allowed = string.Join(", ", AccentCatalog.All(speech.Value).Select(a => a.Id));
                    return Results.BadRequest(new { error = $"Accent must be one of: {allowed}." });
                }

                scenario = scenario with { Accent = accent.Id };
            }
            var row = await repository.CreateSessionAsync(user.UserId(), mode, JsonSerializer.Serialize(scenario, Json), ct);
            if (row is null)
            {
                return await users.GetByIdAsync(user.UserId(), ct) is { SessionLimit: { } limit }
                    ? Results.Json(
                        new { error = $"This account can start {limit} sessions and has used them all. Sign in as another user to continue." },
                        statusCode: StatusCodes.Status403Forbidden)
                    : Results.Unauthorized();
            }

            return Results.Created($"/api/sessions/{row.Id}", ToDto(row));
        });

        sessions.MapGet("/", async (
            ClaimsPrincipal user, ISessionRepository repository, int? limit, string? before, CancellationToken ct) =>
        {
            var cursor = SessionCursor.TryDecode(before);
            if (before is not null && cursor is null)
            {
                return Results.BadRequest(new { error = "Invalid 'before' cursor." });
            }

            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
            var rows = await repository.ListSessionsAsync(user.IsAdmin() ? null : user.UserId(), cursor, take, ct);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? new SessionCursor(page[^1].StartedAt, page[^1].Id).Encode() : null;
            return Results.Ok(new SessionPageDto(
                [.. page.Select(r => new SessionSummaryDto(
                    r.Id, r.Mode, ParseScenario(r.ScenarioJson), Utc(r.StartedAt), Utc(r.EndedAt), r.Status,
                    r.Overall is null
                        ? null
                        : new AssessmentLevels(r.Fluency, r.Grammar, r.Vocabulary, r.Interaction, r.Pronunciation, r.Overall),
                    r.GradingState))],
                next));
        });

        sessions.MapGet("/{id:guid}", async (
            Guid id, ClaimsPrincipal user, ISessionRepository repository, AssessmentService assessments, CancellationToken ct) =>
        {
            var session = await repository.GetSessionAsync(id, ct);
            if (!CanAccess(user, session))
            {
                return Results.NotFound();
            }

            var turns = await repository.GetTurnsAsync(id, includePronunciationJson: false, ct);
            return Results.Ok(new SessionDetailDto(
                ToDto(session), [.. turns.Select(ToDto)], await assessments.GetAsync(id, ct),
                new GradingDto(session.GradingState, session.GradingState == GradingState.Failed ? session.GradingError : null)));
        });

        sessions.MapPost("/{id:guid}/turns", PostTurnAsync).RequireRateLimiting(TurnsPolicy);

        // 200 with the assessment when it's ready (stored already, or too short to grade); otherwise 202 with
        // the grading state, and the client polls GET /api/sessions/{id} until grading is Done or Failed.
        sessions.MapPost("/{id:guid}/end", async (
            Guid id, bool? regrade, ClaimsPrincipal user, ISessionRepository repository, AssessmentService assessments,
            CancellationToken ct) =>
        {
            if (!CanAccess(user, await repository.GetSessionAsync(id, ct)))
            {
                return Results.NotFound();
            }

            return await assessments.EndAsync(id, regrade ?? false, ct) switch
            {
                null => Results.NotFound(),
                { Assessment: { } assessment } => Results.Ok(assessment),
                { GradingState: var state } => Results.Accepted($"/api/sessions/{id}", new { state }),
            };
        });
    }

    /// <summary>
    /// Stores the learner's turn (when present) and streams the tutor's reply as server-sent events:
    /// <c>delta</c> {text} fragments, then <c>done</c> {turnId, text}, or <c>error</c> {message}.
    /// A request without text is only valid as the first call of a session and yields the tutor's opening line.
    /// </summary>
    private static async Task<IResult> PostTurnAsync(
        Guid id, TurnRequest request, HttpContext http, ISessionRepository repository, TurnService turns, CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(id, ct);
        if (!CanAccess(http.User, session))
        {
            return Results.NotFound();
        }

        var start = await turns.StartAsync(session, new LearnerTurn(request.Text, request.DurationMs, request.Phrases), ct);
        if (start.Rejection is { } rejection)
        {
            return Results.Json(
                rejection.Code is null ? (object)new { error = rejection.Error } : new { error = rejection.Error, code = rejection.Code },
                statusCode: rejection.StatusCode);
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var reply = await turns.ReplyAsync(
            session, start.Turns, (text, token) => WriteEventAsync(http, "delta", new { text }, token), ct);

        if (!ct.IsCancellationRequested)
        {
            if (reply.Error is not null)
            {
                await WriteEventAsync(http, "error", new { message = reply.Error }, ct);
            }
            else
            {
                await WriteEventAsync(http, "done", new { turnId = reply.Turn?.Id, text = reply.Turn?.Text ?? "" }, ct);
            }
        }

        return Results.Empty;
    }

    private static async Task WriteEventAsync(HttpContext http, string name, object data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>Users reach only their own sessions; admins reach all. Others get 404, not 403, so ids are not confirmed.</summary>
    private static bool CanAccess(ClaimsPrincipal user, [NotNullWhen(true)] SessionRow? session) =>
        session is not null && (user.IsAdmin() || session.UserId == user.UserId());

    private static SessionDto ToDto(SessionRow row) =>
        new(row.Id, row.Mode, ParseScenario(row.ScenarioJson), Utc(row.StartedAt), Utc(row.EndedAt), row.Status);

    private static TurnDto ToDto(TurnRow row) =>
        new(row.Id, row.Seq, row.Role, row.Text, Utc(row.StartedAt), row.DurationMs, row.WordCount,
            row.AccuracyScore, row.FluencyScore, row.ProsodyScore);

    private static Scenario ParseScenario(string json) => JsonSerializer.Deserialize<Scenario>(json, Json) ?? new Scenario();

    // SQL Server datetime2 comes back with Kind=Unspecified; every stored timestamp is UTC.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
