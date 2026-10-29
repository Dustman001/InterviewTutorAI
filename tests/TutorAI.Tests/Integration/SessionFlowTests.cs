using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.PronunciationAssessment;
using Microsoft.Extensions.DependencyInjection;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;
using TutorAI.Migrations;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class SessionFlowTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    // Roughly 45 words each, with errors a grader should notice.
    private static readonly string[] LearnerTurns =
    [
        "Hello, thank you. Last summer I have travelled to Portugal with my family for two weeks. We stayed in a small house near the beach, and every morning we was going to the market to buy fresh fish and fruits. It was very relaxing for all of us.",
        "I think the most interesting place was Porto, because the city have a lot of history and the people is very friendly. We walked along the river, and we tried the local food. If I would have more time, I would stay there one more week.",
        "Yes, I prefer to travel by train, because it is more comfortable than the plane and you can see the landscape. Also it is better for the environment. The only problem is that sometimes the tickets are more expensive and the journey take a long time.",
        "Next year I want to visit Japan, because I am interested in the culture since many years. I am learning some basic words, but the language is very difficult for me. I hope I can to travel there in the spring, when the trees are with flowers.",
    ];

    private WebApplicationFactory<Program> CreateApi() => TestApi.Create(database.ConnectionString);

    [E2EFact]
    public void Migrations_are_journaled_and_reapplying_does_nothing()
    {
        // The fixture already migrated this database once.
        var appliedAgain = Migrator.Run(database.ConnectionString, quiet: true);

        Assert.Empty(appliedAgain);
        using var db = database.Open();
        var scripts = typeof(Migrator).Assembly.GetManifestResourceNames().Count(n => n.EndsWith(".sql", StringComparison.Ordinal));
        Assert.Equal(scripts, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SchemaVersions"));
        Assert.Equal(
            4,
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Sessions', 'Turns', 'Assessments', 'Users')"));
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Sessions') AND name = 'UserId'"));
    }

    [E2EFact]
    public async Task Short_session_is_reported_as_too_short_instead_of_graded()
    {
        await using var api = CreateApi();
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var sessionId = await CreateSessionAsync(client);

        // Seed the learner's turn directly so this test needs no Claude call.
        await api.Services.GetRequiredService<SessionRepository>().AddTurnAsync(
            new TurnRow { SessionId = sessionId, Role = TurnRoles.User, Text = "I like travel very much.", WordCount = 5 },
            CancellationToken.None);

        var response = await client.PostAsync($"/api/sessions/{sessionId}/end", null);
        var assessment = await response.Content.ReadFromJsonAsync<AssessmentResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(assessment!.Sufficient);
        Assert.Null(assessment.Levels.Overall);
        Assert.Contains("5 words", assessment.Reason);

        using var db = database.Open();
        Assert.Equal("Ended", db.ExecuteScalar<string>("SELECT Status FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId }));
        Assert.Equal(
            1,
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Assessments WHERE SessionId = @sessionId AND Overall IS NULL", new { sessionId }));

        // A turn after the end is refused.
        var late = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "One more thing." });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [E2EFact]
    public async Task Session_validation_and_listing()
    {
        await using var api = CreateApi();
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

        var bad = await client.PostAsJsonAsync("/api/sessions", new { mode = "Karaoke" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sessions/{Guid.NewGuid()}")).StatusCode);

        var sessionId = await CreateSessionAsync(client);
        var list = (await client.GetFromJsonAsync<JsonElement>("/api/sessions")).GetProperty("items");
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetGuid() == sessionId);

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
    }

    [E2EFact(azureSpeech: true)]
    public async Task Azure_assessment_of_real_audio_feeds_the_metrics()
    {
        await using var api = CreateApi();
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var token = await client.GetFromJsonAsync<JsonElement>("/api/speech/token");
        Assert.False(string.IsNullOrWhiteSpace(token.GetProperty("token").GetString()));

        var (recognized, phrases) = await SpeakAndAssessAsync(LearnerTurns[0]);

        Assert.Contains("Portugal", recognized);
        // Real results pass the plausibility checks the API runs on every turn.
        Assert.Null(TurnScoreCheck.Problem(recognized, AzurePronunciation.ParseTurn(phrases), durationMs: null));
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

        Assert.InRange(metrics.Fluency.TimedWords, 35, 55);
        Assert.InRange(metrics.Fluency.WordsPerMinute!.Value, 80, 260);
        Assert.NotNull(metrics.Fluency.AzureFluencyScore);
        Assert.NotNull(metrics.Pronunciation.Accuracy);
        Assert.NotNull(metrics.Pronunciation.Prosody);
        Assert.True(CefrMapper.IsValid(CefrMapper.FromPronunciationScore(metrics.Pronunciation.Composite!.Value)));
    }

    [E2EFact(claude: true)]
    public async Task Typed_conversation_is_graded_by_claude_without_pronunciation()
    {
        await using var api = CreateApi();
        var client = await TestApi.SignInAsync(
            api, TestApi.AdminUsername, TestApi.AdminPassword, timeout: TimeSpan.FromMinutes(5));
        var sessionId = await CreateSessionAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(await PostTurnAsync(client, sessionId, new { })));
        foreach (var text in LearnerTurns)
        {
            Assert.False(string.IsNullOrWhiteSpace(await PostTurnAsync(client, sessionId, new { text })));
        }

        var assessment = await EndAsync(client, sessionId);

        Assert.True(assessment.Sufficient);
        Assert.All(
            new[] { assessment.Levels.Fluency, assessment.Levels.Grammar, assessment.Levels.Vocabulary, assessment.Levels.Interaction, assessment.Levels.Overall },
            level => Assert.True(CefrMapper.IsValid(level), $"'{level}' is not a CEFR level"));
        Assert.Null(assessment.Levels.Pronunciation);
        Assert.NotEmpty(assessment.Detail!.TopCorrections);
        AssertStored(sessionId, expectedTurns: 9);

        using var db = database.Open();
        Assert.Equal(
            5,
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Turns WHERE SessionId = @sessionId AND Role = 'tutor' AND Model = 'claude-sonnet-5-5' AND InputTokens > 0 AND OutputTokens > 0",
                new { sessionId }));
        Assert.Equal(
            1,
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Assessments WHERE SessionId = @sessionId AND Model = 'claude-opus-5-5' AND InputTokens > 0 AND OutputTokens > 0",
                new { sessionId }));
    }

    [E2EFact(claude: true, azureSpeech: true)]
    public async Task Spoken_conversation_gets_all_five_levels()
    {
        await using var api = CreateApi();
        var client = await TestApi.SignInAsync(
            api, TestApi.AdminUsername, TestApi.AdminPassword, timeout: TimeSpan.FromMinutes(5));

        var token = await client.GetFromJsonAsync<JsonElement>("/api/speech/token");
        Assert.False(string.IsNullOrWhiteSpace(token.GetProperty("token").GetString()));

        var sessionId = await CreateSessionAsync(client);
        await PostTurnAsync(client, sessionId, new { });
        foreach (var text in LearnerTurns)
        {
            // The learner's audio is synthetic (Azure TTS), then recognized and assessed exactly as microphone audio would be.
            var (recognized, phrases) = await SpeakAndAssessAsync(text);
            Assert.NotEmpty(phrases);
            await PostTurnAsync(client, sessionId, new { text = recognized, phrases });
        }

        var assessment = await EndAsync(client, sessionId);

        Assert.True(assessment.Sufficient);
        Assert.All(
            new[]
            {
                assessment.Levels.Fluency, assessment.Levels.Grammar, assessment.Levels.Vocabulary,
                assessment.Levels.Interaction, assessment.Levels.Pronunciation, assessment.Levels.Overall,
            },
            level => Assert.True(CefrMapper.IsValid(level), $"'{level}' is not a CEFR level"));
        Assert.NotNull(assessment.Metrics.Fluency.WordsPerMinute);
        Assert.NotNull(assessment.Metrics.Pronunciation.Composite);
        AssertStored(sessionId, expectedTurns: 9);

        using var db = database.Open();
        Assert.Equal(
            LearnerTurns.Length,
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Turns WHERE SessionId = @sessionId AND Role = 'user' AND AccuracyScore IS NOT NULL AND PronunciationJson IS NOT NULL",
                new { sessionId }));
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions",
            new { mode = "Conversation", scenario = new { topic = "Travel", difficulty = "Normal", durationMinutes = 10 } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Posts a turn and returns the tutor's reply reassembled from the event stream.</summary>
    private static async Task<string> PostTurnAsync(HttpClient client, Guid sessionId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", body);
        var stream = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, stream);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("event: error", stream);
        Assert.Contains("event: delta", stream);

        var done = stream.Split("\n\n").Single(block => block.StartsWith("event: done"));
        return JsonDocument.Parse(done[(done.IndexOf("data: ", StringComparison.Ordinal) + 6)..])
            .RootElement.GetProperty("text").GetString()!;
    }

    /// <summary>Ends the session and waits for the background grading (real Claude) to finish.</summary>
    private static async Task<AssessmentResponse> EndAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            var grading = detail.GetProperty("grading");
            var state = grading.GetProperty("state").GetString();
            Assert.NotEqual("Failed", state);
            if (state == "Done")
            {
                return detail.GetProperty("assessment").Deserialize<AssessmentResponse>(Json)!;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Grading did not finish: {grading}");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private void AssertStored(Guid sessionId, int expectedTurns)
    {
        using var db = database.Open();
        Assert.Equal("Ended", db.ExecuteScalar<string>("SELECT Status FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId }));
        Assert.Equal(expectedTurns, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Turns WHERE SessionId = @sessionId", new { sessionId }));
        Assert.Equal(
            1,
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Assessments WHERE SessionId = @sessionId AND Overall IS NOT NULL AND Grammar IS NOT NULL",
                new { sessionId }));
    }

    /// <summary>Speaks the text with an Azure voice, then recognizes and assesses it as the browser would.</summary>
    internal static async Task<(string Text, List<JsonElement> Phrases)> SpeakAndAssessAsync(
        string text, string voice = "en-US-GuyNeural", string locale = "en-US", bool prosody = true)
    {
        var config = SpeechConfig.FromSubscription(E2E.Config["AzureSpeech:Key"], E2E.Config["AzureSpeech:Region"]);
        config.SpeechSynthesisVoiceName = voice;
        config.OutputFormat = OutputFormat.Detailed;
        config.RequestWordLevelTimestamps();

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
            using var recognizer = new SpeechRecognizer(config, locale, audio);
            var assessment = new PronunciationAssessmentConfig("", GradingSystem.HundredMark, Granularity.Phoneme, false);
            if (prosody)
            {
                assessment.EnableProsodyAssessment();
            }
            assessment.ApplyTo(recognizer);

            var texts = new List<string>();
            var phrases = new List<JsonElement>();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                {
                    texts.Add(e.Result.Text);
                    phrases.Add(JsonDocument.Parse(e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult)).RootElement.Clone());
                }
            };
            recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    finished.TrySetException(new InvalidOperationException(e.ErrorDetails));
                }
                else
                {
                    finished.TrySetResult();
                }
            };
            recognizer.SessionStopped += (_, _) => finished.TrySetResult();

            await recognizer.StartContinuousRecognitionAsync();
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(2));
            await recognizer.StopContinuousRecognitionAsync();
            return (string.Join(' ', texts), phrases);
        }
        finally
        {
            File.Delete(wav);
        }
    }
}
