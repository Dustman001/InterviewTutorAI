using System.Text.Json;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;
using static TutorAI.Tests.AzureSamples;

namespace TutorAI.Tests;

public class AzurePronunciationTests
{
    [Fact]
    public void ParsePhrase_reads_scores_and_word_timings()
    {
        var phrase = AzurePronunciation.ParsePhrase(
            Phrase(88, 92, 75, new Word("hello", 0.5, 0.4, 97), new Word("world", 1.0, 0.5, 40, "Mispronunciation")))!;

        Assert.Equal(88, phrase.Accuracy);
        Assert.Equal(92, phrase.Fluency);
        Assert.Equal(75, phrase.Prosody);
        Assert.Equal(2, phrase.Words.Count);
        Assert.Equal(0.5, phrase.Words[0].StartSeconds, 3);
        Assert.Equal(1.5, phrase.Words[1].EndSeconds, 3);
        Assert.Equal("Mispronunciation", phrase.Words[1].ErrorType);
    }

    [Fact]
    public void ParseTurn_skips_objects_that_are_not_assessed_phrases()
    {
        var garbage = JsonSerializer.SerializeToElement(new { RecognitionStatus = "NoMatch" });

        var phrases = AzurePronunciation.ParseTurn([garbage, Phrase(80, 80, 80, Words(3))]);

        Assert.Single(phrases);
    }

    [Fact]
    public void ParseTurn_round_trips_the_stored_json_array()
    {
        var stored = JsonSerializer.Serialize(new[] { Phrase(80, 80, 80, Words(3)), Phrase(90, 90, 90, Words(2, start: 5)) });

        Assert.Equal(2, AzurePronunciation.ParseTurn(stored).Count);
        Assert.Empty(AzurePronunciation.ParseTurn((string?)null));
    }
}

public class FluencyMetricsTests
{
    [Fact]
    public void Computes_rate_and_pauses_from_word_timings()
    {
        // 10 words of 0.3 s: nine gaps of 0.1 s (not pauses), except one stretched to 1.5 s (a long pause).
        var words = Words(10);
        for (var i = 5; i < words.Length; i++)
        {
            words[i] = words[i] with { Start = words[i].Start + 1.4 };
        }

        var stats = FluencyMetrics.Compute([(10, AzurePronunciation.ParseTurn([Phrase(90, 85, 80, words)]))]);

        // Span: 10 * 0.3 + 8 * 0.1 + 1.5 = 5.3 s.
        Assert.Equal(5.3, stats.SpeakingSeconds, 1);
        Assert.Equal(113.2, stats.WordsPerMinute!.Value, 1);
        Assert.Equal(0.283, stats.PauseRatio!.Value, 3);
        Assert.Equal(11.3, stats.LongPausesPerMinute!.Value, 1);
        Assert.Equal(5, stats.MeanRunLength);
        Assert.Equal(85, stats.AzureFluencyScore);
    }

    [Fact]
    public void Counts_fillers_and_weights_azure_fluency_by_words()
    {
        var first = Phrase(90, 100, 80, new Word("um", 0, 0.2), new Word("yes", 0.3, 0.2));
        var second = Phrase(90, 50, 80, Words(6, start: 10));

        var stats = FluencyMetrics.Compute([(2, AzurePronunciation.ParseTurn([first])), (6, AzurePronunciation.ParseTurn([second]))]);

        Assert.Equal(1, stats.FillerCount);
        Assert.Equal(12.5, stats.FillersPer100Words);
        Assert.Equal(62.5, stats.AzureFluencyScore);
    }

    [Fact]
    public void Turns_without_timings_count_words_only()
    {
        var stats = FluencyMetrics.Compute([(40, [])]);

        Assert.Equal(40, stats.TotalWords);
        Assert.Equal(0, stats.TimedWords);
        Assert.Null(stats.WordsPerMinute);
        Assert.Null(stats.PauseRatio);
        Assert.Null(stats.AzureFluencyScore);
    }

    [Fact]
    public void Silence_between_turns_is_not_a_pause()
    {
        var turnOne = AzurePronunciation.ParseTurn([Phrase(90, 90, 90, Words(5))]);
        var turnTwo = AzurePronunciation.ParseTurn([Phrase(90, 90, 90, Words(5, start: 60))]);

        var stats = FluencyMetrics.Compute([(5, turnOne), (5, turnTwo)]);

        Assert.Equal(0, stats.PauseRatio);
        Assert.Equal(3.8, stats.SpeakingSeconds, 1);
    }
}

public class PronunciationAggregatorTests
{
    [Fact]
    public void Weights_phrase_scores_by_word_count_and_blends_prosody()
    {
        var stats = PronunciationAggregator.Compute(
            AzurePronunciation.ParseTurn([Phrase(100, 90, 60, Words(1)), Phrase(80, 90, 80, Words(3, start: 5))]));

        Assert.Equal(4, stats.AssessedWords);
        Assert.Equal(85, stats.Accuracy);
        Assert.Equal(75, stats.Prosody);
        Assert.Equal(81, stats.Composite);
    }

    [Fact]
    public void Falls_back_to_accuracy_when_prosody_is_missing()
    {
        var stats = PronunciationAggregator.Compute(AzurePronunciation.ParseTurn([Phrase(72, 90, null, Words(4))]));

        Assert.Equal(72, stats.Composite);
    }

    [Fact]
    public void Lists_weakest_words_lowest_first_and_groups_repeats()
    {
        var phrase = Phrase(70, 80, 70,
            new Word("thought", 0, 0.3, 35, "Mispronunciation"),
            new Word("the", 0.4, 0.1, 98),
            new Word("Thought,", 0.6, 0.3, 55),
            new Word("comfortable", 1.0, 0.5, 48),
            new Word("fine", 1.6, 0.2, 75, "Mispronunciation"));

        var weakest = PronunciationAggregator.Compute(AzurePronunciation.ParseTurn([phrase])).WeakestWords;

        Assert.Equal(["thought", "comfortable", "fine"], weakest.Select(w => w.Word));
        Assert.Equal(35, weakest[0].Score);
        Assert.Equal(2, weakest[0].Count);
    }

    [Fact]
    public void No_phrases_means_no_score()
    {
        var stats = PronunciationAggregator.Compute([]);

        Assert.Null(stats.Composite);
        Assert.Empty(stats.WeakestWords);
    }
}

public class CefrMapperTests
{
    [Theory]
    [InlineData(100, "C2")]
    [InlineData(95, "C2")]
    [InlineData(94.9, "C1")]
    [InlineData(85, "B2+")]
    [InlineData(80, "B2")]
    [InlineData(74, "B1+")]
    [InlineData(68, "B1")]
    [InlineData(60, "A2+")]
    [InlineData(50, "A2")]
    [InlineData(49.9, "A1")]
    [InlineData(0, "A1")]
    public void Maps_pronunciation_score_to_level(double score, string expected) =>
        Assert.Equal(expected, CefrMapper.FromPronunciationScore(score));

    [Fact]
    public void Median_of_the_users_example_is_B1_plus() =>
        Assert.Equal("B1+", CefrMapper.Median(["C1", "B1+", "A2", "B1+", "B2"]));

    [Fact]
    public void Median_ignores_missing_levels_and_takes_the_lower_middle() =>
        Assert.Equal("B1", CefrMapper.Median(["B2", null, "A2", "B1", "C1"]));

    [Fact]
    public void Median_of_nothing_is_null() => Assert.Null(CefrMapper.Median([null, "nonsense"]));
}

public class SessionMetricsTests
{
    [Fact]
    public void Only_user_turns_feed_the_metrics()
    {
        var json = JsonSerializer.Serialize(new[] { Phrase(80, 80, 80, Words(4)) });
        List<TurnRow> turns =
        [
            new() { Role = TurnRoles.Tutor, Text = "Hello there, how are you today?", WordCount = 6 },
            new() { Role = TurnRoles.User, Text = "word0 word1 word2 word3", WordCount = 4, PronunciationJson = json },
            new() { Role = TurnRoles.User, Text = "typed answer here", WordCount = 3 },
        ];

        var metrics = AssessmentService.ComputeMetrics(turns);

        Assert.Equal(7, metrics.Fluency.TotalWords);
        Assert.Equal(4, metrics.Fluency.TimedWords);
        Assert.Equal(4, metrics.Pronunciation.AssessedWords);
    }
}
