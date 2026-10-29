using System.Text;

namespace TutorAI.Api.Assessment;

/// <summary>
/// Cheap plausibility checks on the speech results the browser sends with a turn. The browser runs Azure
/// recognition itself, so a learner could send inflated scores. These checks catch results that were edited
/// or belong to other speech. They can't catch a careful forgery; only server-side recognition could.
/// </summary>
public static class TurnScoreCheck
{
    /// <summary>Share of the assessed words that must also appear in the turn's text.</summary>
    public const double MinTextOverlap = 0.7;

    /// <summary>
    /// The browser starts the turn clock at the first partial result, a moment after speech starts, so the
    /// spoken words may add up to a little more than the measured duration.
    /// </summary>
    public const double DurationSlackSeconds = 3;
    public const double DurationSlackRatio = 1.1;

    /// <summary>Null when the results look genuine; otherwise why the turn's scores are dropped.</summary>
    public static string? Problem(string text, IReadOnlyList<AssessedPhrase> phrases, int? durationMs)
    {
        if (phrases.Count == 0)
        {
            return null;
        }

        if (ScoreProblem(phrases) is { } scores)
        {
            return scores;
        }

        if (TimingProblem(phrases) is { } timing)
        {
            return timing;
        }

        var words = phrases.SelectMany(p => p.Words).ToList();
        if (durationMs is { } ms && ms >= 0)
        {
            var spoken = words.Sum(w => w.DurationSeconds);
            var allowed = ms / 1000d * DurationSlackRatio + DurationSlackSeconds;
            if (spoken > allowed)
            {
                return $"The words last {spoken:0.0} s but the turn took {ms / 1000d:0.0} s.";
            }
        }

        var overlap = Overlap(words.Select(w => w.Word), Tokens(text));
        if (overlap < MinTextOverlap)
        {
            return $"Only {overlap:P0} of the assessed words appear in the turn's text.";
        }

        return null;
    }

    /// <summary>Every score Azure reports is on a 0-100 scale.</summary>
    internal static string? ScoreProblem(IEnumerable<AssessedPhrase> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (!InRange(phrase.Accuracy) || !InRange(phrase.Fluency) || !InRange(phrase.Prosody)
                || phrase.Words.Any(w => !InRange(w.Accuracy)))
            {
                return "A score is outside 0-100.";
            }
        }

        return null;
    }

    /// <summary>Within a phrase, words start in order and none has a negative start or length.</summary>
    internal static string? TimingProblem(IEnumerable<AssessedPhrase> phrases)
    {
        foreach (var phrase in phrases)
        {
            double previous = 0;
            foreach (var word in phrase.Words)
            {
                if (word.StartSeconds < 0 || word.DurationSeconds < 0 || !double.IsFinite(word.EndSeconds))
                {
                    return "A word has a negative or invalid timing.";
                }

                if (word.StartSeconds < previous)
                {
                    return "Word timings are not in order.";
                }

                previous = word.StartSeconds;
            }
        }

        return null;
    }

    /// <summary>Share of the words in <paramref name="words"/> found in <paramref name="reference"/> (both tokenised), each reference word used once.</summary>
    internal static double Overlap(IEnumerable<string> words, IEnumerable<string> reference)
    {
        var available = new Dictionary<string, int>();
        foreach (var token in reference.SelectMany(Tokens))
        {
            available[token] = available.GetValueOrDefault(token) + 1;
        }

        int total = 0, matched = 0;
        foreach (var token in words.SelectMany(Tokens))
        {
            total++;
            if (available.GetValueOrDefault(token) > 0)
            {
                available[token]--;
                matched++;
            }
        }

        return total == 0 ? 1 : matched / (double)total;
    }

    /// <summary>Lower-case words without punctuation; hyphens and slashes split words.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text)
        {
            var c = raw is '’' or '‘' ? '\'' : char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c) || c == '\'')
            {
                current.Append(c);
            }
            else if (Flush(current) is { } token)
            {
                yield return token;
            }
        }

        if (Flush(current) is { } last)
        {
            yield return last;
        }
    }

    private static string? Flush(StringBuilder current)
    {
        var token = current.ToString().Trim('\'');
        current.Clear();
        return token.Length > 0 ? token : null;
    }

    private static bool InRange(double? score) => score is not { } s || (s >= 0 && s <= 100);
}
