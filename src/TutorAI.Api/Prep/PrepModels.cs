using System.ComponentModel;
using System.Text.Json.Serialization;
using TutorAI.Api.Data;

namespace TutorAI.Api.Prep;

[JsonConverter(typeof(StringValueJsonConverter<PrepRunStatus>))]
public readonly record struct PrepRunStatus : IStringValue<PrepRunStatus>
{
    public static readonly PrepRunStatus Running = new("Running");
    public static readonly PrepRunStatus Completed = new("Completed");
    /// <summary>Hit the step, token or time limit. What it already did is kept.</summary>
    public static readonly PrepRunStatus Stopped = new("Stopped");
    public static readonly PrepRunStatus Failed = new("Failed");

    private PrepRunStatus(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<PrepRunStatus> All { get; } = [Running, Completed, Stopped, Failed];
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringValueJsonConverter<PrepActionStatus>))]
public readonly record struct PrepActionStatus : IStringValue<PrepActionStatus>
{
    /// <summary>A reversible write the tool made straight away.</summary>
    public static readonly PrepActionStatus Applied = new("Applied");
    /// <summary>An irreversible write waiting for the learner.</summary>
    public static readonly PrepActionStatus PendingConfirmation = new("PendingConfirmation");
    public static readonly PrepActionStatus Confirmed = new("Confirmed");
    public static readonly PrepActionStatus Rejected = new("Rejected");

    private PrepActionStatus(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<PrepActionStatus> All { get; } = [Applied, PendingConfirmation, Confirmed, Rejected];
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringValueJsonConverter<DrillStatus>))]
public readonly record struct DrillStatus : IStringValue<DrillStatus>
{
    public static readonly DrillStatus Scheduled = new("Scheduled");
    public static readonly DrillStatus Started = new("Started");
    public static readonly DrillStatus Cancelled = new("Cancelled");

    private DrillStatus(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<DrillStatus> All { get; } = [Scheduled, Started, Cancelled];
    public override string ToString() => Value;
}

/// <summary>One interview question the agent chose, and why.</summary>
public sealed record PlannedQuestion(
    [property: Description("The question as the interviewer would say it.")] string Question,
    [property: Description("What it tests, e.g. behavioural, technical, motivation, situational.")] string Focus,
    [property: Description("One sentence on why it suits this job and this learner.")] string Why);

public sealed class PrepRunRow
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string JobDescription { get; set; } = "";
    public string? Role { get; set; }
    public string? Seniority { get; set; }
    public PrepRunStatus Status { get; set; } = PrepRunStatus.Running;
    public string? StopReason { get; set; }
    public string? Summary { get; set; }
    public string? QuestionsJson { get; set; }
    public string? Model { get; set; }
    public int Steps { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class PrepActionRow
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Tool { get; set; } = "";
    public string ArgsJson { get; set; } = "{}";
    public PrepActionStatus Status { get; set; } = PrepActionStatus.Applied;
    public string? ResultJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public sealed class DrillRow
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public Guid? RunId { get; set; }
    public DateTime ScheduledFor { get; set; }
    public string ScenarioJson { get; set; } = "{}";
    public DrillStatus Status { get; set; } = DrillStatus.Scheduled;
    public Guid? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>How a run ended, as the runner records it.</summary>
public sealed record PrepRunOutcome(
    PrepRunStatus Status, string? StopReason, string? Summary, string Model, int Steps, long InputTokens, long OutputTokens,
    DateTime CompletedAt);
