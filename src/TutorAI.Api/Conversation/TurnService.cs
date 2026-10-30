using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;

namespace TutorAI.Api.Conversation;

/// <summary>What the learner sent for one turn. No text: the session's opening request.</summary>
/// <param name="Phrases">Raw Azure Speech detailed-result JSON, one object per recognized phrase of the turn.</param>
public sealed record LearnerTurn(string? Text, int? DurationMs, JsonElement[]? Phrases);

/// <summary>A refused turn, or the conversation so far (the learner's new turn last) for the tutor to answer.</summary>
public sealed record TurnStart(TurnRejection? Rejection, IReadOnlyList<TurnRow> Turns);

/// <summary>The tutor's stored reply (null when nothing was said), and an error for the learner when the reply failed.</summary>
public sealed record TutorReply(TurnRow? Turn, string? Error);

/// <summary>
/// One exchange of a live session: checks and stores the learner's turn, then has the tutor reply and stores that.
/// Transport-agnostic: the caller decides how fragments reach the learner.
/// </summary>
public sealed class TurnService(
    ISessionRepository repository, ITutorChat chat, IOptions<SessionLimitsOptions> limits, TimeProvider clock,
    ILogger<TurnService> logger)
{
    public const string ReplyFailed = "The tutor could not reply. Check the API logs and your Anthropic credentials.";

    /// <summary>
    /// Refuses the turn when the session's rules don't allow it. Otherwise stores the learner's text (with its speech
    /// scores, unless they are implausible) and returns the conversation for the tutor to answer.
    /// </summary>
    public async Task<TurnStart> StartAsync(SessionRow session, LearnerTurn turn, CancellationToken ct)
    {
        if (session.Status != SessionStatus.Active)
        {
            return Refuse(StatusCodes.Status409Conflict, "The session has ended.");
        }

        var turns = (await repository.GetTurnsAsync(session.Id, includePronunciationJson: false, ct)).ToList();
        var text = turn.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }

        if (SessionRules.CheckTurn(
                session, Scenario(session), turns, text, turn.Phrases?.Length ?? 0, clock.GetUtcNow().UtcDateTime,
                limits.Value) is { } rejection)
        {
            return new TurnStart(rejection, []);
        }

        if (text is null)
        {
            return turns.Count > 0
                ? Refuse(StatusCodes.Status400BadRequest, "Text is required once the session has started.")
                : new TurnStart(null, turns);
        }

        var phrases = AzurePronunciation.ParseTurn(turn.Phrases);
        // Implausible scores are dropped, not refused: the conversation goes on, the turn just isn't scored.
        var rejected = TurnScoreCheck.Problem(text, phrases, turn.DurationMs);
        if (rejected is not null)
        {
            logger.LogWarning("Dropped the speech scores of a turn in session {SessionId}: {Reason}", session.Id, rejected);
            Telemetry.ScoresRejected.Add(1);
            phrases = [];
        }

        turns.Add(await repository.AddTurnAsync(
            new TurnRow
            {
                SessionId = session.Id,
                Role = TurnRole.User,
                Text = text,
                DurationMs = turn.DurationMs,
                WordCount = CountWords(text),
                AccuracyScore = AzurePronunciation.WeightedMean(phrases, p => p.Accuracy),
                FluencyScore = AzurePronunciation.WeightedMean(phrases, p => p.Fluency),
                ProsodyScore = AzurePronunciation.WeightedMean(phrases, p => p.Prosody),
                PronunciationJson = rejected is null && turn.Phrases is { Length: > 0 }
                    ? JsonSerializer.Serialize(turn.Phrases)
                    : null,
                ScoresRejected = rejected,
            },
            ct));
        return new TurnStart(null, turns);
    }

    /// <summary>
    /// Streams the tutor's reply through <paramref name="onFragment"/> and stores what was said, even when the
    /// learner interrupted (cancelled <paramref name="ct"/>) or the reply failed part way.
    /// </summary>
    public async Task<TutorReply> ReplyAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, Func<string, CancellationToken, Task> onFragment,
        CancellationToken ct)
    {
        var reply = new StringBuilder();
        var usage = new ClaudeUsage();
        string? error = null;
        try
        {
            await foreach (var fragment in chat.StreamReplyAsync(session, turns, usage, ct))
            {
                reply.Append(fragment);
                await onFragment(fragment, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The learner interrupted or left; keep whatever was already said.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tutor reply failed for session {SessionId}", session.Id);
            error = ReplyFailed;
        }

        if (reply.Length == 0)
        {
            return new TutorReply(null, error);
        }

        var text = reply.ToString().Trim();
        var stored = await repository.AddTurnAsync(
            new TurnRow
            {
                SessionId = session.Id,
                Role = TurnRole.Tutor,
                Text = text,
                WordCount = CountWords(text),
                // Output stays 0 when the learner interrupted before Claude reported it.
                Model = usage.Model,
                InputTokens = (int)usage.InputTokens,
                OutputTokens = (int)usage.OutputTokens,
                CacheReadTokens = (int)usage.CacheReadTokens,
                CacheWriteTokens = (int)usage.CacheWriteTokens,
            },
            CancellationToken.None);
        return new TutorReply(stored, error);
    }

    internal static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static Scenario Scenario(SessionRow session) =>
        JsonSerializer.Deserialize<Scenario>(session.ScenarioJson, JsonSerializerOptions.Web) ?? new Scenario();

    private static TurnStart Refuse(int statusCode, string error) => new(new TurnRejection(statusCode, null, error), []);
}
