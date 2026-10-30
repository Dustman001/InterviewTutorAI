using Dapper;
using Microsoft.Data.SqlClient;
using TutorAI.Api.Data;

namespace TutorAI.Api.Auth;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Guest = "Guest";

    public static bool IsValid(string? role) => role is Admin or Guest;
}

public sealed class UserRow
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>NULL means unlimited.</summary>
    public int? SessionLimit { get; set; }
    public bool IsDisabled { get; set; }
    public int TokenVersion { get; set; }
    public DateTime? SessionsResetAt { get; set; }
    public string? ConfigPasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public int FailedLoginCount { get; set; }
    /// <summary>UTC. Sign-in is refused until then.</summary>
    public DateTime? LockedUntil { get; set; }
}

public sealed class UserListRow
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public int? SessionLimit { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public int SessionsUsed { get; set; }
}

public sealed class UserRepository(SqlDatabase database) : IUserRepository
{
    private const string Columns =
        "Id, Username, PasswordHash, Role, SessionLimit, IsDisabled, TokenVersion, SessionsResetAt, ConfigPasswordHash, CreatedAt, " +
        "FailedLoginCount, LockedUntil";

    /// <summary>Sessions that count towards the limit: those started since the last reset.</summary>
    internal const string CountedSessionsFilter = "s.UserId = u.Id AND s.StartedAt >= ISNULL(u.SessionsResetAt, '0001-01-01')";

    private SqlConnection Open() => database.Open();

    public async Task<UserRow?> GetByUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE Username = @username", new { username }, cancellationToken: ct));
    }

    public async Task<UserRow?> GetByIdAsync(int id, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE Id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> CountSessionsAsync(int userId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.Users u JOIN dbo.Sessions s ON {CountedSessionsFilter} WHERE u.Id = @userId",
            new { userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<UserListRow>> ListAsync(CancellationToken ct)
    {
        await using var db = Open();
        var rows = await db.QueryAsync<UserListRow>(new CommandDefinition(
            $"""
            SELECT u.Id, u.Username, u.Role, u.SessionLimit, u.IsDisabled, u.CreatedAt,
                   (SELECT COUNT(*) FROM dbo.Sessions s WHERE {CountedSessionsFilter}) AS SessionsUsed
            FROM dbo.Users u
            ORDER BY u.Username
            """,
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Returns the new id, or null when the username is taken.</summary>
    public async Task<int?> InsertAsync(UserRow user, CancellationToken ct)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            """
            INSERT INTO dbo.Users (Username, PasswordHash, Role, SessionLimit, IsDisabled, ConfigPasswordHash)
            OUTPUT inserted.Id
            SELECT @Username, @PasswordHash, @Role, @SessionLimit, @IsDisabled, @ConfigPasswordHash
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Username = @Username)
            """,
            user, cancellationToken: ct));
    }

    public async Task SetPasswordAsync(int id, string passwordHash, string? configPasswordHash, bool signOut, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Users
            SET PasswordHash = @passwordHash,
                ConfigPasswordHash = ISNULL(@configPasswordHash, ConfigPasswordHash),
                TokenVersion = TokenVersion + CASE WHEN @signOut = 1 THEN 1 ELSE 0 END,
                FailedLoginCount = 0,
                LockedUntil = NULL
            WHERE Id = @id
            """,
            new { id, passwordHash, configPasswordHash, signOut }, cancellationToken: ct));
    }

    /// <summary>
    /// Counts a failed sign-in. From the <paramref name="threshold"/>th failure in a row the account is locked:
    /// <paramref name="baseSeconds"/> at first, doubling with each further failure, at most <paramref name="maxSeconds"/>.
    /// Returns the lock's end, or null when the account is not locked. One statement, so parallel attempts all count.
    /// </summary>
    public async Task<DateTime?> RecordFailedLoginAsync(
        int id, DateTime utcNow, int threshold, int baseSeconds, int maxSeconds, CancellationToken ct)
    {
        await using var db = Open();
        // Right-hand side column references read the values from before this update.
        return await db.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            """
            UPDATE dbo.Users
            SET FailedLoginCount = FailedLoginCount + 1,
                LockedUntil = CASE
                    WHEN FailedLoginCount + 1 < @threshold THEN LockedUntil
                    WHEN FailedLoginCount + 1 - @threshold >= 20 THEN DATEADD(second, @maxSeconds, @utcNow)
                    WHEN @baseSeconds * POWER(CAST(2 AS BIGINT), FailedLoginCount + 1 - @threshold) >= @maxSeconds
                        THEN DATEADD(second, @maxSeconds, @utcNow)
                    ELSE DATEADD(second, @baseSeconds * POWER(2, FailedLoginCount + 1 - @threshold), @utcNow)
                END
            OUTPUT inserted.LockedUntil
            WHERE Id = @id
            """,
            new { id, utcNow, threshold, baseSeconds, maxSeconds }, cancellationToken: ct));
    }

    public async Task ClearFailedLoginsAsync(int id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Users SET FailedLoginCount = 0, LockedUntil = NULL
            WHERE Id = @id AND (FailedLoginCount > 0 OR LockedUntil IS NOT NULL)
            """,
            new { id }, cancellationToken: ct));
    }

    public async Task SetConfigPasswordHashAsync(int id, string configPasswordHash, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Users SET ConfigPasswordHash = @configPasswordHash WHERE Id = @id",
            new { id, configPasswordHash }, cancellationToken: ct));
    }

    /// <summary>Updates role, limit and status; signs the user out when role or status changes.</summary>
    public async Task UpdateAsync(int id, string role, int? sessionLimit, bool isDisabled, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Users
            SET TokenVersion = TokenVersion + CASE WHEN Role <> @role OR IsDisabled <> @isDisabled THEN 1 ELSE 0 END,
                Role = @role, SessionLimit = @sessionLimit, IsDisabled = @isDisabled
            WHERE Id = @id
            """,
            new { id, role, sessionLimit, isDisabled }, cancellationToken: ct));
    }

    public async Task ResetSessionsAsync(int id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Users SET SessionsResetAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Deletes the user. Their sessions are kept, without an owner, so only admins see them.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await using var db = Open();
        await db.ExecuteAsync(new CommandDefinition(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.Sessions SET UserId = NULL WHERE UserId = @id;
            DELETE FROM dbo.Users WHERE Id = @id;
            COMMIT;
            """,
            new { id }, cancellationToken: ct));
    }

    /// <summary>Enabled admins other than <paramref name="exceptId"/>; used to never lock every admin out.</summary>
    public async Task<int> CountOtherEnabledAdminsAsync(int exceptId, CancellationToken ct)
    {
        await using var db = Open();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.Users WHERE Role = @admin AND IsDisabled = 0 AND Id <> @exceptId",
            new { admin = UserRoles.Admin, exceptId }, cancellationToken: ct));
    }
}
