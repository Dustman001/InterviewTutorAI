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
    int DurationMinutes = 10);

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
    public string? Fluency { get; set; }
    public string? Grammar { get; set; }
    public string? Vocabulary { get; set; }
    public string? Interaction { get; set; }
    public string? Pronunciation { get; set; }
    public string? Overall { get; set; }
}
