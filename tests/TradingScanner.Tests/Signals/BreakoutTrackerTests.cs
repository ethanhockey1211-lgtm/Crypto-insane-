using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Signals;

public class LevelBreakoutTrackerTests
{
    private static readonly Symbol Xrp = new("XRP-USD");
    private const double Atr = 0.01;
    private static readonly PriceLevel Level = new("5m:1:H", 1.400, LevelSource.Swing, 2, T.Base, T.Base, 0, 2);

    private static Candle Bar(int i, double o, double h, double l, double c) =>
        new(Xrp, Timeframe.M5, T.Base.AddMinutes(5 * i), (decimal)o, (decimal)h, (decimal)l, (decimal)c, 100, (decimal)(100 * c), 50, 50, 10, CandleSource.Live);

    private static LevelBreakoutTracker Up(BreakoutOptions? o = null) => new(Level, BreakoutDirection.Up, o ?? new BreakoutOptions(), T.Base);

    [Fact]
    public void A_wick_through_resistance_that_closes_back_below_is_an_attempt_then_a_failure()
    {
        var t = Up();
        var s1 = t.Update(Bar(0, 1.395, 1.401, 1.393, 1.396), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Attempt, s1.State);
        Assert.Contains("Wicked above 1.4000", s1.Narrative);
        Assert.Contains("unconfirmed", s1.Narrative);

        var s2 = t.Update(Bar(1, 1.396, 1.397, 1.393, 1.394), Atr, 0.9, Level);
        Assert.Equal(BreakoutState.Failed, s2.State);
        Assert.Contains("Failed", s2.Narrative);
        Assert.Null(s2.BarsSinceBreakout);
    }

    [Fact]
    public void A_volume_backed_close_above_then_a_shallow_retest_that_recovers_is_confirmed_and_held()
    {
        var t = Up();
        var s1 = t.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Atr, 2.1, Level);
        Assert.Equal(BreakoutState.Confirmed, s1.State);
        Assert.Equal(2.1, s1.BreakoutRelVol);
        Assert.InRange(s1.BreakoutCloseStrength!.Value, 0.9, 0.95);
        Assert.False(s1.WeakVolume);
        Assert.Equal(0, s1.BarsSinceBreakout);
        Assert.Contains("2.1× volume", s1.Narrative);

        var s2 = t.Update(Bar(1, 1.408, 1.409, 1.402, 1.404), Atr, 0.8, Level);
        Assert.Equal(BreakoutState.Retesting, s2.State);
        Assert.NotNull(s2.Retest);
        Assert.Equal(0.2, s2.Retest!.ClosestApproachAtr, 9);
        Assert.Equal(0, s2.Retest.PenetrationAtr);
        Assert.Equal(1, s2.Retest.BarsNearLevel);

        var s3 = t.Update(Bar(2, 1.404, 1.413, 1.403, 1.412), Atr, 1.8, Level);
        Assert.Equal(BreakoutState.RetestHeld, s3.State);
        Assert.True(s3.Retest!.Held);
        Assert.Equal(1.8, s3.Retest.RecoveryRelVol);
        Assert.Equal(2, s3.Retest.RecoveryBars);
        Assert.Equal(2, s3.BarsSinceBreakout);
        Assert.Equal(1.2, s3.DistanceAtr, 9);
        Assert.Contains("Retest held", s3.Narrative);
        Assert.True(s3.IsActionable);
    }

    [Fact]
    public void A_close_above_without_volume_stays_an_attempt_and_confirms_weakly_by_time()
    {
        var t = Up();
        var s1 = t.Update(Bar(0, 1.398, 1.404, 1.397, 1.403), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Attempt, s1.State);
        Assert.Contains("volume only 1.0×", s1.Narrative);
        t.Update(Bar(1, 1.403, 1.405, 1.402, 1.404), Atr, 1.1, Level);
        var s3 = t.Update(Bar(2, 1.404, 1.405, 1.402, 1.403), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Confirmed, s3.State);
        Assert.True(s3.WeakVolume);
        Assert.Contains("confirmed by time, not volume", s3.Narrative);
    }

    [Fact]
    public void A_weak_close_despite_volume_is_only_an_attempt()
    {
        var t = Up();
        var s = t.Update(Bar(0, 1.398, 1.420, 1.397, 1.402), Atr, 3.0, Level);
        Assert.Equal(BreakoutState.Attempt, s.State);
        Assert.Contains("close was weak", s.Narrative);
    }

    [Fact]
    public void Extended_moves_flag_do_not_chase_and_cool_back_to_confirmed()
    {
        var t = Up();
        t.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Atr, 2.0, Level);
        var s2 = t.Update(Bar(1, 1.408, 1.430, 1.407, 1.428), Atr, 2.5, Level);
        Assert.Equal(BreakoutState.Extended, s2.State);
        Assert.Contains("do not chase", s2.Narrative);
        Assert.False(s2.IsActionable);
        var s3 = t.Update(Bar(2, 1.428, 1.429, 1.410, 1.415), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Confirmed, s3.State);
    }

    [Fact]
    public void Losing_the_level_long_after_the_break_is_not_a_failed_breakout()
    {
        var o = new BreakoutOptions { FailWindowBars = 3 };
        var t = Up(o);
        t.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Atr, 2.0, Level);
        for (var i = 1; i <= 5; i++) t.Update(Bar(i, 1.408, 1.412, 1.405, 1.410), Atr, 1.0, Level);
        var s = t.Update(Bar(6, 1.410, 1.411, 1.390, 1.392), Atr, 1.5, Level);
        Assert.Equal(BreakoutState.Watching, s.State);
        Assert.Contains("lost", s.Narrative);

        var early = Up(o);
        early.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Atr, 2.0, Level);
        var f = early.Update(Bar(1, 1.408, 1.409, 1.390, 1.392), Atr, 1.5, Level);
        Assert.Equal(BreakoutState.Failed, f.State);
        Assert.Contains("1 bars after the break", f.Narrative);
    }

    [Fact]
    public void Failed_state_cools_down_then_watches_again()
    {
        var o = new BreakoutOptions { FailedCooldownBars = 2 };
        var t = Up(o);
        t.Update(Bar(0, 1.395, 1.401, 1.393, 1.396), Atr, 1.0, Level);
        t.Update(Bar(1, 1.396, 1.397, 1.393, 1.394), Atr, 0.9, Level);
        Assert.Equal(BreakoutState.Failed, t.State);
        t.Update(Bar(2, 1.394, 1.395, 1.393, 1.394), Atr, 0.9, Level);
        var s = t.Update(Bar(3, 1.394, 1.395, 1.393, 1.394), Atr, 0.9, Level);
        Assert.Equal(BreakoutState.Watching, s.State);
        Assert.Equal(BreakoutDirection.Up, s.Direction);
    }

    [Fact]
    public void Watching_direction_follows_which_side_of_the_level_price_is_on()
    {
        var t = Up();
        // Price already far above the level while merely watching: the level is support now, watch for a breakdown.
        var s = t.Update(Bar(0, 1.430, 1.431, 1.429, 1.430), Atr, 1.0, Level);
        Assert.Equal(BreakoutDirection.Down, s.Direction);
        Assert.Equal(BreakoutState.Watching, s.State);
        var s2 = t.Update(Bar(1, 1.430, 1.431, 1.385, 1.386), Atr, 2.0, Level);
        Assert.Equal(BreakoutState.Confirmed, s2.State);
        Assert.Contains("below 1.4000", s2.Narrative);
    }

    [Fact]
    public void Approaching_is_reported_within_half_an_atr()
    {
        var t = Up();
        var s = t.Update(Bar(0, 1.390, 1.397, 1.389, 1.396), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Approaching, s.State);
        Assert.Contains("0.40 ATR below 1.4000", s.Narrative);
        var s2 = t.Update(Bar(1, 1.396, 1.397, 1.380, 1.382), Atr, 1.0, Level);
        Assert.Equal(BreakoutState.Watching, s2.State);
    }

    [Fact]
    public void Retest_that_lingers_without_reclaiming_fails_when_below_the_level()
    {
        var o = new BreakoutOptions { MaxRetestBars = 3 };
        var t = Up(o);
        t.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Atr, 2.0, Level);
        t.Update(Bar(1, 1.408, 1.409, 1.400, 1.401), Atr, 0.7, Level);
        t.Update(Bar(2, 1.401, 1.402, 1.399, 1.3995), Atr, 0.7, Level);
        var s = t.Update(Bar(3, 1.3995, 1.401, 1.399, 1.3995), Atr, 0.7, Level);
        Assert.Equal(BreakoutState.Failed, s.State);
        Assert.Contains("lingered", s.Narrative);
        Assert.NotNull(s.Retest);
        Assert.True(s.Retest!.PenetrationAtr > 0);
    }

    [Fact]
    public void Breakdowns_use_the_same_machine_mirrored()
    {
        var t = new LevelBreakoutTracker(Level, BreakoutDirection.Down, new BreakoutOptions(), T.Base);
        var s1 = t.Update(Bar(0, 1.402, 1.403, 1.390, 1.391), Atr, 2.0, Level);
        Assert.Equal(BreakoutState.Confirmed, s1.State);
        Assert.Contains("below 1.4000", s1.Narrative);
        var s2 = t.Update(Bar(1, 1.391, 1.398, 1.390, 1.396), Atr, 0.8, Level);
        Assert.Equal(BreakoutState.Retesting, s2.State);
        var s3 = t.Update(Bar(2, 1.396, 1.397, 1.385, 1.386), Atr, 1.7, Level);
        Assert.Equal(BreakoutState.RetestHeld, s3.State);
    }

    [Fact]
    public void Price_formatting_scales_with_magnitude()
    {
        Assert.Equal("64123.5", LevelBreakoutTracker.P(64123.456));
        Assert.Equal("123.46", LevelBreakoutTracker.P(123.456));
        Assert.Equal("1.4000", LevelBreakoutTracker.P(1.4));
        Assert.Equal("0.20900", LevelBreakoutTracker.P(0.209));
        Assert.Equal("0.0012345", LevelBreakoutTracker.P(0.0012345));
    }
}

public class SymbolBreakoutTrackerTests
{
    private static readonly Symbol Xrp = new("XRP-USD");

    private static Candle Bar(int i, double o, double h, double l, double c) =>
        new(Xrp, Timeframe.M5, T.Base.AddMinutes(5 * i), (decimal)o, (decimal)h, (decimal)l, (decimal)c, 100, (decimal)(100 * c), 50, 50, 10, CandleSource.Live);

    private static IndicatorValues Ind(double atr, double relVol) =>
        new(Timeframe.M5, T.Base, 1.4, null, null, null, null, null, null, atr, null, null, null, relVol, null, null, null, EmaAlignment.Unknown, null, null, CrossDirection.None, null);

    private static StructureSnapshot Struct(double close, params (string id, double price)[] levels) =>
        new(Timeframe.M5, T.Base, 0, close, 0.01,
            [], levels.Select(l => new PriceLevel(l.id, l.price, LevelSource.Swing, 2, T.Base, T.Base, 0, 2)).ToList(),
            StructureTrend.Unknown, "", null, null, 48, null, null);

    [Fact]
    public void Tracks_each_level_prefers_the_most_advanced_state_and_drops_vanished_levels()
    {
        var t = new SymbolBreakoutTracker(Xrp, new BreakoutOptions());
        var a1 = t.Update(Bar(0, 1.398, 1.409, 1.397, 1.408), Struct(1.408, ("R1", 1.400), ("R2", 1.450), ("S1", 1.350)), Ind(0.01, 2.0));
        Assert.Equal(3, a1.Levels.Count);
        Assert.Equal("R1", a1.BestUp!.Level.Id);
        Assert.Equal(BreakoutState.Confirmed, a1.BestUp.State);
        Assert.Equal("S1", a1.BestDown!.Level.Id);
        Assert.Equal(BreakoutState.Watching, a1.BestDown.State);
        Assert.Equal(BreakoutDirection.Up, a1.Levels.Single(l => l.Level.Id == "R2").Direction);
        Assert.Equal(BreakoutDirection.Down, a1.Levels.Single(l => l.Level.Id == "S1").Direction);

        var a2 = t.Update(Bar(1, 1.408, 1.409, 1.402, 1.404), Struct(1.404, ("R1", 1.400), ("R2", 1.450)), Ind(0.01, 0.8));
        Assert.Equal(2, a2.Levels.Count);
        Assert.Equal(BreakoutState.Retesting, a2.BestUp!.State);
        Assert.Null(a2.BestDown);
    }

    [Fact]
    public void Far_away_watching_levels_are_not_selected()
    {
        var t = new SymbolBreakoutTracker(Xrp, new BreakoutOptions { MaxLevelDistanceAtr = 3 });
        var a = t.Update(Bar(0, 1.400, 1.401, 1.399, 1.400), Struct(1.400, ("R_far", 1.500), ("R_near", 1.402)), Ind(0.01, 1.0));
        Assert.Equal("R_near", a.BestUp!.Level.Id);
        var only = t.Update(Bar(1, 1.400, 1.401, 1.399, 1.400), Struct(1.400, ("R_far", 1.500)), Ind(0.01, 1.0));
        Assert.Null(only.BestUp);
    }
}
