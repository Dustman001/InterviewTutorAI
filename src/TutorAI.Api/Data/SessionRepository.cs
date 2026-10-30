using Dapper;
using Microsoft.Data.SqlClient;

namespace TutorAI.Api.Data;

public sealed class SessionRepository(SqlDatabase database) : ISessionRepository
{
    private SqlConnection Open() => database.Open();

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
    public async Task<SessionRow?> CreateSessionAsync(int userId, SessionMode mode, string scenarioJson, CancellationToken ct)
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

    /// <summary>
    /// One page of sessions, newest first (keyset pagination on StartedAt, then Id). Returns up to
    /// <paramref name="limit"/> + 1 rows: an extra row means there is another page.
    /// </summary>
    /// <param name="userId">Only this user's sessions; null lists everyone's (admin).</param>
    /// <param name="before">The last row of the previous page, or null for the first page.</param>
    public async Task<IReadOnlyList<SessionListRow>> ListSessionsAsync(
        int? userId, SessionCursor? before, int limit, CancellationToken ct)
    {
        // Separate statements per case, so each can seek on its index instead of one plan with OR branches.
        var owner = userId is null ? "" : "s.UserId = @userId AND";
        var after = before is null ? "1 = 1" : "s.StartedAt <= @startedAt AND (s.StartedAt < @startedAt OR s.Id < @id)";
        var parameters = new DynamicParameters(new { userId, id = before?.Id, take = limit + 1 });
        // datetime2, not Dapper's default datetime: datetime rounds to ~3 ms and would skip or repeat rows at a page edge.
        parameters.Add("startedAt", before?.StartedAt, System.Data.DbType.DateTime2);
        await using var db = Open();
        var rows = await db.QueryAsync<SessionListRow>(new CommandDefinition(
            $"""
            SELECT TOP (@take) s.Id, s.Mode, s.ScenarioJson, s.StartedAt, s.EndedAt, s.Status, s.GradingState,
                   a.Fluency, a.Grammar, a.Vocabulary, a.Interaction, a.Pronunciation, a.Overall
            FROM dbo.Sessions s
            LEFT JOIN dbo.Assessments a ON a.SessionId = s.Id
            WHERE {owner} {after}
            ORDER BY s.StartedAt DESC, s.Id DESC
            """,
            parameters, cancellationToken: ct));
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
                                   AccuracyScore, FluencyScore, ProsodyScore, PronunciationJson, ScoresRejected,
                                   Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens)
            OUTPUT inserted.*
            SELECT @SessionId,
                   ISNULL((SELECT MAX(Seq) FROM dbo.Turns WITH (UPDLOCK, HOLDLOCK) WHERE SessionId = @SessionId), 0) + 1,
                   @Role, @Text, @DurationMs, @WordCount,
                   @AccuracyScore, @FluencyScore, @ProsodyScore, @PronunciationJson, @ScoresRejected,
                   @Model, @InputTokens, @OutputTokens, @CacheReadTokens, @CacheWriteTokens
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

    public async Task<AssessmentRow> SaveAssessmentAsync(AssessmentRow row, IUnitOfWork work, CancellationToken ct)
    {
        var sql = work.Sql();
        return await sql.Connection.QuerySingleAsync<AssessmentRow>(new CommandDefinition(
            """
            DELETE FROM dbo.Assessments WHERE SessionId = @SessionId;

            INSERT INTO dbo.Assessments (SessionId, Fluency, Grammar, Vocabulary, Interaction, Pronunciation,
                                         Overall, MetricsJson, DetailJson, Model, PromptVersion,
                                         InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens)
            OUTPUT inserted.*
            VALUES (@SessionId, @Fluency, @Grammar, @Vocabulary, @Interaction, @Pronunciation,
                    @Overall, @MetricsJson, @DetailJson, @Model, @PromptVersion,
                    @InputTokens, @OutputTokens, @CacheReadTokens, @CacheWriteTokens);
            """,
            row, sql.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// The user's graded sessions (an overall level exists), oldest first: the latest <paramref name="max"/>.
    /// The measurements come from the stored metrics JSON, so no extra columns are needed.
    /// </summary>
    public async Task<IReadOnlyList<ProgressRow>> GetProgressAsync(int userId, SessionMode? mode, int max, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<ProgressRow>(new CommandDefinition(
            """
            SELECT SessionId, StartedAt, Mode, Fluency, Grammar, Vocabulary, Interaction, Pronunciation, Overall,
                   WordsPerMinute, PauseRatio, PronunciationScore
            FROM (
                SELECT TOP (@max) s.Id AS SessionId, s.StartedAt, s.Mode,
                       a.Fluency, a.Grammar, a.Vocabulary, a.Interaction, a.Pronunciation, a.Overall,
                       TRY_CAST(JSON_VALUE(a.MetricsJson, '$.fluency.wordsPerMinute') AS FLOAT) AS WordsPerMinute,
                       TRY_CAST(JSON_VALUE(a.MetricsJson, '$.fluency.pauseRatio') AS FLOAT) AS PauseRatio,
                       TRY_CAST(JSON_VALUE(a.MetricsJson, '$.pronunciation.composite') AS FLOAT) AS PronunciationScore
                FROM dbo.Sessions s
                JOIN dbo.Assessments a ON a.SessionId = s.Id
                WHERE s.UserId = @userId AND a.Overall IS NOT NULL AND (@mode IS NULL OR s.Mode = @mode)
                ORDER BY s.StartedAt DESC, s.Id DESC
            ) latest
            ORDER BY StartedAt, SessionId
            """,
            new { userId, mode, max }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Claude tokens per user and model since <paramref name="since"/>: tutor turns plus assessments.</summary>
    public async Task<IReadOnlyList<UserModelUsage>> GetUsageByUserAsync(DateTime since, CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<UserModelUsage>(new CommandDefinition(
            """
            SELECT s.UserId, x.Model,
                   SUM(CAST(ISNULL(x.InputTokens, 0) AS BIGINT)) AS Input,
                   SUM(CAST(ISNULL(x.OutputTokens, 0) AS BIGINT)) AS Output,
                   SUM(CAST(ISNULL(x.CacheReadTokens, 0) AS BIGINT)) AS CacheRead,
                   SUM(CAST(ISNULL(x.CacheWriteTokens, 0) AS BIGINT)) AS CacheWrite
            FROM (
                SELECT SessionId, Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens
                FROM dbo.Turns WHERE Model IS NOT NULL AND StartedAt >= @since
                UNION ALL
                SELECT SessionId, Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens
                FROM dbo.Assessments WHERE Model IS NOT NULL AND InputTokens IS NOT NULL AND CreatedAt >= @since
            ) x
            JOIN dbo.Sessions s ON s.Id = x.SessionId
            WHERE s.UserId IS NOT NULL
            GROUP BY s.UserId, x.Model
            """,
            new { since }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Queues the session for grading unless grading is already queued or running. Returns the state afterwards.
    /// </summary>
    public async Task<GradingState?> QueueGradingAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<GradingState?>(new CommandDefinition(
            """
            UPDATE dbo.Sessions
            SET GradingState = @pending, GradingAttempts = 0, GradingError = NULL,
                GradingStartedAt = NULL, GradingNextAttemptAt = NULL
            WHERE Id = @id AND (GradingState IS NULL OR GradingState NOT IN (@pending, @running));

            SELECT GradingState FROM dbo.Sessions WHERE Id = @id;
            """,
            new { id, pending = GradingState.Pending, running = GradingState.Running }, cancellationToken: ct));
    }

    public async Task SetGradingDoneAsync(Guid id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Sessions SET GradingState = @done, GradingError = NULL, GradingNextAttemptAt = NULL WHERE Id = @id",
            new { id, done = GradingState.Done }, cancellationToken: ct));
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
            new { pending = GradingState.Pending, running = GradingState.Running, staleMinutes, maxAttempts },
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
            new { failed = GradingState.Failed, running = GradingState.Running, staleMinutes, maxAttempts, error },
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
            new { pending = GradingState.Pending, running = GradingState.Running, staleMinutes }, cancellationToken: ct));
    }

    /// <summary>Records a finished attempt, unless another worker has since claimed the job.</summary>
    public async Task FinishGradingAttemptAsync(GradingClaim claim, GradingState state, string? error, int? retryAfterSeconds, CancellationToken ct)
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
            new { claim.SessionId, claim.Attempt, state, error, retryAfterSeconds, running = GradingState.Running },
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
            new { claim.SessionId, claim.Attempt, pending = GradingState.Pending, running = GradingState.Running },
            cancellationToken: ct));
    }
}

public sealed record GradingClaim(Guid SessionId, int Attempt);

/// <summary>Where a session list page ends. Opaque to clients: base64 of the row's StartedAt ticks and Id.</summary>
public sealed record SessionCursor(DateTime StartedAt, Guid Id)
{
    public string Encode() =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{StartedAt.Ticks}:{Id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static SessionCursor? TryDecode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return null;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks
                   && Guid.TryParseExact(parts[1], "N", out var id)
                ? new SessionCursor(new DateTime(ticks, DateTimeKind.Unspecified), id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

// A class, not a positional record: Dapper's constructor mapping doesn't apply type handlers (SessionMode).
public sealed class ProgressRow
{
    public Guid SessionId { get; set; }
    public DateTime StartedAt { get; set; }
    public SessionMode Mode { get; set; }
    public string? Fluency { get; set; }
    public string? Grammar { get; set; }
    public string? Vocabulary { get; set; }
    public string? Interaction { get; set; }
    public string? Pronunciation { get; set; }
    public string Overall { get; set; } = "";
    public double? WordsPerMinute { get; set; }
    public double? PauseRatio { get; set; }
    public double? PronunciationScore { get; set; }
}

public sealed record UserModelUsage(int UserId, string Model, long Input, long Output, long CacheRead, long CacheWrite);
