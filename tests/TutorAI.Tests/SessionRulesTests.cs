using System.Text.Json;
using Anthropic.Models.Messages;
using TutorAI.Api;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;

namespace TutorAI.Tests;

public class SessionRulesTests
{
    private static readonly SessionLimitsOptions Limits = new();
    private static readonly DateTime Start = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(100_000, false)]
    public void Duration_must_be_within_bounds(int minutes, bool valid)
    {
        var problem = SessionRules.ScenarioProblem(new Scenario(DurationMinutes: minutes), Limits);

        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void Long_scenario_text_is_refused()
    {
        Assert.Contains("Topic", SessionRules.ScenarioProblem(new Scenario(Topic: new string('x', 201)), Limits));
        Assert.Contains("job description", SessionRules.ScenarioProblem(new Scenario(JobDescription: new string('x', 8001)), Limits));
        Assert.Null(SessionRules.ScenarioProblem(new Scenario(Topic: "Travel", JobDescription: new string('x', 8000)), Limits));
    }

    [Fact]
    public void Turn_text_and_phrase_count_are_bounded()
    {
        var session = Session(minutes: 10);

        var tooLong = Check(session, [], new string('x', Limits.MaxTurnChars + 1), Start.AddMinutes(1));
        var tooManyPhrases = Check(session, [], "Hello.", Start.AddMinutes(1), phrases: Limits.MaxPhrasesPerTurn + 1);

        Assert.Equal((400, SessionRules.TurnTooLong), (tooLong!.StatusCode, tooLong.Code));
        Assert.Equal((400, SessionRules.TurnTooLong), (tooManyPhrases!.StatusCode, tooManyPhrases.Code));
        Assert.Null(Check(session, [], new string('x', Limits.MaxTurnChars), Start.AddMinutes(1), phrases: Limits.MaxPhrasesPerTurn));
    }

    [Fact]
    public void Turns_stop_after_the_planned_time_plus_grace()
    {
        var session = Session(minutes: 10);

        Assert.Null(Check(session, [], "Still here.", Start.AddMinutes(14).AddSeconds(59)));
        var late = Check(session, [], "Still here.", Start.AddMinutes(15));
        Assert.Equal((409, SessionRules.TimeUp), (late!.StatusCode, late.Code));
        // The opening request is refused too, so an abandoned session can't be restarted later.
        Assert.Equal(SessionRules.TimeUp, Check(session, [], null, Start.AddHours(2))!.Code);
    }

    [Fact]
    public void A_stored_out_of_range_duration_falls_back_to_ten_minutes()
    {
        var session = Session(minutes: 100_000);

        Assert.Equal(SessionRules.TimeUp, Check(session, [], "Hi.", Start.AddMinutes(15))!.Code);
    }

    [Fact]
    public void Learner_turns_are_capped_but_the_opening_request_is_not()
    {
        var session = Session(minutes: 10);
        var limits = new SessionLimitsOptions { MaxUserTurns = 2 };
        TurnRow[] turns = [Turn(TurnRoles.Tutor, 0), Turn(TurnRoles.User, 1), Turn(TurnRoles.Tutor, 1), Turn(TurnRoles.User, 2)];

        var refused = SessionRules.CheckTurn(session, Scenario(session), turns, "Third.", 0, Start.AddMinutes(3), limits);

        Assert.Equal((409, SessionRules.TurnLimit), (refused!.StatusCode, refused.Code));
        Assert.Null(SessionRules.CheckTurn(session, Scenario(session), turns[..2], "Second.", 0, Start.AddMinutes(3), limits));
    }

    [Fact]
    public void Time_up_note_goes_on_learner_turns_started_after_the_planned_end_only()
    {
        var session = Session(minutes: 10);
        TurnRow[] turns =
        [
            Turn(TurnRoles.Tutor, 0), Turn(TurnRoles.User, 9), Turn(TurnRoles.Tutor, 9),
            Turn(TurnRoles.User, 10), Turn(TurnRoles.Tutor, 11), Turn(TurnRoles.User, 12),
        ];

        var messages = TutorChat.BuildMessages(session, Scenario(session), turns);

        Assert.Equal(ScenarioPrompts.Kickoff, Text(messages[0]));
        Assert.Equal(
            [Role.User, Role.Assistant, Role.User, Role.Assistant, Role.User, Role.Assistant, Role.User],
            messages.Select(m => (Role)m.Role));
        Assert.DoesNotContain(ScenarioPrompts.TimeUpNote, Text(messages[2]));
        Assert.EndsWith(ScenarioPrompts.TimeUpNote, Text(messages[4]));
        Assert.DoesNotContain(ScenarioPrompts.TimeUpNote, Text(messages[5]));
        Assert.EndsWith(ScenarioPrompts.TimeUpNote, Text(messages[6]));
    }

    [Fact]
    public void Earlier_turns_render_identically_on_later_requests_so_the_cache_stays_valid()
    {
        var session = Session(minutes: 10);
        List<TurnRow> turns = [Turn(TurnRoles.Tutor, 0), Turn(TurnRoles.User, 11)];

        var before = TutorChat.BuildMessages(session, Scenario(session), turns).Select(Text).ToList();
        turns.AddRange([Turn(TurnRoles.Tutor, 11), Turn(TurnRoles.User, 12)]);
        var after = TutorChat.BuildMessages(session, Scenario(session), turns).Select(Text).ToList();

        Assert.Equal(before, after.Take(before.Count));
    }

    private static TurnRejection? Check(SessionRow session, TurnRow[] turns, string? text, DateTime now, int phrases = 0) =>
        SessionRules.CheckTurn(session, Scenario(session), turns, text, phrases, now, Limits);

    private static SessionRow Session(int minutes) => new()
    {
        Id = Guid.NewGuid(),
        Mode = SessionModes.Conversation,
        ScenarioJson = JsonSerializer.Serialize(new Scenario(Topic: "Travel", DurationMinutes: minutes), JsonSerializerOptions.Web),
        // Stored rows come back from SQL Server without a kind.
        StartedAt = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified),
    };

    private static Scenario Scenario(SessionRow session) =>
        JsonSerializer.Deserialize<Scenario>(session.ScenarioJson, JsonSerializerOptions.Web)!;

    private static TurnRow Turn(string role, int minute) => new()
    {
        Role = role,
        Text = $"{role} at minute {minute}",
        StartedAt = DateTime.SpecifyKind(Start.AddMinutes(minute), DateTimeKind.Unspecified),
    };

    private static string Text(MessageParam message) =>
        message.Content.TryPickString(out var text) ? text : throw new InvalidOperationException("Expected text content.");
}
