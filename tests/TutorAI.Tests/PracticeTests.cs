using TutorAI.Api.Assessment;
using TutorAI.Api.Practice;
using static TutorAI.Tests.AzureSamples;

namespace TutorAI.Tests;

public class PracticeScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 2, 3)]
    [InlineData(2, 3, 7)]
    [InlineData(3, 4, 21)]
    public void A_pass_moves_up_a_box_and_spaces_the_next_review(byte box, byte expectedBox, int days)
    {
        Assert.Equal((expectedBox, Now.AddDays(days)), PracticeSchedule.Next(box, 80, Now));
    }

    [Fact]
    public void A_pass_in_the_last_box_masters_the_item()
    {
        Assert.Equal(((byte)4, (DateTime?)null), PracticeSchedule.Next(4, 95, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_miss_goes_back_to_box_0_and_is_due_now(byte box)
    {
        Assert.Equal(((byte)0, (DateTime?)Now), PracticeSchedule.Next(box, 79.9, Now));
    }
}

public class PracticeSeedTests
{
    private static readonly DimensionGrade Grade = new("B1", "medium", [], "");

    private static GradingResult Grading(params Correction[] corrections) => new(Grade, Grade, Grade, Grade, "", [.. corrections]);

    private static PronunciationStats Pronunciation(params WeakWord[] words) => new(100, 80, 70, 76, words);

    [Fact]
    public void Corrections_and_weak_words_become_items()
    {
        var seeds = PracticeSeeds.FromAssessment(
            Grading(new Correction("we was going", "We went to the market.", "Use the past simple.")),
            Pronunciation(new WeakWord("thought", 41, 2)));

        Assert.Collection(
            seeds,
            c => Assert.Equal((PracticeKinds.Correction, "We went to the market.", "we went to the market", "we was going"), (c.Kind, c.Target, c.TargetKey, c.Original)),
            w => Assert.Equal((PracticeKinds.Word, "thought", "Scored 41 out of 100 in your session."), (w.Kind, w.Target, w.Explanation)));
    }

    [Fact]
    public void Duplicates_empty_unchanged_and_overlong_targets_are_dropped()
    {
        var seeds = PracticeSeeds.FromAssessment(
            Grading(
                new Correction("the people is", "The people are friendly.", "Plural."),
                new Correction("people is", "the  people ARE friendly", "Same sentence, other spacing and case."),
                new Correction("I am agree", "", "No correction."),
                new Correction("It's fine.", "it's fine", "Only punctuation and case differ."),
                new Correction("long", new string('x', PracticeSeeds.MaxTargetChars + 1), "Too long to read aloud.")),
            Pronunciation(new WeakWord("a", 30, 1), new WeakWord("schedule", 50, 1)));

        Assert.Equal(["The people are friendly.", "schedule"], seeds.Select(s => s.Target));
    }

    [Fact]
    public void Key_normalises_case_spacing_quotes_and_end_punctuation()
    {
        Assert.Equal("i've been there", PracticeSeeds.Key("  “I’ve   been THERE!” "));
    }
}

public class ScriptedAssessmentTests
{
    private const string Target = "We went to the market.";

    [Fact]
    public void Score_is_the_mean_accuracy_with_omitted_words_as_zero_and_insertions_ignored()
    {
        var phrase = Phrase(70, 90, null,
            new Word("we", 0.5, 0.2, 100), new Word("went", 0.8, 0.3, 60, "Mispronunciation"),
            new Word("um", 1.2, 0.2, 0, "Insertion"), new Word("to", 1.5, 0.1, 90),
            new Word("the", 1.7, 0.1, 0, "Omission"), new Word("market", 1.9, 0.4, 90));

        var result = ScriptedAssessment.Score(Target, [phrase], out var problem);

        Assert.Null(problem);
        // (100 + 60 + 90 + 0 + 90) / 5
        Assert.Equal(68, result!.Score);
        Assert.Equal(["we", "went", "to", "the", "market"], result.Words.Select(w => w.Word));
        Assert.Null(result.Words[3].Accuracy);
        Assert.Equal("Omission", result.Words[3].ErrorType);
    }

    [Fact]
    public void A_good_reading_scores_high()
    {
        var phrase = Phrase(95, 95, null,
            new Word("we", 0.5, 0.2, 98), new Word("went", 0.8, 0.3, 95), new Word("to", 1.2, 0.1, 97),
            new Word("the", 1.4, 0.1, 99), new Word("market", 1.6, 0.4, 92));

        Assert.True(ScriptedAssessment.Score(Target, [phrase], out _)!.Score >= PracticeSchedule.PassScore);
    }

    [Fact]
    public void A_result_for_other_text_is_refused()
    {
        var phrase = Phrase(99, 99, null, new Word("hello", 0.5, 0.3, 100), new Word("there", 0.9, 0.3, 100));

        Assert.Null(ScriptedAssessment.Score(Target, [phrase], out var problem));
        Assert.Contains("doesn't match", problem);
    }

    [Fact]
    public void Silence_and_out_of_range_scores_are_refused()
    {
        var silent = Phrase(0, 0, null, [.. "we went to the market".Split(' ').Select((w, i) => new Word(w, i, 0, 0, "Omission"))]);
        var inflated = Phrase(100, 100, null, new Word("we", 0.5, 0.2, 150), new Word("went", 0.8, 0.3, 100));

        Assert.Null(ScriptedAssessment.Score(Target, [silent], out var silence));
        Assert.Contains("No speech", silence);
        Assert.Null(ScriptedAssessment.Score(Target, [], out _));
        Assert.Null(ScriptedAssessment.Score(Target, [inflated], out var range));
        Assert.Contains("0-100", range);
    }
}
