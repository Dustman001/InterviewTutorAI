namespace TutorAI.Api;

/// <summary>USD per million tokens for one model (configuration section Pricing:&lt;model id&gt;).</summary>
public sealed class ModelPrice
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CacheReadPerMTok { get; set; }
    /// <summary>The 5-minute cache write price, 1.25x input.</summary>
    public decimal CacheWritePerMTok { get; set; }
}

public sealed record TokenTotals(long Input, long Output, long CacheRead, long CacheWrite)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
}

/// <param name="UnpricedModels">Models used but missing from configuration; their tokens are counted but not costed.</param>
public sealed record UsageEstimate(long Tokens, decimal EstimatedCostUsd, IReadOnlyList<string> UnpricedModels);

public static class CostEstimator
{
    /// <summary>Estimated cost of the given token totals per model. Prices come from configuration, not from Anthropic.</summary>
    public static UsageEstimate Estimate(
        IReadOnlyDictionary<string, ModelPrice> prices, IEnumerable<(string Model, TokenTotals Tokens)> usage)
    {
        long tokens = 0;
        decimal cost = 0;
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (model, totals) in usage)
        {
            tokens += totals.Total;
            if (prices.TryGetValue(model, out var price))
            {
                cost += (totals.Input * price.InputPerMTok
                         + totals.Output * price.OutputPerMTok
                         + totals.CacheRead * price.CacheReadPerMTok
                         + totals.CacheWrite * price.CacheWritePerMTok) / 1_000_000m;
            }
            else if (totals.Total > 0)
            {
                unpriced.Add(model);
            }
        }

        return new UsageEstimate(tokens, Math.Round(cost, 4), [.. unpriced]);
    }
}
