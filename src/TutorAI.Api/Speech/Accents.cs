namespace TutorAI.Api.Speech;

/// <summary>One English variety a learner can practise: the recognition locale, the tutor's voice and a prompt note.</summary>
public sealed class AccentOptions
{
    public string Label { get; set; } = "";
    public string Voice { get; set; } = "";
    /// <summary>Added to the tutor prompt so its wording matches the voice, e.g. "Use British English vocabulary".</summary>
    public string? PromptNote { get; set; }
}

/// <param name="Id">The locale, e.g. en-GB. Stored in the session's scenario.</param>
public sealed record Accent(string Id, string Label, string Voice, string? PromptNote)
{
    /// <summary>
    /// Azure pronunciation assessment scores prosody for en-US only (Azure Speech docs, "Use pronunciation
    /// assessment"). Other locales get accuracy and fluency, so their pronunciation level rests on accuracy alone.
    /// </summary>
    public bool ProsodyAssessed => Id == "en-US";
}

public static class AccentCatalog
{
    /// <summary>The allowlist from AzureSpeech:Accents; the default (AzureSpeech:Locale) comes first.</summary>
    public static IReadOnlyList<Accent> All(AzureSpeechOptions options)
    {
        var configured = options.Accents
            .Where(a => !string.IsNullOrWhiteSpace(a.Value.Voice))
            .Select(a => new Accent(a.Key, string.IsNullOrWhiteSpace(a.Value.Label) ? a.Key : a.Value.Label, a.Value.Voice, a.Value.PromptNote))
            .ToList();
        var fallback = configured.FirstOrDefault(a => a.Id == options.Locale)
            ?? new Accent(options.Locale, options.Locale, options.Voice, null);
        return [fallback, .. configured.Where(a => a.Id != fallback.Id).OrderBy(a => a.Label, StringComparer.Ordinal)];
    }

    public static Accent Default(AzureSpeechOptions options) => All(options)[0];

    /// <summary>The accent with this id, the default for null, or null when it isn't allowed.</summary>
    public static Accent? Find(AzureSpeechOptions options, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Default(options)
            : All(options).FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
