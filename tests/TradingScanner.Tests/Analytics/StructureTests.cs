using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Analytics;

public class SwingDetectorTests
{
    private static Candle Bar(int i, double high, double low) =>
        new(new Symbol("BTC-USD"), Timeframe.M5, T.Base.AddMinutes(5 * i), (decimal)((high + low) / 2), (decimal)high, (decimal)low, (decimal)((high + low) / 2), 1, 1, 0.5m, 0.5m, 1, CandleSource.Live);

    [Fact]
    public void Swing_high_is_confirmed_only_after_strength_bars_on_the_right()
    {
        var d = new SwingDetector(2);
        var highs = new[] { 1.0, 2, 3, 2, 1 };
        SwingPoint? found = null;
        for (var i = 0; i < highs.Length; i++)
        {
            var (h, l) = d.Update(Bar(i, highs[i], highs[i] - 1));
            Assert.Null(l);
            if (h is { } sp) { Assert.Null(found); found = sp; Assert.Equal(4, i); }
        }
        Assert.NotNull(found);
        Assert.Equal(3.0, found!.Value.Price);
        Assert.Equal(2, found.Value.BarIndex);
        Assert.Equal(T.Base.AddMinutes(10), found.Value.BarTime);
        Assert.Equal(T.Base.AddMinutes(25), found.Value.ConfirmedAt);
    }

    [Fact]
    public void Flat_double_top_registers_once_and_lows_are_detected_symmetrically()
    {
        var d = new SwingDetector(1);
        var series = new (double h, double l)[] { (5, 4), (6, 5), (6, 5), (5, 4), (4, 2), (5, 3) };
        var highs = new List<SwingPoint>();
        var lows = new List<SwingPoint>();
        for (var i = 0; i < series.Length; i++)
        {
            var (h, l) = d.Update(Bar(i, series[i].h, series[i].l));
            if (h is { } sh) highs.Add(sh);
            if (l is { } sl) lows.Add(sl);
        }
        Assert.Single(highs);
        Assert.Equal(1, highs[0].BarIndex); // first of the equal highs (tie allowed on the right only)
        var low = Assert.Single(lows);
        Assert.Equal(4, low.BarIndex);
        Assert.Equal(2.0, low.Price);
    }
}

public class MarketStructureAnalyzerTests
{
    private static readonly Symbol Btc = new("BTC-USD");

    private static Candle Bar(int i, double price, double wick = 0.1, DateTimeOffset? start = null) =>
        new(Btc, Timeframe.M5, (start ?? T.Base).AddMinutes(5 * i), (decimal)price, (decimal)(price + wick), (decimal)(price - wick), (decimal)price, 1, (decimal)price, 0.5m, 0.5m, 1, CandleSource.Live);

    private static StructureOptions Opts(int strength = 1, int lookback = 6, int maxAge = 400) =>
        new() { SwingStrength = strength, RangeLookback = lookback, LevelMaxAgeBars = maxAge, LevelTolerancePct = 0.002 };

    [Fact]
    public void Rising_zigzag_is_labeled_an_uptrend_with_higher_highs_and_lows()
    {
        var a = new MarketStructureAnalyzer(Timeframe.M5, Opts());
        // peaks 102,104,106 ; troughs 100,101,102 ; strength 1 => every local extreme is a swing
        var path = new[] { 100.0, 102, 100, 101, 104, 101, 102, 106, 102, 103 };
        for (var i = 0; i < path.Length; i++) a.Update(Bar(i, path[i]), atr: null);
        var s = a.Last!;
        Assert.Equal(StructureTrend.Uptrend, s.Trend);
        Assert.EndsWith("HH HL", s.TrendLabels);
        Assert.Contains(s.Swings, sw => sw.Type == SwingType.High && Math.Abs(sw.Price - 106.1) < 1e-9);
        Assert.Equal(103.0, s.Close);
        Assert.NotNull(s.NearestResistance);
        Assert.Equal(104.1, s.NearestResistance!.Price, 9); // the broken 104 high is the first level back above 103
        Assert.Equal(106.1, s.NearestAbove(105)!.Price, 9);
        Assert.NotNull(s.NearestSupport);
        // The 102.1 swing high (bar 1) and the 101.9 swing low (bar 8) are within tolerance: one level, two touches.
        Assert.Equal(102.0, s.NearestSupport!.Price, 9);
        Assert.Equal(2, s.NearestSupport.Touches);
    }

    [Fact]
    public void Falling_zigzag_is_a_downtrend_and_mixed_swings_are_a_range()
    {
        var down = new MarketStructureAnalyzer(Timeframe.M5, Opts());
        var path = new[] { 110.0, 108, 110, 106, 109, 104, 107, 103, 105 };
        for (var i = 0; i < path.Length; i++) down.Update(Bar(i, path[i]), null);
        Assert.Equal(StructureTrend.Downtrend, down.Last!.Trend);

        var range = new MarketStructureAnalyzer(Timeframe.M5, Opts());
        var flat = new[] { 100.0, 105, 100, 105, 100, 105, 100, 105, 100 };
        for (var i = 0; i < flat.Length; i++) range.Update(Bar(i, flat[i]), null);
        Assert.Equal(StructureTrend.Range, range.Last!.Trend);
        Assert.Equal("EH EL EH EL EH", range.Last.TrendLabels);
    }

    [Fact]
    public void Repeated_swings_at_the_same_price_cluster_into_one_level_with_touch_count()
    {
        var a = new MarketStructureAnalyzer(Timeframe.M5, Opts());
        var flat = new[] { 100.0, 105, 100, 105.05, 100, 104.95, 100, 105, 100 };
        for (var i = 0; i < flat.Length; i++) a.Update(Bar(i, flat[i]), null);
        var s = a.Last!;
        var resistance = s.Levels.Where(l => l.Price > 104).ToList();
        var top = Assert.Single(resistance);
        Assert.Equal(4, top.Touches);
        Assert.InRange(top.Price, 105.0, 105.2);
        Assert.Equal(LevelSource.Swing, top.Source);
        Assert.True(top.Strength > s.Levels.Where(l => l.Price < 104).Max(l => l.Strength) || top.Touches >= 3);
        Assert.StartsWith("5m:", top.Id);
    }

    [Fact]
    public void Level_identity_is_stable_across_bars_and_atr_tolerance_widens_clusters()
    {
        var a = new MarketStructureAnalyzer(Timeframe.M5, Opts());
        var path = new[] { 100.0, 105, 100, 105.3, 100, 105 };
        for (var i = 0; i < 4; i++) a.Update(Bar(i, path[i]), atr: 1.0); // tolerance 0.35 ATR = 0.35
        var firstId = a.Last!.Levels.Single(l => l.Price > 104).Id;
        for (var i = 4; i < path.Length; i++) a.Update(Bar(i, path[i]), atr: 1.0);
        var level = a.Last!.Levels.Single(l => l.Price > 104);
        Assert.Equal(firstId, level.Id);
        Assert.Equal(2, level.Touches); // 105.1 and 105.4 highs joined (within 0.35)
    }

    [Fact]
    public void Range_and_session_extremes_track_recent_bars()
    {
        var a = new MarketStructureAnalyzer(Timeframe.M5, Opts(lookback: 3));
        var start = T.At("2024-03-01T23:45:00Z");
        var prices = new[] { 100.0, 101, 102, 103, 104 }; // bars at 23:45, 23:50, 23:55, 00:00, 00:05
        for (var i = 0; i < prices.Length; i++) a.Update(Bar(i, prices[i], start: start), null);
        var s = a.Last!;
        Assert.Equal(104.1, s.RangeHigh!.Value, 9);
        Assert.Equal(101.9, s.RangeLow!.Value, 9);   // last 3 bars: 102,103,104
        Assert.Equal(104.1, s.SessionHigh!.Value, 9); // session reset at 00:00 => bars 103,104
        Assert.Equal(102.9, s.SessionLow!.Value, 9);
    }

    [Fact]
    public void Stale_levels_expire()
    {
        var a = new MarketStructureAnalyzer(Timeframe.M5, Opts(maxAge: 4));
        var path = new[] { 100.0, 105, 100, 101, 101, 101, 101, 101, 101, 101 };
        for (var i = 0; i < path.Length; i++) a.Update(Bar(i, path[i]), null);
        Assert.DoesNotContain(a.Last!.Levels, l => l.Price > 104);
    }
}
