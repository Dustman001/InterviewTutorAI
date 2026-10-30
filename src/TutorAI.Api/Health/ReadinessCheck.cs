using System.Diagnostics;
using Anthropic;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;
using TutorAI.Api.Speech;

namespace TutorAI.Api.Health;

/// <param name="Detail">A short, safe reason when not OK (an HTTP status or an exception type), never a message that could carry secrets.</param>
public sealed record DependencyStatus(bool Ok, long LatencyMs, string? Detail);

public sealed record Readiness(
    bool Ready, DependencyStatus Database, DependencyStatus Anthropic, DependencyStatus Speech, DateTime CheckedAt);

/// <summary>
/// Checks every dependency a session needs: the database, the Anthropic API (a free model lookup) and Azure
/// Speech (issuing a token). The result is cached so frequent probes can't generate traffic or wake the database.
/// </summary>
public sealed class ReadinessCheck(
    ISessionRepository repository,
    AnthropicClient anthropic,
    IOptions<AnthropicOptions> anthropicOptions,
    SpeechTokenService speech,
    TimeProvider clock,
    ILogger<ReadinessCheck> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Readiness? _last;

    public async Task<Readiness> GetAsync(CancellationToken ct)
    {
        if (Fresh(_last) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            return Fresh(_last) ?? (_last = await RunAsync());
        }
        finally
        {
            _gate.Release();
        }
    }

    private Readiness? Fresh(Readiness? last) =>
        last is not null && clock.GetUtcNow().UtcDateTime - last.CheckedAt < CacheFor ? last : null;

    // Not tied to the request's token: one caller giving up must not leave a half-finished result in the cache.
    private async Task<Readiness> RunAsync()
    {
        var database = CheckAsync("database", async ct => await repository.PingAsync(ct) ? null : "unexpected reply");
        var model = CheckAsync("anthropic", async ct =>
        {
            await anthropic.Models.Retrieve(anthropicOptions.Value.ConversationModel, cancellationToken: ct);
            return null;
        });
        var speechToken = speech.IsConfigured
            ? CheckAsync("speech", async ct =>
            {
                await speech.IssueAsync(ct);
                return null;
            })
            : Task.FromResult(new DependencyStatus(false, 0, "not configured"));

        await Task.WhenAll(database, model, speechToken);
        var result = new Readiness(
            database.Result.Ok && model.Result.Ok && speechToken.Result.Ok,
            database.Result, model.Result, speechToken.Result, clock.GetUtcNow().UtcDateTime);
        if (!result.Ready)
        {
            logger.LogWarning(
                "Not ready: database {Database}, anthropic {Anthropic}, speech {Speech}",
                result.Database.Detail ?? "ok", result.Anthropic.Detail ?? "ok", result.Speech.Detail ?? "ok");
        }

        return result;
    }

    /// <param name="probe">Returns null when healthy, or a short reason.</param>
    private async Task<DependencyStatus> CheckAsync(string name, Func<CancellationToken, Task<string?>> probe)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var timer = Stopwatch.StartNew();
        try
        {
            var problem = await probe(timeout.Token);
            return new DependencyStatus(problem is null, timer.ElapsedMilliseconds, problem);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Readiness check for {Dependency} failed", name);
            var detail = ex switch
            {
                _ when timeout.IsCancellationRequested => $"timed out after {Timeout.TotalSeconds:0} s",
                HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
                _ => ex.GetType().Name,
            };
            return new DependencyStatus(false, timer.ElapsedMilliseconds, detail);
        }
    }
}
