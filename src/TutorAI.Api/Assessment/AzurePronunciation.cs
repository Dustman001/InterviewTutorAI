using System.Text.Json;

namespace TutorAI.Api.Assessment;

public sealed record AssessedWord(string Word, double StartSeconds, double DurationSeconds, double? Accuracy, string? ErrorType)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

public sealed record AssessedPhrase(double? Accuracy, double? Fluency, double? Prosody, IReadOnlyList<AssessedWord> Words);

/// <summary>Reads the detailed-result JSON Azure Speech returns for a phrase recognized with pronunciation assessment.</summary>
public static class AzurePronunciation
{
    private const double TicksPerSecond = 10_000_000d;

    public static IReadOnlyList<AssessedPhrase> ParseTurn(string? pronunciationJson)
    {
        if (string.IsNullOrWhiteSpace(pronunciationJson))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(pronunciationJson);
        return ParseTurn(doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray()] : [doc.RootElement]);
    }

    public static IReadOnlyList<AssessedPhrase> ParseTurn(IEnumerable<JsonElement>? phrases)
    {
        if (phrases is null)
        {
            return [];
        }

        var result = new List<AssessedPhrase>();
        foreach (var phrase in phrases)
        {
            if (ParsePhrase(phrase) is { Words.Count: > 0 } parsed)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    public static AssessedPhrase? ParsePhrase(JsonElement phrase)
    {
        if (phrase.ValueKind != JsonValueKind.Object
            || !phrase.TryGetProperty("NBest", out var nbest)
            || nbest.ValueKind != JsonValueKind.Array
            || nbest.GetArrayLength() == 0)
        {
            return null;
        }

        var best = nbest[0];
        var words = new List<AssessedWord>();
        if (best.TryGetProperty("Words", out var wordsElement) && wordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsElement.EnumerateArray())
            {
                var text = w.TryGetProperty("Word", out var wt) ? wt.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                double? accuracy = null;
                string? errorType = null;
                if (w.TryGetProperty("PronunciationAssessment", out var wpa))
                {
                    accuracy = Number(wpa, "AccuracyScore");
                    errorType = wpa.TryGetProperty("ErrorType", out var et) ? et.GetString() : null;
                }

                // Omitted words were never spoken and carry no timing.
                if (errorType == "Omission")
                {
                    continue;
                }

                words.Add(new AssessedWord(
                    text,
                    (Number(w, "Offset") ?? 0) / TicksPerSecond,
                    (Number(w, "Duration") ?? 0) / TicksPerSecond,
                    accuracy,
                    errorType));
            }
        }

        double? phraseAccuracy = null, fluency = null, prosody = null;
        if (best.TryGetProperty("PronunciationAssessment", out var pa))
        {
            phraseAccuracy = Number(pa, "AccuracyScore");
            fluency = Number(pa, "FluencyScore");
            prosody = Number(pa, "ProsodyScore");
        }

        return new AssessedPhrase(phraseAccuracy, fluency, prosody, words);
    }

    /// <summary>Word-count-weighted mean of a phrase-level score, ignoring phrases that lack it.</summary>
    public static double? WeightedMean(IEnumerable<AssessedPhrase> phrases, Func<AssessedPhrase, double?> score)
    {
        double sum = 0, weight = 0;
        foreach (var p in phrases)
        {
            if (score(p) is { } value)
            {
                sum += value * p.Words.Count;
                weight += p.Words.Count;
            }
        }

        return weight > 0 ? sum / weight : null;
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
