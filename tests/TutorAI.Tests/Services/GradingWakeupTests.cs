using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Services;

/// <summary>When a learner's poll wakes the grading worker, and how often.</summary>
public class GradingWakeupTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FixedClock _clock = new(Start);
    private readonly GradingWakeup _wakeup;

    public GradingWakeupTests() =>
        _wakeup = new GradingWakeup(new GradingSignal(), Options.Create(new AssessmentOptions { StaleGradingMinutes = 10 }), _clock);

    // Dapper reads the UTC DATETIME2 columns back with Kind Unspecified.
    private static DateTime Db(DateTimeOffset utc) => DateTime.SpecifyKind(utc.UtcDateTime, DateTimeKind.Unspecified);

    private static SessionRow Session(GradingState? state, DateTime? startedAt = null, DateTime? nextAttemptAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = SessionStatus.Ended,
            GradingState = state,
            GradingStartedAt = startedAt,
            GradingNextAttemptAt = nextAttemptAt,
        };

    [Fact]
    public void A_pending_session_due_now_wakes_the_worker()
    {
        Assert.True(_wakeup.OnPolled(Session(GradingState.Pending)));
    }

    [Fact]
    public void A_pending_retry_not_yet_due_does_not_wake()
    {
        var session = Session(GradingState.Pending, nextAttemptAt: Db(Start.AddSeconds(10)));

        Assert.False(_wakeup.OnPolled(session));

        _clock.Now = Start.AddSeconds(11);
        Assert.True(_wakeup.OnPolled(session));
    }

    [Fact]
    public void A_running_session_wakes_only_once_stale()
    {
        Assert.False(_wakeup.OnPolled(Session(GradingState.Running, startedAt: Db(Start.AddMinutes(-9)))));
        Assert.True(_wakeup.OnPolled(Session(GradingState.Running, startedAt: Db(Start.AddMinutes(-11)))));
    }

    [Fact]
    public void Done_failed_and_never_queued_sessions_do_not_wake()
    {
        Assert.False(_wakeup.OnPolled(Session(GradingState.Done)));
        Assert.False(_wakeup.OnPolled(Session(GradingState.Failed)));
        Assert.False(_wakeup.OnPolled(Session(null)));
    }

    [Fact]
    public void Wake_ups_are_limited_to_one_per_interval_per_instance()
    {
        Assert.True(_wakeup.OnPolled(Session(GradingState.Pending)));
        Assert.False(_wakeup.OnPolled(Session(GradingState.Pending)));

        _clock.Now = Start + GradingWakeup.MinInterval - TimeSpan.FromSeconds(1);
        Assert.False(_wakeup.OnPolled(Session(GradingState.Pending)));

        _clock.Now = Start + GradingWakeup.MinInterval;
        Assert.True(_wakeup.OnPolled(Session(GradingState.Pending)));
    }

    [Fact]
    public void Concurrent_polls_wake_the_worker_once()
    {
        var wakeups = 0;

        Parallel.For(0, 100, _ =>
        {
            if (_wakeup.OnPolled(Session(GradingState.Pending)))
            {
                Interlocked.Increment(ref wakeups);
            }
        });

        Assert.Equal(1, wakeups);
    }
}
