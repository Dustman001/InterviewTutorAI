using Microsoft.Extensions.Options;

namespace TutorAI.Api.Speech;

/// <param name="Prosody">Whether to request prosody assessment: Azure scores it for en-US only.</param>
public sealed record SpeechToken(string Token, string Region, string Locale, string Voice, bool Prosody);

/// <summary>Exchanges the Azure Speech key for a short-lived token so the key never reaches the browser.</summary>
public sealed class SpeechTokenService(HttpClient http, IOptions<AzureSpeechOptions> options)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.Key) && !string.IsNullOrWhiteSpace(options.Value.Region);

    /// <param name="accent">The session's accent; null for the default.</param>
    public async Task<SpeechToken> IssueAsync(CancellationToken ct, Accent? accent = null)
    {
        accent ??= AccentCatalog.Default(options.Value);
        var speech = options.Value;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://{speech.Region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
        request.Headers.Add("Ocp-Apim-Subscription-Key", speech.Key);
        request.Content = new ByteArrayContent([]);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return new SpeechToken(
            await response.Content.ReadAsStringAsync(ct), speech.Region!, accent.Id, accent.Voice, accent.ProsodyAssessed);
    }
}
