using TradingScanner.Core.Market;

namespace TradingScanner.Core.Providers;

/// <summary>
/// Receives engine output on the engine thread. Implementations must be fast and non-blocking
/// (enqueue and return). Registered via DI; the engine fans out to all observers.
/// </summary>
public interface IMarketEventObserver
{
    void OnQuote(PriceQuote quote);
    void OnCandleClosed(in Candle candle);
    void OnFeedStatus(FeedStatusChange change);
    void OnGap(DataGap gap);
}
