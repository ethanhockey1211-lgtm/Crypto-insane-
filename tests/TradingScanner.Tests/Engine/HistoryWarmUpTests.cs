using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Engine;
using TradingScanner.MarketData.Metrics;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Engine;

public sealed class HistoryWarmUpTests
{
    private static readonly Timeframe[] Timeframes = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1];

    [Fact]
    public async Task Opening_a_pending_market_moves_it_next_without_duplicate_work_or_extra_workers()
    {
        var blocked = new[] { "BTC-USD", "ETH-USD", "AAA-USD", "BBB-USD" }
            .ToDictionary(s => s, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var provider = new HistoryProvider(async (request, ct) =>
        {
            if (request.Timeframe == Timeframe.M1 && blocked.TryGetValue(request.Symbol.Value, out var release))
                await release.Task.WaitAsync(ct);
            return Bars(request);
        });
        var symbols = Symbols("AAA-USD", "BBB-USD", "CCC-USD", "DDD-USD", "ZZZ-USD", "BTC-USD", "ETH-USD", "ZZZ-USD");
        await using var harness = await Harness.StartAsync(provider, symbols);
        var run = harness.WarmUp.WarmUpAsync(symbols, harness.Token);
        try
        {
            var first = new List<string>();
            for (var i = 0; i < 4; i++) first.Add((await provider.NextAsync()).Symbol.Value);
            Assert.Equal(new[] { "BTC-USD", "ETH-USD", "AAA-USD", "BBB-USD" }, first);
            Assert.False(harness.WarmUp.Prioritize(new Symbol("UNKNOWN-USD")));
            Assert.False(harness.WarmUp.Prioritize(new Symbol("BTC-USD")));
            for (var i = 0; i < 100; i++) Assert.True(harness.WarmUp.Prioritize(new Symbol("ZZZ-USD")));

            blocked["AAA-USD"].TrySetResult();
            // AAA completes the other three granularities, then the same worker loads the inspected market.
            foreach (var tf in Timeframes.Skip(1))
            {
                var request = await provider.NextAsync();
                Assert.Equal("AAA-USD", request.Symbol.Value);
                Assert.Equal(tf, request.Timeframe);
            }
            var next = await provider.NextAsync();
            Assert.Equal("ZZZ-USD", next.Symbol.Value);
            Assert.Equal(Timeframe.M1, next.Timeframe);
            foreach (var release in blocked.Values) release.TrySetResult();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new WarmUpResult(7, 7, 0, null, true), result);
            Assert.Equal(28, provider.Requests.Count);
            Assert.All(provider.Requests.GroupBy(r => (r.Symbol, r.Timeframe)), g => Assert.Single(g));
            Assert.InRange(provider.MaxConcurrent, 1, 4);
            Assert.False(harness.WarmUp.Prioritize(new Symbol("ZZZ-USD")));
            Assert.Equal(0, harness.Engine.EngineErrors);
        }
        finally
        {
            foreach (var release in blocked.Values) release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_timeframe_is_retried_without_refetching_successes_and_requests_cover_full_closed_buckets(bool requestTimeout)
    {
        var attempts = new ConcurrentDictionary<Timeframe, int>();
        var provider = new HistoryProvider((request, _) =>
        {
            if (attempts.AddOrUpdate(request.Timeframe, 1, (_, count) => count + 1) == 1 && request.Timeframe == Timeframe.M5)
                throw requestTimeout ? new TaskCanceledException("HTTP client timeout") : new HttpRequestException("temporary interruption");
            return Task.FromResult(Bars(request));
        });
        var symbols = Symbols("BTC-USD");
        await using var harness = await Harness.StartAsync(provider, symbols);
        var result = await harness.WarmUp.WarmUpAsync(symbols, harness.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new WarmUpResult(1, 1, 0, null, true), result);
        Assert.Equal(new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1, Timeframe.M5 }, provider.Requests.Select(r => r.Timeframe));
        Assert.All(provider.Requests, request =>
        {
            Assert.Equal(request.Timeframe.BucketStart(Harness.Now), request.To);
            Assert.Equal(request.Timeframe.Duration() * 300, request.To - request.From);
        });
        var state = harness.Engine.Get(symbols[0])!;
        Assert.True(state.HistoryLoaded);
        foreach (var tf in Timeframes) Assert.Single(state.Series(tf).Snapshot().Closed);
    }

    [Fact]
    public async Task Permanent_failure_keeps_partial_history_out_of_the_engine_and_other_markets_continue()
    {
        var provider = new HistoryProvider((request, _) =>
        {
            if (request.Symbol.Value == "BAD-USD" && request.Timeframe == Timeframe.M5)
                throw new HttpRequestException("history unavailable");
            return Task.FromResult(Bars(request));
        });
        var symbols = Symbols("BAD-USD", "GOOD-USD");
        await using var harness = await Harness.StartAsync(provider, symbols);
        var result = await harness.WarmUp.WarmUpAsync(symbols, harness.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Complete);
        Assert.Equal(1, result.Loaded);
        Assert.Equal(1, result.Failed);
        Assert.Contains("BAD-USD", result.LastError);
        Assert.False(harness.Engine.IsHistoryLoaded(symbols[0]));
        foreach (var tf in Timeframes) Assert.Empty(harness.Engine.GetCandles(symbols[0], tf)!.Closed);
        Assert.True(harness.Engine.IsHistoryLoaded(symbols[1]));
        Assert.Equal(2, provider.Requests.Count(r => r.Symbol == symbols[0] && r.Timeframe == Timeframe.M5));
        Assert.All(Timeframes.Where(tf => tf != Timeframe.M5), tf =>
            Assert.Single(provider.Requests, r => r.Symbol == symbols[0] && r.Timeframe == tf));
    }

    [Fact]
    public async Task Cancellation_stops_active_and_pending_work_without_reporting_completion()
    {
        var provider = new HistoryProvider(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Array.Empty<Candle>();
        });
        var symbols = Symbols("AAA-USD", "BBB-USD", "CCC-USD", "DDD-USD", "EEE-USD", "FFF-USD");
        await using var harness = await Harness.StartAsync(provider, symbols);
        var progress = new ConcurrentQueue<WarmUpResult>();
        var run = harness.WarmUp.WarmUpAsync(symbols, harness.Token, progress.Enqueue);
        for (var i = 0; i < 4; i++) await provider.NextAsync();
        harness.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(4, provider.Requests.Count);
        Assert.NotEmpty(progress);
        Assert.All(progress, p => { Assert.False(p.Complete); Assert.Equal(0, p.Loaded); Assert.Equal(0, p.Failed); });
        Assert.False(harness.WarmUp.Prioritize(symbols[^1]));
        Assert.All(symbols, s => Assert.False(harness.Engine.IsHistoryLoaded(s)));
    }

    private static Symbol[] Symbols(params string[] names) => names.Select(s => new Symbol(s)).ToArray();
    private static IReadOnlyList<Candle> Bars(Request request) =>
        [T.Candle(request.Symbol.Value, request.Timeframe, request.To - request.Timeframe.Duration(), 10, 11, 9, 10, source: CandleSource.Historical)];

    private sealed record Request(Symbol Symbol, Timeframe Timeframe, DateTimeOffset From, DateTimeOffset To);

    private sealed class HistoryProvider(Func<Request, CancellationToken, Task<IReadOnlyList<Candle>>> load) : IMarketDataProvider
    {
        private readonly Channel<Request> _started = Channel.CreateUnbounded<Request>();
        private int _active;
        private int _maxConcurrent;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public int MaxConcurrent => _maxConcurrent;
        public string Name => "test";
        public string Exchange => "Test Exchange";
        public FeedStatus Status => FeedStatus.Connected;
        public IReadOnlySet<Timeframe> HistoricalTimeframes { get; } = Timeframes.ToHashSet();
        public Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => default;
        public async Task<Request> NextAsync() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = _maxConcurrent; }
            while (active > observed && Interlocked.CompareExchange(ref _maxConcurrent, active, observed) != observed);
            var request = new Request(symbol, timeframe, from, to);
            Requests.Enqueue(request);
            _started.Writer.TryWrite(request);
            try { return await load(request, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public static readonly DateTimeOffset Now = T.Base.AddMinutes(17).AddSeconds(25);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
        public CancellationToken Token => _cts.Token;
        public MarketStateEngine Engine { get; }
        public HistoryWarmUp WarmUp { get; }
        private Harness(IMarketDataProvider provider, Symbol[] symbols)
        {
            var time = new FrozenTime();
            Engine = new MarketStateEngine(new MarketEventChannel(100), [], T.Opts(), new MarketDataMetrics(), NullLogger<MarketStateEngine>.Instance, time);
            Engine.RegisterSymbols(symbols);
            WarmUp = new HistoryWarmUp(provider, Engine, T.Opts(), NullLogger<HistoryWarmUp>.Instance, time);
        }
        public static async Task<Harness> StartAsync(IMarketDataProvider provider, Symbol[] symbols)
        {
            var harness = new Harness(provider, symbols);
            await harness.Engine.StartAsync(CancellationToken.None);
            return harness;
        }
        public void Cancel() => _cts.Cancel();
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await Engine.StopAsync(CancellationToken.None);
            _cts.Dispose();
        }
        private sealed class FrozenTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
