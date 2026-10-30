using System.Text.Json.Serialization;

namespace TutorAI.Api.Data;

[JsonConverter(typeof(StringValueJsonConverter<SessionMode>))]
public readonly record struct SessionMode : IStringValue<SessionMode>
{
    public static readonly SessionMode Conversation = new("Conversation");
    public static readonly SessionMode Interview = new("Interview");

    private SessionMode(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<SessionMode> All { get; } = [Conversation, Interview];
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringValueJsonConverter<SessionStatus>))]
public readonly record struct SessionStatus : IStringValue<SessionStatus>
{
    public static readonly SessionStatus Active = new("Active");
    public static readonly SessionStatus Ended = new("Ended");

    private SessionStatus(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<SessionStatus> All { get; } = [Active, Ended];
    public override string ToString() => Value;
}

/// <summary>Where a session's background grading stands (Sessions.GradingState). NULL: never queued.</summary>
[JsonConverter(typeof(StringValueJsonConverter<GradingState>))]
public readonly record struct GradingState : IStringValue<GradingState>
{
    public static readonly GradingState Pending = new("Pending");
    public static readonly GradingState Running = new("Running");
    public static readonly GradingState Done = new("Done");
    public static readonly GradingState Failed = new("Failed");

    private GradingState(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<GradingState> All { get; } = [Pending, Running, Done, Failed];

    /// <summary>Queued or being graded now.</summary>
    public bool InProgress => this == Pending || this == Running;

    public override string ToString() => Value;
}

[JsonConverter(typeof(StringValueJsonConverter<TurnRole>))]
public readonly record struct TurnRole : IStringValue<TurnRole>
{
    public static readonly TurnRole User = new("user");
    public static readonly TurnRole Tutor = new("tutor");

    private TurnRole(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<TurnRole> All { get; } = [User, Tutor];
    public override string ToString() => Value;
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

public sealed class SessionRow
{
    public Guid Id { get; set; }
    public SessionMode Mode { get; set; } = SessionMode.Conversation;
    public string ScenarioJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    /// <summary>NULL for sessions created before sign-in existed.</summary>
    public int? UserId { get; set; }
    public GradingState? GradingState { get; set; }
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
    public TurnRole Role { get; set; } = TurnRole.User;
    public string Text { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public int? DurationMs { get; set; }
    public int WordCount { get; set; }
    public double? AccuracyScore { get; set; }
    public double? FluencyScore { get; set; }
    public double? ProsodyScore { get; set; }
    public string? PronunciationJson { get; set; }
    /// <summary>Why the browser's speech scores were dropped (see TurnScoreCheck); null when kept or none were sent.</summary>
    public string? ScoresRejected { get; set; }
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
    /// <summary>The grading prompt's version (ICefrGrader.PromptVersion); null for assessments made before it was recorded.</summary>
    public string? PromptVersion { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SessionListRow
{
    public Guid Id { get; set; }
    public SessionMode Mode { get; set; }
    public string ScenarioJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
    public GradingState? GradingState { get; set; }
    public string? Fluency { get; set; }
    public string? Grammar { get; set; }
    public string? Vocabulary { get; set; }
    public string? Interaction { get; set; }
    public string? Pronunciation { get; set; }
    public string? Overall { get; set; }
}
