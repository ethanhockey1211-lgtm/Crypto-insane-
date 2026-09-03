namespace TradingScanner.Core.Market;

/// <summary>Side of the aggressor (taker). Buy = market buy lifted the ask (up-tick).</summary>
public enum TradeSide : byte
{
    Buy = 0,
    Sell = 1,
}

/// <summary>A single normalized exchange trade (a "match").</summary>
public readonly record struct Trade(
    Symbol Symbol,
    string Provider,
    string Exchange,
    long TradeId,
    decimal Price,
    decimal Size,
    TradeSide TakerSide,
    DateTimeOffset ExchangeTime,
    DateTimeOffset ReceivedAt)
{
    public decimal QuoteSize => Price * Size;
    public TimeSpan Latency => ReceivedAt - ExchangeTime;
}
