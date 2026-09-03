using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Candles;
using TradingScanner.Signals;
using TradingScanner.Tests.Analytics;
using Xunit;

namespace TradingScanner.Tests.Signals;

public class SignalsEngineTests
{
    private static readonly Symbol Btc = new("BTC-USD");

    private sealed class ListReader(Dictionary<Timeframe, Candle[]> series) : ICandleHistoryReader
    {
        public IReadOnlyCollection<Symbol> Symbols => [Btc];
        public CandleSnapshot? GetCandles(Symbol symbol, Timeframe timeframe, int? lastN = null) =>
            series.TryGetValue(timeframe, out var c) ? new CandleSnapshot(symbol, timeframe, c, null) : null;
    }

    [Fact]
    public void History_replay_and_incremental_closes_produce_identical_breakout_state()
    {
        var m1 = SymbolAnalyticsTests.Walk(1500, seed: 9, drift: 0.0003);
        var m5 = TimeframeAggregator.AggregateAll(Btc, Timeframe.M1, Timeframe.M5, m1).ToArray();
        var options = Options.Create(new AnalyticsOptions());

        // Path A: everything arrives as live closes.
        var liveAnalytics = new AnalyticsEngine(new ListReader(new()), options, NullLogger<AnalyticsEngine>.Instance);
        using var liveSignals = new SignalsEngine(liveAnalytics, Options.Create(new SignalsOptions()), NullLogger<SignalsEngine>.Instance);
        var merged = m1.Concat(m5).OrderBy(c => c.CloseTime).ThenBy(c => c.Timeframe).ToList();
        foreach (var c in merged) liveAnalytics.OnCandleClosed(c);

        // Path B: everything arrives as history and is replayed.
        var replayAnalytics = new AnalyticsEngine(new ListReader(new() { [Timeframe.M1] = m1.ToArray(), [Timeframe.M5] = m5 }), options, NullLogger<AnalyticsEngine>.Instance);
        using var replaySignals = new SignalsEngine(replayAnalytics, Options.Create(new SignalsOptions()), NullLogger<SignalsEngine>.Instance);
        replayAnalytics.OnHistoryApplied(Btc);

        var a = liveSignals.GetBreakouts(Btc)!;
        var b = replaySignals.GetBreakouts(Btc)!;
        Assert.Equal(a.AsOf, b.AsOf);
        Assert.Equal(a.Levels.Count, b.Levels.Count);
        Assert.True(a.Levels.Count > 0, "the random walk should have produced structure levels");
        for (var i = 0; i < a.Levels.Count; i++)
        {
            Assert.Equal(a.Levels[i].Level.Id, b.Levels[i].Level.Id);
            Assert.Equal(a.Levels[i].State, b.Levels[i].State);
            Assert.Equal(a.Levels[i].Narrative, b.Levels[i].Narrative);
            Assert.Equal(a.Levels[i].DistanceAtr, b.Levels[i].DistanceAtr, 9);
        }
        Assert.Equal(a.BestUp?.Level.Id, b.BestUp?.Level.Id);
        Assert.Equal(liveAnalytics.GetSnapshot(Btc)!.StructureFor(Timeframe.M5)!.Trend, replayAnalytics.GetSnapshot(Btc)!.StructureFor(Timeframe.M5)!.Trend);
    }

    [Fact]
    public void Rebuild_discards_stale_tracker_state_first()
    {
        var m1 = SymbolAnalyticsTests.Walk(600, seed: 3);
        var m5 = TimeframeAggregator.AggregateAll(Btc, Timeframe.M1, Timeframe.M5, m1).ToArray();
        var reader = new ListReader(new() { [Timeframe.M1] = m1.ToArray(), [Timeframe.M5] = m5 });
        var analytics = new AnalyticsEngine(reader, Options.Create(new AnalyticsOptions()), NullLogger<AnalyticsEngine>.Instance);
        using var signals = new SignalsEngine(analytics, Options.Create(new SignalsOptions()), NullLogger<SignalsEngine>.Instance);

        foreach (var c in m5.Take(50)) analytics.OnCandleClosed(c);
        var partial = signals.GetBreakouts(Btc);
        analytics.OnHistoryApplied(Btc);
        var full = signals.GetBreakouts(Btc)!;
        Assert.Equal(m5[^1].CloseTime, full.AsOf);
        Assert.NotEqual(partial?.AsOf, full.AsOf);
    }
}
