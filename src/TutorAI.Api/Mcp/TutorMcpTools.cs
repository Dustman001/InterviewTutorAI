using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TutorAI.Api.Assessment;
using TutorAI.Api.Auth;
using TutorAI.Api.Data;
using TutorAI.Api.Practice;

namespace TutorAI.Api.Mcp;

/// <summary>
/// Read-only MCP tools over the caller's own data: progress, practice items and session history. Every tool reads
/// the user from the request's MCP token; admins see only their own data here too.
/// </summary>
[McpServerToolType]
public sealed class TutorMcpTools(ISessionRepository sessions, IPracticeRepository practice, TimeProvider clock)
{
    private const int MaxSessions = 50;
    private const int MaxItems = 100;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [McpServerTool(Name = "get_progress", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The learner's graded sessions over time, oldest first: CEFR level per criterion (fluency, grammar, vocabulary, " +
                 "interaction, pronunciation), overall level, speech rate, pause ratio and pronunciation score.")]
    public async Task<object> GetProgress(
        RequestContext<CallToolRequestParams> context,
        [Description("Conversation or Interview; omit for both.")] string? mode = null,
        [Description("The most recent graded sessions to return, 1 to 200 (default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        SessionMode? sessionMode = null;
        if (mode is not null)
        {
            if (!StringValue.TryParse<SessionMode>(mode, out var parsed))
            {
                throw new McpException($"mode must be {StringValue.Describe<SessionMode>()}.");
            }

            sessionMode = parsed;
        }

        var rows = await sessions.GetProgressAsync(UserId(context), sessionMode, Math.Clamp(limit, 1, 200), ct);
        return rows.Select(r => new
        {
            r.SessionId,
            startedAt = Utc(r.StartedAt),
            mode = r.Mode.Value,
            levels = new { r.Overall, r.Fluency, r.Grammar, r.Vocabulary, r.Interaction, r.Pronunciation },
            r.WordsPerMinute,
            r.PauseRatio,
            r.PronunciationScore,
        });
    }

    [McpServerTool(Name = "list_practice_items", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The learner's practice items (corrections, weak words and interview phrases), weakest first, with their review " +
                 "step (0-4), when each is due, attempts and scores, plus how many are due, active and mastered.")]
    public async Task<object> ListPracticeItems(
        RequestContext<CallToolRequestParams> context,
        [Description("Include mastered items (default false).")] bool includeMastered = false,
        [Description("At most this many items, 1 to 100 (default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        var userId = UserId(context);
        var now = clock.GetUtcNow().UtcDateTime;
        var items = await practice.ListAsync(userId, includeMastered, Math.Clamp(limit, 1, MaxItems), ct);
        var counts = await practice.CountAsync(userId, now, ct);
        return new
        {
            counts = new { counts.Due, counts.Active, counts.Mastered, nextDueAt = counts.NextDueAt is { } next ? Utc(next) : (DateTime?)null },
            items = items.Select(i => new
            {
                i.Id,
                kind = i.Kind.Value,
                i.Target,
                i.Original,
                i.Explanation,
                step = (int)i.Box,
                mastered = i.DueAt is null,
                dueAt = i.DueAt is { } due ? Utc(due) : (DateTime?)null,
                i.Attempts,
                i.LastScore,
                i.BestScore,
            }),
        };
    }

    [McpServerTool(Name = "list_sessions", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The learner's sessions, newest first: mode, scenario, when, status, and levels when graded. " +
                 "Use get_session for a transcript.")]
    public async Task<object> ListSessions(
        RequestContext<CallToolRequestParams> context,
        [Description("At most this many sessions, 1 to 50 (default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxSessions);
        var rows = await sessions.ListSessionsAsync(UserId(context), null, take, ct);
        return rows.Take(take).Select(r => new
        {
            r.Id,
            mode = r.Mode.Value,
            scenario = Scenario(r.ScenarioJson),
            startedAt = Utc(r.StartedAt),
            endedAt = r.EndedAt is { } ended ? Utc(ended) : (DateTime?)null,
            status = r.Status.Value,
            levels = r.Overall is null ? null : new { r.Overall, r.Fluency, r.Grammar, r.Vocabulary, r.Interaction, r.Pronunciation },
        });
    }

    [McpServerTool(Name = "get_session", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One of the learner's sessions: the transcript, and the grader's levels, summary, advice and corrections when graded.")]
    public async Task<object> GetSession(
        RequestContext<CallToolRequestParams> context,
        [Description("The session id, from list_sessions or get_progress.")] Guid sessionId,
        CancellationToken ct = default)
    {
        var session = await sessions.GetSessionAsync(sessionId, ct);
        if (session is null || session.UserId != UserId(context))
        {
            throw new McpException("No such session.");
        }

        var turns = await sessions.GetTurnsAsync(sessionId, includePronunciationJson: false, ct);
        var assessment = await sessions.GetAssessmentAsync(sessionId, ct);
        GradingResult? detail = null;
        if (assessment?.Overall is not null)
        {
            try
            {
                detail = JsonSerializer.Deserialize<GradingResult>(assessment.DetailJson, Json);
            }
            catch (JsonException)
            {
                // An older or partial assessment: levels only.
            }
        }

        return new
        {
            session.Id,
            mode = session.Mode.Value,
            scenario = Scenario(session.ScenarioJson),
            startedAt = Utc(session.StartedAt),
            status = session.Status.Value,
            transcript = turns.Select(t => new { role = t.Role.Value, t.Text }),
            assessment = assessment?.Overall is null ? null : new
            {
                levels = new
                {
                    assessment.Overall, assessment.Fluency, assessment.Grammar, assessment.Vocabulary, assessment.Interaction,
                    assessment.Pronunciation,
                },
                detail?.Summary,
                advice = detail is null ? null : new
                {
                    fluency = detail.Fluency?.Advice,
                    grammar = detail.Grammar?.Advice,
                    vocabulary = detail.Vocabulary?.Advice,
                    interaction = detail.Interaction?.Advice,
                },
                corrections = detail?.TopCorrections,
            },
        };
    }

    private static int UserId(RequestContext<CallToolRequestParams> context) =>
        (context.User ?? throw new McpException("Not signed in.")).UserId();

    private static Scenario Scenario(string json) => JsonSerializer.Deserialize<Scenario>(json, Json) ?? new Scenario();

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
