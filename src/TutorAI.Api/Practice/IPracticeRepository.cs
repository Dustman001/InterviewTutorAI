using TutorAI.Api.Data;

namespace TutorAI.Api.Practice;

/// <summary>Each user's practice items and their review schedule. See <see cref="PracticeRepository"/>.</summary>
public interface IPracticeRepository
{
    /// <summary>Adds the batch's items or merges them into the user's existing ones, as part of <paramref name="work"/>.</summary>
    Task UpsertAsync(PracticeBatch batch, IUnitOfWork work, CancellationToken ct);

    /// <summary>The user's due items, longest overdue first.</summary>
    Task<IReadOnlyList<PracticeItemRow>> ListDueAsync(int userId, DateTime now, int limit, CancellationToken ct);

    Task<PracticeCounts> CountAsync(int userId, DateTime now, CancellationToken ct);

    /// <summary>The item, when it belongs to the user.</summary>
    Task<PracticeItemRow?> GetAsync(long id, int userId, CancellationToken ct);

    Task<PracticeItemRow?> RecordAttemptAsync(long id, int userId, double score, byte box, DateTime? dueAt, DateTime now, CancellationToken ct);

    /// <returns>False when the user has no such item.</returns>
    Task<bool> DeleteAsync(long id, int userId, CancellationToken ct);

    /// <summary>The user's items, mastered ones only when asked: the weakest first (lowest box, then lowest last score).</summary>
    Task<IReadOnlyList<PracticeItemRow>> ListAsync(int userId, bool includeMastered, int limit, CancellationToken ct);

    /// <summary>The user's items whose TargetKey is one of <paramref name="keys"/>, of any kind.</summary>
    Task<IReadOnlyList<PracticeItemRow>> FindByTargetKeysAsync(int userId, IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>Adds the batch's items that the user doesn't have yet, as part of <paramref name="work"/>; never merges.</summary>
    /// <returns>The items added.</returns>
    Task<IReadOnlyList<PracticeItemRow>> InsertNewAsync(PracticeBatch batch, IUnitOfWork work, CancellationToken ct);

    /// <summary>Sends the item back to box 0, due now, as part of <paramref name="work"/>. Its attempts and best score are kept.</summary>
    Task<PracticeItemRow?> RestartAsync(long id, int userId, DateTime now, IUnitOfWork work, CancellationToken ct);
}
