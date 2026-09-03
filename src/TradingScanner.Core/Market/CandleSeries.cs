namespace TradingScanner.Core.Market;

/// <summary>
/// Fixed-capacity ring buffer of CLOSED candles for one symbol/timeframe, plus an optional forming candle.
/// Thread model: one writer. Readers must call <see cref="Snapshot"/> (takes a short lock) unless they run
/// on the writer thread, in which case the indexers are safe.
/// Only closed candles are indexable, which is what prevents look-ahead in analytics and backtests.
/// </summary>
public sealed class CandleSeries
{
    private readonly Candle[] _buffer;
    private int _head;   // index of oldest
    private int _count;
    private readonly object _gate = new();

    public Symbol Symbol { get; }
    public Timeframe Timeframe { get; }
    public int Capacity => _buffer.Length;
    public int Count => _count;
    public bool IsEmpty => _count == 0;

    /// <summary>The in-progress bar, if any. Never used by indicators that must be look-ahead free.</summary>
    public Candle? Forming { get; private set; }

    public CandleSeries(Symbol symbol, Timeframe timeframe, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Symbol = symbol;
        Timeframe = timeframe;
        _buffer = new Candle[capacity];
    }

    /// <summary>Oldest = 0, newest = Count-1.</summary>
    public Candle this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _buffer[(_head + index) % _buffer.Length];
        }
    }

    /// <summary>0 = most recent closed candle, 1 = the one before, ...</summary>
    public Candle FromEnd(int back)
    {
        if ((uint)back >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(back));
        return this[_count - 1 - back];
    }

    public Candle? Last => _count == 0 ? null : this[_count - 1];

    public void Append(Candle candle)
    {
        if (candle.Timeframe != Timeframe) throw new ArgumentException($"Expected {Timeframe}, got {candle.Timeframe}.");
        lock (_gate)
        {
            if (_count > 0)
            {
                var last = this[_count - 1];
                if (candle.OpenTime <= last.OpenTime)
                    throw new InvalidOperationException($"Candle {candle.OpenTime:O} is not after last {last.OpenTime:O} ({Symbol} {Timeframe}).");
            }
            if (_count == _buffer.Length)
            {
                _buffer[_head] = candle;
                _head = (_head + 1) % _buffer.Length;
            }
            else
            {
                _buffer[(_head + _count) % _buffer.Length] = candle;
                _count++;
            }
        }
    }

    public void SetForming(Candle? forming)
    {
        lock (_gate) Forming = forming;
    }

    /// <summary>Replace all closed candles (used when merging REST history with live-built bars). Input must be sorted ascending.</summary>
    public void Reset(IReadOnlyList<Candle> closed)
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
            var start = Math.Max(0, closed.Count - _buffer.Length);
            DateTimeOffset? prev = null;
            for (var i = start; i < closed.Count; i++)
            {
                var c = closed[i];
                if (c.Timeframe != Timeframe) throw new ArgumentException($"Expected {Timeframe}, got {c.Timeframe}.");
                if (prev is { } p && c.OpenTime <= p) throw new ArgumentException("Candles must be strictly ascending by OpenTime.");
                prev = c.OpenTime;
                _buffer[_count++] = c;
            }
        }
    }

    /// <summary>Copy of the closed candles (ascending) plus the forming candle, consistent at one instant.</summary>
    public CandleSnapshot Snapshot(int? lastN = null)
    {
        lock (_gate)
        {
            var n = lastN is { } l ? Math.Min(l, _count) : _count;
            var arr = new Candle[n];
            var from = _count - n;
            for (var i = 0; i < n; i++) arr[i] = this[from + i];
            return new CandleSnapshot(Symbol, Timeframe, arr, Forming);
        }
    }

    /// <summary>Writer-thread-only view without copying. Do not hold across writes.</summary>
    public ReadOnlySpan<Candle> DangerousLastSpan(int n)
    {
        // Only contiguous when the buffer has not wrapped; callers that need speed can use this in tests/backtests.
        if (_head != 0) throw new InvalidOperationException("Buffer has wrapped; use Snapshot().");
        n = Math.Min(n, _count);
        return new ReadOnlySpan<Candle>(_buffer, _count - n, n);
    }
}

public sealed record CandleSnapshot(Symbol Symbol, Timeframe Timeframe, Candle[] Closed, Candle? Forming)
{
    public Candle? Last => Closed.Length == 0 ? null : Closed[^1];
}
