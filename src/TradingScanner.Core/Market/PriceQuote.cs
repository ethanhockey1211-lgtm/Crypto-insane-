namespace TradingScanner.Core.Market;

/// <summary>
/// The latest known price for a symbol with full provenance. Every price shown anywhere carries
/// provider, exchange, exchange timestamp and receive timestamp so its age can be displayed.
/// </summary>
public sealed record PriceQuote(
    Symbol Symbol,
    decimal Price,
    decimal Bid,
    decimal Ask,
    string Provider,
    string Exchange,
    DateTimeOffset ExchangeTime,
    DateTimeOffset ReceivedAt)
{
    public TimeSpan Age(DateTimeOffset now) => now - ExchangeTime;
    public bool IsStale(DateTimeOffset now, TimeSpan threshold) => Age(now) > threshold;
    public decimal Mid => Bid > 0 && Ask > 0 ? (Bid + Ask) / 2m : Price;
    public double SpreadBps => Bid > 0 && Ask > 0 ? (double)((Ask - Bid) / Bid) * 10_000d : double.NaN;
}
