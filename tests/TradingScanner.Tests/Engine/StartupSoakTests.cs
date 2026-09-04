using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using Microsoft.Extensions.Logging;
using TradingScanner.MarketData.Engine;
using TradingScanner.MarketData.Metrics;
using TradingScanner.Signals;
using TradingScanner.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace TradingScanner.Tests.Engine;

/// <summary>
/// Replays the production startup sequence through the real engine, analytics and signals: live trades start
/// before REST history arrives, history has holes (the exchange omits empty buckets) and lags (the newest buckets
/// are missing), symbols range from liquid to nearly dead, and the clock keeps closing bars. Nothing in that
/// sequence may throw on the engine thread.
/// </summary>
public class StartupSoakTests
{
    private readonly ITestOutputHelper _out;
    public StartupSoakTests(ITestOutputHelper output) => _out = output;

    /// <param name="LagBuckets">Newest buckets missing from history, per timeframe (M1, M5, M15, H1). The exchange
    /// publishes each granularity independently, so 1m can lag while 5m is complete (the VVV-USD production failure).</param>
    private sealed record Profile(string Symbol, double TradeIntervalSeconds, TimeSpan FirstLiveTradeAfter, double HoleRate, int[] LagBuckets, int MergeAfterSeconds, decimal Price);

    private static readonly Profile[] Profiles =
    [
        new("AAA-USD", 5, TimeSpan.Zero, 0.0, [0, 0, 0, 0], 35, 50_000m),
        new("BBB-USD", 40, TimeSpan.Zero, 0.05, [1, 1, 1, 1], 48, 3_000m),
        new("CCC-USD", 420, TimeSpan.FromMinutes(2), 0.4, [2, 2, 2, 2], 61, 0.42m),
        new("DDD-USD", 900, TimeSpan.FromMinutes(12), 0.6, [1, 1, 1, 1], 70, 0.0000123m),
        new("EEE-USD", 15, TimeSpan.FromSeconds(50), 0.0, [3, 3, 3, 3], 90, 180m),
        new("FFF-USD", 65, TimeSpan.Zero, 0.2, [0, 0, 0, 0], 110, 12m),
        // 1m history stops well before the 5m/15m/1h history does, and no trade arrives before the clock fills the gap.
        new("VVV-USD", 600, TimeSpan.FromMinutes(6), 0.3, [4, 0, 0, 0], 41, 0.09m),
        new("WIF-USD", 300, TimeSpan.FromMinutes(3), 0.0, [7, 0, 1, 0], 52, 0.8m),
    ];

    private sealed class FrozenTime : TimeProvider
    {
        public DateTimeOffset Now = T.Base;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    private static readonly Timeframe[] HistoryTimeframes = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1];

    private static List<Candle> History(Profile p, Timeframe tf, DateTimeOffset requestedAt, Random rng)
    {
        var lag = p.LagBuckets[Array.IndexOf(HistoryTimeframes, tf)];
        // Mirrors CoinbaseRestClient.GetCandlesAsync: 300 buckets, the still-forming bucket excluded, then the
        // exchange's own quirks: empty buckets omitted, and the newest buckets not yet published.
        var end = tf.BucketStart(requestedAt);
        var list = new List<Candle>();
        var price = p.Price;
        for (var i = 300; i >= 1; i--)
        {
            var open = end - tf.Duration() * i;
            var o = price;
            var c = o * (1m + (decimal)(rng.NextDouble() - 0.5) * 0.01m);
            var h = Math.Max(o, c) * (1m + (decimal)rng.NextDouble() * 0.003m);
            var l = Math.Min(o, c) * (1m - (decimal)rng.NextDouble() * 0.003m);
            price = c;
            if (rng.NextDouble() < p.HoleRate) continue;
            if (i <= lag) continue;
            var v = (decimal)(rng.NextDouble() * 100) + 1m;
            list.Add(new Candle(new Symbol(p.Symbol), tf, open, o, h, l, c, v, v * (h + l + c) / 3m, 0m, 0m, 0, CandleSource.Historical));
        }
        return list;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    public async Task Startup_with_live_trades_before_history_never_throws_on_the_engine_thread(int seed)
    {
        var rng = new Random(seed);
        var time = new FrozenTime();
        var start = T.Base.AddMinutes(10).AddSeconds(25); // the process comes up mid-bucket, like Render did
        time.Now = start;

        var channel = new MarketEventChannel(200_000);
        var metrics = new MarketDataMetrics();
        var observers = new List<IMarketEventObserver>();
        var engine = new MarketStateEngine(channel, observers, T.Opts(), metrics, new OutputLogger<MarketStateEngine>(_out), time);
        var analytics = new AnalyticsEngine(engine, Options.Create(new AnalyticsOptions()), NullLogger<AnalyticsEngine>.Instance);
        using var signals = new SignalsEngine(analytics, Options.Create(new SignalsOptions()), NullLogger<SignalsEngine>.Instance);
        observers.Add(analytics);

        engine.RegisterSymbols(Profiles.Select(p => new Symbol(p.Symbol)).ToArray());
        await engine.StartAsync(CancellationToken.None);
        try
        {
            channel.Writer.TryWrite(MarketEvent.FromStatus(new FeedStatusChange("test", 0, FeedStatus.Connected, null, start)));

            // Build the event script in exchange-time order: trades, clock ticks, and history merges.
            var script = new List<(DateTimeOffset at, int order, Func<Task> act)>();
            var seq = 0;
            var end = start.AddMinutes(45);
            for (var t = start; t <= end; t = t.AddSeconds(1))
            {
                var tick = t;
                script.Add((tick, seq++, () => { time.Now = tick; channel.Writer.TryWrite(MarketEvent.Clock(tick)); return Task.CompletedTask; }));
            }

            var id = 1L;
            foreach (var p in Profiles)
            {
                var price = p.Price;
                var t = start + p.FirstLiveTradeAfter + TimeSpan.FromSeconds(rng.NextDouble() * p.TradeIntervalSeconds);
                while (t <= end)
                {
                    price *= 1m + (decimal)(rng.NextDouble() - 0.5) * 0.002m;
                    var trade = T.Trade(p.Symbol, price, (decimal)rng.NextDouble() + 0.01m, t, rng.NextDouble() < 0.5 ? TradeSide.Buy : TradeSide.Sell, id++);
                    script.Add((t, seq++, () => { channel.Writer.TryWrite(MarketEvent.FromTrade(trade)); return Task.CompletedTask; }));
                    t = t.AddSeconds(p.TradeIntervalSeconds * (0.5 + rng.NextDouble()));
                }

                // History is requested per timeframe over ~10 s and merged once all four have arrived (HistoryWarmUp).
                var requestedAt = start.AddSeconds(p.MergeAfterSeconds - 10);
                var loaded = HistoryTimeframes.Select((tf, i) => (tf, candles: History(p, tf, requestedAt.AddSeconds(i * 3), rng))).ToArray();
                var mergeAt = start.AddSeconds(p.MergeAfterSeconds);
                var symbol = new Symbol(p.Symbol);
                script.Add((mergeAt, seq++, () => engine.PostAsync((e, closed) =>
                {
                    var state = e.Get(symbol)!;
                    foreach (var (tf, candles) in loaded) state.ApplyHistory(tf, candles);
                    state.RebuildDerived(closed);
                    e.NotifyHistoryApplied(symbol);
                }, CancellationToken.None)));
            }

            var pending = new List<Task>();
            foreach (var step in script.OrderBy(s => s.at).ThenBy(s => s.order))
                pending.Add(step.act());
            await engine.PostAsync((_, _) => { }, CancellationToken.None);
            await Task.WhenAll(pending);

            foreach (var p in Profiles)
            {
                var symbol = new Symbol(p.Symbol);
                foreach (var tf in TimeframeExtensions.All)
                {
                    var snap = engine.GetCandles(symbol, tf)!;
                    for (var i = 1; i < snap.Closed.Length; i++)
                        Assert.True(snap.Closed[i].OpenTime > snap.Closed[i - 1].OpenTime, $"{p.Symbol} {tf} out of order at {i}");
                }
                _out.WriteLine($"{p.Symbol}: m1={engine.GetCandles(symbol, Timeframe.M1)!.Closed.Length} m3={engine.GetCandles(symbol, Timeframe.M3)!.Closed.Length} m5={engine.GetCandles(symbol, Timeframe.M5)!.Closed.Length} h4={engine.GetCandles(symbol, Timeframe.H4)!.Closed.Length}");
            }
            Assert.Equal(0, engine.EngineErrors);
        }
        finally
        {
            await engine.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
    }
}

/// <summary>Routes engine log lines (including caught exceptions) to the test output.</summary>
file sealed class OutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
    }
}
