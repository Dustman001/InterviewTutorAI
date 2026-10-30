using Dapper;
using Microsoft.Data.SqlClient;
using TutorAI.Api.Data;

namespace TutorAI.Api.Prep;

public sealed class PrepRepository(SqlDatabase database) : IPrepRepository
{
    /// <summary>Unique index violation.</summary>
    private const int DuplicateKey = 2601;

    private SqlConnection Open() => database.Open();

    public async Task<(PrepRunRow Run, bool Created)> CreateRunAsync(PrepRunRow run, CancellationToken ct)
    {
        if (run.IdempotencyKey is not null && await FindRunByKeyAsync(run.UserId, run.IdempotencyKey, ct) is { } existing)
        {
            return (existing, false);
        }

        try
        {
            await using var db = Open();
            var created = await db.QuerySingleAsync<PrepRunRow>(new CommandDefinition(
                """
                INSERT INTO dbo.PrepRuns (Id, UserId, IdempotencyKey, JobDescription, Role, Seniority, Status, Model, CreatedAt)
                OUTPUT inserted.*
                VALUES (@Id, @UserId, @IdempotencyKey, @JobDescription, @Role, @Seniority, @Status, @Model, @CreatedAt)
                """,
                run, cancellationToken: ct));
            return (created, true);
        }
        catch (SqlException ex) when (ex.Number == DuplicateKey && run.IdempotencyKey is not null)
        {
            // The same request raced in; the first one owns the run.
            return (await FindRunByKeyAsync(run.UserId, run.IdempotencyKey, ct)
                    ?? throw new InvalidOperationException("The run with this idempotency key disappeared."), false);
        }
    }

    public async Task<PrepRunRow?> FindRunByKeyAsync(int userId, string key, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PrepRunRow>(new CommandDefinition(
            "SELECT * FROM dbo.PrepRuns WHERE UserId = @userId AND IdempotencyKey = @key",
            new { userId, key }, cancellationToken: ct));
    }

    public async Task<PrepRunRow?> GetRunAsync(Guid id, int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PrepRunRow>(new CommandDefinition(
            "SELECT * FROM dbo.PrepRuns WHERE Id = @id AND UserId = @userId",
            new { id, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PrepRunRow>> ListRunsAsync(int userId, int limit, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<PrepRunRow>(new CommandDefinition(
            "SELECT TOP (@limit) * FROM dbo.PrepRuns WHERE UserId = @userId ORDER BY CreatedAt DESC, Id",
            new { userId, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> CountRunsSinceAsync(int userId, DateTime since, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.PrepRuns WHERE UserId = @userId AND CreatedAt >= @since",
            new { userId, since }, cancellationToken: ct));
    }

    public async Task SetQuestionsAsync(Guid runId, string questionsJson, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.PrepRuns SET QuestionsJson = @questionsJson WHERE Id = @runId",
            new { runId, questionsJson }, cancellationToken: ct));
    }

    public async Task FinishRunAsync(Guid runId, PrepRunOutcome outcome, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.PrepRuns
            SET Status = @Status, StopReason = @StopReason, Summary = @Summary, Model = @Model, Steps = @Steps,
                InputTokens = @InputTokens, OutputTokens = @OutputTokens, CompletedAt = @CompletedAt
            WHERE Id = @runId
            """,
            new
            {
                runId, outcome.Status, StopReason = Truncate(outcome.StopReason, 400), Summary = Truncate(outcome.Summary, 4000),
                outcome.Model, outcome.Steps, InputTokens = (int)Math.Min(outcome.InputTokens, int.MaxValue),
                OutputTokens = (int)Math.Min(outcome.OutputTokens, int.MaxValue), outcome.CompletedAt,
            },
            cancellationToken: ct));
    }

    public async Task<PrepActionRow?> GetActionByKeyAsync(Guid runId, string idempotencyKey, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PrepActionRow>(new CommandDefinition(
            "SELECT * FROM dbo.PrepActions WHERE RunId = @runId AND IdempotencyKey = @idempotencyKey",
            new { runId, idempotencyKey }, cancellationToken: ct));
    }

    public async Task<PrepActionRow?> GetActionAsync(long id, Guid runId, int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<PrepActionRow>(new CommandDefinition(
            """
            SELECT a.*
            FROM dbo.PrepActions a
            JOIN dbo.PrepRuns r ON r.Id = a.RunId
            WHERE a.Id = @id AND a.RunId = @runId AND r.UserId = @userId
            """,
            new { id, runId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PrepActionRow>> ListActionsAsync(Guid runId, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<PrepActionRow>(new CommandDefinition(
            "SELECT * FROM dbo.PrepActions WHERE RunId = @runId ORDER BY Id",
            new { runId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<PrepActionRow> AddActionAsync(PrepActionRow action, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        return await sql.Connection.QuerySingleAsync<PrepActionRow>(new CommandDefinition(
            """
            INSERT INTO dbo.PrepActions (RunId, IdempotencyKey, Tool, ArgsJson, Status, ResultJson, CreatedAt)
            OUTPUT inserted.*
            VALUES (@RunId, @IdempotencyKey, @Tool, @ArgsJson, @Status, @ResultJson, @CreatedAt)
            """,
            action, sql.Transaction, cancellationToken: ct));
    }

    public async Task<bool> DecideActionAsync(
        long id, PrepActionStatus status, string? resultJson, DateTime now, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        return await sql.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.PrepActions
            SET Status = @status, ResultJson = COALESCE(@resultJson, ResultJson), DecidedAt = @now
            WHERE Id = @id AND Status = @pending
            """,
            new { id, status, resultJson, now, pending = PrepActionStatus.PendingConfirmation },
            sql.Transaction, cancellationToken: ct)) == 1;
    }

    public async Task<DrillRow> AddDrillAsync(DrillRow drill, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        return await sql.Connection.QuerySingleAsync<DrillRow>(new CommandDefinition(
            """
            INSERT INTO dbo.Drills (Id, UserId, RunId, ScheduledFor, ScenarioJson, Status, CreatedAt, UpdatedAt)
            OUTPUT inserted.*
            VALUES (@Id, @UserId, @RunId, @ScheduledFor, @ScenarioJson, @Status, @CreatedAt, @UpdatedAt)
            """,
            drill, sql.Transaction, cancellationToken: ct));
    }

    public async Task<DrillRow?> GetDrillAsync(Guid id, int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<DrillRow>(new CommandDefinition(
            "SELECT * FROM dbo.Drills WHERE Id = @id AND UserId = @userId",
            new { id, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DrillRow>> ListDrillsAsync(int userId, DateTime now, int recentDays, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<DrillRow>(new CommandDefinition(
            """
            SELECT *
            FROM dbo.Drills
            WHERE UserId = @userId AND (Status = @scheduled OR UpdatedAt >= @since)
            ORDER BY CASE WHEN Status = @scheduled THEN 0 ELSE 1 END, ScheduledFor, Id
            """,
            new { userId, scheduled = DrillStatus.Scheduled, since = now.AddDays(-recentDays) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<DrillRow>> ListDrillsForRunAsync(Guid runId, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<DrillRow>(new CommandDefinition(
            "SELECT * FROM dbo.Drills WHERE RunId = @runId ORDER BY ScheduledFor, Id",
            new { runId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> UpdateDrillAsync(Guid id, int userId, DrillStatus status, Guid? sessionId, DateTime now, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Drills
            SET Status = @status, SessionId = COALESCE(@sessionId, SessionId), UpdatedAt = @now
            WHERE Id = @id AND UserId = @userId AND Status = @scheduled
            """,
            new { id, userId, status, sessionId, now, scheduled = DrillStatus.Scheduled }, cancellationToken: ct)) == 1;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}
