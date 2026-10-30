namespace TutorAI.Api.Data;

/// <summary>Sessions, their turns and assessments, and the grading queue. See <see cref="SessionRepository"/>.</summary>
public interface ISessionRepository
{
    Task<bool> PingAsync(CancellationToken ct);

    /// <summary>Creates a session owned by the user; null when the user is disabled or out of sessions.</summary>
    Task<SessionRow?> CreateSessionAsync(int userId, SessionMode mode, string scenarioJson, CancellationToken ct);

    Task<SessionRow?> GetSessionAsync(Guid id, CancellationToken ct);

    /// <summary>One page of sessions, newest first; up to <paramref name="limit"/> + 1 rows (an extra row means more pages).</summary>
    /// <param name="userId">Only this user's sessions; null lists everyone's (admin).</param>
    Task<IReadOnlyList<SessionListRow>> ListSessionsAsync(int? userId, SessionCursor? before, int limit, CancellationToken ct);

    Task<IReadOnlyList<TurnRow>> GetTurnsAsync(Guid sessionId, bool includePronunciationJson, CancellationToken ct);

    /// <summary>Stores the turn as the session's next in sequence and returns it as stored.</summary>
    Task<TurnRow> AddTurnAsync(TurnRow turn, CancellationToken ct);

    Task EndSessionAsync(Guid id, CancellationToken ct);

    Task<AssessmentRow?> GetAssessmentAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Replaces the session's assessment, as part of <paramref name="work"/>.</summary>
    Task<AssessmentRow> SaveAssessmentAsync(AssessmentRow row, IUnitOfWork work, CancellationToken ct);

    /// <summary>The user's graded sessions, oldest first: the latest <paramref name="max"/>.</summary>
    Task<IReadOnlyList<ProgressRow>> GetProgressAsync(int userId, SessionMode? mode, int max, CancellationToken ct);

    /// <summary>Claude tokens per user and model since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<UserModelUsage>> GetUsageByUserAsync(DateTime since, CancellationToken ct);

    /// <summary>Queues grading unless it's already queued or running; returns the state afterwards.</summary>
    Task<GradingState?> QueueGradingAsync(Guid id, CancellationToken ct);

    Task SetGradingDoneAsync(Guid id, CancellationToken ct);

    Task<GradingClaim?> ClaimGradingAsync(int staleMinutes, int maxAttempts, CancellationToken ct);

    Task<int> FailStaleGradingAsync(int staleMinutes, int maxAttempts, string error, CancellationToken ct);

    Task<long?> MillisecondsUntilNextGradingAsync(int staleMinutes, CancellationToken ct);

    Task FinishGradingAttemptAsync(GradingClaim claim, GradingState state, string? error, int? retryAfterSeconds, CancellationToken ct);

    Task ReleaseGradingAsync(GradingClaim claim, CancellationToken ct);
}
