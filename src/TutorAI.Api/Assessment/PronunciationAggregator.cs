namespace TutorAI.Api.Assessment;

public sealed record WeakWord(string Word, double Score, int Count);

/// <param name="Composite">The single 0-100 figure mapped to a CEFR level: accuracy blended with prosody when available.</param>
public sealed record PronunciationStats(
    int AssessedWords,
    double? Accuracy,
    double? Prosody,
    double? Composite,
    IReadOnlyList<WeakWord> WeakestWords);

public static class PronunciationAggregator
{
    public const double AccuracyWeight = 0.6;
    public const double WeakWordThreshold = 60;
    public const int MaxWeakWords = 10;

    public static PronunciationStats Compute(IEnumerable<AssessedPhrase> phrases)
    {
        var list = phrases as IReadOnlyList<AssessedPhrase> ?? [.. phrases];
        var accuracy = AzurePronunciation.WeightedMean(list, p => p.Accuracy);
        var prosody = AzurePronunciation.WeightedMean(list, p => p.Prosody);

        double? composite = (accuracy, prosody) switch
        {
            ({ } a, { } p) => AccuracyWeight * a + (1 - AccuracyWeight) * p,
            ({ } a, null) => a,
            _ => null,
        };

        var weakest = list
            .SelectMany(p => p.Words)
            .Where(w => w.Accuracy is { } score && (score < WeakWordThreshold || w.ErrorType == "Mispronunciation"))
            .GroupBy(w => w.Word.Trim('.', ',', '?', '!').ToLowerInvariant())
            .Where(g => g.Key.Length > 0)
            .Select(g => new WeakWord(g.Key, Round(g.Min(w => w.Accuracy!.Value)), g.Count()))
            .OrderBy(w => w.Score)
            .ThenByDescending(w => w.Count)
            .Take(MaxWeakWords)
            .ToList();

        return new PronunciationStats(
            AssessedWords: list.Sum(p => p.Words.Count),
            Accuracy: accuracy is { } acc ? Round(acc) : null,
            Prosody: prosody is { } pro ? Round(pro) : null,
            Composite: composite is { } c ? Round(c) : null,
            WeakestWords: weakest);
    }

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
