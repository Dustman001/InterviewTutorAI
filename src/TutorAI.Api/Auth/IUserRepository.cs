namespace TutorAI.Api.Auth;

/// <summary>User accounts, sign-in state and session allowances. See <see cref="UserRepository"/>.</summary>
public interface IUserRepository
{
    Task<UserRow?> GetByUsernameAsync(string username, CancellationToken ct);

    Task<UserRow?> GetByIdAsync(int id, CancellationToken ct);

    /// <summary>Sessions that count towards the user's limit.</summary>
    Task<int> CountSessionsAsync(int userId, CancellationToken ct);

    Task<IReadOnlyList<UserListRow>> ListAsync(CancellationToken ct);

    /// <summary>Returns the new id, or null when the username is taken.</summary>
    Task<int?> InsertAsync(UserRow user, CancellationToken ct);

    Task SetPasswordAsync(int id, string passwordHash, string? configPasswordHash, bool signOut, CancellationToken ct);

    /// <summary>Counts a failed sign-in and returns the lock's end, or null when the account is not locked.</summary>
    Task<DateTime?> RecordFailedLoginAsync(int id, DateTime utcNow, int threshold, int baseSeconds, int maxSeconds, CancellationToken ct);

    Task ClearFailedLoginsAsync(int id, CancellationToken ct);

    Task SetConfigPasswordHashAsync(int id, string configPasswordHash, CancellationToken ct);

    /// <summary>Updates role, limit and status; signs the user out when role or status changes.</summary>
    Task UpdateAsync(int id, string role, int? sessionLimit, bool isDisabled, CancellationToken ct);

    Task ResetSessionsAsync(int id, CancellationToken ct);

    /// <summary>Deletes the user. Their sessions are kept, without an owner.</summary>
    Task DeleteAsync(int id, CancellationToken ct);

    /// <summary>Enabled admins other than <paramref name="exceptId"/>.</summary>
    Task<int> CountOtherEnabledAdminsAsync(int exceptId, CancellationToken ct);
}
