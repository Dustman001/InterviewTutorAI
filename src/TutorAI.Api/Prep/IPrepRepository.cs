using TutorAI.Api.Data;

namespace TutorAI.Api.Prep;

/// <summary>Prep runs, the actions their tools took, and scheduled drills. See <see cref="PrepRepository"/>.</summary>
public interface IPrepRepository
{
    /// <summary>
    /// Creates the run, or returns the user's existing run with the same idempotency key (Created false).
    /// </summary>
    Task<(PrepRunRow Run, bool Created)> CreateRunAsync(PrepRunRow run, CancellationToken ct);

    Task<PrepRunRow?> FindRunByKeyAsync(int userId, string idempotencyKey, CancellationToken ct);

    /// <summary>The run, when it belongs to the user.</summary>
    Task<PrepRunRow?> GetRunAsync(Guid id, int userId, CancellationToken ct);

    /// <summary>The user's runs, newest first.</summary>
    Task<IReadOnlyList<PrepRunRow>> ListRunsAsync(int userId, int limit, CancellationToken ct);

    Task<int> CountRunsSinceAsync(int userId, DateTime since, CancellationToken ct);

    Task SetQuestionsAsync(Guid runId, string questionsJson, CancellationToken ct);

    Task FinishRunAsync(Guid runId, PrepRunOutcome outcome, CancellationToken ct);

    Task<PrepActionRow?> GetActionByKeyAsync(Guid runId, string idempotencyKey, CancellationToken ct);

    /// <summary>The action, when its run belongs to the user.</summary>
    Task<PrepActionRow?> GetActionAsync(long id, Guid runId, int userId, CancellationToken ct);

    Task<IReadOnlyList<PrepActionRow>> ListActionsAsync(Guid runId, CancellationToken ct);

    /// <summary>Records the action as part of <paramref name="work"/>; its (run, key) pair is unique.</summary>
    Task<PrepActionRow> AddActionAsync(PrepActionRow action, IUnitOfWork work, CancellationToken ct);

    /// <summary>Moves a pending action to <paramref name="status"/>, as part of <paramref name="work"/>.</summary>
    /// <returns>False when the action was no longer pending (decided already).</returns>
    Task<bool> DecideActionAsync(long id, PrepActionStatus status, string? resultJson, DateTime now, IUnitOfWork work, CancellationToken ct);

    Task<DrillRow> AddDrillAsync(DrillRow drill, IUnitOfWork work, CancellationToken ct);

    /// <summary>The drill, when it belongs to the user.</summary>
    Task<DrillRow?> GetDrillAsync(Guid id, int userId, CancellationToken ct);

    /// <summary>The user's drills still scheduled, soonest first, then those started or cancelled in the last <paramref name="recentDays"/> days.</summary>
    Task<IReadOnlyList<DrillRow>> ListDrillsAsync(int userId, DateTime now, int recentDays, CancellationToken ct);

    Task<IReadOnlyList<DrillRow>> ListDrillsForRunAsync(Guid runId, CancellationToken ct);

    /// <summary>Moves a scheduled drill to Started or Cancelled.</summary>
    /// <returns>False when the drill was no longer scheduled.</returns>
    Task<bool> UpdateDrillAsync(Guid id, int userId, DrillStatus status, Guid? sessionId, DateTime now, CancellationToken ct);
}
