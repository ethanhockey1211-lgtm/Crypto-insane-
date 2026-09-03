using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Scanner;

public class OverextensionAnalyzerTests
{
    [Fact]
    public void Calm_price_near_vwap_and_ema_is_not_extended()
    {
        var p = Proj(mom: Mom(r5m: 0.001, r15m: 0.003, r1h: 0.005, r24h: 0.01), vwap: Vwap(vwap: 1.395, price: 1.4, std: 0.02), m5: Ind(distEma20Atr: 0.3, ema20: 1.397), s5: Struct(levels: Level("S", 1.38)));
        var a = OverextensionAnalyzer.Assess(p, new OverextensionConfig());
        Assert.False(a.DoNotChase);
        Assert.True(a.Score < 0.3, $"score {a.Score}");
        Assert.Empty(a.Flags);
    }

    [Fact]
    public void Vertical_move_far_above_vwap_is_flagged_do_not_chase()
    {
        var p = Proj(price: 1.5, mom: Mom(r5m: 0.03, r15m: 0.06, r1h: 0.12, r24h: 0.30, accel5: 0.01, accel15: 0.01), vwap: Vwap(vwap: 1.40, price: 1.5, std: 0.025), m5: Ind(close: 1.5, ema20: 1.45, atr: 0.01, rsi: 85, volumeZ: 5), s5: Struct(close: 1.5, levels: Level("S", 1.40)));
        var a = OverextensionAnalyzer.Assess(p, new OverextensionConfig());
        Assert.True(a.DoNotChase);
        Assert.True(a.Score > 0.8, $"score {a.Score}");
        Assert.Contains(a.Flags, f => f.Contains("σ above session VWAP"));
        Assert.Contains(a.Flags, f => f.Contains("ATR in 5 minutes"));
        Assert.Contains(a.Flags, f => f.Contains("volume climax"));
        Assert.Contains(a.Flags, f => f.Contains("24h"));
        Assert.Contains(a.Flags, f => f.Contains("parabolic"));
    }

    [Fact]
    public void Ramp_is_clamped_linear()
    {
        Assert.Equal(0, OverextensionAnalyzer.Ramp(0, 1, 3));
        Assert.Equal(0.5, OverextensionAnalyzer.Ramp(2, 1, 3), 9);
        Assert.Equal(1, OverextensionAnalyzer.Ramp(10, 1, 3));
    }
}

public class SetupClassifierTests
{
    private static readonly PriceLevel R = Level("R", 1.40, 3);

    [Fact]
    public void Retest_held_beats_everything_and_is_high_confidence_with_confirmations()
    {
        var b = Breakouts(Status(R, BreakoutState.RetestHeld, barsSince: 3, retest: new RetestMetrics(0.2, 0, 2, 0.8, 1.8, 2, true), narrative: "Retest held: reclaimed 1.4000"));
        var c = SetupClassifier.Classify(Proj(price: 1.412), b, Market());
        Assert.Equal(SetupType.BreakoutRetest, c.Type);
        Assert.Equal(Confidence.High, c.Confidence);
        Assert.Equal(1.40, c.KeyLevel);
        Assert.Contains(c.Evidence, e => e.Contains("Retest held"));
        Assert.Contains(c.Evidence, e => e.Contains("breakout bar volume 2.1×"));
    }

    [Fact]
    public void Fresh_confirmed_breakout_at_the_range_high_is_a_range_breakout()
    {
        var b = Breakouts(Status(R, BreakoutState.Confirmed, barsSince: 1));
        var c = SetupClassifier.Classify(Proj(price: 1.408, s5: Struct(close: 1.408, rangeHigh: 1.402, rangeLow: 1.30, levels: R)), b, Market());
        Assert.Equal(SetupType.RangeBreakout, c.Type);
        Assert.Contains(c.Evidence, e => e.Contains("range high"));

        var plain = SetupClassifier.Classify(Proj(price: 1.408, s5: Struct(close: 1.408, rangeHigh: 1.45, rangeLow: 1.30, levels: R)), b, Market());
        Assert.Equal(SetupType.Breakout, plain.Type);

        var weak = SetupClassifier.Classify(Proj(price: 1.408), Breakouts(Status(R, BreakoutState.Confirmed, weak: true, relVol: 1.0)), Market(btcTrend: TrendBias.Neutral));
        Assert.Equal(SetupType.Breakout, weak.Type);
        Assert.True(weak.Confidence < Confidence.High);
    }

    [Fact]
    public void Stale_confirmed_breakouts_no_longer_qualify()
    {
        var b = Breakouts(Status(R, BreakoutState.Confirmed, barsSince: 9));
        var c = SetupClassifier.Classify(Proj(price: 1.408, m5: Ind(align: EmaAlignment.Mixed, relVol: 0.9), mom: Mom(accel5: -0.001)), b, Market());
        Assert.NotEqual(SetupType.Breakout, c.Type);
    }

    [Fact]
    public void Vwap_reclaim_needs_time_below_and_volume()
    {
        var state = new VwapState(true, 2, 25, CrossDirection.Bullish, T.Base, 1.6);
        var c = SetupClassifier.Classify(Proj(price: 1.4, vwapState: state, m5: Ind(align: EmaAlignment.Mixed, relVol: 1.0), mom: Mom(accel5: -0.001)), null, Market());
        Assert.Equal(SetupType.VwapReclaim, c.Type);
        Assert.Contains(c.Evidence, e => e.Contains("reclaimed session VWAP"));

        var tooSoon = new VwapState(true, 2, 4, CrossDirection.Bullish, T.Base, 1.6);
        var c2 = SetupClassifier.Classify(Proj(price: 1.4, vwapState: tooSoon, m5: Ind(align: EmaAlignment.Mixed, relVol: 1.0), mom: Mom(accel5: -0.001)), null, Market());
        Assert.NotEqual(SetupType.VwapReclaim, c2.Type);

        var noVolume = new VwapState(true, 2, 25, CrossDirection.Bullish, T.Base, 0.7);
        var c3 = SetupClassifier.Classify(Proj(price: 1.4, vwapState: noVolume, m5: Ind(align: EmaAlignment.Mixed, relVol: 0.8), mom: Mom(accel5: -0.001)), null, Market());
        Assert.NotEqual(SetupType.VwapReclaim, c3.Type);
    }

    [Fact]
    public void Support_bounce_is_a_wick_below_support_that_closed_back_above_on_an_up_bar()
    {
        var s = Level("S", 1.38, 4);
        var bounce = Status(s, BreakoutState.Attempt, BreakoutDirection.Down, barsSince: 0, distance: -0.4, barsInState: 0, narrative: "Wicked below 1.3800");
        var c = SetupClassifier.Classify(Proj(price: 1.385, m5: Ind(close: 1.385, open: 1.379, align: EmaAlignment.Mixed, relVol: 1.3), mom: Mom(accel5: -0.001, r15m: -0.002)), Breakouts(null, bounce), Market());
        Assert.Equal(SetupType.SupportBounce, c.Type);
        Assert.Equal(1.38, c.KeyLevel);
        Assert.Contains(c.Evidence, e => e.Contains("closed back above"));
    }

    [Fact]
    public void Reversal_needs_a_recent_bullish_divergence_outside_an_uptrend()
    {
        var div = new Divergence(DivergenceType.BullishRsi, T.Base, 98, 1.42, 1.40, 28, 35);
        var s5 = Struct(trend: StructureTrend.Downtrend, divergences: [div], barIndex: 100);
        var c = SetupClassifier.Classify(Proj(price: 1.405, m5: Ind(close: 1.405, open: 1.401, align: EmaAlignment.Bearish, relVol: 1.1), m15: Ind(Timeframe.M15, close: 1.405, align: EmaAlignment.Bearish), s5: s5, mom: Mom(accel5: -0.001, r15m: -0.004), vwap: Vwap(vwap: 1.41, price: 1.405)), null, Market());
        Assert.Equal(SetupType.Reversal, c.Type);
        Assert.Contains(c.Evidence, e => e.Contains("RSI divergence"));
        Assert.Equal(TrendBias.Bearish, c.Bias);
    }

    [Fact]
    public void Trend_pullback_and_momentum_continuation_are_distinguished_by_distance_from_ema20()
    {
        var pull = SetupClassifier.Classify(Proj(price: 1.4, m5: Ind(distEma20Atr: -0.2, rsi: 48, relVol: 1.0), m15: Ind(Timeframe.M15, align: EmaAlignment.Bullish), mom: Mom(accel5: 0.001, r15m: 0.001)), null, Market());
        Assert.Equal(SetupType.TrendPullback, pull.Type);

        var cont = SetupClassifier.Classify(Proj(price: 1.4, m5: Ind(distEma20Atr: 1.0, rsi: 64, relVol: 1.6), m15: Ind(Timeframe.M15, align: EmaAlignment.Bullish), mom: Mom(accel5: 0.002, r15m: 0.01)), null, Market());
        Assert.Equal(SetupType.MomentumContinuation, cont.Type);
        Assert.Equal(Confidence.High, cont.Confidence);
    }

    [Fact]
    public void Nothing_qualifies_in_bearish_structure_without_a_trigger()
    {
        var c = SetupClassifier.Classify(Proj(price: 1.4, m5: Ind(align: EmaAlignment.Bearish, relVol: 0.8, rsi: 40), m15: Ind(Timeframe.M15, align: EmaAlignment.Bearish), s15: Struct(Timeframe.M15, trend: StructureTrend.Downtrend), vwap: Vwap(vwap: 1.42, price: 1.4), mom: Mom(r5m: -0.003, r15m: -0.01, accel5: -0.001)), null, Market());
        Assert.Equal(SetupType.None, c.Type);
        Assert.Equal(TrendBias.Bearish, c.Bias);
    }
}

public class TradePlanBuilderTests
{
    private static readonly PriceLevel R = Level("R", 1.40, 3);

    [Fact]
    public void Breakout_plan_puts_the_stop_under_the_level_and_caps_targets_at_resistance()
    {
        var setup = new SetupClassification(SetupType.Breakout, Confidence.High, TrendBias.Bullish, [], Status(R, BreakoutState.Confirmed), 1.40);
        var p = Proj(price: 1.406, s5: Struct(close: 1.406, levels: [R, Level("R2", 1.412), Level("R3", 1.45)]));
        var plan = TradePlanBuilder.Build(setup, p)!;
        Assert.Equal(1.40, plan.EntryLow, 9);
        Assert.InRange(plan.EntryHigh, 1.403, 1.41);
        Assert.Equal(1.397, plan.Stop, 9);
        Assert.Equal(1.398, plan.Invalidation, 9);
        Assert.Equal(1.403, plan.EntryMid, 9);
        Assert.Equal(1.412, plan.Target1, 9); // 2R would be 1.415; capped at the 1.412 level sitting in the way
        Assert.Contains(plan.Basis, b => b.Contains("T1 capped at resistance"));
        Assert.True(plan.Target2 > plan.Target1 && plan.Target3 > plan.Target2);
        Assert.Equal((plan.Target1 - plan.EntryMid) / plan.RiskPerUnit, plan.RewardRatio1, 9);
        Assert.Equal(1.5, plan.RewardRatio1, 6);

        // With the nearest resistance beyond the ideal 2R target, the target is not capped.
        var clear = TradePlanBuilder.Build(setup, Proj(price: 1.406, s5: Struct(close: 1.406, levels: [R, Level("R2", 1.418)])))!;
        Assert.Equal(1.415, clear.Target1, 9);
        Assert.DoesNotContain(clear.Basis, b => b.Contains("T1 capped"));
        Assert.Contains("holding above 1.4000", plan.Trigger);
    }

    [Fact]
    public void Extended_breakout_tells_the_trader_to_wait_for_a_retest()
    {
        var setup = new SetupClassification(SetupType.Breakout, Confidence.Medium, TrendBias.Bullish, [], Status(R, BreakoutState.Confirmed), 1.40);
        var plan = TradePlanBuilder.Build(setup, Proj(price: 1.43))!;
        Assert.Contains("wait for a retest", plan.Trigger);
        Assert.Equal(1.41, plan.EntryHigh, 9);
    }

    [Fact]
    public void Retest_plan_uses_the_retest_low_and_range_breakout_projects_the_measured_move()
    {
        var retest = new RetestMetrics(0.15, 0, 2, 0.8, 1.7, 2, true);
        var setup = new SetupClassification(SetupType.BreakoutRetest, Confidence.High, TrendBias.Bullish, [], Status(R, BreakoutState.RetestHeld, retest: retest), 1.40);
        var plan = TradePlanBuilder.Build(setup, Proj(price: 1.41))!;
        Assert.True(plan.Stop < 1.398);
        Assert.Contains(plan.Basis, b => b.Contains("retest low 1.4015"));

        var range = new SetupClassification(SetupType.RangeBreakout, Confidence.High, TrendBias.Bullish, [], Status(R, BreakoutState.Confirmed), 1.40);
        var rp = TradePlanBuilder.Build(range, Proj(price: 1.406, s5: Struct(close: 1.406, rangeHigh: 1.40, rangeLow: 1.30, levels: R)))!;
        Assert.Equal(1.50, rp.Target3, 9);
        Assert.Contains(rp.Basis, b => b.Contains("measured move"));
    }

    [Fact]
    public void No_setup_means_no_plan()
    {
        Assert.Null(TradePlanBuilder.Build(new SetupClassification(SetupType.None, Confidence.Low, TrendBias.Neutral, [], null, null), Proj()));
    }
}

public class OpportunityScorerTests
{
    private static readonly PriceLevel R = Level("R", 1.40, 3);
    private static readonly ScoringConfig Cfg = new();

    private static (ScoreBreakdown breakdown, TradePlan? plan) ScoreFor(AnalyticsProjection p, BreakoutAnalysis? b, MarketContext market, double? spread = 4, double? vol = 50_000_000)
    {
        var setup = SetupClassifier.Classify(p, b, market);
        var over = OverextensionAnalyzer.Assess(p, new OverextensionConfig());
        var plan = TradePlanBuilder.Build(setup, p);
        return (OpportunityScorer.Score(p, setup, plan, over, b, market, spread, vol, Cfg), plan);
    }

    [Fact]
    public void Textbook_retest_setup_scores_high_and_explains_each_component()
    {
        var b = Breakouts(Status(R, BreakoutState.RetestHeld, barsSince: 3, retest: new RetestMetrics(0.2, 0, 2, 0.8, 1.8, 2, true), distance: 1.2));
        var p = Proj(price: 1.412, m5: Ind(close: 1.412, ema20: 1.402, distEma20Atr: 1.0, rsi: 64, relVol: 2.2, fastRatio: 1.5, buyShare: 0.66), s5: Struct(close: 1.412, levels: [R, Level("R2", 1.44)]), s15: Struct(Timeframe.M15, close: 1.412), vwap: Vwap(vwap: 1.395, price: 1.412, std: 0.012));
        var (s, plan) = ScoreFor(p, b, Market());
        Assert.NotNull(plan);
        Assert.True(s.Total >= 70, $"total {s.Total}: {string.Join(" | ", s.Components.Select(c => $"{c.Name}={c.Points}"))} penalties {string.Join(" | ", s.Penalties.Select(c => $"{c.Name}={c.Points}"))}");
        Assert.Equal(Cfg.BreakoutMax, s.ComponentPoints(OpportunityScorer.Breakout));
        Assert.Equal(Cfg.MarketMax, s.ComponentPoints(OpportunityScorer.Market));
        Assert.All(s.Components, c => Assert.False(string.IsNullOrWhiteSpace(c.Evidence)));
        Assert.Equal(1, s.ConfigVersion);
    }

    [Fact]
    public void Btc_dump_applies_the_full_disagreement_penalty_and_zero_market_credit()
    {
        var b = Breakouts(Status(R, BreakoutState.Confirmed, barsSince: 1));
        var p = Proj(price: 1.406, s5: Struct(close: 1.406, levels: [R, Level("R2", 1.44)]));
        var (calm, _) = ScoreFor(p, b, Market());
        var (dump, _) = ScoreFor(p, b, Market(dumping: true, regime: MarketRegime.RiskOff, altsFavorable: false));
        Assert.Equal(Cfg.BtcDisagreementPenaltyMax, dump.PenaltyPoints(OpportunityScorer.BtcDisagreement));
        Assert.Equal(0, dump.ComponentPoints(OpportunityScorer.Market));
        Assert.True(calm.Total - dump.Total >= Cfg.BtcDisagreementPenaltyMax + Cfg.MarketMax - 0.5, $"{calm.Total} vs {dump.Total}");
    }

    [Fact]
    public void Extended_pump_is_penalized_below_a_fresh_breakout_despite_bigger_gains()
    {
        var fresh = Proj(price: 1.406, m5: Ind(close: 1.406, relVol: 2.0, rsi: 62, distEma20Atr: 0.8), s5: Struct(close: 1.406, levels: [R, Level("R2", 1.44)]), mom: Mom(r5m: 0.004, r15m: 0.01, r1h: 0.02, r24h: 0.04));
        var (a, _) = ScoreFor(fresh, Breakouts(Status(R, BreakoutState.Confirmed, barsSince: 1)), Market());

        var pump = Proj(price: 1.75, m5: Ind(close: 1.75, ema20: 1.62, distEma20Atr: 6, rsi: 88, relVol: 4.5, volumeZ: 6, atr: 0.02), s5: Struct(close: 1.75, levels: [Level("S", 1.55)]), mom: Mom(r5m: 0.04, r15m: 0.09, r1h: 0.18, r24h: 0.25, accel5: 0.02, accel15: 0.02), vwap: Vwap(vwap: 1.50, price: 1.75, std: 0.05));
        var (b, _) = ScoreFor(pump, Breakouts(Status(Level("L", 1.55), BreakoutState.Extended, distance: 10, narrative: "10.0 ATR above 1.5500 — do not chase")), Market());

        Assert.True(a.Total > b.Total + 15, $"fresh {a.Total} vs pump {b.Total}");
        Assert.True(b.PenaltyPoints(OpportunityScorer.Overextension) >= 0.9 * Cfg.OverextensionPenaltyMax);
    }

    [Fact]
    public void Weak_volume_thin_liquidity_and_nearby_resistance_cost_points()
    {
        var p = Proj(price: 1.406, m5: Ind(close: 1.406, relVol: 0.5), s5: Struct(close: 1.406, levels: [R, Level("R2", 1.41)]));
        var (s, _) = ScoreFor(p, Breakouts(Status(R, BreakoutState.Confirmed, barsSince: 1, weak: true)), Market(), spread: 25, vol: 1_500_000);
        Assert.Equal(7.5, s.PenaltyPoints(OpportunityScorer.WeakVolume), 1);
        Assert.True(s.PenaltyPoints(OpportunityScorer.NearbyResistance) > 5);
        Assert.True(s.PenaltyPoints(OpportunityScorer.Spread) > 10);
        Assert.True(s.ComponentPoints(OpportunityScorer.Liquidity) < 3);
        Assert.True(s.Total < 45, $"total {s.Total}");
    }

    [Fact]
    public void Failed_breakout_penalty_decays_with_bars_since_failure()
    {
        var p = Proj(price: 1.39);
        var (fresh, _) = ScoreFor(p, Breakouts(Status(R, BreakoutState.Failed, barsInState: 0, narrative: "Failed")), Market());
        var (old, _) = ScoreFor(p, Breakouts(Status(R, BreakoutState.Failed, barsInState: 9, narrative: "Failed")), Market());
        Assert.Equal(Cfg.FailedBreakoutPenaltyMax, fresh.PenaltyPoints(OpportunityScorer.FailedBreakout));
        Assert.True(old.PenaltyPoints(OpportunityScorer.FailedBreakout) < fresh.PenaltyPoints(OpportunityScorer.FailedBreakout));
        Assert.True(old.PenaltyPoints(OpportunityScorer.FailedBreakout) >= 5);
    }
}

public class MarketContextBuilderTests
{
    private static AnalyticsProjection Asset(string symbol, double r5, double r15, double r1h, bool aboveVwap, EmaAlignment align, StructureTrend trend = StructureTrend.Range, double relVol = 1.0)
    {
        var price = 100.0;
        var m5 = Ind(close: price, align: align, relVol: relVol);
        var m15 = Ind(Timeframe.M15, close: price, align: align);
        var s15 = Struct(Timeframe.M15, close: price, trend: trend);
        return new AnalyticsProjection(new Symbol(symbol), T.Base, T.Base, price, [m5, m15], Vwap(vwap: aboveVwap ? 99 : 101, price: price), Mom(r5m: r5, r15m: r15, r1h: r1h), [s15], null);
    }

    [Fact]
    public void Bullish_btc_with_broad_participation_is_risk_on()
    {
        var list = new List<AnalyticsProjection> { Asset("BTC-USD", 0.002, 0.005, 0.015, true, EmaAlignment.Bullish, StructureTrend.Uptrend) };
        for (var i = 0; i < 10; i++) list.Add(Asset($"A{i}-USD", 0.001, 0.002, i < 8 ? 0.01 : -0.01, i < 8, i < 6 ? EmaAlignment.Bullish : EmaAlignment.Mixed));
        var m = MarketContextBuilder.Build(list, T.Base);
        Assert.Equal(MarketRegime.StrongRiskOn, m.Regime);
        Assert.True(m.AltsFavorable);
        Assert.Equal(TrendBias.Bullish, m.Btc!.Trend);
        Assert.False(m.Btc.Dumping);
        Assert.InRange(m.BreadthAboveVwap, 0.8, 0.85);
        Assert.Contains(m.Notes, n => n.Contains("favor aggressive"));
    }

    [Fact]
    public void Btc_dump_forces_risk_off_regardless_of_breadth()
    {
        var list = new List<AnalyticsProjection> { Asset("BTC-USD", -0.012, -0.02, -0.01, false, EmaAlignment.Bullish, StructureTrend.Uptrend, relVol: 3) };
        for (var i = 0; i < 10; i++) list.Add(Asset($"A{i}-USD", 0.001, 0.002, 0.01, true, EmaAlignment.Bullish));
        var m = MarketContextBuilder.Build(list, T.Base);
        Assert.True(m.Btc!.Dumping);
        Assert.True(m.Regime <= MarketRegime.Neutral, m.Regime.ToString());
        Assert.False(m.AltsFavorable);
        Assert.Contains(m.Notes, n => n.Contains("selling off"));
    }

    [Fact]
    public void Missing_btc_is_stated_not_assumed()
    {
        var list = new List<AnalyticsProjection>();
        for (var i = 0; i < 4; i++) list.Add(Asset($"A{i}-USD", -0.001, -0.002, -0.01, false, EmaAlignment.Bearish, StructureTrend.Downtrend));
        var m = MarketContextBuilder.Build(list, T.Base);
        Assert.Null(m.Btc);
        Assert.Equal(MarketRegime.RiskOff, m.Regime);
        Assert.Contains(m.Notes, n => n.Contains("BTC analytics unavailable"));
    }
}
