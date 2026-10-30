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
}
