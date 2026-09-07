using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Scanner;

public class ExecutionAssessmentTests
{
    private static Opportunity Opportunity()
    {
        var metrics = new OpportunityMetrics(null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, 4, 10_000_000, null, null, null, null, null);
        return new(Xrp, T.Base, 100, 1, 90, new([], [], 90, 90, 1),
            new(SetupType.TrendPullback, Confidence.High, TrendBias.Bullish, [], null, null),
            new(99, 101, "confirm", 98, 98, 106, 108, 110, 2, 3, 4, 5, [], 101.2, EntryState.InZone),
            new(0, false, [], null, null, null, null, null, null, null), [], "", [], metrics, null, new(false, 0, true, "test", "test"));
    }

    [Fact]
    public void Calculates_costs_on_both_sides_and_never_claims_a_buy_prediction()
    {
        var result = ExecutionAssessor.Assess(Opportunity(), Market(), new() { FeeBps = 10, SlippageBps = 5 });
        Assert.Equal("Watch", result.Status);
        var rate = 17.0 / 10_000;
        var reward = 6 - 100 * rate - 106 * rate;
        var risk = 2 + 100 * rate + 98 * rate;
        Assert.Equal(reward / risk, result.NetRewardRatio!.Value, 9);
        Assert.Equal(risk / (risk + reward), result.BreakEvenWinRate!.Value, 9);
        var ceiling = result.MaxEntryPriceAfterCosts!.Value;
        var ceilingReward = 106 * (1 - rate) - ceiling * (1 + rate);
        var ceilingRisk = ceiling * (1 + rate) - 98 * (1 - rate);
        Assert.Equal(1.5, ceilingReward / ceilingRisk, 9);
    }

    [Fact]
    public void Fees_can_veto_an_attractive_gross_plan()
    {
        var o = Opportunity();
        var result = ExecutionAssessor.Assess(o with { Plan = o.Plan! with { Target1 = 101 } }, Market(), new());
        Assert.Equal("Blocked", result.Status);
        Assert.True(result.NetRewardPerUnit < 0);
        Assert.Null(result.BreakEvenWinRate);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("history")]
    [InlineData("spread")]
    [InlineData("liquidity")]
    [InlineData("chase")]
    [InlineData("future")]
    public void High_scores_do_not_override_execution_vetoes(string reason)
    {
        var o = Opportunity();
        o = reason switch {
            "stale" => o with { Quality = o.Quality with { Stale = true } },
            "history" => o with { Quality = o.Quality with { HistoryLoaded = false } },
            "spread" => o with { Metrics = o.Metrics with { SpreadBps = null } },
            "liquidity" => o with { Metrics = o.Metrics with { Volume24hQuote = 10 } },
            "future" => o with { Quality = o.Quality with { AgeMs = -5000 } },
            _ => o with { Plan = o.Plan! with { EntryState = EntryState.Chase } }
        };
        Assert.Equal("Blocked", ExecutionAssessor.Assess(o, Market(), new()).Status);
    }

    [Fact]
    public void Late_price_is_visible_as_a_wait_not_misclassified_as_a_chase()
    {
        var o = Opportunity();
        o = o with { Plan = o.Plan! with { EntryState = EntryState.Late } };

        var result = ExecutionAssessor.Assess(o, Market(), new() { FeeBps = 10, SlippageBps = 5 });

        Assert.Equal("Watch", result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("wait for a pullback", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(60, "Watch")]
    [InlineData(59.99, "Blocked")]
    public void Default_execution_score_gate_matches_the_active_setup_threshold(double score, string expectedStatus)
    {
        // Keep the cost model deliberately permissive here so this test isolates the default
        // score gate instead of failing on the fixture's compact first target.
        var result = ExecutionAssessor.Assess(Opportunity() with { Score = score }, Market(), new() { FeeBps = 10, SlippageBps = 5 });

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public void Missing_btc_or_dumping_market_is_blocked()
    {
        Assert.Equal("Blocked", ExecutionAssessor.Assess(Opportunity(), Market() with { Btc = null }, new()).Status);
        Assert.Equal("Blocked", ExecutionAssessor.Assess(Opportunity(), Market(dumping: true), new()).Status);
    }

    [Fact]
    public void Invalid_configuration_and_nonfinite_spreads_fail_closed()
    {
        Assert.Equal("Blocked", ExecutionAssessor.Assess(Opportunity(), Market(), new() { FeeBps = double.NaN }).Status);
        var o = Opportunity();
        var r = ExecutionAssessor.Assess(o with { Metrics = o.Metrics with { SpreadBps = double.PositiveInfinity } }, Market(), new());
        Assert.Equal("Blocked", r.Status);
        Assert.Null(r.NetRewardRatio);
    }

    [Fact]
    public void Nearby_resistance_is_not_skipped_to_manufacture_reward()
    {
        var setup = new SetupClassification(SetupType.TrendPullback, Confidence.High, TrendBias.Bullish, [], null, null);
        var p = Proj(s5: Struct(levels: [Level("wall", 1.4001)]));
        var plan = TradePlanBuilder.Build(setup, p)!;
        Assert.Equal(1.4001, plan.Target1, 8);
        Assert.True(plan.RewardRatio1 < 1);
    }
}
