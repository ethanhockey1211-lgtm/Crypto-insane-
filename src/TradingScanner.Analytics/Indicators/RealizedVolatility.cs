namespace TradingScanner.Analytics.Indicators;

/// <summary>Population standard deviation of log returns over the last <c>period</c> bars, as a fraction per bar.</summary>
public sealed class RealizedVolatility
{
    private readonly RollingWindow _returns;
    private double _prevClose = double.NaN;

    public int Period => _returns.Capacity;
    public bool IsReady => _returns.IsFull;
    public double Value => _returns.IsFull ? _returns.Std : double.NaN;

    public RealizedVolatility(int period = 20)
    {
        _returns = new RollingWindow(period);
    }

    public void Update(double close)
    {
        if (!double.IsNaN(_prevClose) && _prevClose > 0 && close > 0) _returns.Add(Math.Log(close / _prevClose));
        _prevClose = close;
    }

    public void Reset()
    {
        _returns.Clear(); _prevClose = double.NaN;
    }
}
