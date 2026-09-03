namespace TradingScanner.Core.Market;

/// <summary>Latest 24h statistics from the ticker stream. Immutable so readers can grab it without locks.</summary>
public sealed record MarketStats(decimal Open24h, decimal High24h, decimal Low24h, decimal Volume24hBase, DateTimeOffset At);

/// <summary>Thread-safe per-symbol quote and statistics access for readers outside the engine.</summary>
public interface ISymbolInfoReader
{
    IReadOnlyCollection<Symbol> Symbols { get; }
    PriceQuote? GetQuote(Symbol symbol);
    MarketStats? GetStats(Symbol symbol);
    bool IsHistoryLoaded(Symbol symbol);
}
