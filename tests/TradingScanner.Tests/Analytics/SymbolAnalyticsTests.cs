using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Analytics;

public class SymbolAnalyticsTests
{
    private static readonly Symbol Btc = new("BTC-USD");

    /// <summary>Deterministic random walk of 1m candles with a mild drift.</summary>
    internal static List<Candle> Walk(int minutes, int seed = 5, double drift = 0.0002, DateTimeOffset? start = null)
    {
        var rng = new Random(seed);
        var t = start ?? T.At("2024-03-01T00:00:00Z");
        var price = 100.0;
        var list = new List<Candle>(minutes);
        for (var i = 0; i < minutes; i++)
        {
            var open = price;
            var close = open * (1 + drift + (rng.NextDouble() - 0.5) * 0.004);
            var high = Math.Max(open, close) * (1 + rng.NextDouble() * 0.001);
            var low = Math.Min(open, close) * (1 - rng.NextDouble() * 0.001);
            var vol = 10 + rng.NextDouble() * 5;
            var o = (decimal)open; var h = (decimal)high; var l = (decimal)low; var c = (decimal)close; var v = (decimal)vol;
            list.Add(new Candle(Btc, Timeframe.M1, t.AddMinutes(i), o, h, l, c, v, v * (h + l + c) / 3, v / 2, v / 2, 5, CandleSource.Live));
            price = close;
        }
        return list;
    }

    private sealed class ListReader(Dictionary<Timeframe, Candle[]> series) : ICandleHistoryReader
    {
        public IReadOnlyCollection<Symbol> Symbols => [Btc];
        public CandleSnapshot? GetCandles(Symbol symbol, Timeframe timeframe, int? lastN = null) =>
            series.TryGetValue(timeframe, out var c) ? new CandleSnapshot(symbol, timeframe, lastN is { } n ? c.TakeLast(n).ToArray() : c, null) : null;
    }

    [Fact]
    public void Incremental_updates_equal_a_rebuild_from_the_same_series()
    {
        var m1 = Walk(600);
        var m5 = TradingScanner.MarketData.Candles.TimeframeAggregator.AggregateAll(Btc, Timeframe.M1, Timeframe.M5, m1).ToArray();
        var options = new AnalyticsOptions();

        var incremental = new SymbolAnalytics(Btc, options);
        foreach (var c in m1) incremental.Update(c);
        foreach (var c in m5) incremental.Update(c);

        var rebuilt = new SymbolAnalytics(Btc, options);
        rebuilt.Rebuild(new ListReader(new() { [Timeframe.M1] = m1.ToArray(), [Timeframe.M5] = m5 }));

        var a = incremental.Snapshot();
        var b = rebuilt.Snapshot();
        Assert.Equal(a.AsOf, b.AsOf);
        Assert.Equal(a.For(Timeframe.M1), b.For(Timeframe.M1));
        Assert.Equal(a.For(Timeframe.M5), b.For(Timeframe.M5));
        Assert.Equal(a.Vwap, b.Vwap);
        Assert.Equal(a.Momentum!.LastClose, b.Momentum!.LastClose);
        Assert.Equal(a.Momentum.ReferenceCloses, b.Momentum.ReferenceCloses);
    }

    [Fact]
    public void Indicator_values_populate_as_history_becomes_sufficient()
    {
        var s = new SymbolAnalytics(Btc, new AnalyticsOptions());
        var m1 = Walk(250);
        foreach (var c in m1.Take(30)) s.Update(c);
        var early = s.Snapshot().For(Timeframe.M1)!;
        Assert.NotNull(early.Ema9);
        Assert.NotNull(early.Ema20);
        Assert.Null(early.Ema50);
        Assert.Null(early.Ema200);
        Assert.NotNull(early.Rsi);
        Assert.NotNull(early.Atr);
        Assert.NotNull(early.RelVolume);
        Assert.Equal(EmaAlignment.Unknown, early.Alignment);

        foreach (var c in m1.Skip(30)) s.Update(c);
        var full = s.Snapshot();
        var v = full.For(Timeframe.M1)!;
        Assert.NotNull(v.Ema200);
        Assert.NotEqual(EmaAlignment.Unknown, v.Alignment);
        Assert.NotNull(v.EmaSpreadPct);
        Assert.NotNull(v.DistanceToEma20Atr);
        Assert.NotNull(v.RealizedVolPct);
        Assert.Equal(m1[^1].CloseTime, v.AsOf);
        Assert.Equal(m1[^1].CloseTime, full.AsOf);
        Assert.NotNull(full.Vwap);
        Assert.Equal(T.At("2024-03-01T00:00:00Z"), full.Vwap!.SessionStart);
        Assert.NotNull(full.Momentum!.Return(240, (double)m1[^1].Close));
        Assert.Null(full.Momentum.Return(1440, (double)m1[^1].Close));
    }

    [Fact]
    public void Alignment_and_cross_tracking_follow_a_trend_reversal()
    {
        var s = new SymbolAnalytics(Btc, new AnalyticsOptions());
        var t = T.At("2024-03-01T00:00:00Z");
        var i = 0;
        Candle Bar(double px) { var p = (decimal)px; return new Candle(Btc, Timeframe.M1, t.AddMinutes(i++), p, p, p, p, 1, p, 0.5m, 0.5m, 1, CandleSource.Live); }

        for (var k = 0; k < 260; k++) s.Update(Bar(100 + k * 0.1));   // steady uptrend
        var up = s.Snapshot().For(Timeframe.M1)!;
        Assert.Equal(EmaAlignment.Bullish, up.Alignment);
        Assert.Equal(CrossDirection.None, up.LastEma9x20Cross);
        Assert.Null(up.BarsSinceEma9x20Cross);

        var crossBar = -1;
        for (var k = 0; k < 60; k++)
        {
            s.Update(Bar(126 - k * 0.3));                               // sharp reversal
            var v = s.Snapshot().For(Timeframe.M1)!;
            if (crossBar < 0 && v.LastEma9x20Cross == CrossDirection.Bearish) crossBar = k;
        }
        var down = s.Snapshot().For(Timeframe.M1)!;
        Assert.True(crossBar >= 0, "bearish 9/20 cross should have been detected");
        Assert.Equal(CrossDirection.Bearish, down.LastEma9x20Cross);
        Assert.Equal(59 - crossBar, down.BarsSinceEma9x20Cross);
        Assert.True(down.Ema9 < down.Ema20);
        Assert.True(down.DistanceToEma20Atr < 0);
    }

    [Fact]
    public void Projection_derives_live_values_without_touching_state()
    {
        var s = new SymbolAnalytics(Btc, new AnalyticsOptions());
        foreach (var c in Walk(120)) s.Update(c);
        var snap = s.Snapshot();
        var last = snap.Momentum!.LastClose;
        var proj = snap.Project(last * 1.01, T.At("2024-03-01T02:00:30Z"));
        Assert.Equal(0.01, proj.Momentum!.R1m!.Value, 9);
        Assert.True(proj.Vwap!.Above);
        Assert.Equal((last * 1.01 - snap.Vwap!.Vwap) / snap.Vwap.Vwap, proj.Vwap.DeviationPct, 12);
        Assert.Same(snap.Timeframes, proj.Timeframes);
    }

    [Fact]
    public void Engine_rebuilds_from_history_and_updates_on_closes()
    {
        var m1 = Walk(300);
        var reader = new ListReader(new() { [Timeframe.M1] = m1.Take(280).ToArray() });
        var engine = new AnalyticsEngine(reader, Options.Create(new AnalyticsOptions()), NullLogger<AnalyticsEngine>.Instance);

        Assert.Null(engine.GetSnapshot(Btc));
        engine.OnHistoryApplied(Btc);
        var afterHistory = engine.GetSnapshot(Btc)!;
        Assert.Equal(m1[279].CloseTime, afterHistory.AsOf);
        Assert.NotNull(afterHistory.For(Timeframe.M1)!.Ema200);

        foreach (var c in m1.Skip(280)) engine.OnCandleClosed(c);
        var live = engine.GetSnapshot(Btc)!;
        Assert.Equal(m1[^1].CloseTime, live.AsOf);
        Assert.Equal((double)m1[^1].Close, live.Momentum!.LastClose);
        Assert.Contains(Btc, engine.Symbols);
        Assert.NotNull(engine.Project(Btc, 101, T.Base));
    }
}
