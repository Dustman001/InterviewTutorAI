using Dapper;
using Microsoft.Data.SqlClient;

namespace TutorAI.Api.Data;

public sealed class SessionRepository(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("TutorAI")
        ?? throw new InvalidOperationException("ConnectionStrings:TutorAI is not configured.");

    private SqlConnection Open() => new(_connectionString);

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1;
    }

    /// <summary>
    /// Creates a session owned by the user, unless that user has reached their session limit,
    /// in which case it returns null. The count and insert run as one statement so parallel
    /// requests cannot overshoot the limit.
    /// </summary>
    public async Task<SessionRow?> CreateSessionAsync(int userId, string mode, string scenarioJson, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            INSERT INTO dbo.Sessions (Id, Mode, ScenarioJson, UserId)
            OUTPUT inserted.*
            SELECT NEWID(), @mode, @scenarioJson, u.Id
            FROM dbo.Users u
            WHERE u.Id = @userId
              AND u.IsDisabled = 0
              AND (u.SessionLimit IS NULL
                   OR (SELECT COUNT(*) FROM dbo.Sessions s WITH (UPDLOCK, HOLDLOCK)
                       WHERE s.UserId = u.Id AND s.StartedAt >= ISNULL(u.SessionsResetAt, '0001-01-01')) < u.SessionLimit)
            """,
            new { userId, mode, scenarioJson }, cancellationToken: ct));
    }

    public async Task<SessionRow?> GetSessionAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            "SELECT * FROM dbo.Sessions WHERE Id = @id", new { id }, cancellationToken: ct));
    }

    /// <param name="userId">Only this user's sessions; null lists everyone's (admin).</param>
    public async Task<IReadOnlyList<SessionListRow>> ListSessionsAsync(int? userId, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<SessionListRow>(new CommandDefinition(
            """
            SELECT TOP (200) s.Id, s.Mode, s.ScenarioJson, s.StartedAt, s.EndedAt, s.Status,
                   a.Fluency, a.Grammar, a.Vocabulary, a.Interaction, a.Pronunciation, a.Overall
            FROM dbo.Sessions s
            LEFT JOIN dbo.Assessments a ON a.SessionId = s.Id
            WHERE @userId IS NULL OR s.UserId = @userId
            ORDER BY s.StartedAt DESC
            """,
            new { userId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TurnRow>> GetTurnsAsync(Guid sessionId, bool includePronunciationJson, CancellationToken ct)
    {
        await using var db = Open();
        var json = includePronunciationJson ? "PronunciationJson" : "CAST(NULL AS NVARCHAR(MAX)) AS PronunciationJson";
        var rows = await db.QueryAsync<TurnRow>(new CommandDefinition(
            $"""
            SELECT Id, SessionId, Seq, Role, Text, StartedAt, DurationMs, WordCount,
                   AccuracyScore, FluencyScore, ProsodyScore, {json}
            FROM dbo.Turns
            WHERE SessionId = @sessionId
            ORDER BY Seq
            """,
            new { sessionId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<TurnRow> AddTurnAsync(TurnRow turn, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleAsync<TurnRow>(new CommandDefinition(
            """
            INSERT INTO dbo.Turns (SessionId, Seq, Role, Text, DurationMs, WordCount,
                                   AccuracyScore, FluencyScore, ProsodyScore, PronunciationJson)
            OUTPUT inserted.*
            SELECT @SessionId,
                   ISNULL((SELECT MAX(Seq) FROM dbo.Turns WITH (UPDLOCK, HOLDLOCK) WHERE SessionId = @SessionId), 0) + 1,
                   @Role, @Text, @DurationMs, @WordCount,
                   @AccuracyScore, @FluencyScore, @ProsodyScore, @PronunciationJson
            """,
            turn, cancellationToken: ct));
    }

    public async Task EndSessionAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET Status = @status, EndedAt = ISNULL(EndedAt, SYSUTCDATETIME())
            WHERE Id = @id
            """,
            new { id, status = SessionStatus.Ended }, cancellationToken: ct));
    }

    public async Task<AssessmentRow?> GetAssessmentAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<AssessmentRow>(new CommandDefinition(
            "SELECT * FROM dbo.Assessments WHERE SessionId = @sessionId", new { sessionId }, cancellationToken: ct));
    }

    public async Task<AssessmentRow> SaveAssessmentAsync(AssessmentRow row, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleAsync<AssessmentRow>(new CommandDefinition(
            """
            DELETE FROM dbo.Assessments WHERE SessionId = @SessionId;

            INSERT INTO dbo.Assessments (SessionId, Fluency, Grammar, Vocabulary, Interaction, Pronunciation,
                                         Overall, MetricsJson, DetailJson, Model)
            OUTPUT inserted.*
            VALUES (@SessionId, @Fluency, @Grammar, @Vocabulary, @Interaction, @Pronunciation,
                    @Overall, @MetricsJson, @DetailJson, @Model);
            """,
            row, cancellationToken: ct));
    }
}
