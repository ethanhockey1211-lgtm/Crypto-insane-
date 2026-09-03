namespace TradingScanner.Analytics.Indicators;

/// <summary>Exponential moving average, seeded with the simple average of the first <c>period</c> values (standard convention).</summary>
public sealed class Ema
{
    private readonly double _k;
    private double _seedSum;
    private int _seedCount;

    public int Period { get; }
    public bool IsReady { get; private set; }
    public double Value { get; private set; } = double.NaN;
    /// <summary>Value before the most recent update (NaN until two ready values exist).</summary>
    public double Previous { get; private set; } = double.NaN;

    public Ema(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        Period = period;
        _k = 2.0 / (period + 1);
    }

    public void Update(double value)
    {
        if (!IsReady)
        {
            _seedSum += value;
            if (++_seedCount < Period) return;
            Value = _seedSum / Period;
            IsReady = true;
            return;
        }
        Previous = Value;
        Value = value * _k + Value * (1 - _k);
    }

    /// <summary>What the EMA would be if <paramref name="value"/> were the next close. Does not mutate.</summary>
    public double? Project(double value) => IsReady ? value * _k + Value * (1 - _k) : null;

    public void Reset()
    {
        _seedSum = 0; _seedCount = 0; IsReady = false; Value = double.NaN; Previous = double.NaN;
    }
}
