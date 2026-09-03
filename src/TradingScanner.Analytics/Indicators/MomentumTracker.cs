namespace TradingScanner.Analytics.Indicators;

/// <summary>
/// Keeps base-timeframe (1m) closes and computes returns over fixed horizons plus acceleration
/// (return over the latest window minus the return over the preceding window of equal length).
/// </summary>
public sealed class MomentumTracker
{
    public static readonly int[] HorizonsMinutes = [1, 3, 5, 15, 30, 60, 240, 1440];
    private readonly RollingWindow _closes;

    public MomentumTracker(int capacity = 1441)
    {
        _closes = new RollingWindow(capacity);
    }

    public int Count => _closes.Count;

    public void Update(double close) => _closes.Add(close);

    /// <summary>Close of the bar <paramref name="minutesAgo"/> bars back from the newest closed bar (0 = newest).</summary>
    public double? CloseAgo(int minutesAgo) => minutesAgo < _closes.Count ? _closes.FromEnd(minutesAgo) : null;

    /// <summary>Return of <paramref name="price"/> versus the close <paramref name="minutes"/> bars ago (the bar that closed k minutes before the current forming bar started).</summary>
    public double? Return(int minutes, double price)
    {
        if (minutes < 1 || minutes > _closes.Count) return null;
        var reference = _closes.FromEnd(minutes - 1);
        return reference > 0 ? price / reference - 1 : null;
    }

    /// <summary>Closed-bar return: newest close versus the close <paramref name="minutes"/> bars earlier.</summary>
    public double? ClosedReturn(int minutes)
    {
        if (minutes < 1 || minutes >= _closes.Count) return null;
        var reference = _closes.FromEnd(minutes);
        return reference > 0 ? _closes.Last / reference - 1 : null;
    }

    /// <summary>Return over the last <paramref name="minutes"/> minus the return over the <paramref name="minutes"/> before that. Positive = strengthening.</summary>
    public double? Acceleration(int minutes, double price)
    {
        var recent = Return(minutes, price);
        if (recent is null || 2 * minutes > _closes.Count) return null;
        var a = _closes.FromEnd(minutes - 1);
        var b = _closes.FromEnd(2 * minutes - 1);
        if (a <= 0 || b <= 0) return null;
        return recent - (a / b - 1);
    }

    public void Reset() => _closes.Clear();
}
