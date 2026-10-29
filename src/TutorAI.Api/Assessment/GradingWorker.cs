using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

/// <summary>Wakes the grading worker. Several notifications before it wakes count as one.</summary>
public sealed class GradingSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>
    /// Waits for a notification, until <paramref name="timeout"/> passes (null: no timeout), or until
    /// <paramref name="ct"/> is cancelled. Never throws on cancellation; the caller checks the token.
    /// </summary>
    public async Task WaitAsync(TimeSpan? timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            wait.CancelAfter(t);
        }

        try
        {
            await _channel.Reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled.
        }
    }
}

/// <summary>
/// Grades ended sessions in the background, so ending a session doesn't hold a request open for the
/// whole Claude call. Jobs live in dbo.Sessions (GradingState), so they survive restarts and are shared
/// safely between API instances.
///
/// The worker queries the database only when work can be due: at startup (to pick up orphaned jobs),
/// when a session is queued on this instance, and when a queued retry or a stale job falls due. It does
/// not poll while idle, so a serverless database can still pause.
/// </summary>
public sealed class GradingWorker(
    SessionRepository repository,
    AssessmentService assessments,
    GradingSignal signal,
    IOptions<AssessmentOptions> options,
    ILogger<GradingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DatabaseErrorBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Task, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var settings = options.Value;
        using var slots = new SemaphoreSlim(Math.Max(1, settings.GradingConcurrency));

        TimeSpan? wait = TimeSpan.Zero;
        while (!stop.IsCancellationRequested)
        {
            if (wait != TimeSpan.Zero)
            {
                await signal.WaitAsync(wait, stop);
                if (stop.IsCancellationRequested)
                {
                    break;
                }
            }

            try
            {
                wait = await DispatchAsync(slots, settings, stop);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Grading worker could not read the queue; trying again in {Delay}", DatabaseErrorBackoff);
                wait = DatabaseErrorBackoff;
            }
        }

        // Let in-flight jobs put themselves back in the queue before the host stops.
        await Task.WhenAll(_running.Keys);
    }

    /// <summary>Starts every due job there is a free slot for, then returns how long to wait before looking again.</summary>
    private async Task<TimeSpan?> DispatchAsync(SemaphoreSlim slots, AssessmentOptions settings, CancellationToken stop)
    {
        var failed = await repository.FailStaleGradingAsync(
            settings.StaleGradingMinutes, settings.MaxGradingAttempts, "Scoring stopped unexpectedly. Try again.", stop);
        if (failed > 0)
        {
            logger.LogWarning("Marked {Count} stale grading job(s) as failed after the last attempt", failed);
        }

        while (true)
        {
            if (!slots.Wait(0))
            {
                // Every slot is busy; a finishing job wakes the loop.
                return null;
            }

            GradingClaim? claim;
            try
            {
                claim = await repository.ClaimGradingAsync(settings.StaleGradingMinutes, settings.MaxGradingAttempts, stop);
            }
            catch
            {
                slots.Release();
                throw;
            }

            if (claim is null)
            {
                slots.Release();
                break;
            }

            Task job = null!;
            job = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(claim, settings, stop);
                }
                finally
                {
                    slots.Release();
                    _running.TryRemove(job, out _);
                    signal.Notify();
                }
            }, CancellationToken.None);
            _running.TryAdd(job, 0);
        }

        var ms = await repository.MillisecondsUntilNextGradingAsync(settings.StaleGradingMinutes, stop);
        if (ms is null)
        {
            return null;
        }

        // A job that is due but couldn't be claimed is being claimed by another instance; look again shortly.
        var next = TimeSpan.FromMilliseconds(ms.Value);
        return next < MinWait ? MinWait : next > MaxWait ? MaxWait : next;
    }

    private async Task RunAsync(GradingClaim claim, AssessmentOptions settings, CancellationToken stop)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["SessionId"] = claim.SessionId });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.GradingTimeoutSeconds));

        try
        {
            await assessments.GradeAsync(claim.SessionId, timeout.Token);
            await repository.FinishGradingAttemptAsync(claim, GradingStates.Done, null, null, CancellationToken.None);
            logger.LogInformation("Graded session {SessionId} on attempt {Attempt}", claim.SessionId, claim.Attempt);
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // Shutting down. Not necessarily an OperationCanceledException: a cancelled SQL command throws SqlException.
            await TryAsync(() => repository.ReleaseGradingAsync(claim, CancellationToken.None), claim);
        }
        catch (Exception ex)
        {
            var timedOut = ex is OperationCanceledException && timeout.IsCancellationRequested;
            var retry = claim.Attempt < settings.MaxGradingAttempts;
            logger.LogError(
                ex, "Grading attempt {Attempt} of {MaxAttempts} failed for session {SessionId}{Next}",
                claim.Attempt, settings.MaxGradingAttempts, claim.SessionId, retry ? "; will retry" : "; giving up");

            if (retry)
            {
                await TryAsync(
                    () => repository.FinishGradingAttemptAsync(
                        claim, GradingStates.Pending, null, RetryDelay(settings, claim.Attempt), CancellationToken.None),
                    claim);
            }
            else
            {
                var error = timedOut
                    ? $"Scoring took longer than {settings.GradingTimeoutSeconds} seconds, {claim.Attempt} times. Try again later."
                    : $"Scoring failed after {claim.Attempt} attempts. Try again later.";
                await TryAsync(
                    () => repository.FinishGradingAttemptAsync(claim, GradingStates.Failed, error, null, CancellationToken.None),
                    claim);
            }
        }
    }

    private static readonly int[] DefaultRetryDelaySeconds = [15, 60];

    /// <summary>Seconds to wait after failed attempt <paramref name="attempt"/> (1-based).</summary>
    internal static int RetryDelay(AssessmentOptions settings, int attempt)
    {
        var delays = settings.RetryDelaySeconds is { Length: > 0 } configured ? configured : DefaultRetryDelaySeconds;
        return delays[Math.Clamp(attempt - 1, 0, delays.Length - 1)];
    }

    /// <summary>
    /// Records an outcome. If the database is unreachable the job stays Running and is claimed again once
    /// it goes stale.
    /// </summary>
    private async Task TryAsync(Func<Task> record, GradingClaim claim)
    {
        try
        {
            await record();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the grading outcome for session {SessionId}", claim.SessionId);
        }
    }
}
