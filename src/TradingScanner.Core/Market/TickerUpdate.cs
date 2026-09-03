namespace TradingScanner.Core.Market;

/// <summary>Normalized best-bid/ask + 24h stats update. Not used for candle construction.</summary>
public readonly record struct TickerUpdate(
    Symbol Symbol,
    string Provider,
    string Exchange,
    decimal LastPrice,
    decimal BestBid,
    decimal BestAsk,
    decimal Open24h,
    decimal High24h,
    decimal Low24h,
    decimal Volume24h,
    DateTimeOffset ExchangeTime,
    DateTimeOffset ReceivedAt)
{
    public decimal Spread => BestAsk - BestBid;
    public double SpreadBps => BestBid > 0 ? (double)((BestAsk - BestBid) / BestBid) * 10_000d : double.NaN;
}
