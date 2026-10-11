using Anthropic;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Endpoints;
using TutorAI.Api.Speech;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureSpeechOptions>(builder.Configuration.GetSection("AzureSpeech"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<AssessmentOptions>(builder.Configuration.GetSection("Assessment"));

builder.Services.AddSingleton(_ =>
{
    var apiKey = builder.Configuration["Anthropic:ApiKey"];
    return string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
});
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<TutorChat>();
builder.Services.AddSingleton<CefrGrader>();
builder.Services.AddSingleton<AssessmentService>();
builder.Services.AddHttpClient<SpeechTokenService>();

var app = builder.Build();

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
});

app.MapGet("/api/speech/token", async (SpeechTokenService speech, CancellationToken ct) =>
{
    if (!speech.IsConfigured)
    {
        return Results.Json(
            new { error = "Azure Speech is not configured. Set AzureSpeech:Key and AzureSpeech:Region with dotnet user-secrets." },
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

app.MapSessionEndpoints();

app.Run();

public partial class Program;
