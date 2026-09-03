namespace TradingScanner.Analytics.Indicators;

/// <summary>Wilder's Average True Range. First value is the simple mean of the first <c>period</c> true ranges.</summary>
public sealed class Atr
{
    private double _prevClose = double.NaN;
    private double _seedSum;
    private int _count;

    public int Period { get; }
    public bool IsReady => _count >= Period;
    public double Value { get; private set; } = double.NaN;
    public double LastTrueRange { get; private set; } = double.NaN;

    public Atr(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        Period = period;
    }

    public void Update(double high, double low, double close)
    {
        var tr = double.IsNaN(_prevClose)
            ? high - low
            : Math.Max(high - low, Math.Max(Math.Abs(high - _prevClose), Math.Abs(low - _prevClose)));
        _prevClose = close;
        LastTrueRange = tr;

        if (_count < Period)
        {
            _seedSum += tr;
            _count++;
            if (_count == Period) Value = _seedSum / Period;
            return;
        }
        Value = (Value * (Period - 1) + tr) / Period;
    }

    /// <summary>ATR as a fraction of price.</summary>
    public double? Fraction(double price) => IsReady && price > 0 ? Value / price : null;

    public void Reset()
    {
        _prevClose = double.NaN; _seedSum = 0; _count = 0; Value = double.NaN; LastTrueRange = double.NaN;
    }
}
