using System.Text.Json;

namespace TutorAI.Tests;

/// <summary>Builds phrase objects shaped like Azure Speech's detailed result with pronunciation assessment.</summary>
internal static class AzureSamples
{
    public sealed record Word(string Text, double Start, double Duration, double Accuracy = 90, string ErrorType = "None");

    public static JsonElement Phrase(double? accuracy, double? fluency, double? prosody, params Word[] words)
    {
        var assessment = new Dictionary<string, object>();
        if (accuracy is { } a) assessment["AccuracyScore"] = a;
        if (fluency is { } f) assessment["FluencyScore"] = f;
        if (prosody is { } p) assessment["ProsodyScore"] = p;

        return JsonSerializer.SerializeToElement(new
        {
            RecognitionStatus = "Success",
            DisplayText = string.Join(' ', words.Select(w => w.Text)),
            NBest = new[]
            {
                new
                {
                    Confidence = 0.95,
                    PronunciationAssessment = assessment,
                    Words = words.Select(w => new
                    {
                        Word = w.Text,
                        Offset = (long)(w.Start * 10_000_000),
                        Duration = (long)(w.Duration * 10_000_000),
                        PronunciationAssessment = new { AccuracyScore = w.Accuracy, w.ErrorType },
                    }),
                },
            },
        });
    }

    /// <summary>Evenly spaced words: each lasts <paramref name="wordSeconds"/> and is followed by <paramref name="gapSeconds"/> of silence.</summary>
    public static Word[] Words(int count, double start = 0, double wordSeconds = 0.3, double gapSeconds = 0.1) =>
        [.. Enumerable.Range(0, count).Select(i => new Word($"word{i}", start + i * (wordSeconds + gapSeconds), wordSeconds))];
}
