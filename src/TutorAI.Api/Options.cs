namespace TutorAI.Api;

public sealed class AzureSpeechOptions
{
    public string? Key { get; set; }
    public string? Region { get; set; }
    public string Locale { get; set; } = "en-US";
    public string Voice { get; set; } = "en-US-AvaMultilingualNeural";
}

public sealed class AnthropicOptions
{
    /// <summary>Optional. When empty the SDK resolves credentials itself (ANTHROPIC_API_KEY, profile, ...).</summary>
    public string? ApiKey { get; set; }
    public string ConversationModel { get; set; } = "claude-sonnet-5-5";
    public string GradingModel { get; set; } = "claude-opus-5-5";
}

public sealed class AssessmentOptions
{
    /// <summary>Below this many user words the session is reported as too short to grade.</summary>
    public int MinUserWords { get; set; } = 150;
}
