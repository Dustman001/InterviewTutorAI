using TutorAI.Api;

namespace TutorAI.Tests;

public class CostEstimatorTests
{
    private static readonly Dictionary<string, ModelPrice> Prices = new()
    {
        ["claude-sonnet-5-5"] = new() { InputPerMTok = 2, OutputPerMTok = 10, CacheReadPerMTok = 0.2m, CacheWritePerMTok = 2.5m },
        ["claude-opus-5-5"] = new() { InputPerMTok = 4, OutputPerMTok = 20, CacheReadPerMTok = 0.2m, CacheWritePerMTok = 5 },
    };

    [Fact]
    public void Each_token_kind_is_priced_per_million()
    {
        var estimate = CostEstimator.Estimate(Prices, [("claude-sonnet-5-5", new TokenTotals(1_000_000, 100_000, 2_000_000, 400_000))]);

        // 2 + 1 + 0.4 + 1
        Assert.Equal(4.4m, estimate.EstimatedCostUsd);
        Assert.Equal(3_500_000, estimate.Tokens);
        Assert.Empty(estimate.UnpricedModels);
    }

    [Fact]
    public void Models_are_summed_and_unpriced_ones_are_counted_but_listed()
    {
        var estimate = CostEstimator.Estimate(Prices,
        [
            ("claude-sonnet-5-5", new TokenTotals(10_000, 2_000, 50_000, 1_000)),
            ("claude-opus-5-5", new TokenTotals(6_000, 3_000, 0, 0)),
            ("some-new-model", new TokenTotals(1_000, 1_000, 0, 0)),
        ]);

        // Sonnet 0.02 + 0.02 + 0.01 + 0.0025, Opus 0.024 + 0.06.
        Assert.Equal(0.1365m, estimate.EstimatedCostUsd);
        Assert.Equal(74_000, estimate.Tokens);
        Assert.Equal(["some-new-model"], estimate.UnpricedModels);
    }

    [Fact]
    public void No_usage_costs_nothing()
    {
        var estimate = CostEstimator.Estimate(Prices, []);

        Assert.Equal((0L, 0m), (estimate.Tokens, estimate.EstimatedCostUsd));
    }
}
