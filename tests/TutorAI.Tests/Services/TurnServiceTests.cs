using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using static TutorAI.Tests.AzureSamples;

namespace TutorAI.Tests.Services;

/// <summary>The turn logic without HTTP, a database or Claude.</summary>
public class TurnServiceTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private const string Text = "Last summer we went to Porto";

    private readonly InMemorySessionRepository _repository = new();
    private readonly FixedClock _clock = new(Start.AddMinutes(1));

    private TurnService Service(ITutorChat? chat = null, SessionLimitsOptions? limits = null) =>
        new(_repository, chat ?? new ScriptedTutorChat("Hello!"), Options.Create(limits ?? new SessionLimitsOptions()), _clock,
            NullLogger<TurnService>.Instance);

    private SessionRow Session(SessionStatus? status = null) => _repository.Add(new SessionRow
    {
        Id = Guid.NewGuid(),
        UserId = 7,
        StartedAt = Start,
        ScenarioJson = """{"topic":"Travel","durationMinutes":10}""",
        Status = status ?? SessionStatus.Active,
    });

    private static Word[] SpokenWords() =>
    [
        new("last", 0.6, 0.3), new("summer", 0.95, 0.4), new("we", 1.4, 0.15),
        new("went", 1.6, 0.3), new("to", 1.95, 0.12), new("porto", 2.1, 0.45),
    ];

    [Fact]
    public async Task The_opening_request_stores_nothing_and_the_reply_is_streamed_and_stored()
    {
        var session = Session();
        var service = Service(new ScriptedTutorChat("Hi, ", "how are you?"));

        var start = await service.StartAsync(session, new LearnerTurn(null, null, null), CancellationToken.None);
        var streamed = new List<string>();
        var reply = await service.ReplyAsync(
            session, start.Turns, (text, _) => { streamed.Add(text); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Null(start.Rejection);
        Assert.Empty(start.Turns);
        Assert.Equal(["Hi, ", "how are you?"], streamed);
        Assert.Null(reply.Error);
        var stored = Assert.Single(_repository.Turns);
        Assert.Same(stored, reply.Turn);
        Assert.Equal((TurnRole.Tutor, "Hi, how are you?", 4, "fake-tutor", 500, 40),
            (stored.Role, stored.Text, stored.WordCount, stored.Model, stored.InputTokens, stored.OutputTokens));
    }

    [Fact]
    public async Task A_learner_turn_is_stored_with_its_speech_scores_and_passed_to_the_tutor()
    {
        var session = Session();
        var chat = new ScriptedTutorChat("Nice!");
        var service = Service(chat);

        var start = await service.StartAsync(
            session, new LearnerTurn($"  {Text} ", 3000, [Phrase(86, 90, 78, SpokenWords())]), CancellationToken.None);
        await service.ReplyAsync(session, start.Turns, (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Null(start.Rejection);
        var learner = _repository.Turns[0];
        Assert.Equal((TurnRole.User, Text, 6, 3000), (learner.Role, learner.Text, learner.WordCount, learner.DurationMs));
        Assert.Equal((86.0, 90.0, 78.0), (learner.AccuracyScore, learner.FluencyScore, learner.ProsodyScore));
        Assert.NotNull(learner.PronunciationJson);
        Assert.Null(learner.ScoresRejected);
        Assert.Same(learner, Assert.Single(chat.LastTurns!));
    }

    [Fact]
    public async Task Implausible_speech_scores_are_dropped_but_the_turn_is_kept()
    {
        var session = Session();
        // A high-scoring result for different words, sent with this turn's text.
        var other = SpokenWords().Select((w, i) => w with { Text = $"other{i}" }).ToArray();

        var start = await Service().StartAsync(
            session, new LearnerTurn(Text, 3000, [Phrase(99, 99, 99, other)]), CancellationToken.None);

        Assert.Null(start.Rejection);
        var learner = Assert.Single(_repository.Turns);
        Assert.Equal(Text, learner.Text);
        Assert.Contains("appear in the turn", learner.ScoresRejected);
        Assert.Null(learner.AccuracyScore);
        Assert.Null(learner.PronunciationJson);
    }

    [Fact]
    public async Task An_ended_session_refuses_turns()
    {
        var start = await Service().StartAsync(
            Session(SessionStatus.Ended), new LearnerTurn(Text, null, null), CancellationToken.None);

        Assert.Equal(new TurnRejection(StatusCodes.Status409Conflict, null, "The session has ended."), start.Rejection);
        Assert.Empty(_repository.Turns);
    }

    [Fact]
    public async Task Text_is_required_after_the_opening()
    {
        var session = Session();
        await _repository.AddTurnAsync(new TurnRow { SessionId = session.Id, Role = TurnRole.Tutor, Text = "Hi" }, CancellationToken.None);

        var start = await Service().StartAsync(session, new LearnerTurn("   ", null, null), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, start.Rejection?.StatusCode);
        Assert.Null(start.Rejection?.Code);
        Assert.Single(_repository.Turns);
    }

    [Fact]
    public async Task The_session_rules_apply()
    {
        var session = Session();
        _clock.Now = Start.AddMinutes(16); // 10 planned + 5 grace

        var late = await Service().StartAsync(session, new LearnerTurn(Text, null, null), CancellationToken.None);
        _clock.Now = Start.AddMinutes(1);
        var tooLong = await Service(limits: new SessionLimitsOptions { MaxTurnChars = 10 })
            .StartAsync(session, new LearnerTurn(Text, null, null), CancellationToken.None);

        Assert.Equal(SessionRules.TimeUp, late.Rejection?.Code);
        Assert.Equal(SessionRules.TurnTooLong, tooLong.Rejection?.Code);
        Assert.Empty(_repository.Turns);
    }

    [Fact]
    public async Task A_failing_reply_keeps_what_was_said_and_reports_an_error()
    {
        var session = Session();
        var service = Service(new ScriptedTutorChat("So, ", "tell me") { FailAfter = new HttpRequestException("overloaded") });

        var reply = await service.ReplyAsync(session, [], (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(TurnService.ReplyFailed, reply.Error);
        Assert.Equal("So, tell me", reply.Turn?.Text);
        Assert.Single(_repository.Turns);
    }

    [Fact]
    public async Task A_reply_that_fails_before_any_text_stores_nothing()
    {
        var service = Service(new ScriptedTutorChat { FailAfter = new HttpRequestException("down") });

        var reply = await service.ReplyAsync(Session(), [], (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(new TutorReply(null, TurnService.ReplyFailed), reply);
        Assert.Empty(_repository.Turns);
    }

    [Fact]
    public async Task An_interrupted_reply_keeps_what_was_said_without_an_error()
    {
        using var interrupt = new CancellationTokenSource();
        var chat = new ScriptedTutorChat("Well, ", "I think ", "that...") { AfterFragment = i => { if (i == 1) interrupt.Cancel(); } };

        var reply = await Service(chat).ReplyAsync(Session(), [], (_, _) => Task.CompletedTask, interrupt.Token);

        Assert.Null(reply.Error);
        Assert.Equal("Well, I think", reply.Turn?.Text);
    }
}
