namespace TradingScanner.Analytics.Indicators;

/// <summary>
/// Fixed-capacity ring of doubles with running sum and sum of squares. Sums are re-derived from the
/// buffer periodically so floating-point drift stays bounded over long-running sessions.
/// </summary>
public sealed class RollingWindow
{
    private readonly double[] _buf;
    private int _head, _count;
    private double _sum, _sumSq;
    private int _addsSinceRescan;

    public RollingWindow(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buf = new double[capacity];
    }

    public int Capacity => _buf.Length;
    public int Count => _count;
    public bool IsFull => _count == _buf.Length;
    public double Sum => _sum;
    public double Mean => _count == 0 ? double.NaN : _sum / _count;

    /// <summary>Population standard deviation.</summary>
    public double Std
    {
        get
        {
            if (_count == 0) return double.NaN;
            var mean = _sum / _count;
            var v = _sumSq / _count - mean * mean;
            return v <= 0 ? 0 : Math.Sqrt(v);
        }
    }

    /// <summary>0 = newest.</summary>
    public double FromEnd(int back)
    {
        if ((uint)back >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(back));
        return _buf[(_head + _count - 1 - back) % _buf.Length];
    }

    public double Last => FromEnd(0);

    /// <summary>Mean of the newest <paramref name="n"/> values.</summary>
    public double MeanOfLast(int n)
    {
        n = Math.Min(n, _count);
        if (n == 0) return double.NaN;
        double s = 0;
        for (var i = 0; i < n; i++) s += FromEnd(i);
        return s / n;
    }

    public void Add(double value)
    {
        if (_count == _buf.Length)
        {
            var old = _buf[_head];
            _sum -= old;
            _sumSq -= old * old;
            _buf[_head] = value;
            _head = (_head + 1) % _buf.Length;
        }
        else
        {
            _buf[(_head + _count) % _buf.Length] = value;
            _count++;
        }
        _sum += value;
        _sumSq += value * value;
        if (++_addsSinceRescan >= 4096) Rescan();
    }

    public void Clear()
    {
        _head = 0; _count = 0; _sum = 0; _sumSq = 0; _addsSinceRescan = 0;
    }

    private void Rescan()
    {
        double s = 0, sq = 0;
        for (var i = 0; i < _count; i++)
        {
            var v = _buf[(_head + i) % _buf.Length];
            s += v; sq += v * v;
        }
        _sum = s; _sumSq = sq; _addsSinceRescan = 0;
    }
}
