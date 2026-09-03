using Microsoft.AspNetCore.SignalR;
using TradingScanner.Core.Market;

namespace TradingScanner.Api.Hubs;

/// <summary>
/// Real-time channel to browsers. Server → client messages:
///   "quotes"  QuoteDto[]        batched every 250ms, changed symbols only
///   "candle"  CandleClosedDto   for subscribed symbol/timeframe groups
///   "feed"    FeedStatusDto     provider status transitions
///   "gap"     GapDto            missed-trade notices
///   "scanner" ScannerStreamDto  ranked universe + market context, every scanner cycle
///   "tape"    TapeEvent         what's-moving-now events as they happen
///   "alert"   AlertEvent        a user alert rule fired
/// </summary>
public sealed class MarketHub : Hub
{
    public static string CandleGroup(Symbol symbol, Timeframe tf) => $"candles:{symbol.Value}:{tf.Label()}";

    public async Task SubscribeCandles(string symbol, string timeframe)
    {
        if (!TimeframeExtensions.TryParse(timeframe, out var tf)) throw new HubException($"Unknown timeframe '{timeframe}'.");
        await Groups.AddToGroupAsync(Context.ConnectionId, CandleGroup(new Symbol(symbol), tf));
    }

    public async Task UnsubscribeCandles(string symbol, string timeframe)
    {
        if (!TimeframeExtensions.TryParse(timeframe, out var tf)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, CandleGroup(new Symbol(symbol), tf));
    }
}
