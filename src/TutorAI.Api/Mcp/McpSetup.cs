using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using TutorAI.Api.Auth;

namespace TutorAI.Api.Mcp;

public sealed record McpTokenResponse(string Token, DateTime ExpiresAt, string Path);

/// <summary>
/// The MCP server: streamable HTTP at <see cref="Path"/>, stateless, read-only tools (<see cref="TutorMcpTools"/>).
/// It accepts only MCP tokens (their own scheme and audience), and the rest of the API rejects them.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";
    public const string Scheme = "Mcp";
    public const string Policy = "mcp";
    public const string RatePolicy = "mcp";
    public const int CallsPerMinute = 60;

    public static void AddTutorMcp(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddJwtBearer(Scheme);
        builder.Services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = TokenService.ValidationParameters(auth.Value, auth.Value.McpAudience);
                // Same revocation as web tokens: disabled, deleted or signed-out users lose MCP access at once.
                jwt.Events = new JwtBearerEvents { OnTokenValidated = TokenRevocation.CheckAsync };
            });
        builder.Services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(TokenService.ScopeClaim, TokenService.McpReadScope));

        builder.Services.AddMcpServer(server => server.ServerInfo = new() { Name = "TutorAI", Version = "1.0" })
            .WithHttpTransport(http => http.Stateless = true)
            .WithTools<TutorMcpTools>();
    }

    /// <summary>A per-user limit on MCP calls. The authorization middleware has set the MCP user by the time it runs.</summary>
    public static void AddMcpRateLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limiter) =>
        limiter.AddPolicy(RatePolicy, http => RateLimitPartition.GetTokenBucketLimiter(
            http.User.FindFirst(TokenService.UserIdClaim)?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = CallsPerMinute,
                TokensPerPeriod = CallsPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    public static void MapTutorMcp(this WebApplication app)
    {
        app.MapMcp(Path).RequireAuthorization(Policy).RequireRateLimiting(RatePolicy);

        // Issues a read-only MCP token to the signed-in user (web token only).
        app.MapPost("/api/auth/mcp-token", async (ClaimsPrincipal principal, IUserRepository users, TokenService tokens, CancellationToken ct) =>
        {
            if (await users.GetByIdAsync(principal.UserId(), ct) is not { IsDisabled: false } user)
            {
                return Results.Unauthorized();
            }

            var token = tokens.IssueMcp(user);
            return Results.Ok(new McpTokenResponse(token.Token, token.ExpiresAt, Path));
        });
    }
}
