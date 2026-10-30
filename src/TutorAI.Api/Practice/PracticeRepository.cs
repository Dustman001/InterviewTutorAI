using Dapper;
using Microsoft.Data.SqlClient;
using TutorAI.Api.Data;

namespace TutorAI.Api.Practice;

public sealed class PracticeRepository(SqlDatabase database) : IPracticeRepository
{
    private SqlConnection Open() => database.Open();

    /// <summary>
    /// Adds the batch's items, or merges each into the user's existing item with the same target, which is then
    /// due again from box 0. Grading the same session again leaves its own items' progress alone.
    /// </summary>
    public async Task UpsertAsync(PracticeBatch batch, IUnitOfWork work, CancellationToken ct)
    {
        if (batch.Items.Count == 0)
        {
            return;
        }

        var sql = work.Sql();
        await sql.Connection.ExecuteAsync(new CommandDefinition(
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
            sql.Transaction,
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

    public async Task<IReadOnlyList<PracticeItemRow>> ListAsync(int userId, bool includeMastered, int limit, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<PracticeItemRow>(new CommandDefinition(
            """
            SELECT TOP (@limit) *
            FROM dbo.PracticeItems
            WHERE UserId = @userId AND (@includeMastered = 1 OR DueAt IS NOT NULL)
            ORDER BY CASE WHEN DueAt IS NULL THEN 1 ELSE 0 END, Box, ISNULL(LastScore, -1), Id
            """,
            new { userId, includeMastered, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PracticeItemRow>> FindByTargetKeysAsync(int userId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        await using var db = Open();
        var rows = await db.QueryAsync<PracticeItemRow>(new CommandDefinition(
            "SELECT * FROM dbo.PracticeItems WHERE UserId = @userId AND TargetKey IN @keys",
            new { userId, keys }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PracticeItemRow>> InsertNewAsync(PracticeBatch batch, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        var added = new List<PracticeItemRow>();
        foreach (var item in batch.Items)
        {
            added.AddRange(await sql.Connection.QueryAsync<PracticeItemRow>(new CommandDefinition(
                """
                INSERT INTO dbo.PracticeItems (UserId, SessionId, Kind, Target, TargetKey, Original, Explanation, Locale, Box, DueAt, CreatedAt, UpdatedAt)
                OUTPUT inserted.*
                SELECT @UserId, @SessionId, @Kind, @Target, @TargetKey, @Original, @Explanation, @Locale, 0, @Now, @Now, @Now
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.PracticeItems WITH (UPDLOCK, HOLDLOCK)
                    WHERE UserId = @UserId AND TargetKey = @TargetKey)
                """,
                new
                {
                    batch.UserId, batch.SessionId, batch.Locale, batch.Now,
                    item.Kind, item.Target, item.TargetKey, item.Original, item.Explanation,
                },
                sql.Transaction,
                cancellationToken: ct)));
        }

        return added;
    }

    public async Task<PracticeItemRow?> RestartAsync(long id, int userId, DateTime now, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        return await sql.Connection.QuerySingleOrDefaultAsync<PracticeItemRow>(new CommandDefinition(
            """
            UPDATE dbo.PracticeItems
            SET Box = 0, DueAt = @now, UpdatedAt = @now
            OUTPUT inserted.*
            WHERE Id = @id AND UserId = @userId
            """,
            new { id, userId, now }, sql.Transaction, cancellationToken: ct));
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
