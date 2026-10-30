using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;
using TutorAI.Api.Practice;
using TutorAI.Tests.Integration;

namespace TutorAI.Tests.Services;

/// <summary>Ending and grading sessions without a database or Claude.</summary>
public class AssessmentServiceTests
{
    private const int MinWords = 10;

    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryPracticeRepository _practice = new();
    private readonly FakeUnitOfWorkFactory _database = new();
    private readonly GradingSignal _signal = new();

    private AssessmentService Service(ICefrGrader grader, params IGradingFollowUp[] extra) =>
        new(_sessions, _database, grader,
            [new PracticeFromGrading(_practice, new FixedClock(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero))), .. extra],
            _signal, Options.Create(new AssessmentOptions { MinUserWords = MinWords }));

    private SessionRow Session(int learnerWords, GradingState? grading = null)
    {
        var session = _sessions.Add(new SessionRow
        {
            Id = Guid.NewGuid(),
            UserId = 7,
            ScenarioJson = """{"topic":"Travel","accent":"en-GB"}""",
            GradingState = grading,
        });
        _sessions.Turns.Add(new TurnRow { SessionId = session.Id, Seq = 1, Role = TurnRole.Tutor, Text = "Hi", WordCount = 1 });
        _sessions.Turns.Add(new TurnRow
        {
            SessionId = session.Id, Seq = 2, Role = TurnRole.User, WordCount = learnerWords,
            Text = string.Join(' ', Enumerable.Repeat("word", learnerWords)),
        });
        return session;
    }

    [Fact]
    public async Task A_session_too_short_to_grade_is_assessed_at_once_without_Claude()
    {
        var grader = new FakeGrader();
        var session = Session(learnerWords: MinWords - 1);

        var result = await Service(grader).EndAsync(session.Id, regrade: false, CancellationToken.None);

        Assert.Equal(GradingState.Done, result?.GradingState);
        Assert.False(result?.Assessment?.Sufficient);
        Assert.Contains($"at least {MinWords}", result?.Assessment?.Reason);
        Assert.Equal(0, grader.Calls);
        Assert.True(Assert.Single(_database.Started).Committed);
        Assert.Equal((SessionStatus.Ended, GradingState.Done), (session.Status, session.GradingState));
    }

    [Fact]
    public async Task A_long_enough_session_is_queued_for_the_worker()
    {
        var grader = new FakeGrader();
        var session = Session(learnerWords: MinWords);

        var result = await Service(grader).EndAsync(session.Id, regrade: false, CancellationToken.None);

        Assert.Equal(new EndResult(null, GradingState.Pending), result);
        Assert.Equal(0, grader.Calls);
        Assert.Empty(_sessions.Assessments);
    }

    [Fact]
    public async Task Ending_while_grading_is_in_progress_reports_the_state_without_queueing_again()
    {
        var session = Session(learnerWords: MinWords, GradingState.Running);

        var result = await Service(new FakeGrader()).EndAsync(session.Id, regrade: true, CancellationToken.None);

        Assert.Equal(new EndResult(null, GradingState.Running), result);
        Assert.Empty(_database.Started);
    }

    [Fact]
    public async Task Grading_saves_the_assessment_and_practice_items_together()
    {
        var grader = new FakeGrader { Corrections = [new("I go yesterday", "I went yesterday", "Past tense.")] };
        var session = Session(learnerWords: MinWords);

        await Service(grader).GradeAsync(session.Id, CancellationToken.None);

        var work = Assert.Single(_database.Started);
        Assert.True(work.Committed);
        var assessment = _sessions.Assessments[session.Id];
        Assert.Equal(("B1", FakeGrader.ModelName, "fake-1", 1200), (assessment.Overall, assessment.Model, assessment.PromptVersion, assessment.InputTokens));
        var batch = Assert.Single(_practice.Saved);
        Assert.Equal((7, session.Id, "en-GB"), (batch.UserId, batch.SessionId, batch.Locale));
        Assert.Equal("I went yesterday", Assert.Single(batch.Items).Target);
    }

    [Fact]
    public async Task A_failed_grading_saves_nothing()
    {
        var session = Session(learnerWords: MinWords);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Service(new FakeGrader { FailFirst = 1 }).GradeAsync(session.Id, CancellationToken.None));

        Assert.Empty(_database.Started);
        Assert.Empty(_sessions.Assessments);
        Assert.Empty(_practice.Saved);
    }

    [Fact]
    public async Task A_session_without_an_owner_gets_no_practice_items()
    {
        var session = Session(learnerWords: MinWords);
        session.UserId = null;

        await Service(new FakeGrader { Corrections = [new("a", "b", "c")] }).GradeAsync(session.Id, CancellationToken.None);

        Assert.True(_sessions.Assessments.ContainsKey(session.Id));
        Assert.Empty(_practice.Saved);
    }

    [Fact]
    public async Task A_failing_follow_up_saves_neither_it_nor_the_assessment()
    {
        var session = Session(learnerWords: MinWords);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new FakeGrader { Corrections = [new("a", "b", "c")] }, new FailingFollowUp()).GradeAsync(session.Id, CancellationToken.None));

        Assert.False(Assert.Single(_database.Started).Committed);
        Assert.Empty(_sessions.Assessments);
        Assert.Empty(_practice.Saved);
    }

    [Fact]
    public async Task Follow_ups_run_only_for_sessions_that_were_graded()
    {
        var followUp = new RecordingFollowUp();
        var tooShort = Session(learnerWords: MinWords - 1);
        var graded = Session(learnerWords: MinWords);

        await Service(new FakeGrader(), followUp).GradeAsync(tooShort.Id, CancellationToken.None);
        await Service(new FakeGrader(), followUp).GradeAsync(graded.Id, CancellationToken.None);

        Assert.Equal(graded.Id, Assert.Single(followUp.Seen).Session.Id);
        Assert.Equal("B1", followUp.Seen[0].Grading.Fluency.Level);
    }

    private sealed class FailingFollowUp : IGradingFollowUp
    {
        public Task OnGradedAsync(GradedSession graded, IUnitOfWork work, CancellationToken ct) =>
            throw new InvalidOperationException("follow-up failed");
    }

    private sealed class RecordingFollowUp : IGradingFollowUp
    {
        public List<GradedSession> Seen { get; } = [];

        public Task OnGradedAsync(GradedSession graded, IUnitOfWork work, CancellationToken ct)
        {
            Seen.Add(graded);
            return Task.CompletedTask;
        }
    }
}
