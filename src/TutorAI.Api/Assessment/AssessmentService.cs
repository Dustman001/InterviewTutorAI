using System.Text.Json;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

public sealed record SessionMetrics(FluencyStats Fluency, PronunciationStats Pronunciation);

public sealed record AssessmentLevels(
    string? Fluency, string? Grammar, string? Vocabulary, string? Interaction, string? Pronunciation, string? Overall);

/// <param name="Sufficient">False when the learner did not speak enough to grade; levels are then null.</param>
public sealed record AssessmentResponse(
    Guid SessionId,
    bool Sufficient,
    string? Reason,
    AssessmentLevels Levels,
    SessionMetrics Metrics,
    GradingResult? Detail,
    string? Model,
    DateTime CreatedAt);

/// <summary>The result of ending a session: the stored assessment when there is one, and the grading state.</summary>
public sealed record EndResult(AssessmentResponse? Assessment, GradingState? GradingState);

public sealed class AssessmentService(
    ISessionRepository repository, IUnitOfWorkFactory database, ICefrGrader grader, IEnumerable<IGradingFollowUp> followUps,
    GradingSignal signal, IOptions<AssessmentOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Ends the session. A stored assessment is returned as is (unless <paramref name="regrade"/>). A session too
    /// short to grade is assessed right away, since that needs no Claude call. Anything else is queued for the
    /// background worker, and the result carries the grading state instead of an assessment.
    /// </summary>
    public async Task<EndResult?> EndAsync(Guid sessionId, bool regrade, CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        await repository.EndSessionAsync(sessionId, ct);

        if (session.GradingState is { InProgress: true })
        {
            return new EndResult(null, session.GradingState);
        }

        if (!regrade && await repository.GetAssessmentAsync(sessionId, ct) is { } existing)
        {
            return new EndResult(ToResponse(existing), GradingState.Done);
        }

        var turns = await repository.GetTurnsAsync(sessionId, includePronunciationJson: true, ct);
        var metrics = ComputeMetrics(turns);
        if (Insufficient(sessionId, metrics) is { } shortRow)
        {
            var saved = await SaveAsync(shortRow, null, ct);
            await repository.SetGradingDoneAsync(sessionId, ct);
            return new EndResult(ToResponse(saved), GradingState.Done);
        }

        var state = await repository.QueueGradingAsync(sessionId, ct);
        signal.Notify();
        return new EndResult(null, state);
    }

    /// <summary>
    /// Grades the session from its stored turns and saves the assessment, together with whatever the follow-ups
    /// (<see cref="IGradingFollowUp"/>) build on it. Called by the grading worker.
    /// </summary>
    public async Task GradeAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} no longer exists.");
        var turns = await repository.GetTurnsAsync(sessionId, includePronunciationJson: true, ct);
        var metrics = ComputeMetrics(turns);

        var row = Insufficient(sessionId, metrics);
        GradedSession? graded = null;
        if (row is null)
        {
            var usage = new ClaudeUsage();
            var grading = await grader.GradeAsync(session, turns, metrics.Fluency, usage, ct);
            graded = new GradedSession(session, grading, metrics);

            row = new AssessmentRow
            {
                SessionId = sessionId,
                MetricsJson = JsonSerializer.Serialize(metrics, Json),
                Fluency = grading.Fluency.Level,
                Grammar = grading.Grammar.Level,
                Vocabulary = grading.Vocabulary.Level,
                Interaction = grading.Interaction.Level,
                Pronunciation = metrics.Pronunciation.Composite is { } composite
                    ? CefrMapper.FromPronunciationScore(composite)
                    : null,
                DetailJson = JsonSerializer.Serialize(grading, Json),
                Model = grader.Model,
                PromptVersion = grader.PromptVersion,
                InputTokens = (int)usage.InputTokens,
                OutputTokens = (int)usage.OutputTokens,
                CacheReadTokens = (int)usage.CacheReadTokens,
                CacheWriteTokens = (int)usage.CacheWriteTokens,
            };
            row.Overall = CefrMapper.Median([row.Fluency, row.Grammar, row.Vocabulary, row.Interaction, row.Pronunciation]);
        }

        await SaveAsync(row, graded, ct);
    }

    /// <summary>Saves the assessment and runs the follow-ups of a graded session in one transaction: all or nothing.</summary>
    private async Task<AssessmentRow> SaveAsync(AssessmentRow row, GradedSession? graded, CancellationToken ct)
    {
        await using var work = await database.BeginAsync(ct);
        var saved = await repository.SaveAssessmentAsync(row, work, ct);
        if (graded is not null)
        {
            foreach (var followUp in followUps)
            {
                await followUp.OnGradedAsync(graded, work, ct);
            }
        }

        await work.CommitAsync(ct);
        return saved;
    }

    /// <summary>The "too short to grade" assessment, or null when the learner spoke enough.</summary>
    private AssessmentRow? Insufficient(Guid sessionId, SessionMetrics metrics)
    {
        var minWords = options.Value.MinUserWords;
        if (metrics.Fluency.TotalWords >= minWords)
        {
            return null;
        }

        return new AssessmentRow
        {
            SessionId = sessionId,
            MetricsJson = JsonSerializer.Serialize(metrics, Json),
            DetailJson = JsonSerializer.Serialize(
                new InsufficientDetail($"You spoke {metrics.Fluency.TotalWords} words; at least {minWords} are needed for a reliable estimate."),
                Json),
        };
    }

    public async Task<AssessmentResponse?> GetAsync(Guid sessionId, CancellationToken ct) =>
        await repository.GetAssessmentAsync(sessionId, ct) is { } row ? ToResponse(row) : null;

    public static SessionMetrics ComputeMetrics(IReadOnlyList<TurnRow> turns)
    {
        var userTurns = turns
            .Where(t => t.Role == TurnRole.User)
            .Select(t => (t.WordCount, Phrases: AzurePronunciation.ParseTurn(t.PronunciationJson)))
            .ToList();

        return new SessionMetrics(
            FluencyMetrics.Compute(userTurns),
            PronunciationAggregator.Compute(userTurns.SelectMany(t => t.Phrases)));
    }

    private static AssessmentResponse ToResponse(AssessmentRow row)
    {
        var sufficient = row.Overall is not null;
        return new AssessmentResponse(
            row.SessionId,
            sufficient,
            sufficient ? null : JsonSerializer.Deserialize<InsufficientDetail>(row.DetailJson, Json)?.Reason,
            new AssessmentLevels(row.Fluency, row.Grammar, row.Vocabulary, row.Interaction, row.Pronunciation, row.Overall),
            JsonSerializer.Deserialize<SessionMetrics>(row.MetricsJson, Json)!,
            sufficient ? JsonSerializer.Deserialize<GradingResult>(row.DetailJson, Json) : null,
            row.Model,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    private sealed record InsufficientDetail(string Reason);
}
