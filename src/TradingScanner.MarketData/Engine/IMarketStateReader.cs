using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Engine;

/// <summary>Read-only, thread-safe view of the engine for the API and broadcasters.</summary>
public interface IMarketStateReader : ICandleHistoryReader
{
    SymbolState? Get(Symbol symbol);
    PriceQuote? GetQuote(Symbol symbol);
    FeedStatus FeedStatus { get; }
    IReadOnlyDictionary<int, FeedStatusChange> ConnectionStatus { get; }
    DateTimeOffset? LastEventAt { get; }
    DateTimeOffset? LastTradeAt { get; }
    long EngineErrors { get; }
}
