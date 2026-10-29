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

    /// <summary>
    /// Queues the session for grading unless grading is already queued or running. Returns the state afterwards.
    /// </summary>
    public async Task<string?> QueueGradingAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET GradingState = @pending, GradingAttempts = 0, GradingError = NULL,
                GradingStartedAt = NULL, GradingNextAttemptAt = NULL
            WHERE Id = @id AND (GradingState IS NULL OR GradingState NOT IN (@pending, @running));

            SELECT GradingState FROM dbo.Sessions WHERE Id = @id;
            """,
            new { id, pending = GradingStates.Pending, running = GradingStates.Running }, cancellationToken: ct));
    }

    public async Task SetGradingDoneAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Sessions SET GradingState = @done, GradingError = NULL, GradingNextAttemptAt = NULL WHERE Id = @id",
            new { id, done = GradingStates.Done }, cancellationToken: ct));
    }

    /// <summary>
    /// Claims one due grading job: a Pending row whose retry time has come, or a Running row whose worker
    /// stopped answering (claimed more than <paramref name="staleMinutes"/> ago) with attempts left.
    /// READPAST skips rows another instance is claiming, so each job goes to exactly one worker.
    /// </summary>
    public async Task<GradingClaim?> ClaimGradingAsync(int staleMinutes, int maxAttempts, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<GradingClaim>(new CommandDefinition(
            """
            UPDATE TOP (1) dbo.Sessions WITH (ROWLOCK, UPDLOCK, READPAST)
            SET GradingState = @running, GradingStartedAt = SYSUTCDATETIME(),
                GradingAttempts = GradingAttempts + 1, GradingNextAttemptAt = NULL
            OUTPUT inserted.Id AS SessionId, inserted.GradingAttempts AS Attempt
            WHERE (GradingState = @pending AND (GradingNextAttemptAt IS NULL OR GradingNextAttemptAt <= SYSUTCDATETIME()))
               OR (GradingState = @running AND GradingStartedAt < DATEADD(minute, -@staleMinutes, SYSUTCDATETIME())
                   AND GradingAttempts < @maxAttempts)
            """,
            new { pending = GradingStates.Pending, running = GradingStates.Running, staleMinutes, maxAttempts },
            cancellationToken: ct));
    }

    /// <summary>Running jobs that went stale on their last attempt are failed instead of claimed again.</summary>
    public async Task<int> FailStaleGradingAsync(int staleMinutes, int maxAttempts, string error, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET GradingState = @failed, GradingError = @error
            WHERE GradingState = @running AND GradingStartedAt < DATEADD(minute, -@staleMinutes, SYSUTCDATETIME())
              AND GradingAttempts >= @maxAttempts
            """,
            new { failed = GradingStates.Failed, running = GradingStates.Running, staleMinutes, maxAttempts, error },
            cancellationToken: ct));
    }

    /// <summary>
    /// Milliseconds until the next grading job falls due (a retry time or a stale Running row), 0 if one is due
    /// now, or null when nothing is queued. Measured on the database clock, like the claim.
    /// </summary>
    public async Task<long?> MillisecondsUntilNextGradingAsync(int staleMinutes, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT MIN(CASE WHEN due < SYSUTCDATETIME() THEN 0 ELSE DATEDIFF_BIG(millisecond, SYSUTCDATETIME(), due) END)
            FROM (
                SELECT CASE WHEN GradingState = @pending THEN ISNULL(GradingNextAttemptAt, SYSUTCDATETIME())
                            ELSE DATEADD(minute, @staleMinutes, GradingStartedAt) END AS due
                FROM dbo.Sessions
                WHERE GradingState IN (@pending, @running)
            ) jobs
            """,
            new { pending = GradingStates.Pending, running = GradingStates.Running, staleMinutes }, cancellationToken: ct));
    }

    /// <summary>Records a finished attempt, unless another worker has since claimed the job.</summary>
    public async Task FinishGradingAttemptAsync(GradingClaim claim, string state, string? error, int? retryAfterSeconds, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET GradingState = @state, GradingError = @error,
                GradingNextAttemptAt = CASE WHEN @retryAfterSeconds IS NULL THEN NULL
                                            ELSE DATEADD(second, @retryAfterSeconds, SYSUTCDATETIME()) END
            WHERE Id = @SessionId AND GradingState = @running AND GradingAttempts = @Attempt
            """,
            new { claim.SessionId, claim.Attempt, state, error, retryAfterSeconds, running = GradingStates.Running },
            cancellationToken: ct));
    }

    /// <summary>Puts a claimed job back in the queue without using up an attempt (the worker is shutting down).</summary>
    public async Task ReleaseGradingAsync(GradingClaim claim, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET GradingState = @pending, GradingAttempts = GradingAttempts - 1, GradingStartedAt = NULL
            WHERE Id = @SessionId AND GradingState = @running AND GradingAttempts = @Attempt
            """,
            new { claim.SessionId, claim.Attempt, pending = GradingStates.Pending, running = GradingStates.Running },
            cancellationToken: ct));
    }
}

public sealed record GradingClaim(Guid SessionId, int Attempt);
