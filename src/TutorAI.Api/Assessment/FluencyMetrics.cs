namespace TutorAI.Api.Assessment;

/// <param name="TotalWords">All user words in the session, including turns without timings.</param>
/// <param name="TimedWords">Words that carry Azure timings; the rate and pause figures are computed from these only.</param>
public sealed record FluencyStats(
    int TotalWords,
    int TimedWords,
    double SpeakingSeconds,
    double? WordsPerMinute,
    double? PauseRatio,
    double? LongPausesPerMinute,
    double? MeanRunLength,
    int FillerCount,
    double? FillersPer100Words,
    double? AzureFluencyScore);

public static class FluencyMetrics
{
    public const double PauseSeconds = 0.25;
    public const double LongPauseSeconds = 1.0;

    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "uhm", "umm", "er", "erm", "hmm", "mm", "ah", "eh",
    };

    /// <param name="turns">One entry per user turn: the turn's word count from its text, and its assessed phrases (may be empty).</param>
    public static FluencyStats Compute(IEnumerable<(int WordCount, IReadOnlyList<AssessedPhrase> Phrases)> turns)
    {
        int totalWords = 0, timedWords = 0, fillers = 0, pauses = 0, longPauses = 0;
        double speakingSeconds = 0, pauseSeconds = 0;
        var allPhrases = new List<AssessedPhrase>();

        foreach (var (wordCount, phrases) in turns)
        {
            totalWords += wordCount;
            allPhrases.AddRange(phrases);

            var words = phrases.SelectMany(p => p.Words).OrderBy(w => w.StartSeconds).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            timedWords += words.Count;
            fillers += words.Count(w => Fillers.Contains(w.Word.Trim('.', ',', '?', '!')));
            speakingSeconds += Math.Max(0, words[^1].EndSeconds - words[0].StartSeconds);

            for (var i = 1; i < words.Count; i++)
            {
                var gap = words[i].StartSeconds - words[i - 1].EndSeconds;
                if (gap >= PauseSeconds)
                {
                    pauses++;
                    pauseSeconds += gap;
                    if (gap >= LongPauseSeconds)
                    {
                        longPauses++;
                    }
                }
            }
        }

        var hasTimings = timedWords > 0 && speakingSeconds > 0;
        var minutes = speakingSeconds / 60d;
        return new FluencyStats(
            TotalWords: totalWords,
            TimedWords: timedWords,
            SpeakingSeconds: Round(speakingSeconds),
            WordsPerMinute: hasTimings ? Round(timedWords / minutes) : null,
            PauseRatio: hasTimings ? Round(pauseSeconds / speakingSeconds, 3) : null,
            LongPausesPerMinute: hasTimings ? Round(longPauses / minutes) : null,
            MeanRunLength: hasTimings ? Round(timedWords / (double)(pauses + 1)) : null,
            FillerCount: fillers,
            FillersPer100Words: timedWords > 0 ? Round(fillers * 100d / timedWords) : null,
            AzureFluencyScore: AzurePronunciation.WeightedMean(allPhrases, p => p.Fluency) is { } f ? Round(f) : null);
    }

    private static double Round(double value, int digits = 1) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
