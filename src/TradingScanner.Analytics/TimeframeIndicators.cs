using TradingScanner.Analytics.Indicators;
using TradingScanner.Core.Market;

namespace TradingScanner.Analytics;

/// <summary>All indicators for one symbol/timeframe, updated incrementally per closed bar. Single-writer.</summary>
public sealed class TimeframeIndicators
{
    private readonly Ema _ema9 = new(9), _ema20 = new(20), _ema50 = new(50), _ema200 = new(200);
    private readonly Rsi _rsi;
    private readonly Atr _atr;
    private readonly Atr _atrFast;
    private readonly RollingWindow _buy;
    private readonly RollingWindow _sell;
    private readonly RelativeVolume _relVol;
    private readonly RealizedVolatility _rvol;
    private bool? _ema9Above20;
    private int? _barsSinceCross;
    private CrossDirection _lastCross;
    private IndicatorValues? _last;

    public Timeframe Timeframe { get; }
    public int Bars { get; private set; }
    public IndicatorValues? Last => _last;

    public TimeframeIndicators(Timeframe timeframe, AnalyticsOptions options)
    {
        Timeframe = timeframe;
        _rsi = new Rsi(options.RsiPeriod);
        _atr = new Atr(options.AtrPeriod);
        _atrFast = new Atr(options.AtrFastPeriod);
        _buy = new RollingWindow(options.BuyShareBars);
        _sell = new RollingWindow(options.BuyShareBars);
        _relVol = new RelativeVolume(options.RelativeVolumeBaseline, options.RelativeVolumeFast);
        _rvol = new RealizedVolatility(options.RealizedVolatilityPeriod);
    }

    public void Update(in Candle c)
    {
        if (c.Timeframe != Timeframe) throw new ArgumentException($"Expected {Timeframe}, got {c.Timeframe}.");
        var close = (double)c.Close;
        _ema9.Update(close); _ema20.Update(close); _ema50.Update(close); _ema200.Update(close);
        _rsi.Update(close);
        _atr.Update((double)c.High, (double)c.Low, close);
        _atrFast.Update((double)c.High, (double)c.Low, close);
        _buy.Add((double)c.BuyVolume);
        _sell.Add((double)c.SellVolume);
        _relVol.Update((double)c.Volume);
        _rvol.Update(close);
        Bars++;

        if (_ema9.IsReady && _ema20.IsReady)
        {
            var above = _ema9.Value > _ema20.Value;
            if (_ema9Above20 is { } prev && prev != above)
            {
                _barsSinceCross = 0;
                _lastCross = above ? CrossDirection.Bullish : CrossDirection.Bearish;
            }
            else if (_barsSinceCross is { } b)
            {
                _barsSinceCross = b + 1;
            }
            _ema9Above20 = above;
        }

        var alignment = Classify();
        double? spread = null;
        if (_ema9.IsReady && _ema20.IsReady && _ema50.IsReady && close > 0)
        {
            var hi = Math.Max(_ema9.Value, Math.Max(_ema20.Value, _ema50.Value));
            var lo = Math.Min(_ema9.Value, Math.Min(_ema20.Value, _ema50.Value));
            spread = (hi - lo) / close;
        }

        _last = new IndicatorValues(
            Timeframe, c.CloseTime, close,
            Val(_ema9), Val(_ema20), Val(_ema50), Val(_ema200),
            _rsi.IsReady ? _rsi.Value : null, _rsi.IsReady && !double.IsNaN(_rsi.Previous) ? _rsi.Previous : null,
            _atr.IsReady ? _atr.Value : null, _atr.Fraction(close), _atr.IsReady ? _atr.LastTrueRange : null,
            _atr.IsReady && _atr.Value > 0 ? _atr.LastTrueRange / _atr.Value : null,
            _relVol.IsReady && !double.IsNaN(_relVol.Value) ? _relVol.Value : null,
            _relVol.IsReady && !double.IsNaN(_relVol.ZScore) ? _relVol.ZScore : null,
            _relVol.IsReady && !double.IsNaN(_relVol.FastRatio) ? _relVol.FastRatio : null,
            _rvol.IsReady ? _rvol.Value * 100 : null,
            alignment, spread, _barsSinceCross, _lastCross,
            _ema20.IsReady && _atr.IsReady && _atr.Value > 0 ? (close - _ema20.Value) / _atr.Value : null,
            _atr.IsReady && _atrFast.IsReady && _atr.Value > 0 ? _atrFast.Value / _atr.Value : null,
            _buy.IsFull && _buy.Sum + _sell.Sum > 0 ? _buy.Sum / (_buy.Sum + _sell.Sum) : null,
            (double)c.Open, (double)c.High, (double)c.Low);
    }

    private static double? Val(Ema e) => e.IsReady ? e.Value : null;

    private EmaAlignment Classify()
    {
        if (!_ema9.IsReady || !_ema20.IsReady || !_ema50.IsReady) return EmaAlignment.Unknown;
        var e9 = _ema9.Value; var e20 = _ema20.Value; var e50 = _ema50.Value;
        var bull = e9 > e20 && e20 > e50;
        var bear = e9 < e20 && e20 < e50;
        if (_ema200.IsReady)
        {
            bull &= e50 > _ema200.Value;
            bear &= e50 < _ema200.Value;
        }
        return bull ? EmaAlignment.Bullish : bear ? EmaAlignment.Bearish : EmaAlignment.Mixed;
    }

    public void Reset()
    {
        _ema9.Reset(); _ema20.Reset(); _ema50.Reset(); _ema200.Reset();
        _rsi.Reset(); _atr.Reset(); _atrFast.Reset(); _buy.Clear(); _sell.Clear(); _relVol.Reset(); _rvol.Reset();
        _ema9Above20 = null; _barsSinceCross = null; _lastCross = CrossDirection.None; _last = null; Bars = 0;
    }
}
