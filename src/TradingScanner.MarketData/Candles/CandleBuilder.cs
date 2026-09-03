using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Candles;

/// <summary>
/// Builds base-timeframe (1m) candles from trades. Buckets by the EXCHANGE timestamp, never by receive time.
/// Closes bars either when a trade for a later bucket arrives, or when the wall clock passes the bucket end
/// plus a grace period (so illiquid symbols still produce regular series). Buckets with no trades are
/// filled with synthetic flat candles so every series is time-regular.
/// Single-writer. Not thread-safe.
/// </summary>
public sealed class CandleBuilder
{
    private readonly Symbol _symbol;
    private readonly Timeframe _tf;
    private readonly TimeSpan _grace;
    private readonly int _maxSyntheticFill;

    private DateTimeOffset? _bucket;
    private decimal _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume;
    private int _count;

    private decimal? _lastClose;
    private DateTimeOffset? _lastClosedBucket;

    public long LateTrades { get; private set; }
    public long SyntheticCandles { get; private set; }
    public DateTimeOffset? LastClosedBucket => _lastClosedBucket;
    public decimal? LastClose => _lastClose;
    public bool HasForming => _bucket is not null;

    public CandleBuilder(Symbol symbol, Timeframe timeframe, TimeSpan grace, int maxSyntheticFill = 1500)
    {
        _symbol = symbol;
        _tf = timeframe;
        _grace = grace;
        _maxSyntheticFill = maxSyntheticFill;
    }

    /// <summary>Seed continuity state from history so the first live bar is preceded by synthetic fill rather than a hole.</summary>
    public void Seed(DateTimeOffset lastClosedBucket, decimal lastClose)
    {
        if (_lastClosedBucket is { } existing && existing >= lastClosedBucket) return;
        _lastClosedBucket = lastClosedBucket;
        _lastClose = lastClose;
    }

    /// <summary>Apply a trade. Any candles closed as a result are appended to <paramref name="closed"/>.</summary>
    public void OnTrade(in Trade trade, List<Candle> closed)
    {
        var bucket = _tf.BucketStart(trade.ExchangeTime);

        if (_bucket is { } current)
        {
            if (bucket == current)
            {
                Accumulate(trade);
                return;
            }
            if (bucket < current)
            {
                LateTrades++;
                return;
            }
            CloseCurrent(closed);
        }
        else if (_lastClosedBucket is { } lastClosed && bucket <= lastClosed)
        {
            LateTrades++;
            return;
        }

        FillSyntheticUpTo(bucket, closed);
        Start(bucket, trade);
    }

    /// <summary>Clock-driven close for buckets whose end (plus grace) has passed. Fills synthetic bars for empty buckets.</summary>
    public void OnClock(DateTimeOffset now, List<Candle> closed)
    {
        if (_bucket is { } current)
        {
            if (now < current + _tf.Duration() + _grace) return;
            CloseCurrent(closed);
        }

        if (_lastClosedBucket is null || _lastClose is null) return;

        // Fill every bucket that is fully complete (end + grace has passed) but received no trades.
        var next = _lastClosedBucket.Value + _tf.Duration();
        var fills = 0;
        while (next + _tf.Duration() + _grace <= now && fills < _maxSyntheticFill)
        {
            EmitSynthetic(next, closed);
            next += _tf.Duration();
            fills++;
        }
    }

    /// <summary>Snapshot of the in-progress bar, if any.</summary>
    public Candle? Forming() => _bucket is { } b
        ? new Candle(_symbol, _tf, b, _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume, _count, CandleSource.Live)
        : null;

    private void Start(DateTimeOffset bucket, in Trade t)
    {
        _bucket = bucket;
        _open = _high = _low = _close = t.Price;
        _volume = _quoteVolume = _buyVolume = _sellVolume = 0m;
        _count = 0;
        Accumulate(t);
    }

    private void Accumulate(in Trade t)
    {
        if (t.Price > _high) _high = t.Price;
        if (t.Price < _low) _low = t.Price;
        _close = t.Price;
        _volume += t.Size;
        _quoteVolume += t.Price * t.Size;
        if (t.TakerSide == TradeSide.Buy) _buyVolume += t.Size; else _sellVolume += t.Size;
        _count++;
    }

    private void CloseCurrent(List<Candle> closed)
    {
        var b = _bucket!.Value;
        closed.Add(new Candle(_symbol, _tf, b, _open, _high, _low, _close, _volume, _quoteVolume, _buyVolume, _sellVolume, _count, CandleSource.Live));
        _lastClose = _close;
        _lastClosedBucket = b;
        _bucket = null;
    }

    private void FillSyntheticUpTo(DateTimeOffset exclusiveEnd, List<Candle> closed)
    {
        if (_lastClosedBucket is null || _lastClose is null) return;
        var next = _lastClosedBucket.Value + _tf.Duration();
        // If the hole is larger than we are willing to fill, skip the oldest part; the ring buffer would drop it anyway.
        var holes = (long)((exclusiveEnd - next) / _tf.Duration());
        if (holes > _maxSyntheticFill) next = exclusiveEnd - _tf.Duration() * _maxSyntheticFill;
        while (next < exclusiveEnd)
        {
            EmitSynthetic(next, closed);
            next += _tf.Duration();
        }
    }

    private void EmitSynthetic(DateTimeOffset bucket, List<Candle> closed)
    {
        closed.Add(Candle.Synthetic(_symbol, _tf, bucket, _lastClose!.Value));
        _lastClosedBucket = bucket;
        SyntheticCandles++;
    }
}
