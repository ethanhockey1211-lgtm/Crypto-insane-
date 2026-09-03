namespace TradingScanner.Analytics.Indicators;

/// <summary>Wilder's RSI: first average gain/loss is a simple mean over <c>period</c> changes, then Wilder smoothing.</summary>
public sealed class Rsi
{
    private double _prevClose = double.NaN;
    private double _avgGain, _avgLoss;
    private int _changes;

    public int Period { get; }
    public bool IsReady => _changes >= Period;
    public double Value { get; private set; } = double.NaN;
    public double Previous { get; private set; } = double.NaN;

    public Rsi(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        Period = period;
    }

    public void Update(double close)
    {
        if (double.IsNaN(_prevClose))
        {
            _prevClose = close;
            return;
        }
        var change = close - _prevClose;
        _prevClose = close;
        var gain = change > 0 ? change : 0;
        var loss = change < 0 ? -change : 0;

        if (_changes < Period)
        {
            _avgGain += gain / Period;
            _avgLoss += loss / Period;
            _changes++;
            if (_changes == Period) Value = Compute(_avgGain, _avgLoss);
            return;
        }

        _avgGain = (_avgGain * (Period - 1) + gain) / Period;
        _avgLoss = (_avgLoss * (Period - 1) + loss) / Period;
        Previous = Value;
        Value = Compute(_avgGain, _avgLoss);
    }

    public double? Project(double close)
    {
        if (!IsReady) return null;
        var change = close - _prevClose;
        var g = (_avgGain * (Period - 1) + (change > 0 ? change : 0)) / Period;
        var l = (_avgLoss * (Period - 1) + (change < 0 ? -change : 0)) / Period;
        return Compute(g, l);
    }

    private static double Compute(double avgGain, double avgLoss)
    {
        if (avgLoss == 0) return avgGain == 0 ? 50 : 100;
        return 100 - 100 / (1 + avgGain / avgLoss);
    }

    public void Reset()
    {
        _prevClose = double.NaN; _avgGain = 0; _avgLoss = 0; _changes = 0; Value = double.NaN; Previous = double.NaN;
    }
}
