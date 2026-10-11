namespace TutorAI.Api.Assessment;

public static class CefrMapper
{
    /// <summary>Ordered lowest to highest.</summary>
    public static readonly IReadOnlyList<string> Levels = ["A1", "A2", "A2+", "B1", "B1+", "B2", "B2+", "C1", "C2"];

    // Heuristic: Azure does not publish a CEFR mapping for its 0-100 pronunciation scores.
    private static readonly (double Min, string Level)[] PronunciationThresholds =
    [
        (95, "C2"),
        (90, "C1"),
        (85, "B2+"),
        (80, "B2"),
        (74, "B1+"),
        (68, "B1"),
        (60, "A2+"),
        (50, "A2"),
    ];

    public static bool IsValid(string? level) => level is not null && Levels.Contains(level);

    public static string FromPronunciationScore(double composite)
    {
        foreach (var (min, level) in PronunciationThresholds)
        {
            if (composite >= min)
            {
                return level;
            }
        }

        return "A1";
    }

    /// <summary>Median of the given levels (lower middle for an even count); null when none are valid.</summary>
    public static string? Median(IEnumerable<string?> levels)
    {
        var indexes = levels
            .Where(IsValid)
            .Select(l => IndexOf(l!))
            .Order()
            .ToList();

        return indexes.Count == 0 ? null : Levels[indexes[(indexes.Count - 1) / 2]];
    }

    private static int IndexOf(string level)
    {
        for (var i = 0; i < Levels.Count; i++)
        {
            if (Levels[i] == level)
            {
                return i;
            }
        }

        return -1;
    }
}
