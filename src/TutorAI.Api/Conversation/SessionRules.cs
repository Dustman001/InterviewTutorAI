using TutorAI.Api.Data;

namespace TutorAI.Api.Conversation;

/// <summary>
/// Why a turn was refused. <see cref="Code"/> lets the web app react, e.g. end the session on <c>time_up</c>;
/// null when there is nothing for it to act on.
/// </summary>
public sealed record TurnRejection(int StatusCode, string? Code, string Error);

/// <summary>The bounds from <see cref="SessionLimitsOptions"/>, checked before any Claude call.</summary>
public static class SessionRules
{
    public const string TimeUp = "time_up";
    public const string TurnLimit = "turn_limit";
    public const string TurnTooLong = "turn_too_long";

    /// <summary>Returns a message for the learner when the scenario is out of bounds, otherwise null.</summary>
    public static string? ScenarioProblem(Scenario scenario, SessionLimitsOptions limits)
    {
        if (scenario.DurationMinutes < limits.MinDurationMinutes || scenario.DurationMinutes > limits.MaxDurationMinutes)
        {
            return $"Sessions last {limits.MinDurationMinutes} to {limits.MaxDurationMinutes} minutes.";
        }

        (string Name, string? Value)[] fields =
        [
            ("Topic", scenario.Topic), ("Role", scenario.Role), ("Seniority", scenario.Seniority),
            ("Interview type", scenario.InterviewType), ("Difficulty", scenario.Difficulty),
        ];
        foreach (var (name, value) in fields)
        {
            if (value?.Length > limits.MaxScenarioFieldChars)
            {
                return $"{name} can be at most {limits.MaxScenarioFieldChars} characters.";
            }
        }

        return scenario.JobDescription?.Length > limits.MaxJobDescriptionChars
            ? $"The job description can be at most {limits.MaxJobDescriptionChars} characters."
            : null;
    }

    /// <param name="turns">The session's stored turns, before this one.</param>
    /// <param name="text">The learner's text, or null for the opening request.</param>
    public static TurnRejection? CheckTurn(
        SessionRow session, Scenario scenario, IReadOnlyList<TurnRow> turns, string? text, int phraseCount,
        DateTime utcNow, SessionLimitsOptions limits)
    {
        if (text?.Length > limits.MaxTurnChars)
        {
            return new(StatusCodes.Status400BadRequest, TurnTooLong, $"A turn can be at most {limits.MaxTurnChars} characters.");
        }

        if (phraseCount > limits.MaxPhrasesPerTurn)
        {
            return new(StatusCodes.Status400BadRequest, TurnTooLong, $"A turn can have at most {limits.MaxPhrasesPerTurn} recognized phrases.");
        }

        var hardStop = DateTime.SpecifyKind(session.StartedAt, DateTimeKind.Utc)
            .AddMinutes(ScenarioPrompts.PlannedMinutes(scenario) + limits.GraceMinutes);
        if (utcNow >= hardStop)
        {
            return new(StatusCodes.Status409Conflict, TimeUp, "The time for this session is up.");
        }

        if (text is not null && turns.Count(t => t.Role == TurnRole.User) >= limits.MaxUserTurns)
        {
            return new(StatusCodes.Status409Conflict, TurnLimit, $"This session has reached its limit of {limits.MaxUserTurns} turns.");
        }

        return null;
    }
}
