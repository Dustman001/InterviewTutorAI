using System.Text.Json;
using System.Text.Json.Serialization;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;

namespace TutorAI.Api.Practice;

[JsonConverter(typeof(StringValueJsonConverter<PracticeKind>))]
public readonly record struct PracticeKind : IStringValue<PracticeKind>
{
    /// <summary>A corrected sentence from the grader's top corrections.</summary>
    public static readonly PracticeKind Correction = new("correction");

    /// <summary>A word the learner mispronounced.</summary>
    public static readonly PracticeKind Word = new("word");

    private PracticeKind(string value) => Value = value;

    public string Value { get; }
    public static IReadOnlyList<PracticeKind> All { get; } = [Correction, Word];
    public override string ToString() => Value;
}

/// <summary>An item to add (or merge) when a session is graded.</summary>
public sealed record PracticeSeed(PracticeKind Kind, string Target, string TargetKey, string? Original, string? Explanation);

/// <summary>The items one graded session adds, saved in the same transaction as its assessment.</summary>
public sealed record PracticeBatch(int UserId, Guid SessionId, string? Locale, DateTime Now, IReadOnlyList<PracticeSeed> Items);

public sealed class PracticeItemRow
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public Guid? SessionId { get; set; }
    public PracticeKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string? Original { get; set; }
    public string? Explanation { get; set; }
    public string? Locale { get; set; }
    public byte Box { get; set; }
    public DateTime? DueAt { get; set; }
    public int Attempts { get; set; }
    public double? LastScore { get; set; }
    public double? BestScore { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PracticeCounts
{
    public int Due { get; set; }
    /// <summary>Not yet mastered, due or not.</summary>
    public int Active { get; set; }
    public int Mastered { get; set; }
    /// <summary>The soonest due time still in the future; null when nothing else is scheduled.</summary>
    public DateTime? NextDueAt { get; set; }
}

/// <summary>Leitner boxes: a pass moves an item up a box and spaces it out; a miss sends it back to the start.</summary>
public static class PracticeSchedule
{
    public const double PassScore = 80;
    public const byte LastBox = 4;

    /// <summary>Days until the next review after passing from box 0, 1, 2 and 3. Passing in box 4 masters the item.</summary>
    public static readonly IReadOnlyList<int> IntervalDays = [1, 3, 7, 21];

    /// <returns>The new box, and the new due time (null: mastered).</returns>
    public static (byte Box, DateTime? DueAt) Next(byte box, double score, DateTime now)
    {
        if (score < PassScore)
        {
            return (0, now);
        }

        return box >= LastBox ? (LastBox, null) : ((byte)(box + 1), now.AddDays(IntervalDays[box]));
    }
}

public static class PracticeSeeds
{
    public const int MaxTargetChars = 300;
    public const int MinWordChars = 2;

    /// <summary>The corrections and weak words of a graded session, without duplicates.</summary>
    public static IReadOnlyList<PracticeSeed> FromAssessment(GradingResult grading, PronunciationStats pronunciation)
    {
        var seeds = new List<PracticeSeed>();
        foreach (var correction in grading.TopCorrections ?? [])
        {
            var target = correction.Corrected?.Trim() ?? "";
            if (target.Length is 0 or > MaxTargetChars || Key(target) == Key(correction.Original ?? ""))
            {
                continue;
            }

            seeds.Add(new PracticeSeed(
                PracticeKind.Correction, target, Key(target), Truncate(correction.Original?.Trim(), 400),
                Truncate(correction.Explanation?.Trim(), 1000)));
        }

        foreach (var word in pronunciation.WeakestWords)
        {
            var target = word.Word.Trim();
            if (target.Length < MinWordChars || target.Length > MaxTargetChars)
            {
                continue;
            }

            seeds.Add(new PracticeSeed(
                PracticeKind.Word, target, Key(target), null, $"Scored {word.Score:0} out of 100 in your session."));
        }

        return [.. seeds.DistinctBy(s => (s.Kind, s.TargetKey))];
    }

    /// <summary>Lower case, single spaces, curly quotes straightened, no surrounding punctuation.</summary>
    public static string Key(string text)
    {
        var words = text
            .Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words).Trim('.', ',', '!', '?', ';', ':', '"', '\'', ' ');
    }

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

/// <param name="Accuracy">Null for a word that was left out.</param>
/// <param name="ErrorType">None, Mispronunciation or Omission.</param>
public sealed record ScriptedWord(string Word, double? Accuracy, string ErrorType);

public sealed record ScriptedResult(double Score, IReadOnlyList<ScriptedWord> Words);

/// <summary>
/// Scores a read-aloud attempt from Azure's scripted pronunciation assessment (reference text plus miscue
/// detection). The score is computed here, not taken from the browser: the mean accuracy over the words to
/// read, with left-out words counting zero and extra words ignored.
/// </summary>
public static class ScriptedAssessment
{
    /// <summary>Share of the target's words that the result must cover.</summary>
    public const double MinCoverage = 0.7;

    public static ScriptedResult? Score(string target, IReadOnlyList<JsonElement>? phrases, out string? problem)
    {
        var words = new List<ScriptedWord>();
        foreach (var phrase in phrases ?? [])
        {
            if (phrase.ValueKind != JsonValueKind.Object
                || !phrase.TryGetProperty("NBest", out var nbest) || nbest.ValueKind != JsonValueKind.Array
                || nbest.GetArrayLength() == 0
                || !nbest[0].TryGetProperty("Words", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var w in list.EnumerateArray())
            {
                var text = w.TryGetProperty("Word", out var wt) && wt.ValueKind == JsonValueKind.String ? wt.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                double? accuracy = null;
                var error = "None";
                if (w.TryGetProperty("PronunciationAssessment", out var pa) && pa.ValueKind == JsonValueKind.Object)
                {
                    if (pa.TryGetProperty("AccuracyScore", out var a) && a.ValueKind == JsonValueKind.Number)
                    {
                        accuracy = a.GetDouble();
                    }

                    if (pa.TryGetProperty("ErrorType", out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        error = e.GetString() ?? "None";
                    }
                }

                if (error == "Insertion")
                {
                    continue;
                }

                if (accuracy is < 0 or > 100)
                {
                    problem = "A score is outside 0-100.";
                    return null;
                }

                words.Add(error == "Omission" ? new ScriptedWord(text, null, error) : new ScriptedWord(text, accuracy ?? 0, error));
            }
        }

        if (words.All(w => w.ErrorType == "Omission"))
        {
            problem = "No speech was recognised. Read the text aloud and try again.";
            return null;
        }

        if (TurnScoreCheck.Overlap(TurnScoreCheck.Tokens(target), words.Select(w => w.Word)) < MinCoverage)
        {
            problem = "The recording doesn't match the text to read.";
            return null;
        }

        problem = null;
        var score = words.Average(w => w.Accuracy ?? 0);
        return new ScriptedResult(Math.Round(score, 1, MidpointRounding.AwayFromZero), words);
    }
}
