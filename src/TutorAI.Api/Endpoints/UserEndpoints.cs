using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TutorAI.Api.Auth;
using TutorAI.Api.Data;

namespace TutorAI.Api.Endpoints;

/// <param name="Usage30Days">Claude tokens and estimated cost over the last 30 days. Only set in the user list.</param>
public sealed record AdminUserDto(
    int Id, string Username, string Role, int? SessionLimit, int SessionsUsed, bool IsDisabled, DateTime CreatedAt,
    UsageEstimate? Usage30Days = null);

public sealed record CreateUserRequest(string? Username, string? Password, string? Role, int? SessionLimit);

/// <param name="SessionLimit">Ignored for admins, who are always unlimited.</param>
public sealed record UpdateUserRequest(string? Role, int? SessionLimit, bool? IsDisabled);

public sealed record SetPasswordRequest(string? Password);

/// <summary>Admin-only user management. Every change that affects access signs the user out at once.</summary>
public static partial class UserEndpoints
{
    public const int MinPasswordLength = 8;

    [GeneratedRegex("^[A-Za-z0-9._@-]{3,100}$")]
    private static partial Regex UsernamePattern();

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").RequireAuthorization(UserRoles.Admin);

        users.MapGet("/", async (
            IUserRepository repository, ISessionRepository sessions, IOptions<Dictionary<string, ModelPrice>> pricing,
            TimeProvider clock, CancellationToken ct) =>
        {
            var usage = (await sessions.GetUsageByUserAsync(clock.GetUtcNow().UtcDateTime.AddDays(-30), ct))
                .ToLookup(u => u.UserId);
            return (await repository.ListAsync(ct)).Select(u => new AdminUserDto(
                u.Id, u.Username, u.Role, u.SessionLimit, u.SessionsUsed, u.IsDisabled, Utc(u.CreatedAt),
                CostEstimator.Estimate(
                    pricing.Value,
                    usage[u.Id].Select(m => (m.Model, new TokenTotals(m.Input, m.Output, m.CacheRead, m.CacheWrite))))));
        });

        users.MapPost("/", async (
            CreateUserRequest request, IUserRepository repository, IPasswordHasher<UserRow> hasher, CancellationToken ct) =>
        {
            var username = request.Username?.Trim() ?? "";
            if (!UsernamePattern().IsMatch(username))
            {
                return Error("Usernames are 3 to 100 characters: letters, digits, and . _ @ -");
            }

            if (PasswordProblem(request.Password) is { } problem)
            {
                return Error(problem);
            }

            if (!UserRoles.IsValid(request.Role))
            {
                return Error($"Role must be '{UserRoles.Admin}' or '{UserRoles.Guest}'.");
            }

            if (LimitProblem(request.Role!, request.SessionLimit) is { } limitProblem)
            {
                return Error(limitProblem);
            }

            var user = new UserRow
            {
                Username = username,
                Role = request.Role!,
                SessionLimit = request.Role == UserRoles.Admin ? null : request.SessionLimit,
            };
            user.PasswordHash = hasher.HashPassword(user, request.Password!);

            if (await repository.InsertAsync(user, ct) is not { } id)
            {
                return Results.Conflict(new { error = $"The username '{username}' is taken." });
            }

            return Results.Created($"/api/users/{id}", await GetDtoAsync(repository, id, ct));
        });

        users.MapPut("/{id:int}", async (
            int id, UpdateUserRequest request, ClaimsPrincipal principal, IUserRepository repository, CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is not { } user)
            {
                return Results.NotFound();
            }

            var role = request.Role ?? user.Role;
            var disabled = request.IsDisabled ?? user.IsDisabled;
            var limit = role == UserRoles.Admin ? null : request.SessionLimit ?? user.SessionLimit;
            if (!UserRoles.IsValid(role))
            {
                return Error($"Role must be '{UserRoles.Admin}' or '{UserRoles.Guest}'.");
            }

            if (LimitProblem(role, limit) is { } limitProblem)
            {
                return Error(limitProblem);
            }

            var losesAdmin = user.Role == UserRoles.Admin && !user.IsDisabled && (role != UserRoles.Admin || disabled);
            if (losesAdmin && id == principal.UserId())
            {
                return Error("You can't disable your own account or remove your own admin role.");
            }

            if (losesAdmin && await repository.CountOtherEnabledAdminsAsync(id, ct) == 0)
            {
                return Error("At least one enabled admin must remain.");
            }

            await repository.UpdateAsync(id, role, limit, disabled, ct);
            return Results.Ok(await GetDtoAsync(repository, id, ct));
        });

        users.MapPost("/{id:int}/password", async (
            int id, SetPasswordRequest request, IUserRepository repository, IPasswordHasher<UserRow> hasher, CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is not { } user)
            {
                return Results.NotFound();
            }

            if (PasswordProblem(request.Password) is { } problem)
            {
                return Error(problem);
            }

            await repository.SetPasswordAsync(id, hasher.HashPassword(user, request.Password!), configPasswordHash: null, signOut: true, ct);
            return Results.NoContent();
        });

        users.MapPost("/{id:int}/reset-sessions", async (int id, IUserRepository repository, CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            await repository.ResetSessionsAsync(id, ct);
            return Results.Ok(await GetDtoAsync(repository, id, ct));
        });

        users.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, IUserRepository repository, CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is not { } user)
            {
                return Results.NotFound();
            }

            if (id == principal.UserId())
            {
                return Error("You can't delete your own account.");
            }

            if (user.Role == UserRoles.Admin && !user.IsDisabled && await repository.CountOtherEnabledAdminsAsync(id, ct) == 0)
            {
                return Error("At least one enabled admin must remain.");
            }

            await repository.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    internal static string? PasswordProblem(string? password) =>
        password is null || password.Length < MinPasswordLength
            ? $"Passwords need at least {MinPasswordLength} characters."
            : password.Length > 200 ? "Passwords can be at most 200 characters." : null;

    private static string? LimitProblem(string role, int? limit) =>
        role == UserRoles.Guest && limit is null or < 1 or > 10_000
            ? "Guests need a session limit between 1 and 10000."
            : null;

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static async Task<AdminUserDto> GetDtoAsync(IUserRepository repository, int id, CancellationToken ct)
    {
        var row = (await repository.ListAsync(ct)).Single(u => u.Id == id);
        return new AdminUserDto(row.Id, row.Username, row.Role, row.SessionLimit, row.SessionsUsed, row.IsDisabled, Utc(row.CreatedAt));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
