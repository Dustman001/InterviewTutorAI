using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

/// <summary>
/// Wakes this instance's grading worker when a learner polls a session whose grading is due. Only the instance that
/// queues a grading signals its own worker, and an idle worker doesn't poll the database, so a job left by an instance
/// that went away (scale-in, crash) would otherwise wait until some other worker happens to wake. The check runs on the
/// row the request already loaded, and at most one wake-up per <see cref="MinInterval"/> per instance gets through: a
/// wake-up makes the worker claim every due job, not just the polled one.
/// </summary>
public sealed class GradingWakeup(GradingSignal signal, IOptions<AssessmentOptions> options, TimeProvider clock)
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private long _nextAllowedTicks;

    /// <summary>Wakes the worker if the session's grading is due and no wake-up went out in the last interval.</summary>
    /// <returns>True when this call woke the worker.</returns>
    public bool OnPolled(SessionRow session)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!IsDue(session, options.Value.StaleGradingMinutes, now))
        {
            return false;
        }

        var next = Interlocked.Read(ref _nextAllowedTicks);
        if (now.Ticks < next
            || Interlocked.CompareExchange(ref _nextAllowedTicks, (now + MinInterval).Ticks, next) != next)
        {
            return false;
        }

        signal.Notify();
        Telemetry.GradingWakeups.Add(1);
        return true;
    }

    /// <summary>
    /// The worker's claim rules, read from the row: Pending with no retry time or a past one, or Running and stale.
    /// The grading columns are UTC (SYSUTCDATETIME) read back with Kind Unspecified.
    /// </summary>
    internal static bool IsDue(SessionRow session, int staleMinutes, DateTime nowUtc)
    {
        if (session.GradingState == GradingState.Pending)
        {
            return session.GradingNextAttemptAt is not { } nextAttempt || nextAttempt <= nowUtc;
        }

        return session.GradingState == GradingState.Running
            && session.GradingStartedAt is { } started
            && started < nowUtc.AddMinutes(-staleMinutes);
    }
}
