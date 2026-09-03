using TradingScanner.Analytics.Indicators;
using TradingScanner.Core.Market;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Analytics;

public class RollingWindowTests
{
    [Fact]
    public void Tracks_mean_std_and_order_across_wraparound()
    {
        var w = new RollingWindow(3);
        foreach (var v in new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }) w.Add(v);
        Assert.Equal(3, w.Count);
        Assert.Equal(4.0, w.Mean, 12);
        Assert.Equal(Math.Sqrt(2.0 / 3.0), w.Std, 12);
        Assert.Equal(5.0, w.Last);
        Assert.Equal(3.0, w.FromEnd(2));
        Assert.Equal(4.5, w.MeanOfLast(2), 12);
    }

    [Fact]
    public void Running_sums_stay_consistent_after_many_adds()
    {
        var w = new RollingWindow(50);
        var rng = new Random(11);
        var all = new List<double>();
        for (var i = 0; i < 10_000; i++) { var v = 1000 + rng.NextDouble(); all.Add(v); w.Add(v); }
        var tail = all.Skip(all.Count - 50).ToList();
        var mean = tail.Average();
        var std = Math.Sqrt(tail.Sum(x => (x - mean) * (x - mean)) / tail.Count);
        Assert.Equal(mean, w.Mean, 9);
        Assert.Equal(std, w.Std, 6);
    }
}

public class EmaTests
{
    [Fact]
    public void Seeds_with_simple_average_then_smooths()
    {
        var ema = new Ema(3);
        var seen = new List<double>();
        foreach (var v in new[] { 1.0, 2, 3, 4, 5, 6, 7, 8 })
        {
            ema.Update(v);
            if (ema.IsReady) seen.Add(ema.Value);
        }
        Assert.Equal([2.0, 3.0, 4.0, 5.0, 6.0, 7.0], seen.Select(x => Math.Round(x, 9)));
        Assert.Equal(6.0, ema.Previous, 9);
    }

    [Fact]
    public void Project_does_not_mutate_and_reset_clears()
    {
        var ema = new Ema(2);
        Assert.Null(ema.Project(1));
        ema.Update(1); ema.Update(3);
        Assert.Equal(2.0, ema.Value, 9);
        Assert.Equal(3 * (2.0 / 3) + 2 * (1.0 / 3), ema.Project(3)!.Value, 9);
        Assert.Equal(2.0, ema.Value, 9);
        ema.Reset();
        Assert.False(ema.IsReady);
    }
}

public class RsiTests
{
    // Reference values from an independent straight-line implementation of Wilder's RSI over a classic 33-close dataset.
    private static readonly double[] Closes = [44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28, 46.00, 46.03, 46.41, 46.22, 45.64, 46.21, 46.25, 45.71, 46.45, 45.78, 45.35, 44.03, 44.18, 44.22, 44.57, 43.42, 42.66, 43.13];
    private static readonly double[] Expected = [70.46, 66.25, 66.48, 69.35, 66.29, 57.92, 62.88, 63.21, 56.01, 62.34, 54.67, 50.39, 40.02, 41.49, 41.90, 45.50, 37.32, 33.09, 37.79];

    [Fact]
    public void Matches_wilder_reference_values()
    {
        var rsi = new Rsi(14);
        var got = new List<double>();
        foreach (var c in Closes)
        {
            rsi.Update(c);
            if (rsi.IsReady) got.Add(rsi.Value);
        }
        Assert.Equal(Expected.Length, got.Count);
        for (var i = 0; i < Expected.Length; i++) Assert.Equal(Expected[i], got[i], 2);
        Assert.Equal(33.09, rsi.Previous, 2);
    }

    [Fact]
    public void Extremes_and_projection()
    {
        var up = new Rsi(3);
        foreach (var c in new[] { 1.0, 2, 3, 4 }) up.Update(c);
        Assert.Equal(100, up.Value);

        var flat = new Rsi(3);
        foreach (var c in new[] { 5.0, 5, 5, 5 }) flat.Update(c);
        Assert.Equal(50, flat.Value);

        var down = new Rsi(3);
        foreach (var c in new[] { 4.0, 3, 2, 1 }) down.Update(c);
        Assert.Equal(0, down.Value);

        var before = up.Value;
        var projected = up.Project(3.0)!.Value;
        Assert.True(projected < 100);
        Assert.Equal(before, up.Value);
    }
}

public class AtrTests
{
    [Fact]
    public void Matches_wilder_reference_values()
    {
        var bars = new (double h, double l, double c)[] { (12, 9, 11), (13, 10, 12), (15, 11, 14), (14, 12, 13), (16, 13, 15), (17, 14, 16) };
        var atr = new Atr(3);
        var values = new List<double>();
        var trs = new List<double>();
        foreach (var (h, l, c) in bars)
        {
            atr.Update(h, l, c);
            trs.Add(atr.LastTrueRange);
            if (atr.IsReady) values.Add(atr.Value);
        }
        Assert.Equal([3.0, 3, 4, 2, 3, 3], trs);
        Assert.Equal([3.333333, 2.888889, 2.925926, 2.950617], values.Select(v => Math.Round(v, 6)));
        Assert.Equal(2.950617 / 16, atr.Fraction(16)!.Value, 5);
        Assert.Null(new Atr(3).Fraction(10));
    }

    [Fact]
    public void True_range_uses_gaps_from_previous_close()
    {
        var atr = new Atr(1);
        atr.Update(10, 9, 10);
        atr.Update(15, 14, 14); // gap up: TR = 15 - 10
        Assert.Equal(5, atr.LastTrueRange);
        atr.Update(8, 7, 7);    // gap down: TR = 14 - 7
        Assert.Equal(7, atr.LastTrueRange);
    }
}

public class SessionVwapTests
{
    private static Candle Bar(DateTimeOffset t, decimal price, decimal vol) =>
        new(new Symbol("BTC-USD"), Timeframe.M1, t, price, price, price, price, vol, price * vol, vol / 2, vol / 2, 1, CandleSource.Live);

    [Fact]
    public void Computes_volume_weighted_average_and_deviation()
    {
        var v = new SessionVwap();
        var t0 = T.At("2024-03-01T10:00:00Z");
        Assert.False(v.IsReady);
        v.Update(Bar(t0, 100, 1));
        v.Update(Bar(t0.AddMinutes(1), 110, 3));
        Assert.Equal(107.5, v.Value, 9);
        // volume-weighted variance: (1*(100-107.5)^2 + 3*(110-107.5)^2)/4 = (56.25 + 18.75)/4 = 18.75
        Assert.Equal(Math.Sqrt(18.75), v.Std, 9);
        Assert.Equal((112 - 107.5) / Math.Sqrt(18.75), v.Sigma(112)!.Value, 9);
        Assert.Equal(2, v.Bars);
    }

    [Fact]
    public void Zero_volume_bars_do_not_move_vwap_but_count_as_bars()
    {
        var v = new SessionVwap();
        var t0 = T.At("2024-03-01T10:00:00Z");
        v.Update(Bar(t0, 100, 2));
        v.Update(Candle.Synthetic(new Symbol("BTC-USD"), Timeframe.M1, t0.AddMinutes(1), 500));
        Assert.Equal(100, v.Value, 9);
        Assert.Equal(2, v.Bars);
    }

    [Fact]
    public void Resets_at_the_utc_day_boundary()
    {
        var v = new SessionVwap();
        v.Update(Bar(T.At("2024-03-01T23:58:00Z"), 100, 1));
        v.Update(Bar(T.At("2024-03-01T23:59:00Z"), 100, 1));
        Assert.Equal(T.At("2024-03-01T00:00:00Z"), v.SessionStart);
        v.Update(Bar(T.At("2024-03-02T00:00:00Z"), 200, 1));
        Assert.Equal(T.At("2024-03-02T00:00:00Z"), v.SessionStart);
        Assert.Equal(200, v.Value, 9);
        Assert.Equal(1, v.Bars);
    }

    [Fact]
    public void Uses_the_bars_own_vwap_not_its_close()
    {
        var v = new SessionVwap();
        var c = new Candle(new Symbol("BTC-USD"), Timeframe.M1, T.At("2024-03-01T10:00:00Z"), 100, 120, 90, 118, 10, 1050, 5, 5, 4, CandleSource.Live);
        v.Update(c);
        Assert.Equal(105, v.Value, 9); // quote volume / volume
    }
}

public class RelativeVolumeTests
{
    [Fact]
    public void Compares_current_bar_to_prior_baseline_only()
    {
        var rv = new RelativeVolume(baselinePeriod: 4, fastPeriod: 2);
        foreach (var vol in new[] { 10.0, 10, 10, 10 }) rv.Update(vol);
        Assert.True(rv.IsReady);
        Assert.True(double.IsNaN(rv.Value));
        rv.Update(30);
        Assert.Equal(3.0, rv.Value, 9);          // 30 / mean(10,10,10,10)
        Assert.Equal(2.0, rv.FastRatio, 9);      // mean(10,30) / 10
        Assert.Equal(15.0 / 15.0, rv.Project(15)!.Value, 9); // baseline now (10,10,10,30) mean 15
        rv.Update(15);
        Assert.Equal(1.0, rv.Value, 9);
        Assert.True(rv.ZScore < 0.0001 && rv.ZScore > -0.0001);
    }

    [Fact]
    public void Not_ready_until_baseline_is_full()
    {
        var rv = new RelativeVolume(3, 2);
        rv.Update(1); rv.Update(2);
        Assert.False(rv.IsReady);
        Assert.Null(rv.Project(5));
    }
}

public class RealizedVolatilityTests
{
    [Fact]
    public void Constant_growth_has_zero_volatility_and_alternation_is_measurable()
    {
        var rv = new RealizedVolatility(3);
        foreach (var c in new[] { 100.0, 110, 121, 133.1 }) rv.Update(c);
        Assert.True(rv.IsReady);
        Assert.Equal(0, rv.Value, 9);

        var alt = new RealizedVolatility(2);
        foreach (var c in new[] { 100.0, 110, 100 }) alt.Update(c);
        var r1 = Math.Log(1.1); var r2 = Math.Log(1 / 1.1);
        var mean = (r1 + r2) / 2;
        var expected = Math.Sqrt(((r1 - mean) * (r1 - mean) + (r2 - mean) * (r2 - mean)) / 2);
        Assert.Equal(expected, alt.Value, 12);
    }
}

public class MomentumTrackerTests
{
    [Fact]
    public void Returns_and_acceleration_over_horizons()
    {
        var m = new MomentumTracker();
        // closes: 100, 101, ..., 110 (11 closed bars); live price 112
        for (var i = 0; i <= 10; i++) m.Update(100 + i);
        Assert.Equal(112.0 / 110 - 1, m.Return(1, 112)!.Value, 12);
        Assert.Equal(112.0 / 106 - 1, m.Return(5, 112)!.Value, 12);
        Assert.Null(m.Return(12, 112));
        Assert.Equal(110.0 / 105 - 1, m.ClosedReturn(5)!.Value, 12);
        // accel(5): (112/106 - 1) - (106/101 - 1)
        Assert.Equal((112.0 / 106 - 1) - (106.0 / 101 - 1), m.Acceleration(5, 112)!.Value, 12);
        Assert.Null(m.Acceleration(6, 112));
    }
}
