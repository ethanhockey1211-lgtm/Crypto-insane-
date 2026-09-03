using System.Threading.Channels;
using TradingScanner.Core.Market;

namespace TradingScanner.Core.Providers;

/// <summary>
/// A source of live and historical market data for one exchange. Implementations own their connection
/// lifecycle (reconnect, resubscribe, heartbeat watchdog) and emit normalized <see cref="MarketEvent"/>s.
/// The application never depends on a concrete provider.
/// </summary>
public interface IMarketDataProvider : IAsyncDisposable
{
    /// <summary>Short machine name, e.g. "coinbase".</summary>
    string Name { get; }

    /// <summary>Human-readable exchange name attached to every price, e.g. "Coinbase Exchange".</summary>
    string Exchange { get; }

    /// <summary>Aggregate status across connections. Connected only when every connection is connected.</summary>
    FeedStatus Status { get; }

    Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct);

    /// <summary>Closed candles in [from, to), ascending. Providers page internally and respect rate limits.</summary>
    Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Timeframes the provider can serve historically. Others must be aggregated by the caller.</summary>
    IReadOnlySet<Timeframe> HistoricalTimeframes { get; }

    /// <summary>Stream events for the given symbols until cancelled. Never completes normally while connected.</summary>
    Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct);
}
