namespace TradingScanner.Core.Market;

public enum CandleSource : byte
{
    /// <summary>Built from live trades.</summary>
    Live = 0,
    /// <summary>Loaded from exchange REST history. Buy/sell split and trade count are unavailable (zero).</summary>
    Historical = 1,
    /// <summary>No trades occurred in the bucket; O=H=L=C=previous close, zero volume.</summary>
    Synthetic = 2,
}

/// <summary>
/// An immutable OHLCV bar. Prices/volumes are decimal for exactness; analytics convert to double.
/// </summary>
public readonly record struct Candle(
    Symbol Symbol,
    Timeframe Timeframe,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    decimal BuyVolume,
    decimal SellVolume,
    int TradeCount,
    CandleSource Source)
{
    public DateTimeOffset CloseTime => OpenTime + Timeframe.Duration();
    public bool IsSynthetic => Source == CandleSource.Synthetic;
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public bool IsBullish => Close > Open;
    public decimal UpperWick => High - Math.Max(Open, Close);
    public decimal LowerWick => Math.Min(Open, Close) - Low;
    public decimal TypicalPrice => (High + Low + Close) / 3m;
    /// <summary>Volume-weighted average price within the bar when quote volume is known; falls back to typical price.</summary>
    public decimal Vwap => Volume > 0 && QuoteVolume > 0 ? QuoteVolume / Volume : TypicalPrice;

    public static Candle Synthetic(Symbol symbol, Timeframe tf, DateTimeOffset openTime, decimal price) =>
        new(symbol, tf, openTime, price, price, price, price, 0m, 0m, 0m, 0m, 0, CandleSource.Synthetic);
}
