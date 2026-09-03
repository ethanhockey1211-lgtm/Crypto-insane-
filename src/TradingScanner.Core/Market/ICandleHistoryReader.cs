namespace TradingScanner.Core.Market;

/// <summary>Thread-safe read access to candle series, the only dependency analytics take on market data.</summary>
public interface ICandleHistoryReader
{
    IReadOnlyCollection<Symbol> Symbols { get; }
    CandleSnapshot? GetCandles(Symbol symbol, Timeframe timeframe, int? lastN = null);
}
