using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Speech;

namespace TutorAI.Tests;

public class AccentTests
{
    private static readonly AzureSpeechOptions Options = new()
    {
        Locale = "en-US",
        Voice = "en-US-AvaMultilingualNeural",
        Accents = new()
        {
            ["en-GB"] = new() { Label = "British English", Voice = "en-GB-SoniaNeural", PromptNote = "Use British English vocabulary." },
            ["en-US"] = new() { Label = "American English", Voice = "en-US-AvaMultilingualNeural" },
            ["en-AU"] = new() { Label = "Australian English", Voice = "en-AU-NatashaNeural" },
            ["xx-YY"] = new() { Label = "No voice" },
        },
    };

    [Fact]
    public void The_default_accent_comes_first_and_entries_without_a_voice_are_left_out()
    {
        var all = AccentCatalog.All(Options);

        Assert.Equal(["en-US", "en-AU", "en-GB"], all.Select(a => a.Id));
        Assert.Equal("en-US", AccentCatalog.Default(Options).Id);
    }

    [Fact]
    public void Find_allows_only_configured_accents()
    {
        Assert.Equal("en-GB-SoniaNeural", AccentCatalog.Find(Options, "en-GB")!.Voice);
        Assert.Equal("en-GB", AccentCatalog.Find(Options, " EN-gb ")!.Id);
        Assert.Equal("en-US", AccentCatalog.Find(Options, null)!.Id);
        Assert.Null(AccentCatalog.Find(Options, "en-NZ"));
        Assert.Null(AccentCatalog.Find(Options, "xx-YY"));
    }

    [Fact]
    public void Without_configured_accents_the_default_locale_and_voice_are_the_only_choice()
    {
        var bare = new AzureSpeechOptions { Locale = "en-US", Voice = "en-US-AvaMultilingualNeural" };

        var only = Assert.Single(AccentCatalog.All(bare));
        Assert.Equal(("en-US", "en-US-AvaMultilingualNeural"), (only.Id, only.Voice));
    }

    [Fact]
    public void Prosody_is_assessed_for_american_english_only()
    {
        Assert.True(AccentCatalog.Find(Options, "en-US")!.ProsodyAssessed);
        Assert.False(AccentCatalog.Find(Options, "en-GB")!.ProsodyAssessed);
        Assert.False(AccentCatalog.Find(Options, "en-AU")!.ProsodyAssessed);
    }

    [Fact]
    public void The_tutor_prompt_names_the_variety_only_when_one_is_chosen()
    {
        var session = new SessionRow { Mode = SessionMode.Conversation, ScenarioJson = "{}" };
        var scenario = new Scenario(Topic: "Travel");

        Assert.Contains("Language variety: Use British English vocabulary.", ScenarioPrompts.Build(session, scenario, "Use British English vocabulary."));
        Assert.DoesNotContain("Language variety", ScenarioPrompts.Build(session, scenario));
    }

    [Fact]
    public void Without_prosody_the_pronunciation_composite_is_the_accuracy()
    {
        // What Azure returns for en-GB: accuracy and fluency, no prosody.
        var phrases = AzurePronunciation.ParseTurn([AzureSamples.Phrase(82, 90, null, AzureSamples.Words(12))]);

        var stats = PronunciationAggregator.Compute(phrases);

        Assert.Null(stats.Prosody);
        Assert.NotNull(stats.Accuracy);
        Assert.Equal(stats.Accuracy, stats.Composite);
    }
}
