using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using TutorAI.Api.Auth;

namespace TutorAI.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);

/// <param name="SessionLimit">Null when the user has no limit.</param>
public sealed record UserDto(string Username, string Role, int? SessionLimit, int SessionsUsed);

public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (
            LoginRequest request, UserSeeder seeder, UserRepository users, IPasswordHasher<UserRow> hasher,
            TokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { error = "Enter a username and password." });
            }

            await seeder.EnsureAsync(ct);
            var user = await users.GetByUsernameAsync(request.Username.Trim(), ct);
            if (user is null
                || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                return Results.Json(new { error = "Wrong username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (user.IsDisabled)
            {
                return Results.Json(new { error = "This account is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var token = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, await ToDtoAsync(user, users, ct)));
        }).AllowAnonymous();

        // A new token for a still-valid one. The web app calls this while the user is active, so the short
        // token lifetime acts as an idle timeout. Disabled or signed-out users fail token validation first.
        auth.MapPost("/refresh", async (ClaimsPrincipal principal, UserRepository users, TokenService tokens, CancellationToken ct) =>
        {
            if (await users.GetByIdAsync(principal.UserId(), ct) is not { } user)
            {
                return Results.Unauthorized();
            }

            var token = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, await ToDtoAsync(user, users, ct)));
        });

        auth.MapGet("/me", async (ClaimsPrincipal principal, UserRepository users, CancellationToken ct) =>
            await users.GetByIdAsync(principal.UserId(), ct) is { } user
                ? Results.Ok(await ToDtoAsync(user, users, ct))
                : Results.Unauthorized());
    }

    internal static async Task<UserDto> ToDtoAsync(UserRow user, UserRepository users, CancellationToken ct) =>
        new(user.Username, user.Role, user.SessionLimit, await users.CountSessionsAsync(user.Id, ct));
}
