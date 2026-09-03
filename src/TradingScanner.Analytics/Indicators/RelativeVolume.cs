namespace TradingScanner.Analytics.Indicators;

/// <summary>
/// Volume relative to a rolling baseline of prior bars (current bar excluded from the baseline).
/// Also exposes a z-score and a fast/baseline ratio for volume acceleration.
/// Known limitation: the baseline is not time-of-day aware; a session-profile baseline is a planned refinement.
/// </summary>
public sealed class RelativeVolume
{
    private readonly RollingWindow _baseline;
    private readonly RollingWindow _fast;

    public int BaselinePeriod => _baseline.Capacity;
    public int FastPeriod => _fast.Capacity;
    public bool IsReady => _baseline.IsFull;
    /// <summary>Current bar volume / baseline mean.</summary>
    public double Value { get; private set; } = double.NaN;
    /// <summary>(current − baseline mean) / baseline std.</summary>
    public double ZScore { get; private set; } = double.NaN;
    /// <summary>Mean of the last <c>fast</c> bars (including current) / baseline mean.</summary>
    public double FastRatio { get; private set; } = double.NaN;
    public double BaselineMean => _baseline.Mean;

    public RelativeVolume(int baselinePeriod = 20, int fastPeriod = 3)
    {
        _baseline = new RollingWindow(baselinePeriod);
        _fast = new RollingWindow(fastPeriod);
    }

    public void Update(double volume)
    {
        if (_baseline.IsFull)
        {
            var mean = _baseline.Mean;
            var std = _baseline.Std;
            Value = mean > 0 ? volume / mean : double.NaN;
            ZScore = std > 0 ? (volume - mean) / std : double.NaN;
            _fast.Add(volume);
            FastRatio = mean > 0 ? _fast.MeanOfLast(_fast.Capacity) / mean : double.NaN;
        }
        else
        {
            _fast.Add(volume);
        }
        _baseline.Add(volume);
    }

    /// <summary>Relative volume a hypothetical current-bar volume would have. Does not mutate.</summary>
    public double? Project(double volume) => _baseline.IsFull && _baseline.Mean > 0 ? volume / _baseline.Mean : null;

    public void Reset()
    {
        _baseline.Clear(); _fast.Clear(); Value = double.NaN; ZScore = double.NaN; FastRatio = double.NaN;
    }
}
