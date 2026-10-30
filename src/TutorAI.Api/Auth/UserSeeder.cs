using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TutorAI.Api.Auth;

/// <summary>
/// Creates the built-in admin account from configuration when it doesn't exist yet. Every other user,
/// guests included, is created on the Users page. After creation the Users page owns the admin too, except
/// that changing the configured password (and restarting) still resets it, so a lost admin password can
/// always be recovered from Key Vault.
/// </summary>
public sealed class UserSeeder(
    IUserRepository users, IPasswordHasher<UserRow> hasher, IOptions<AuthOptions> options, ILogger<UserSeeder> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (_done)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_done)
            {
                return;
            }

            var auth = options.Value;
            await EnsureAdminAsync(auth.AdminUsername, auth.AdminPassword, ct);
            _done = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureAdminAsync(string username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            logger.LogWarning("No Auth:AdminPassword configured; the built-in admin '{Username}' is not created or updated.", username);
            return;
        }

        var existing = await users.GetByUsernameAsync(username, ct);
        if (existing is null)
        {
            var user = new UserRow { Username = username, Role = UserRoles.Admin };
            user.PasswordHash = hasher.HashPassword(user, password);
            user.ConfigPasswordHash = user.PasswordHash;
            await users.InsertAsync(user, ct);
            logger.LogInformation("Created the built-in admin account '{Username}'.", username);
            return;
        }

        if (existing.ConfigPasswordHash is not null && Verifies(existing, existing.ConfigPasswordHash, password))
        {
            return; // The configured password hasn't changed since it was last applied.
        }

        var configHash = hasher.HashPassword(existing, password);
        if (Verifies(existing, existing.PasswordHash, password))
        {
            await users.SetConfigPasswordHashAsync(existing.Id, configHash, ct);
            return;
        }

        await users.SetPasswordAsync(existing.Id, configHash, configHash, signOut: true, ct);
        logger.LogInformation("Applied the changed configured password to '{Username}'.", username);
    }

    private bool Verifies(UserRow user, string hash, string password) =>
        hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
}

/// <summary>Seeds at startup without blocking it; sign-in retries if the database was not reachable yet.</summary>
public sealed class UserSeederStartup(UserSeeder seeder, ILogger<UserSeederStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await seeder.EnsureAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the sign-in accounts at startup; will retry on the first sign-in.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
