namespace TutorAI.Api;

public sealed class AzureSpeechOptions
{
    public string? Key { get; set; }
    public string? Region { get; set; }
    /// <summary>The default accent's locale and voice, used when a session doesn't choose one.</summary>
    public string Locale { get; set; } = "en-US";
    public string Voice { get; set; } = "en-US-AvaMultilingualNeural";
    /// <summary>The accents a learner may choose, keyed by locale (the allowlist). See appsettings.json.</summary>
    public Dictionary<string, TutorAI.Api.Speech.AccentOptions> Accents { get; set; } = [];
}

public sealed class AnthropicOptions
{
    /// <summary>Optional. When empty the SDK resolves credentials itself (ANTHROPIC_API_KEY, profile, ...).</summary>
    public string? ApiKey { get; set; }
    public string ConversationModel { get; set; } = "claude-sonnet-5-5";
    public string GradingModel { get; set; } = "claude-opus-5-5";
}

public sealed class AuthOptions
{
    /// <summary>Secret, at least 32 characters. Key Vault secret Auth--JwtSigningKey.</summary>
    public string? JwtSigningKey { get; set; }
    public string Issuer { get; set; } = "TutorAI.Api";
    public string Audience { get; set; } = "TutorAI.Web";
    /// <summary>The audience of read-only MCP tokens; /api/* rejects them.</summary>
    public string McpAudience { get; set; } = "TutorAI.Mcp";
    public int McpTokenDays { get; set; } = 30;
    /// <summary>The web app swaps in a fresh token while the user is active, so this is effectively the idle timeout.</summary>
    public int TokenLifetimeMinutes { get; set; } = 15;

    public string AdminUsername { get; set; } = "admin";
    /// <summary>Secret. Key Vault secret Auth--AdminPassword. The admin account is not created while this is empty.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Failed sign-ins in a row that lock the account.</summary>
    public int LockoutThreshold { get; set; } = 5;
    /// <summary>The first lock lasts this long; each further failure doubles it, up to <see cref="LockoutMaxSeconds"/>.</summary>
    public int LockoutBaseSeconds { get; set; } = 60;
    public int LockoutMaxSeconds { get; set; } = 900;
    /// <summary>Sign-in attempts per minute across all users, against password spraying.</summary>
    public int LoginAttemptsPerMinute { get; set; } = 30;
}

/// <summary>Bounds on a single session, so one session can't run up unlimited Claude cost.</summary>
public sealed class SessionLimitsOptions
{
    public int MinDurationMinutes { get; set; } = 5;
    public int MaxDurationMinutes { get; set; } = 60;
    /// <summary>Turns are refused this long after the planned duration; the tutor closes the session at the planned time.</summary>
    public int GraceMinutes { get; set; } = 5;
    public int MaxUserTurns { get; set; } = 150;
    public int MaxTurnChars { get; set; } = 4000;
    public int MaxPhrasesPerTurn { get; set; } = 100;
    /// <summary>Applies to every request. Azure's detailed results with phoneme scores are roughly 10-20 KB per phrase.</summary>
    public long MaxRequestBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxScenarioFieldChars { get; set; } = 200;
    public int MaxJobDescriptionChars { get; set; } = 8000;
    /// <summary>Planned interview questions a session may carry (Scenario.Questions), and the length of each.</summary>
    public int MaxQuestions { get; set; } = 8;
    public int MaxQuestionChars { get; set; } = 300;
    /// <summary>Turn requests per user per minute. A real learner sends a few.</summary>
    public int TurnsPerMinute { get; set; } = 20;
}

public sealed class AssessmentOptions
{
    /// <summary>Below this many user words the session is reported as too short to grade.</summary>
    public int MinUserWords { get; set; } = 150;

    /// <summary>Attempts per grading before it is marked Failed. The SDK also retries each call twice.</summary>
    public int MaxGradingAttempts { get; set; } = 3;
    /// <summary>
    /// Wait before attempt 2, 3, ...; the last value repeats. Empty means 15 s, then 60 s. (No default values
    /// here: the configuration binder appends configured items to an array's existing ones.)
    /// </summary>
    public int[] RetryDelaySeconds { get; set; } = [];
    /// <summary>
    /// Must be greater than 0 and less than <see cref="StaleGradingMinutes"/> in seconds; the API refuses to start otherwise.
    /// </summary>
    public int GradingTimeoutSeconds { get; set; } = 300;
    /// <summary>A Running grading claimed longer ago than this is taken to be orphaned (the worker stopped) and claimed again.</summary>
    public int StaleGradingMinutes { get; set; } = 10;
    /// <summary>Gradings that run at once on one API instance.</summary>
    public int GradingConcurrency { get; set; } = 2;
    /// <summary>
    /// Cache the grader's system prompt. A cache entry lives 5 minutes, so this only pays off when gradings run
    /// back to back, as in calibration runs; off by default.
    /// </summary>
    public bool CacheGraderPrompt { get; set; }
}

/// <summary>The interview-prep agent (section Prep): its model and the limits on one run.</summary>
public sealed class PrepOptions
{
    public string Model { get; set; } = "claude-sonnet-5-5";
    /// <summary>Model calls per run. Each tool round trip is one call.</summary>
    public int MaxSteps { get; set; } = 8;
    /// <summary>Input plus output tokens per run, checked before each model call.</summary>
    public int MaxTokens { get; set; } = 60_000;
    public int MaxOutputTokensPerCall { get; set; } = 2048;
    public int RunTimeoutSeconds { get; set; } = 120;
    /// <summary>Runs a guest may start in any 24 hours. Admins are not limited.</summary>
    public int RunsPerDay { get; set; } = 3;
    /// <summary>Run requests per user per minute, for everyone.</summary>
    public int RunsPerMinute { get; set; } = 2;
    public int MinQuestions { get; set; } = 3;
    public int MaxQuestions { get; set; } = 8;
    public int MaxPhrasesPerRun { get; set; } = 10;
    public int MaxDrillsPerRun { get; set; } = 2;
    /// <summary>How far ahead a drill may be scheduled.</summary>
    public int MaxDrillDaysAhead { get; set; } = 30;
    /// <summary>Graded sessions get_weak_areas reads, newest first.</summary>
    public int WeakAreaSessions { get; set; } = 5;
}
