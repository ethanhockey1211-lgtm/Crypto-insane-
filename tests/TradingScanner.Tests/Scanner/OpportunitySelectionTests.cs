using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Scanner;

public class OpportunitySelectionTests
{
    private static readonly ScannerOptions Options = new() { Execution = new() { FeeBps = 0, SlippageBps = 5 } };

    private static ScanInput Input(double heldLevel = 97, double resistance = 110)
    {
        const double price = 100.25;
        var held = Status(Level("held", heldLevel, 3), BreakoutState.RetestHeld, barsSince: 8,
            retest: new RetestMetrics(0.1, 0, 2, 0.8, 1.8, 2, true), narrative: "held retest");
        var fresh = Status(Level("fresh", 100, 3), BreakoutState.Confirmed, barsSince: 1, narrative: "fresh confirmed break");
        var p = Proj(price: price,
            m5: Ind(close: price, atr: 2, ema20: 100, distEma20Atr: 0.125, relVol: 2.2, fastRatio: 1.5, buyShare: 0.66),
            m15: Ind(Timeframe.M15, close: price, atr: 2),
            s5: Struct(close: price, levels: [held.Level, fresh.Level, Level("wall", resistance)]),
            s15: Struct(Timeframe.M15, close: price), vwap: Vwap(vwap: 100, price: price, std: 2));
        var snap = new AnalyticsSnapshot(Xrp, T.Base, p.Timeframes, null, null, p.Structure, null);
        var quote = new PriceQuote(Xrp, (decimal)price, 100.23m, 100.27m, "test", "test", T.Base, T.Base);
        return new ScanInput(Xrp, snap, p, quote, 50_000_000, Breakouts(held, null, fresh), true);
    }

    private static Opportunity Evaluate(ScanInput input, MarketContext? market = null, ScannerOptions? options = null) =>
        OpportunityEvaluator.Evaluate(input, market ?? Market(), null, null, T.Base, options ?? Options);

    [Fact]
    public void Spent_retest_does_not_hide_a_fresh_break_that_passes_cost_and_data_checks()
    {
        var input = Input();
        Assert.Equal(SetupType.BreakoutRetest, SetupClassifier.Classify(input.Projection, input.Breakouts, Market()).Type);

        var result = Evaluate(input);

        Assert.Equal(SetupType.Breakout, result.Setup.Type);
        Assert.Equal("fresh", result.Setup.Breakout!.Level.Id);
        Assert.Equal("Watch", result.Execution!.Status);
        Assert.Equal(EntryState.InZone, result.Plan!.EntryState);
        Assert.True(result.Execution.NetRewardRatio >= Options.Execution.MinNetRewardRatio);
        Assert.Contains("fresh confirmed break", result.Why);
        Assert.DoesNotContain("held retest", result.Why);
        Assert.Contains(result.Breakdown.Components, c => c.Name == OpportunityScorer.Breakout && c.Evidence.Contains("fresh confirmed break"));
    }

    [Fact]
    public void Existing_priority_is_preserved_when_the_retest_is_also_feasible()
    {
        var result = Evaluate(Input(heldLevel: 100.1));

        Assert.Equal(SetupType.BreakoutRetest, result.Setup.Type);
        Assert.Equal("Watch", result.Execution!.Status);
    }

    [Fact]
    public void A_matching_momentum_setup_is_evaluated_when_the_preferred_retest_is_spent()
    {
        var input = Input();
        input = input with { Breakouts = Breakouts(input.Breakouts!.BestUp) };

        var result = Evaluate(input);

        Assert.Equal(SetupType.MomentumContinuation, result.Setup.Type);
        Assert.Equal("Watch", result.Execution!.Status);
        Assert.True(result.Execution.NetRewardRatio >= Options.Execution.MinNetRewardRatio);
        Assert.Contains("continuation on a 5m close", result.Plan!.Trigger);
    }

    [Fact]
    public void Resistance_above_live_price_but_below_zone_midpoint_still_caps_the_target()
    {
        var input = Input(resistance: 100.26);
        var fresh = SetupClassifier.ClassifyCandidates(input.Projection, input.Breakouts, Market())
            .Single(c => c.Type == SetupType.Breakout);

        var plan = TradePlanBuilder.Build(fresh, input.Projection)!;

        Assert.True(plan.EntryMid > 100.26);
        Assert.Equal(100.26, plan.Target1, 8);
        Assert.True(plan.RewardRatio1 < 0);
        Assert.Equal(EntryState.Chase, plan.EntryState);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("history")]
    [InlineData("liquidity")]
    [InlineData("spread")]
    [InlineData("btc")]
    [InlineData("risk-off")]
    [InlineData("fees")]
    [InlineData("resistance")]
    [InlineData("score")]
    [InlineData("overextended")]
    public void Alternatives_cannot_bypass_shared_execution_vetoes(string veto)
    {
        var input = Input(resistance: veto == "resistance" ? 100.26 : 110);
        input = veto switch
        {
            "stale" => input with { Quote = input.Quote with { ExchangeTime = T.Base.AddMinutes(-1) } },
            "future" => input with { Quote = input.Quote with { ExchangeTime = T.Base.AddSeconds(5) } },
            "history" => input with { HistoryLoaded = false },
            "liquidity" => input with { Volume24hQuote = 100 },
            "spread" => input with { Quote = input.Quote with { Bid = 99, Ask = 101 } },
            "overextended" => input with { Projection = input.Projection with { Vwap = Vwap(vwap: 95, price: 100.25, std: 1) } },
            _ => input,
        };
        var market = veto == "btc" ? Market() with { Btc = null } : Market(dumping: veto == "risk-off");
        var options = veto switch
        {
            "fees" => new ScannerOptions { Execution = new() { FeeBps = 60 } },
            "score" => new ScannerOptions { Execution = new() { FeeBps = 0, MinScore = 100 } },
            _ => Options,
        };

        var result = Evaluate(input, market, options);

        Assert.Equal("Blocked", result.Execution!.Status);
        Assert.Equal(SetupType.BreakoutRetest, result.Setup.Type);
    }
}
