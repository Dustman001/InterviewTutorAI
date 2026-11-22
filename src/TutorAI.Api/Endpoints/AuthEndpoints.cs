using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TutorAI.Api.Auth;

namespace TutorAI.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);

/// <param name="SessionLimit">Null when the user has no limit.</param>
public sealed record UserDto(string Username, string Role, int? SessionLimit, int SessionsUsed);

public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public static class AuthEndpoints
{
    /// <summary>Rate-limit policy for sign-in attempts across all users (see Program.cs).</summary>
    public const string LoginPolicy = "login";

    private static void FailedLogin(string reason) =>
        Telemetry.LoginsFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    // Security events log the user id only: never the username (a mistyped password often lands there) or a password.

    private static void LockedRefused(ILogger logger, UserRow user, DateTime lockedUntil)
    {
        FailedLogin("locked");
        logger.LogInformation("Sign-in refused for locked account {UserId} until {LockedUntil}", user.Id, lockedUntil);
    }

    /// <summary>Counts a wrong password toward the lockout and logs it, and the lock if this failure started one.</summary>
    private static async Task WrongPasswordAsync(
        ILogger logger, IUserRepository users, UserRow user, AuthOptions options, DateTime now, CancellationToken ct)
    {
        var lockedUntil = await users.RecordFailedLoginAsync(
            user.Id, now, options.LockoutThreshold, options.LockoutBaseSeconds, options.LockoutMaxSeconds, ct);
        FailedLogin("password");
        logger.LogInformation("Sign-in failed for account {UserId}: wrong password", user.Id);

        // A locked account is refused before its password is checked, so a lock returned here began with this failure.
        if (lockedUntil is { } until && DateTime.SpecifyKind(until, DateTimeKind.Utc) > now)
        {
            logger.LogWarning(
                "Account {UserId} locked until {LockedUntil} after repeated failed sign-ins",
                user.Id, DateTime.SpecifyKind(until, DateTimeKind.Utc));
        }
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (
            LoginRequest request, HttpContext http, UserSeeder seeder, IUserRepository users, IPasswordHasher<UserRow> hasher,
            TokenService tokens, IOptions<AuthOptions> auth, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger(typeof(AuthEndpoints));
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { error = "Enter a username and password." });
            }

            await seeder.EnsureAsync(ct);
            var wrong = Results.Json(new { error = "Wrong username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            var user = await users.GetByUsernameAsync(request.Username.Trim(), ct);
            if (user is null)
            {
                FailedLogin("unknown_user");
                return wrong;
            }

            // A locked account is refused before the password is checked, so guessing stops working.
            var now = clock.GetUtcNow().UtcDateTime;
            if (user.LockedUntil is { } lockedUntil && DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc) > now)
            {
                LockedRefused(logger, user, DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc));
                return Locked(http, DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc) - now);
            }

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                await WrongPasswordAsync(logger, users, user, auth.Value, now, ct);
                return wrong;
            }

            await users.ClearFailedLoginsAsync(user.Id, ct);

            if (user.IsDisabled)
            {
                logger.LogWarning("Sign-in refused for disabled account {UserId}", user.Id);
                return Results.Json(new { error = "This account is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var token = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, await ToDtoAsync(user, users, ct)));
        }).AllowAnonymous().RequireRateLimiting(LoginPolicy);

        // A new token for a still-valid one. The web app calls this while the user is active, so the short
        // token lifetime acts as an idle timeout. Disabled or signed-out users fail token validation first.
        auth.MapPost("/refresh", async (ClaimsPrincipal principal, IUserRepository users, TokenService tokens, CancellationToken ct) =>
        {
            if (await users.GetByIdAsync(principal.UserId(), ct) is not { } user)
            {
                return Results.Unauthorized();
            }

            var token = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, await ToDtoAsync(user, users, ct)));
        });

        // Change your own password. The current one is checked like a sign-in (a wrong one counts toward the
        // lockout). Every other session is signed out; this one gets a fresh token so it stays signed in.
        auth.MapPost("/password", async (
            ChangePasswordRequest request, ClaimsPrincipal principal, HttpContext http, IUserRepository users,
            IPasswordHasher<UserRow> hasher, TokenService tokens, IOptions<AuthOptions> auth, TimeProvider clock,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (await users.GetByIdAsync(principal.UserId(), ct) is not { } user)
            {
                return Results.Unauthorized();
            }

            var logger = loggers.CreateLogger(typeof(AuthEndpoints));
            var now = clock.GetUtcNow().UtcDateTime;
            if (user.LockedUntil is { } lockedUntil && DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc) > now)
            {
                LockedRefused(logger, user, DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc));
                return Locked(http, DateTime.SpecifyKind(lockedUntil, DateTimeKind.Utc) - now);
            }

            if (string.IsNullOrEmpty(request.CurrentPassword)
                || hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            {
                await WrongPasswordAsync(logger, users, user, auth.Value, now, ct);
                return Results.BadRequest(new { error = "Your current password is wrong." });
            }

            if (UserEndpoints.PasswordProblem(request.NewPassword) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            if (request.NewPassword == request.CurrentPassword)
            {
                return Results.BadRequest(new { error = "Choose a password different from the current one." });
            }

            // configPasswordHash null keeps the built-in admin's record of the configured password, so the
            // change survives restarts until the configured password itself changes (as with an admin reset).
            await users.SetPasswordAsync(user.Id, hasher.HashPassword(user, request.NewPassword!), configPasswordHash: null, signOut: true, ct);

            var updated = await users.GetByIdAsync(user.Id, ct) ?? user;
            var token = tokens.Issue(updated);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, await ToDtoAsync(updated, users, ct)));
        }).RequireRateLimiting(LoginPolicy);

        auth.MapGet("/me", async (ClaimsPrincipal principal, IUserRepository users, CancellationToken ct) =>
            await users.GetByIdAsync(principal.UserId(), ct) is { } user
                ? Results.Ok(await ToDtoAsync(user, users, ct))
                : Results.Unauthorized());
    }

    private static IResult Locked(HttpContext http, TimeSpan remaining)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wait = seconds <= 60 ? "a minute" : $"{(int)Math.Ceiling(seconds / 60.0)} minutes";
        return Results.Json(
            new { error = $"Too many failed sign-ins for this account. Try again in {wait}." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    internal static async Task<UserDto> ToDtoAsync(UserRow user, IUserRepository users, CancellationToken ct) =>
        new(user.Username, user.Role, user.SessionLimit, await users.CountSessionsAsync(user.Id, ct));
}
