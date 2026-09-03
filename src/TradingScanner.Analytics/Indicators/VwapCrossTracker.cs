namespace TradingScanner.Analytics.Indicators;

/// <summary>Tracks which side of VWAP closed bars are on, and the most recent cross (reclaim/loss).</summary>
public sealed class VwapCrossTracker
{
    private bool? _above;
    public int BarsOnCurrentSide { get; private set; }
    public int BarsOnPreviousSide { get; private set; }
    public CrossDirection LastCross { get; private set; }
    public DateTimeOffset? LastCrossAt { get; private set; }
    public double? CrossRelVol { get; private set; }
    public bool? Above => _above;

    public void Update(double close, double vwap, DateTimeOffset closeTime, double? relVol)
    {
        var above = close > vwap;
        if (_above is { } prev && prev != above)
        {
            BarsOnPreviousSide = BarsOnCurrentSide;
            BarsOnCurrentSide = 1;
            LastCross = above ? CrossDirection.Bullish : CrossDirection.Bearish;
            LastCrossAt = closeTime;
            CrossRelVol = relVol;
        }
        else
        {
            BarsOnCurrentSide++;
        }
        _above = above;
    }

    public VwapState? State => _above is { } a ? new VwapState(a, BarsOnCurrentSide, BarsOnPreviousSide, LastCross, LastCrossAt, CrossRelVol) : null;

    public void Reset()
    {
        _above = null; BarsOnCurrentSide = 0; BarsOnPreviousSide = 0; LastCross = CrossDirection.None; LastCrossAt = null; CrossRelVol = null;
    }
}
