using System.Globalization;
using System.Threading.RateLimiting;
using Anthropic;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Auth;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Endpoints;
using TutorAI.Api.Health;
using TutorAI.Api.Speech;

var builder = WebApplication.CreateBuilder(args);

// In Azure, secrets come from Key Vault. Secret names use "--" for ":" (e.g. Anthropic--ApiKey).
// Locally, leave KeyVault:Uri empty and use dotnet user-secrets instead.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// Traces, metrics and logs to Application Insights, only when it's configured (App Service sets this setting
// when Application Insights is attached). Locally nothing is exported.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor()
        .WithTracing(tracing => tracing.AddSource(Telemetry.Name))
        .WithMetrics(metrics => metrics.AddMeter(Telemetry.Name));
}

builder.Services.Configure<AzureSpeechOptions>(builder.Configuration.GetSection("AzureSpeech"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<AssessmentOptions>(builder.Configuration.GetSection("Assessment"));
builder.Services.Configure<SessionLimitsOptions>(builder.Configuration.GetSection("Sessions"));
builder.Services.Configure<Dictionary<string, ModelPrice>>(builder.Configuration.GetSection("Pricing"));

// No request needs more than a long spoken turn with its pronunciation results.
var maxRequestBytes = (builder.Configuration.GetSection("Sessions").Get<SessionLimitsOptions>() ?? new()).MaxRequestBytes;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<IISServerOptions>(iis => iis.MaxRequestBodySize = maxRequestBytes);
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection("Auth"))
    .Validate(
        auth => auth.JwtSigningKey is { Length: >= 32 },
        "Auth:JwtSigningKey must be set to at least 32 characters (Key Vault secret Auth--JwtSigningKey, or dotnet user-secrets locally).")
    .ValidateOnStart();

// Every endpoint needs the JWT the web app forwards, unless it is marked AllowAnonymous.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((jwt, auth) =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = TokenService.ValidationParameters(auth.Value);
        jwt.Events = new JwtBearerEvents { OnTokenValidated = TokenRevocation.CheckAsync };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(UserRoles.Admin, policy => policy.RequireRole(UserRoles.Admin));

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Wait a moment and try again." }, ct);
    };

    // One shared window for every sign-in attempt: usernames can't be sprayed with guesses. The account
    // lockout (AuthEndpoints) handles guessing against a single account.
    limiter.AddPolicy(AuthEndpoints.LoginPolicy, http =>
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginAttemptsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    limiter.AddPolicy(SessionEndpoints.TurnsPolicy, http =>
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<SessionLimitsOptions>>().Value.TurnsPerMinute;
        return RateLimitPartition.GetTokenBucketLimiter(
            http.User.FindFirst(TokenService.UserIdClaim)?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = perMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

builder.Services.AddSingleton(_ =>
{
    var apiKey = builder.Configuration["Anthropic:ApiKey"];
    return string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
});
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IPasswordHasher<UserRow>, PasswordHasher<UserRow>>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<UserSeeder>();
builder.Services.AddHostedService<UserSeederStartup>();
builder.Services.AddSingleton<TutorChat>();
builder.Services.AddSingleton<ICefrGrader, CefrGrader>();
builder.Services.AddSingleton<AssessmentService>();
builder.Services.AddSingleton<GradingSignal>();
builder.Services.AddHostedService<GradingWorker>();
builder.Services.AddHttpClient<SpeechTokenService>();
builder.Services.AddSingleton<ReadinessCheck>();

var app = builder.Build();

// Kestrel enforces the limit too, but only once the body is read; this answers 413 up front from the declared length.
app.Use((http, next) =>
{
    if (http.Request.ContentLength > maxRequestBytes)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return http.Response.WriteAsJsonAsync(new { error = "The request is too large." });
    }

    return next(http);
});

app.UseAuthentication();
app.UseAuthorization();
// After authentication, so turn requests are limited per user.
app.UseRateLimiter();

// Liveness, for the App Service health probe (about once a minute). It deliberately touches no dependency:
// a database query here would keep a serverless database from ever auto-pausing, and restarting the
// instance wouldn't fix a dependency outage anyway.
app.MapGet("/api/health", (SpeechTokenService speech) =>
    Results.Ok(new { status = "ok", speechConfigured = speech.IsConfigured })).AllowAnonymous();

// Readiness: can a session run end to end? Checks the database, Anthropic and Azure Speech, cached for a minute.
app.MapGet("/api/health/ready", async (ReadinessCheck readiness, CancellationToken ct) =>
{
    var result = await readiness.GetAsync(ct);
    return Results.Json(result, statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapGet("/api/speech/token", async (SpeechTokenService speech, CancellationToken ct) =>
{
    if (!speech.IsConfigured)
    {
        return Results.Json(
            new { error = "Azure Speech is not configured. Set AzureSpeech:Key (Key Vault secret AzureSpeech--Key, or dotnet user-secrets locally) and AzureSpeech:Region." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        return Results.Ok(await speech.IssueAsync(ct));
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(
            new { error = $"Azure Speech rejected the token request ({(int?)ex.StatusCode}). Check the key and region." },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapSessionEndpoints();
app.MapProgressEndpoints();

app.Run();

public partial class Program;
