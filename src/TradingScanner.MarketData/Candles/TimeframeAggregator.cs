using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Candles;

/// <summary>
/// Aggregates closed base (1m) candles into a higher timeframe. Because the base series is time-regular
/// (synthetic fill), a higher-TF bar closes the instant its last base bar closes: no one-minute lag.
/// Single-writer. Not thread-safe.
/// </summary>
public sealed class TimeframeAggregator
{
    private readonly Symbol _symbol;
    private readonly Timeframe _base;
    private readonly Timeframe _target;

    private DateTimeOffset? _bucket;
    private decimal _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume;
    private int _count;
    private int _live, _historical, _synthetic;

    public Timeframe Target => _target;

    public TimeframeAggregator(Symbol symbol, Timeframe baseTimeframe, Timeframe target)
    {
        if (target.Seconds() % baseTimeframe.Seconds() != 0 || target.Seconds() <= baseTimeframe.Seconds())
            throw new ArgumentException($"{target} is not a multiple of {baseTimeframe}.");
        _symbol = symbol;
        _base = baseTimeframe;
        _target = target;
    }

    public void Reset() => _bucket = null;

    public void OnBaseCandleClosed(in Candle c, List<Candle> closed)
    {
        if (c.Timeframe != _base) throw new ArgumentException($"Expected {_base} candle, got {c.Timeframe}.");
        var bucket = _target.BucketStart(c.OpenTime);

        if (_bucket is { } current && bucket != current)
        {
            // Base series had a hole (only possible around seeding). Close what we have rather than merging buckets.
            CloseCurrent(closed);
        }

        if (_bucket is null)
        {
            _bucket = bucket;
            _open = c.Open; _high = c.High; _low = c.Low; _close = c.Close;
            _volume = _quoteVolume = _buyVolume = _sellVolume = 0m;
            _count = 0; _live = _historical = _synthetic = 0;
        }

        if (c.High > _high) _high = c.High;
        if (c.Low < _low) _low = c.Low;
        _close = c.Close;
        _volume += c.Volume;
        _quoteVolume += c.QuoteVolume;
        _buyVolume += c.BuyVolume;
        _sellVolume += c.SellVolume;
        _count += c.TradeCount;
        switch (c.Source)
        {
            case CandleSource.Live: _live++; break;
            case CandleSource.Historical: _historical++; break;
            default: _synthetic++; break;
        }

        if (c.CloseTime >= _bucket.Value + _target.Duration()) CloseCurrent(closed);
    }

    public Candle? Forming() => _bucket is { } b
        ? new Candle(_symbol, _target, b, _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume, _count, DeriveSource())
        : null;

    /// <summary>
    /// The forming target bar including the still-forming base bar (what a chart should draw). Closed-bar analytics
    /// never use this; it exists so the in-progress higher-timeframe bar reflects the current partial minute.
    /// </summary>
    public Candle? FormingWith(Candle? formingBase)
    {
        var partial = Forming();
        if (formingBase is not { } fb) return partial;
        var bucket = _target.BucketStart(fb.OpenTime);
        if (partial is { } p && p.OpenTime == bucket)
        {
            return p with
            {
                High = Math.Max(p.High, fb.High),
                Low = Math.Min(p.Low, fb.Low),
                Close = fb.Close,
                Volume = p.Volume + fb.Volume,
                QuoteVolume = p.QuoteVolume + fb.QuoteVolume,
                BuyVolume = p.BuyVolume + fb.BuyVolume,
                SellVolume = p.SellVolume + fb.SellVolume,
                TradeCount = p.TradeCount + fb.TradeCount,
                Source = p.Source == CandleSource.Synthetic && fb.Source == CandleSource.Synthetic ? CandleSource.Synthetic : CandleSource.Live,
            };
        }
        // Either nothing is forming yet for this bucket, or the partial belongs to an older bucket (only possible around seeding).
        return fb with { Timeframe = _target, OpenTime = bucket };
    }

    private CandleSource DeriveSource()
    {
        if (_live == 0 && _historical == 0) return CandleSource.Synthetic;
        return _live > 0 ? CandleSource.Live : CandleSource.Historical;
    }

    private void CloseCurrent(List<Candle> closed)
    {
        closed.Add(new Candle(_symbol, _target, _bucket!.Value, _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume, _count, DeriveSource()));
        _bucket = null;
    }

    /// <summary>Aggregate a full sorted list of base candles into closed target candles (partial trailing bucket dropped).</summary>
    public static List<Candle> AggregateAll(Symbol symbol, Timeframe baseTf, Timeframe target, IReadOnlyList<Candle> baseCandles)
    {
        var agg = new TimeframeAggregator(symbol, baseTf, target);
        var closed = new List<Candle>(baseCandles.Count / (target.Seconds() / baseTf.Seconds()) + 1);
        foreach (var c in baseCandles) agg.OnBaseCandleClosed(c, closed);
        return closed;
    }
}
