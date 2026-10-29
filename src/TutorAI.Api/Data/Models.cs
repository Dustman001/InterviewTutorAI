using System.Text.Json;

namespace TutorAI.Api.Data;

public static class SessionModes
{
    public const string Conversation = "Conversation";
    public const string Interview = "Interview";

    public static bool IsValid(string? mode) => mode is Conversation or Interview;
}

public static class SessionStatus
{
    public const string Active = "Active";
    public const string Ended = "Ended";
}

/// <summary>Where a session's background grading stands (Sessions.GradingState). NULL: never queued.</summary>
public static class GradingStates
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";

    public static bool InProgress(string? state) => state is Pending or Running;
}

public static class TurnRoles
{
    public const string User = "user";
    public const string Tutor = "tutor";
}

public sealed record Scenario(
    string? Topic = null,
    string? Role = null,
    string? Seniority = null,
    string? InterviewType = null,
    string? JobDescription = null,
    string? Difficulty = null,
    int DurationMinutes = 10,
    /// <summary>A locale from AzureSpeech:Accents, e.g. en-GB. Null: the default accent.</summary>
    string? Accent = null);

public sealed record CreateSessionRequest(string Mode, Scenario? Scenario);

/// <param name="Phrases">Raw Azure Speech detailed-result JSON, one object per recognized phrase of the turn.</param>
public sealed record TurnRequest(string? Text, int? DurationMs, JsonElement[]? Phrases);

public sealed class SessionRow
{
    public Guid Id { get; set; }
    public string Mode { get; set; } = "";
    public string ScenarioJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Status { get; set; } = SessionStatus.Active;
    /// <summary>NULL for sessions created before sign-in existed.</summary>
    public int? UserId { get; set; }
    public string? GradingState { get; set; }
    public int GradingAttempts { get; set; }
    public string? GradingError { get; set; }
    public DateTime? GradingStartedAt { get; set; }
    public DateTime? GradingNextAttemptAt { get; set; }
}

public sealed class TurnRow
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public int Seq { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public int? DurationMs { get; set; }
    public int WordCount { get; set; }
    public double? AccuracyScore { get; set; }
    public double? FluencyScore { get; set; }
    public double? ProsodyScore { get; set; }
    public string? PronunciationJson { get; set; }
    /// <summary>Tutor turns: the Claude call that produced the reply.</summary>
    public string? Model { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
}

public sealed class AssessmentRow
{
    public Guid SessionId { get; set; }
    public string? Fluency { get; set; }
    public string? Grammar { get; set; }
    public string? Vocabulary { get; set; }
    public string? Interaction { get; set; }
    public string? Pronunciation { get; set; }
    public string? Overall { get; set; }
    public string MetricsJson { get; set; } = "{}";
    public string DetailJson { get; set; } = "{}";
    public string? Model { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SessionListRow
{
    public Guid Id { get; set; }
    public string Mode { get; set; } = "";
    public string ScenarioJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Status { get; set; } = "";
    public string? GradingState { get; set; }
    public string? Fluency { get; set; }
    public string? Grammar { get; set; }
    public string? Vocabulary { get; set; }
    public string? Interaction { get; set; }
    public string? Pronunciation { get; set; }
    public string? Overall { get; set; }
}
