using TutorAI.Api.Assessment;
using static TutorAI.Tests.AzureSamples;

namespace TutorAI.Tests;

public class TurnScoreCheckTests
{
    private const string Text = "Last summer we went to Porto, and the people were very friendly.";

    // Shaped like a real result: lexical, lower-case words, about 0.35 s each with short gaps.
    private static Word[] Spoken() =>
    [
        new("last", 0.60, 0.30), new("summer", 0.95, 0.40), new("we", 1.40, 0.15), new("went", 1.60, 0.30),
        new("to", 1.95, 0.12), new("porto", 2.10, 0.45), new("and", 2.80, 0.20), new("the", 3.05, 0.10),
        new("people", 3.20, 0.35), new("were", 3.60, 0.20), new("very", 3.85, 0.30), new("friendly", 4.20, 0.50),
    ];

    private static IReadOnlyList<AssessedPhrase> Parse(params System.Text.Json.JsonElement[] phrases) =>
        AzurePronunciation.ParseTurn(phrases);

    [Fact]
    public void A_genuine_turn_passes()
    {
        Assert.Null(TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, Spoken())), durationMs: 5200));
        // No duration (typed tools, older clients) skips only the duration check.
        Assert.Null(TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, Spoken())), durationMs: null));
        Assert.Null(TurnScoreCheck.Problem(Text, [], durationMs: 100));
    }

    [Fact]
    public void Text_from_the_display_form_still_matches_the_lexical_words()
    {
        // Display text has capitals, punctuation, curly apostrophes and hyphens; Azure's words don't.
        var phrases = Parse(Phrase(90, 90, 80,
            new Word("i've", 0.5, 0.3), new Word("got", 0.9, 0.2), new Word("a", 1.2, 0.1),
            new Word("well", 1.4, 0.2), new Word("known", 1.7, 0.3), new Word("recipe", 2.1, 0.4)));

        Assert.Null(TurnScoreCheck.Problem("I’ve got a well-known recipe!", phrases, 3000));
    }

    [Theory]
    [InlineData(140, 90, 78, 90)]
    [InlineData(86, -5, 78, 90)]
    [InlineData(86, 90, 101, 90)]
    [InlineData(86, 90, 78, 250)]
    public void Scores_outside_0_to_100_are_rejected(double accuracy, double fluency, double prosody, double wordAccuracy)
    {
        var words = Spoken();
        words[3] = words[3] with { Accuracy = wordAccuracy };

        Assert.Contains("0-100", TurnScoreCheck.Problem(Text, Parse(Phrase(accuracy, fluency, prosody, words)), 5200));
    }

    [Fact]
    public void Words_out_of_order_or_with_negative_timings_are_rejected()
    {
        var swapped = Spoken();
        (swapped[4], swapped[5]) = (swapped[5], swapped[4]);
        var negative = Spoken();
        negative[2] = negative[2] with { Duration = -0.2 };

        Assert.Contains("order", TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, swapped)), 5200));
        Assert.Contains("negative", TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, negative)), 5200));
    }

    [Fact]
    public void Words_lasting_longer_than_the_turn_are_rejected()
    {
        // 3.37 s of words: fine for a 1 s turn with the slack, not when each word is stretched tenfold.
        var stretched = Spoken().Select((w, i) => w with { Start = i * 4.0, Duration = 3.5 }).ToArray();

        Assert.Null(TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, Spoken())), 1000));
        Assert.Contains("turn took", TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, stretched)), 5200));
    }

    [Fact]
    public void Results_for_other_speech_are_rejected()
    {
        // A high-scoring result recorded for a different sentence, sent with this turn's text.
        var other = Words(12).Select((w, i) => w with { Text = $"other{i}" }).ToArray();
        // The genuine phrase plus an unrelated one: half the words don't belong to the text.
        var padded = Parse(Phrase(86, 90, 78, Spoken()), Phrase(99, 99, 99, Words(12, start: 6)));

        Assert.Contains("appear in the turn", TurnScoreCheck.Problem(Text, Parse(Phrase(99, 99, 99, other)), 5200));
        Assert.Contains("appear in the turn", TurnScoreCheck.Problem(Text, padded, 12000));
    }

    [Fact]
    public void A_few_misrecognised_words_are_tolerated()
    {
        // Text came from the same recognition, but numbers and the odd word can differ in form.
        var words = Spoken();
        words[0] = words[0] with { Text = "lust" };
        words[5] = words[5] with { Text = "portal" };

        Assert.Null(TurnScoreCheck.Problem(Text, Parse(Phrase(86, 90, 78, words)), 5200));
    }

    [Fact]
    public void Tokens_split_on_punctuation_and_drop_quotes()
    {
        Assert.Equal(["it's", "a", "well", "known", "fact", "10"], TurnScoreCheck.Tokens("'It's a well-known fact,' 10."));
    }
}
