using Anthropic;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Auth;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Endpoints;
using TutorAI.Api.Speech;

var builder = WebApplication.CreateBuilder(args);

// In Azure, secrets come from Key Vault. Secret names use "--" for ":" (e.g. Anthropic--ApiKey).
// Locally, leave KeyVault:Uri empty and use dotnet user-secrets instead.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Services.Configure<AzureSpeechOptions>(builder.Configuration.GetSection("AzureSpeech"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<AssessmentOptions>(builder.Configuration.GetSection("Assessment"));
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
builder.Services.AddSingleton<CefrGrader>();
builder.Services.AddSingleton<AssessmentService>();
builder.Services.AddHttpClient<SpeechTokenService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", async (SessionRepository repository, SpeechTokenService speech, CancellationToken ct) =>
{
    bool database;
    try
    {
        database = await repository.PingAsync(ct);
    }
    catch (Exception)
    {
        database = false;
    }

    return Results.Json(
        new { database, speechConfigured = speech.IsConfigured },
        statusCode: database ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
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

app.Run();

public partial class Program;
