using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Coinbase;

public enum CoinbaseMessageType : byte
{
    Unknown = 0,
    Match = 1,
    LastMatch = 2,
    Ticker = 3,
    Heartbeat = 4,
    Subscriptions = 5,
    Error = 6,
}

/// <summary>
/// One parsed Coinbase Exchange feed message. Fields not present for a given type are default.
/// Coinbase's "side" on match/ticker is the MAKER side; <see cref="TakerSide"/> is already flipped.
/// </summary>
public readonly record struct CoinbaseMessage(
    CoinbaseMessageType Type,
    string? ProductId,
    long Sequence,
    long TradeId,
    long LastTradeId,
    decimal Price,
    decimal Size,
    decimal BestBid,
    decimal BestAsk,
    decimal Open24h,
    decimal High24h,
    decimal Low24h,
    decimal Volume24h,
    TradeSide TakerSide,
    DateTimeOffset Time,
    string? ErrorMessage,
    string? ErrorReason,
    int SubscribedProducts);
