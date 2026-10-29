using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TutorAI.Api.Practice;

public sealed class PracticeRepository(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("TutorAI")
        ?? throw new InvalidOperationException("ConnectionStrings:TutorAI is not configured.");

    private SqlConnection Open() => new(_connectionString);

    /// <summary>
    /// Adds the batch's items, or merges each into the user's existing item with the same target, which is then
    /// due again from box 0. Grading the same session again leaves its own items' progress alone.
    /// Runs on the caller's connection and transaction (the assessment's save).
    /// </summary>
    public static async Task UpsertAsync(DbConnection db, DbTransaction transaction, PracticeBatch batch, CancellationToken ct)
    {
        if (batch.Items.Count == 0)
        {
            return;
        }

        await db.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.PracticeItems WITH (HOLDLOCK) AS t
            USING (SELECT @UserId AS UserId, @Kind AS Kind, @TargetKey AS TargetKey) AS s
               ON t.UserId = s.UserId AND t.Kind = s.Kind AND t.TargetKey = s.TargetKey
            WHEN MATCHED AND (t.SessionId IS NULL OR t.SessionId <> @SessionId) THEN
                UPDATE SET Target = @Target, Original = @Original, Explanation = @Explanation, SessionId = @SessionId,
                           Locale = @Locale, Box = 0, DueAt = @Now, UpdatedAt = @Now
            WHEN NOT MATCHED THEN
                INSERT (UserId, SessionId, Kind, Target, TargetKey, Original, Explanation, Locale, Box, DueAt, CreatedAt, UpdatedAt)
                VALUES (@UserId, @SessionId, @Kind, @Target, @TargetKey, @Original, @Explanation, @Locale, 0, @Now, @Now, @Now);
            """,
            batch.Items.Select(i => new
            {
                batch.UserId, batch.SessionId, batch.Locale, batch.Now,
                i.Kind, i.Target, i.TargetKey, i.Original, i.Explanation,
            }),
            transaction,
            cancellationToken: ct));
    }

    /// <summary>The user's due items, longest overdue first.</summary>
    public async Task<IReadOnlyList<PracticeItemRow>> ListDueAsync(int userId, DateTime now, int limit, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<PracticeItemRow>(new CommandDefinition(
            """
            SELECT TOP (@limit) *
            FROM dbo.PracticeItems
            WHERE UserId = @userId AND DueAt <= @now
            ORDER BY DueAt, Id
            """,
            new { userId, now, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<PracticeCounts> CountAsync(int userId, DateTime now, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleAsync<PracticeCounts>(new CommandDefinition(
            """
            SELECT ISNULL(SUM(CASE WHEN DueAt <= @now THEN 1 ELSE 0 END), 0) AS Due,
                   ISNULL(SUM(CASE WHEN DueAt IS NOT NULL THEN 1 ELSE 0 END), 0) AS Active,
                   ISNULL(SUM(CASE WHEN DueAt IS NULL THEN 1 ELSE 0 END), 0) AS Mastered,
                   MIN(CASE WHEN DueAt > @now THEN DueAt END) AS NextDueAt
            FROM dbo.PracticeItems
            WHERE UserId = @userId
            """,
            new { userId, now }, cancellationToken: ct));
    }

    /// <summary>The item, when it belongs to the user.</summary>
    public async Task<PracticeItemRow?> GetAsync(long id, int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PracticeItemRow>(new CommandDefinition(
            "SELECT * FROM dbo.PracticeItems WHERE Id = @id AND UserId = @userId",
            new { id, userId }, cancellationToken: ct));
    }

    public async Task<PracticeItemRow?> RecordAttemptAsync(
        long id, int userId, double score, byte box, DateTime? dueAt, DateTime now, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PracticeItemRow>(new CommandDefinition(
            """
            UPDATE dbo.PracticeItems
            SET Box = @box, DueAt = @dueAt, Attempts = Attempts + 1, LastScore = @score,
                BestScore = CASE WHEN BestScore IS NULL OR @score > BestScore THEN @score ELSE BestScore END,
                UpdatedAt = @now
            OUTPUT inserted.*
            WHERE Id = @id AND UserId = @userId
            """,
            new { id, userId, score, box, dueAt, now }, cancellationToken: ct));
    }

    /// <returns>False when the user has no such item.</returns>
    public async Task<bool> DeleteAsync(long id, int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.PracticeItems WHERE Id = @id AND UserId = @userId",
            new { id, userId }, cancellationToken: ct)) > 0;
    }
}
