using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.PronunciationAssessment;
using Microsoft.Extensions.DependencyInjection;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;
using static TutorAI.Tests.AzureSamples;

namespace TutorAI.Tests.Integration;

/// <summary>Practice drills against the real database: items from grading (fake grader), attempts and their schedule.</summary>
[Trait("Category", "Integration")]
public sealed class PracticeFlowTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private const string Corrected = "We went to the market every morning.";

    private static readonly Correction PastSimple = new("we was going to the market", Corrected, "Use the past simple for finished, repeated actions.");

    [E2EFact]
    public async Task Grading_adds_corrections_and_weak_words_and_a_repeat_merges_into_the_same_item()
    {
        var grader = new FakeGrader { Corrections = [PastSimple] };
        await using var api = CreateApi(grader);
        var client = await SignInAsync(api);

        var first = await CreateGradedSessionAsync(api, client);
        var due = await DueAsync(client);

        Assert.Equal(2, due.GetProperty("dueCount").GetInt32());
        var items = due.GetProperty("items").EnumerateArray().ToList();
        var correction = items.Single(i => i.GetProperty("kind").GetString() == "correction");
        Assert.Equal(Corrected, correction.GetProperty("target").GetString());
        Assert.Equal(PastSimple.Original, correction.GetProperty("original").GetString());
        Assert.Equal(first, correction.GetProperty("sessionId").GetGuid());
        var word = items.Single(i => i.GetProperty("kind").GetString() == "word");
        Assert.Equal("thought", word.GetProperty("target").GetString());

        // Progress made on the item, then the same correction comes up in another session: back to box 0, due.
        using (var db = database.Open())
        {
            db.Execute("UPDATE dbo.PracticeItems SET Box = 2, DueAt = DATEADD(day, 5, SYSUTCDATETIME())");
        }

        var second = await CreateGradedSessionAsync(api, client);
        Assert.Equal(2, (await DueAsync(client)).GetProperty("dueCount").GetInt32());
        Assert.Equal(2, Count());
        using (var db = database.Open())
        {
            Assert.All(
                db.Query<(byte Box, Guid SessionId)>("SELECT Box, SessionId FROM dbo.PracticeItems"),
                row => Assert.Equal(((byte)0, second), row));

            // Grading that session again keeps the progress made since.
            db.Execute("UPDATE dbo.PracticeItems SET Box = 3, DueAt = DATEADD(day, 5, SYSUTCDATETIME())");
        }

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/sessions/{second}/end?regrade=true", null)).StatusCode);
        await WaitForGradingAsync(client, second, expectedCalls: 3, grader);
        using (var db = database.Open())
        {
            Assert.All(db.Query<byte>("SELECT Box FROM dbo.PracticeItems"), box => Assert.Equal(3, box));
            Assert.Equal("fake-1", db.ExecuteScalar<string>("SELECT PromptVersion FROM dbo.Assessments WHERE SessionId = @second", new { second }));
        }

        Assert.Equal(0, (await DueAsync(client)).GetProperty("dueCount").GetInt32());
        DeleteAll();
    }

    [E2EFact]
    public async Task An_attempt_is_scored_on_the_server_and_rescheduled()
    {
        await using var api = CreateApi(new FakeGrader { Corrections = [PastSimple] });
        var learner = await SignInAsync(api);
        var other = await TestApi.SignInGuestAsync(api);
        await CreateGradedSessionAsync(api, learner);
        var id = (await DueAsync(learner)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == "correction").GetProperty("id").GetInt64();

        // A good reading: every word around 95.
        var good = await AttemptAsync(learner, id, Reading(Corrected, accuracy: 95));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var result = await good.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.Equal(95, result.GetProperty("score").GetDouble());
        var item = result.GetProperty("item");
        Assert.Equal((1, 1), (item.GetProperty("box").GetInt32(), item.GetProperty("attempts").GetInt32()));
        Assert.InRange(item.GetProperty("dueAt").GetDateTime(), DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
        Assert.DoesNotContain(
            (await DueAsync(learner)).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == id);

        // A poor one sends it back to box 0, due now; the best score stays.
        var poor = await (await AttemptAsync(learner, id, Reading(Corrected, accuracy: 50))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(poor.GetProperty("passed").GetBoolean());
        Assert.Equal((0, 95.0), (poor.GetProperty("item").GetProperty("box").GetInt32(), poor.GetProperty("item").GetProperty("bestScore").GetDouble()));

        // Reading something else, or another user's item, is refused.
        var mismatch = await AttemptAsync(learner, id, Reading("Hello there, how are you today?", accuracy: 100));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("doesn't match", await mismatch.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await AttemptAsync(other, id, Reading(Corrected, 95))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/practice/{id}")).StatusCode);
        Assert.Equal(0, (await DueAsync(other)).GetProperty("dueCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await learner.DeleteAsync($"/api/practice/{id}")).StatusCode);
        Assert.Equal(1, Count());
        DeleteAll();
    }

    [E2EFact(azureSpeech: true)]
    public async Task A_real_reading_assessed_by_azure_against_the_target_passes()
    {
        await using var api = CreateApi(new FakeGrader { Corrections = [PastSimple] });
        var learner = await SignInAsync(api);
        await CreateGradedSessionAsync(api, learner);
        var id = (await DueAsync(learner)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == "correction").GetProperty("id").GetInt64();

        // As the practice page does: scripted assessment with the target as reference text and miscue detection.
        var phrases = await ReadAloudAsync(Corrected);
        var response = await AttemptAsync(learner, id, phrases);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("passed").GetBoolean(), result.ToString());
        Assert.Equal(7, result.GetProperty("words").GetArrayLength());

        // A different sentence read against the same target is refused.
        var wrong = await AttemptAsync(learner, id, await ReadAloudAsync("I would like a cup of tea please.", reference: Corrected));
        Assert.True(wrong.StatusCode == HttpStatusCode.BadRequest || !(await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passed").GetBoolean());
        DeleteAll();
    }

    [E2EFact(azureSpeech: true)]
    public async Task The_speech_token_takes_an_accent_and_falls_back_to_the_default()
    {
        await using var api = CreateApi(new FakeGrader());
        var client = await SignInAsync(api);

        var gb = await client.GetFromJsonAsync<JsonElement>("/api/speech/token?accent=en-GB");

        Assert.Equal("en-GB-SoniaNeural", gb.GetProperty("voice").GetString());
        var unknown = await client.GetFromJsonAsync<JsonElement>("/api/speech/token?accent=fr-FR");
        Assert.Equal(("en-US", "en-US-AvaMultilingualNeural"), (unknown.GetProperty("locale").GetString(), unknown.GetProperty("voice").GetString()));
    }

    [E2EFact]
    public async Task Tampered_speech_scores_are_dropped_and_the_turn_kept()
    {
        // An unusable key: the tutor's reply fails fast, after the learner's turn is stored.
        await using var api = TestApi.CreateWithServices(
            database.ConnectionString, _ => { }, ("Anthropic:ApiKey", "sk-ant-invalid"));
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var sessionId = await CreateSessionAsync(client);

        const string text = "I went to the market and bought some fruit.";
        var genuine = Phrase(85, 88, 80, Spoken(text, accuracy: 85));
        var inflated = Phrase(100, 100, 100, Spoken("this was never said by the learner at all", accuracy: 100));
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { }); // opening line
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text, durationMs = 4000, phrases = new[] { genuine } });
        await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text, durationMs = 4000, phrases = new[] { inflated } });

        using var db = database.Open();
        var turns = db.Query<(double? Accuracy, string? Json, string? Rejected)>(
            "SELECT AccuracyScore, PronunciationJson, ScoresRejected FROM dbo.Turns WHERE SessionId = @sessionId AND Role = 'user' ORDER BY Seq",
            new { sessionId }).ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal((85.0, true, null), (turns[0].Accuracy, turns[0].Json is not null, turns[0].Rejected));
        Assert.Equal((null, null), (turns[1].Accuracy, turns[1].Json));
        Assert.Contains("appear in the turn", turns[1].Rejected);
    }

    private static Task<HttpClient> SignInAsync(WebApplicationFactory<Program> api) =>
        TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

    private WebApplicationFactory<Program> CreateApi(FakeGrader grader) =>
        TestApi.CreateWithServices(database.ConnectionString, services => services.AddSingleton<ICefrGrader>(grader));

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10, accent = "en-GB" } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// A session with enough words to grade and a badly pronounced "thought", seeded directly, then ended and
    /// graded by the fake grader.
    /// </summary>
    private async Task<Guid> CreateGradedSessionAsync(WebApplicationFactory<Program> api, HttpClient client)
    {
        var sessionId = await CreateSessionAsync(client);
        var text = string.Join(' ', Enumerable.Repeat("I thought we went to the market and bought some fresh fruit.", 15));
        var words = Spoken(text, accuracy: 88);
        for (var i = 1; i < words.Length; i += 12)
        {
            words[i] = words[i] with { Accuracy = 40, ErrorType = "Mispronunciation" };
        }

        await api.Services.GetRequiredService<ISessionRepository>().AddTurnAsync(
            new TurnRow
            {
                SessionId = sessionId, Role = TurnRole.User, Text = text, WordCount = words.Length,
                PronunciationJson = JsonSerializer.Serialize(new[] { Phrase(85, 85, null, words) }),
            },
            CancellationToken.None);

        var end = await client.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.Accepted, end.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}")).GetProperty("grading").GetProperty("state").GetString() != "Done")
        {
            Assert.True(DateTime.UtcNow < deadline, "Grading did not finish.");
            await Task.Delay(200);
        }

        return sessionId;
    }

    private static async Task WaitForGradingAsync(HttpClient client, Guid sessionId, int expectedCalls, FakeGrader grader)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (grader.Calls < expectedCalls
               || (await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}")).GetProperty("grading").GetProperty("state").GetString() != "Done")
        {
            Assert.True(DateTime.UtcNow < deadline, "Grading did not finish.");
            await Task.Delay(200);
        }
    }

    private static async Task<JsonElement> DueAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/practice/due");

    private static Task<HttpResponseMessage> AttemptAsync(HttpClient client, long id, IEnumerable<JsonElement> phrases) =>
        client.PostAsJsonAsync($"/api/practice/{id}/attempts", new { phrases });

    /// <summary>A scripted result for reading <paramref name="text"/>, every word at the given accuracy.</summary>
    private static JsonElement[] Reading(string text, double accuracy) => [Phrase(accuracy, 90, null, Spoken(text, accuracy))];

    private static Word[] Spoken(string text, double accuracy) =>
        [.. text.Split(' ').Select((w, i) => new Word(w.Trim('.', ',', '?').ToLowerInvariant(), 0.5 + i * 0.35, 0.3, accuracy))];

    private int Count()
    {
        using var db = database.Open();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PracticeItems");
    }

    private void DeleteAll()
    {
        using var db = database.Open();
        db.Execute("DELETE FROM dbo.PracticeItems");
    }

    /// <summary>Speaks <paramref name="text"/> with Azure TTS and assesses it against <paramref name="reference"/> (default: the same text).</summary>
    private static async Task<List<JsonElement>> ReadAloudAsync(string text, string? reference = null)
    {
        var config = SpeechConfig.FromSubscription(E2E.Config["AzureSpeech:Key"], E2E.Config["AzureSpeech:Region"]);
        config.SpeechSynthesisVoiceName = "en-GB-SoniaNeural";
        config.OutputFormat = OutputFormat.Detailed;

        var wav = Path.Combine(Path.GetTempPath(), $"tutorai-{Guid.NewGuid():N}.wav");
        try
        {
            using (var synthesizer = new SpeechSynthesizer(config, null))
            {
                using var spoken = await synthesizer.SpeakTextAsync(text);
                Assert.Equal(ResultReason.SynthesizingAudioCompleted, spoken.Reason);
                await File.WriteAllBytesAsync(wav, spoken.AudioData);
            }

            using var audio = AudioConfig.FromWavFileInput(wav);
            using var recognizer = new SpeechRecognizer(config, "en-GB", audio);
            new PronunciationAssessmentConfig(reference ?? text, GradingSystem.HundredMark, Granularity.Phoneme, enableMiscue: true)
                .ApplyTo(recognizer);
            var result = await recognizer.RecognizeOnceAsync();
            Assert.Equal(ResultReason.RecognizedSpeech, result.Reason);
            return [JsonDocument.Parse(result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult)).RootElement.Clone()];
        }
        finally
        {
            File.Delete(wav);
        }
    }
}
