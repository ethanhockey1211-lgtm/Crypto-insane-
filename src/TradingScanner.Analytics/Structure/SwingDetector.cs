using TradingScanner.Core.Market;

namespace TradingScanner.Analytics.Structure;

/// <summary>
/// Fractal swing detector: bar i is a swing high when its high exceeds the highs of the <c>strength</c> bars on each
/// side (ties on the right side are allowed so flat double-tops still register). A swing is emitted when the
/// right-side bars have closed, i.e. <c>strength</c> bars after the fact — no look-ahead.
/// </summary>
public sealed class SwingDetector
{
    private readonly int _strength;
    private readonly Candle[] _window;   // 2*strength + 1 bars, oldest first
    private int _count;
    private long _barIndex = -1;

    public SwingDetector(int strength)
    {
        if (strength < 1) throw new ArgumentOutOfRangeException(nameof(strength));
        _strength = strength;
        _window = new Candle[2 * strength + 1];
    }

    public int Strength => _strength;
    public long BarsSeen => _barIndex + 1;

    /// <summary>Feed a closed bar. Returns the swing(s) confirmed by this bar (at most one high and one low).</summary>
    public (SwingPoint? high, SwingPoint? low) Update(in Candle c)
    {
        _barIndex++;
        if (_count == _window.Length)
        {
            Array.Copy(_window, 1, _window, 0, _window.Length - 1);
            _window[^1] = c;
        }
        else
        {
            _window[_count++] = c;
        }
        if (_count < _window.Length) return (null, null);

        var center = _window[_strength];
        var centerIndex = _barIndex - _strength;
        var isHigh = true;
        var isLow = true;
        for (var k = 1; k <= _strength; k++)
        {
            var left = _window[_strength - k];
            var right = _window[_strength + k];
            if (!(center.High > left.High && center.High >= right.High)) isHigh = false;
            if (!(center.Low < left.Low && center.Low <= right.Low)) isLow = false;
            if (!isHigh && !isLow) break;
        }

        SwingPoint? high = isHigh ? new SwingPoint(SwingType.High, (double)center.High, center.OpenTime, centerIndex, c.CloseTime) : null;
        SwingPoint? low = isLow ? new SwingPoint(SwingType.Low, (double)center.Low, center.OpenTime, centerIndex, c.CloseTime) : null;
        return (high, low);
    }

    public void Reset()
    {
        _count = 0; _barIndex = -1;
    }
}
