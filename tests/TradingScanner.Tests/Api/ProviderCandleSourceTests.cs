using System.Threading.Channels;
using TradingScanner.Api.Endpoints;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.Tests.Api;

public class ProviderCandleSourceTests
{
    private static readonly Symbol Btc = new("BTC-USD");
    private static readonly DateTimeOffset End = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static Candle Bar(DateTimeOffset at) => new(Btc, Timeframe.M1, at,
        100m, 101m, 99m, 100m, 1m, 100m, 0m, 0m, 1, CandleSource.Historical);

    [Fact]
    public async Task Truncated_recent_history_cannot_masquerade_as_a_multiday_backtest()
    {
        var source = new ProviderCandleSource(new Provider([Bar(End.AddHours(-11)), Bar(End.AddMinutes(-1))]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetM1Async(Btc, End.AddDays(-3), End, default));
        Assert.Contains("archived history", error.Message);
    }

    [Fact]
    public async Task Covered_window_is_preserved_and_stale_tail_is_rejected()
    {
        var bars = Enumerable.Range(0, 60).Select(i => Bar(End.AddHours(-1).AddMinutes(i))).ToArray();
        var source = new ProviderCandleSource(new Provider(bars));
        Assert.Same(bars, await source.GetM1Async(Btc, End.AddHours(-1), End, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetM1Async(Btc, End.AddHours(-1), End.AddMinutes(3), default));
    }

    private sealed class Provider(IReadOnlyList<Candle> bars) : IMarketDataProvider
    {
        public string Name => "test";
        public string Exchange => "Test exchange";
        public FeedStatus Status => FeedStatus.Disconnected;
        public IReadOnlySet<Timeframe> HistoricalTimeframes { get; } = new HashSet<Timeframe> { Timeframe.M1 };
        public Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProductInfo>>([]);
        public Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => Task.FromResult(bars);
        public Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
