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

public sealed class AuthOptions
{
    /// <summary>Secret, at least 32 characters. Key Vault secret Auth--JwtSigningKey.</summary>
    public string? JwtSigningKey { get; set; }
    public string Issuer { get; set; } = "TutorAI.Api";
    public string Audience { get; set; } = "TutorAI.Web";
    /// <summary>The web app swaps in a fresh token while the user is active, so this is effectively the idle timeout.</summary>
    public int TokenLifetimeMinutes { get; set; } = 15;

    public string AdminUsername { get; set; } = "admin";
    /// <summary>Secret. Key Vault secret Auth--AdminPassword. The admin account is not created while this is empty.</summary>
    public string? AdminPassword { get; set; }
}

public sealed class AssessmentOptions
{
    /// <summary>Below this many user words the session is reported as too short to grade.</summary>
    public int MinUserWords { get; set; } = 150;
}
