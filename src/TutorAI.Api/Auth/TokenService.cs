using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TutorAI.Api.Auth;

public sealed record IssuedToken(string Token, DateTime ExpiresAt);

/// <summary>Issues and describes the HMAC-signed JWTs the web app forwards to this API.</summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider clock)
{
    public const string UserIdClaim = JwtRegisteredClaimNames.Sub;
    public const string NameClaim = JwtRegisteredClaimNames.Name;
    public const string RoleClaim = "role";
    /// <summary>Must match dbo.Users.TokenVersion; bumping that column revokes every token issued before.</summary>
    public const string VersionClaim = "ver";

    public IssuedToken Issue(UserRow user)
    {
        var auth = options.Value;
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(auth.TokenLifetimeMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = auth.Audience,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [UserIdClaim] = user.Id.ToString(),
                [NameClaim] = user.Username,
                [RoleClaim] = user.Role,
                [VersionClaim] = user.TokenVersion.ToString(),
            },
            SigningCredentials = new SigningCredentials(SigningKey(auth), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, expires);
    }

    public static TokenValidationParameters ValidationParameters(AuthOptions auth) => new()
    {
        ValidIssuer = auth.Issuer,
        ValidAudience = auth.Audience,
        IssuerSigningKey = SigningKey(auth),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim,
        ClockSkew = TimeSpan.FromMinutes(1),
    };

    private static SymmetricSecurityKey SigningKey(AuthOptions auth)
    {
        if (string.IsNullOrWhiteSpace(auth.JwtSigningKey) || auth.JwtSigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Auth:JwtSigningKey must be set to at least 32 characters (Key Vault secret Auth--JwtSigningKey, or dotnet user-secrets locally).");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSigningKey));
    }
}

/// <summary>
/// Runs after the signature and expiry checks: the user must still exist, be enabled, and the token must
/// carry their current version. This is what makes disabling a user or resetting a password take effect
/// immediately, rather than when the token expires.
/// </summary>
public static class TokenRevocation
{
    public static async Task CheckAsync(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!int.TryParse(principal.FindFirstValue(TokenService.UserIdClaim), out var userId)
            || !int.TryParse(principal.FindFirstValue(TokenService.VersionClaim), out var version))
        {
            context.Fail("The token is missing its user or version.");
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
        var user = await users.GetByIdAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.IsDisabled || user.TokenVersion != version)
        {
            context.Fail("The user was disabled, deleted, or signed out.");
        }
    }
}

public static class ClaimsPrincipalExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(TokenService.UserIdClaim)
            ?? throw new InvalidOperationException("The token has no user id."));

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(UserRoles.Admin);
}
