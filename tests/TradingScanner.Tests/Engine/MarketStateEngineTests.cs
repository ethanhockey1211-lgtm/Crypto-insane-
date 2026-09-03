using Microsoft.Extensions.Logging.Abstractions;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Engine;
using TradingScanner.MarketData.Metrics;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Engine;

public class MarketStateEngineTests
{
    private sealed class RecordingObserver : IMarketEventObserver
    {
        public List<PriceQuote> Quotes { get; } = new();
        public List<Candle> Candles { get; } = new();
        public List<FeedStatusChange> Statuses { get; } = new();
        public List<DataGap> Gaps { get; } = new();
        public void OnQuote(PriceQuote quote) { lock (Quotes) Quotes.Add(quote); }
        public void OnCandleClosed(in Candle candle) { lock (Candles) Candles.Add(candle); }
        public void OnFeedStatus(FeedStatusChange change) { lock (Statuses) Statuses.Add(change); }
        public void OnGap(DataGap gap) { lock (Gaps) Gaps.Add(gap); }
        public List<Symbol> HistoryApplied { get; } = new();
        public void OnHistoryApplied(Symbol symbol) { lock (HistoryApplied) HistoryApplied.Add(symbol); }
    }

    private static async Task WaitUntil(Func<bool> cond, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Engine_consumes_events_updates_state_and_notifies_observers()
    {
        var channel = new MarketEventChannel(1000);
        var observer = new RecordingObserver();
        var metrics = new MarketDataMetrics();
        var engine = new MarketStateEngine(channel, [observer], T.Opts(), metrics, NullLogger<MarketStateEngine>.Instance);
        engine.RegisterSymbols([new Symbol("BTC-USD"), new Symbol("ETH-USD")]);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(2, engine.Symbols.Count);
            Assert.Equal(FeedStatus.Disconnected, engine.FeedStatus);

            Assert.True(channel.Writer.TryWrite(MarketEvent.FromStatus(new FeedStatusChange("test", 0, FeedStatus.Connected, null, T.Base))));
            Assert.True(channel.Writer.TryWrite(MarketEvent.FromTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(10), id: 1))));
            Assert.True(channel.Writer.TryWrite(MarketEvent.FromTrade(T.Trade("BTC-USD", 101m, 2m, T.Base.AddSeconds(70), id: 2))));
            Assert.True(channel.Writer.TryWrite(MarketEvent.FromGap(new DataGap("test", new Symbol("ETH-USD"), 5, 9, T.Base))));

            await WaitUntil(() => observer.Candles.Count >= 1 && observer.Gaps.Count == 1, "candle + gap observed");

            Assert.Equal(FeedStatus.Connected, engine.FeedStatus);
            Assert.Equal(101m, engine.GetQuote(new Symbol("BTC-USD"))!.Price);
            Assert.Equal(T.Base.AddSeconds(70), engine.LastTradeAt);
            var snap = engine.GetCandles(new Symbol("BTC-USD"), Timeframe.M1)!;
            Assert.Single(snap.Closed);
            Assert.Equal(100m, snap.Closed[0].Close);
            Assert.Equal(101m, snap.Forming!.Value.Close);
            Assert.Equal(2, observer.Quotes.Count);
            Assert.Equal(1, metrics.Snapshot().CandleCloses);

            // Clock tick closes the forming bar for quiet symbols.
            Assert.True(channel.Writer.TryWrite(MarketEvent.Clock(T.Base.AddMinutes(2).AddSeconds(3))));
            await WaitUntil(() => observer.Candles.Count >= 2, "clock close");
            Assert.Equal(2, engine.GetCandles(new Symbol("BTC-USD"), Timeframe.M1)!.Closed.Length);

            // Commands run on the engine thread.
            var ran = false;
            await engine.PostAsync((e, _) => { ran = e.Get(new Symbol("BTC-USD")) is not null; e.NotifyHistoryApplied(new Symbol("BTC-USD")); }, CancellationToken.None);
            Assert.True(ran);
            Assert.Equal(["BTC-USD"], observer.HistoryApplied.Select(s => s.Value));
            Assert.Equal(0, engine.EngineErrors);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Observer_exceptions_are_counted_and_do_not_stop_the_engine()
    {
        var channel = new MarketEventChannel(100);
        var throwing = new ThrowingObserver();
        var engine = new MarketStateEngine(channel, [throwing], T.Opts(), new MarketDataMetrics(), NullLogger<MarketStateEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            channel.Writer.TryWrite(MarketEvent.FromTrade(T.Trade("BTC-USD", 1m, 1m, T.Base)));
            channel.Writer.TryWrite(MarketEvent.FromTrade(T.Trade("BTC-USD", 2m, 1m, T.Base.AddSeconds(1))));
            await WaitUntil(() => engine.EngineErrors == 2, "errors counted");
            Assert.Equal(2m, engine.GetQuote(new Symbol("BTC-USD"))!.Price);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingObserver : IMarketEventObserver
    {
        public void OnQuote(PriceQuote quote) => throw new InvalidOperationException("boom");
        public void OnCandleClosed(in Candle candle) { }
        public void OnFeedStatus(FeedStatusChange change) { }
        public void OnGap(DataGap gap) { }
        public void OnHistoryApplied(Symbol symbol) { }
    }
}
