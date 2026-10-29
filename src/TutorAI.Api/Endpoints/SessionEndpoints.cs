using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using TutorAI.Api.Assessment;
using TutorAI.Api.Auth;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;

namespace TutorAI.Api.Endpoints;

public sealed record SessionDto(Guid Id, string Mode, Scenario Scenario, DateTime StartedAt, DateTime? EndedAt, string Status);

public sealed record SessionSummaryDto(
    Guid Id, string Mode, Scenario Scenario, DateTime StartedAt, DateTime? EndedAt, string Status, AssessmentLevels? Levels,
    string? GradingState);

/// <param name="NextCursor">Pass as <c>before</c> to get the next page; null on the last page.</param>
public sealed record SessionPageDto(IReadOnlyList<SessionSummaryDto> Items, string? NextCursor);

public sealed record TurnDto(
    long Id, int Seq, string Role, string Text, DateTime StartedAt, int? DurationMs, int WordCount,
    double? AccuracyScore, double? FluencyScore, double? ProsodyScore);

/// <param name="State">Pending, Running, Done or Failed; null when the session was never queued for grading.</param>
public sealed record GradingDto(string? State, string? Error);

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
            CreateSessionRequest request, ClaimsPrincipal user, SessionRepository repository, UserRepository users,
            IOptions<SessionLimitsOptions> limits, CancellationToken ct) =>
        {
            if (!SessionModes.IsValid(request.Mode))
            {
                return Results.BadRequest(new { error = $"Mode must be '{SessionModes.Conversation}' or '{SessionModes.Interview}'." });
            }

            var scenario = request.Scenario ?? new Scenario();
            if (SessionRules.ScenarioProblem(scenario, limits.Value) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }
            var row = await repository.CreateSessionAsync(user.UserId(), request.Mode, JsonSerializer.Serialize(scenario, Json), ct);
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
            ClaimsPrincipal user, SessionRepository repository, int? limit, string? before, CancellationToken ct) =>
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
            Guid id, ClaimsPrincipal user, SessionRepository repository, AssessmentService assessments, CancellationToken ct) =>
        {
            var session = await repository.GetSessionAsync(id, ct);
            if (!CanAccess(user, session))
            {
                return Results.NotFound();
            }

            var turns = await repository.GetTurnsAsync(id, includePronunciationJson: false, ct);
            return Results.Ok(new SessionDetailDto(
                ToDto(session), [.. turns.Select(ToDto)], await assessments.GetAsync(id, ct),
                new GradingDto(session.GradingState, session.GradingState == GradingStates.Failed ? session.GradingError : null)));
        });

        sessions.MapPost("/{id:guid}/turns", PostTurnAsync).RequireRateLimiting(TurnsPolicy);

        // 200 with the assessment when it's ready (stored already, or too short to grade); otherwise 202 with
        // the grading state, and the client polls GET /api/sessions/{id} until grading is Done or Failed.
        sessions.MapPost("/{id:guid}/end", async (
            Guid id, bool? regrade, ClaimsPrincipal user, SessionRepository repository, AssessmentService assessments,
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
        Guid id, TurnRequest request, HttpContext http, SessionRepository repository, TutorChat chat,
        IOptions<SessionLimitsOptions> limits, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(id, ct);
        if (!CanAccess(http.User, session))
        {
            return Results.NotFound();
        }

        if (session.Status != SessionStatus.Active)
        {
            return Results.Conflict(new { error = "The session has ended." });
        }

        var turns = (await repository.GetTurnsAsync(id, includePronunciationJson: false, ct)).ToList();
        var text = request.Text?.Trim();
        if (SessionRules.CheckTurn(
                session, ParseScenario(session.ScenarioJson), turns, string.IsNullOrEmpty(text) ? null : text,
                request.Phrases?.Length ?? 0, clock.GetUtcNow().UtcDateTime, limits.Value) is { } rejection)
        {
            return Results.Json(new { error = rejection.Error, code = rejection.Code }, statusCode: rejection.StatusCode);
        }

        if (string.IsNullOrEmpty(text))
        {
            if (turns.Count > 0)
            {
                return Results.BadRequest(new { error = "Text is required once the session has started." });
            }
        }
        else
        {
            var phrases = AzurePronunciation.ParseTurn(request.Phrases);
            turns.Add(await repository.AddTurnAsync(
                new TurnRow
                {
                    SessionId = id,
                    Role = TurnRoles.User,
                    Text = text,
                    DurationMs = request.DurationMs,
                    WordCount = CountWords(text),
                    AccuracyScore = AzurePronunciation.WeightedMean(phrases, p => p.Accuracy),
                    FluencyScore = AzurePronunciation.WeightedMean(phrases, p => p.Fluency),
                    ProsodyScore = AzurePronunciation.WeightedMean(phrases, p => p.Prosody),
                    PronunciationJson = request.Phrases is { Length: > 0 } ? JsonSerializer.Serialize(request.Phrases) : null,
                },
                ct));
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var reply = new StringBuilder();
        var usage = new ClaudeUsage();
        string? error = null;
        try
        {
            await foreach (var fragment in chat.StreamReplyAsync(session, turns, usage, ct))
            {
                reply.Append(fragment);
                await WriteEventAsync(http, "delta", new { text = fragment }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The learner interrupted or left; keep whatever was already said.
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(SessionEndpoints)).LogError(ex, "Tutor reply failed for session {SessionId}", id);
            error = "The tutor could not reply. Check the API logs and your Anthropic credentials.";
        }

        TurnRow? tutorTurn = null;
        if (reply.Length > 0)
        {
            var replyText = reply.ToString().Trim();
            tutorTurn = await repository.AddTurnAsync(
                new TurnRow
                {
                    SessionId = id,
                    Role = TurnRoles.Tutor,
                    Text = replyText,
                    WordCount = CountWords(replyText),
                    // Output stays 0 when the learner interrupted before Claude reported it.
                    Model = usage.Model,
                    InputTokens = (int)usage.InputTokens,
                    OutputTokens = (int)usage.OutputTokens,
                    CacheReadTokens = (int)usage.CacheReadTokens,
                    CacheWriteTokens = (int)usage.CacheWriteTokens,
                },
                CancellationToken.None);
        }

        if (!ct.IsCancellationRequested)
        {
            if (error is not null)
            {
                await WriteEventAsync(http, "error", new { message = error }, ct);
            }
            else
            {
                await WriteEventAsync(http, "done", new { turnId = tutorTurn?.Id, text = tutorTurn?.Text ?? "" }, ct);
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

    internal static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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
