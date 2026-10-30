using System.Runtime.CompilerServices;
using TutorAI.Api;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Practice;

namespace TutorAI.Tests.Services;

/// <summary>A unit of work whose writes take effect only on commit, like the SQL one.</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    private readonly List<Action> _pending = [];

    public bool Committed { get; private set; }

    public void OnCommit(Action write) => _pending.Add(write);

    public Task CommitAsync(CancellationToken ct)
    {
        _pending.ForEach(write => write());
        Committed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeUnitOfWorkFactory : IUnitOfWorkFactory
{
    public List<FakeUnitOfWork> Started { get; } = [];

    public Task<IUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var work = new FakeUnitOfWork();
        Started.Add(work);
        return Task.FromResult<IUnitOfWork>(work);
    }
}

/// <summary>Sessions, turns and assessments in memory. Only what the services use is implemented.</summary>
internal sealed class InMemorySessionRepository : ISessionRepository
{
    public Dictionary<Guid, SessionRow> Sessions { get; } = [];
    public List<TurnRow> Turns { get; } = [];
    public Dictionary<Guid, AssessmentRow> Assessments { get; } = [];

    public SessionRow Add(SessionRow session)
    {
        Sessions[session.Id] = session;
        return session;
    }

    public Task<SessionRow?> GetSessionAsync(Guid id, CancellationToken ct) => Task.FromResult(Sessions.GetValueOrDefault(id));

    public Task<IReadOnlyList<TurnRow>> GetTurnsAsync(Guid sessionId, bool includePronunciationJson, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TurnRow>>([.. Turns.Where(t => t.SessionId == sessionId).OrderBy(t => t.Seq)]);

    public Task<TurnRow> AddTurnAsync(TurnRow turn, CancellationToken ct)
    {
        turn.Id = Turns.Count + 1;
        turn.Seq = Turns.Count(t => t.SessionId == turn.SessionId) + 1;
        Turns.Add(turn);
        return Task.FromResult(turn);
    }

    public Task EndSessionAsync(Guid id, CancellationToken ct)
    {
        Sessions[id].Status = SessionStatus.Ended;
        return Task.CompletedTask;
    }

    public Task<AssessmentRow?> GetAssessmentAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(Assessments.GetValueOrDefault(sessionId));

    public Task<AssessmentRow> SaveAssessmentAsync(AssessmentRow row, IUnitOfWork work, CancellationToken ct)
    {
        ((FakeUnitOfWork)work).OnCommit(() => Assessments[row.SessionId] = row);
        return Task.FromResult(row);
    }

    public Task<GradingState?> QueueGradingAsync(Guid id, CancellationToken ct)
    {
        var session = Sessions[id];
        if (session.GradingState is not { InProgress: true })
        {
            session.GradingState = GradingState.Pending;
        }

        return Task.FromResult(session.GradingState);
    }

    public Task SetGradingDoneAsync(Guid id, CancellationToken ct)
    {
        Sessions[id].GradingState = GradingState.Done;
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<SessionRow?> CreateSessionAsync(int userId, SessionMode mode, string scenarioJson, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionListRow>> ListSessionsAsync(int? userId, SessionCursor? before, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProgressRow>> GetProgressAsync(int userId, SessionMode? mode, int max, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<UserModelUsage>> GetUsageByUserAsync(DateTime since, CancellationToken ct) => throw new NotSupportedException();
    public Task<GradingClaim?> ClaimGradingAsync(int staleMinutes, int maxAttempts, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> FailStaleGradingAsync(int staleMinutes, int maxAttempts, string error, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> MillisecondsUntilNextGradingAsync(int staleMinutes, CancellationToken ct) => throw new NotSupportedException();
    public Task FinishGradingAttemptAsync(GradingClaim claim, GradingState state, string? error, int? retryAfterSeconds, CancellationToken ct) => throw new NotSupportedException();
    public Task ReleaseGradingAsync(GradingClaim claim, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class InMemoryPracticeRepository : IPracticeRepository
{
    public List<PracticeBatch> Saved { get; } = [];

    public Task UpsertAsync(PracticeBatch batch, IUnitOfWork work, CancellationToken ct)
    {
        ((FakeUnitOfWork)work).OnCommit(() => Saved.Add(batch));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PracticeItemRow>> ListDueAsync(int userId, DateTime now, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<PracticeCounts> CountAsync(int userId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
    public Task<PracticeItemRow?> GetAsync(long id, int userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<PracticeItemRow?> RecordAttemptAsync(long id, int userId, double score, byte box, DateTime? dueAt, DateTime now, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(long id, int userId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Replies with the given fragments, then optionally fails as a broken Claude stream would.</summary>
internal sealed class ScriptedTutorChat(params string[] fragments) : ITutorChat
{
    public Exception? FailAfter { get; init; }

    /// <summary>Runs after each fragment is yielded, e.g. to simulate the learner interrupting.</summary>
    public Action<int>? AfterFragment { get; init; }

    public IReadOnlyList<TurnRow>? LastTurns { get; private set; }

    public async IAsyncEnumerable<string> StreamReplyAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, ClaudeUsage? usage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastTurns = [.. turns];
        if (usage is not null)
        {
            usage.Model = "fake-tutor";
            usage.InputTokens = 500;
            usage.OutputTokens = 40;
        }

        for (var i = 0; i < fragments.Length; i++)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return fragments[i];
            AfterFragment?.Invoke(i);
        }

        ct.ThrowIfCancellationRequested();
        if (FailAfter is not null)
        {
            throw FailAfter;
        }
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
