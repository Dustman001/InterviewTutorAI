using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class AccentFlowTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Accents_come_from_the_allowlist_and_unknown_ones_are_refused()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

        var accents = await client.GetFromJsonAsync<List<JsonElement>>("/api/speech/accents");
        var unknown = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10, accent = "en-NZ" } });
        var british = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10, accent = "en-gb" } });

        Assert.Equal("en-US", accents![0].GetProperty("id").GetString());
        Assert.Contains(accents, a => a.GetProperty("id").GetString() == "en-GB" && !a.GetProperty("prosody").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("en-GB", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, british.StatusCode);
        // Stored with the allowlist's spelling.
        Assert.Equal("en-GB", (await british.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario").GetProperty("accent").GetString());
    }

    [E2EFact(azureSpeech: true)]
    public async Task The_speech_token_follows_the_sessions_accent_and_only_for_its_owner()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var guest = await TestApi.SignInGuestAsync(api);
        var british = await CreateAsync(admin, "en-GB");
        var american = await CreateAsync(admin, null);

        var gb = await admin.GetFromJsonAsync<JsonElement>($"/api/speech/token?sessionId={british}");
        var us = await admin.GetFromJsonAsync<JsonElement>($"/api/speech/token?sessionId={american}");

        Assert.Equal(("en-GB", "en-GB-SoniaNeural", false), (Text(gb, "locale"), Text(gb, "voice"), gb.GetProperty("prosody").GetBoolean()));
        Assert.Equal(("en-US", true), (Text(us, "locale"), us.GetProperty("prosody").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/speech/token?sessionId={british}")).StatusCode);
    }

    [E2EFact(azureSpeech: true)]
    public async Task British_speech_is_recognised_in_en_GB_and_graded_on_accuracy_alone()
    {
        // A British voice, recognised and assessed with the en-GB locale and no prosody, as the browser does.
        var (recognized, phrases) = await SessionFlowTests.SpeakAndAssessAsync(
            "I've booked a flat near the river for the holiday, and we'll take the lift to the fourth floor.",
            voice: "en-GB-SoniaNeural", locale: "en-GB", prosody: false);

        Assert.Contains("flat", recognized, StringComparison.OrdinalIgnoreCase);
        var metrics = AssessmentService.ComputeMetrics(
        [
            new TurnRow
            {
                Role = TurnRoles.User,
                Text = recognized,
                WordCount = recognized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                PronunciationJson = JsonSerializer.Serialize(phrases),
            },
        ]);

        Assert.NotNull(metrics.Pronunciation.Accuracy);
        Assert.Null(metrics.Pronunciation.Prosody);
        Assert.Equal(metrics.Pronunciation.Accuracy, metrics.Pronunciation.Composite);
        Assert.NotNull(metrics.Fluency.WordsPerMinute);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string? accent)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10, accent } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();
}
