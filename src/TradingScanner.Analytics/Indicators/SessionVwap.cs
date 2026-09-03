using TradingScanner.Core.Market;

namespace TradingScanner.Analytics.Indicators;

/// <summary>
/// Volume-weighted average price anchored to a session. Crypto trades continuously, so the default session is
/// the UTC day; the anchor function is injectable (e.g. rolling anchors later). Uses each bar's own VWAP
/// (quote volume / volume, exact for live bars) and tracks volume-weighted variance for deviation bands.
/// Zero-volume (synthetic) bars leave the value unchanged.
/// </summary>
public sealed class SessionVwap
{
    private readonly Func<DateTimeOffset, DateTimeOffset> _anchor;
    private double _sumV, _sumPV, _sumP2V;

    public DateTimeOffset? SessionStart { get; private set; }
    public int Bars { get; private set; }
    public bool IsReady => _sumV > 0;
    public double Value => _sumV > 0 ? _sumPV / _sumV : double.NaN;

    /// <summary>Volume-weighted standard deviation of price around the VWAP.</summary>
    public double Std
    {
        get
        {
            if (_sumV <= 0) return double.NaN;
            var mean = _sumPV / _sumV;
            var v = _sumP2V / _sumV - mean * mean;
            return v <= 0 ? 0 : Math.Sqrt(v);
        }
    }

    public SessionVwap(Func<DateTimeOffset, DateTimeOffset>? anchor = null)
    {
        _anchor = anchor ?? UtcDay;
    }

    public static DateTimeOffset UtcDay(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
    }

    public void Update(in Candle c)
    {
        var session = _anchor(c.OpenTime);
        if (SessionStart != session)
        {
            SessionStart = session;
            _sumV = 0; _sumPV = 0; _sumP2V = 0; Bars = 0;
        }
        Bars++;
        if (c.Volume <= 0) return;
        var v = (double)c.Volume;
        var p = (double)c.Vwap;
        _sumV += v;
        _sumPV += p * v;
        _sumP2V += p * p * v;
    }

    /// <summary>Signed distance of <paramref name="price"/> from VWAP in standard deviations (null when undefined).</summary>
    public double? Sigma(double price)
    {
        if (!IsReady) return null;
        var std = Std;
        return std > 0 ? (price - Value) / std : null;
    }

    public void Reset()
    {
        SessionStart = null; _sumV = 0; _sumPV = 0; _sumP2V = 0; Bars = 0;
    }
}
