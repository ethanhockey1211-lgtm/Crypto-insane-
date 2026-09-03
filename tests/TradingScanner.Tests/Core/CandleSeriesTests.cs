using TradingScanner.Core.Market;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Core;

public class CandleSeriesTests
{
    private static Candle C(int minute, decimal close = 100m) =>
        T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(minute), close, close, close, close);

    [Fact]
    public void Ring_buffer_keeps_only_the_newest_capacity_candles()
    {
        var s = new CandleSeries(new Symbol("BTC-USD"), Timeframe.M1, 3);
        for (var i = 0; i < 5; i++) s.Append(C(i, 100 + i));
        Assert.Equal(3, s.Count);
        Assert.Equal(102m, s[0].Close);
        Assert.Equal(104m, s[2].Close);
        Assert.Equal(104m, s.FromEnd(0).Close);
        Assert.Equal(102m, s.FromEnd(2).Close);
        Assert.Equal(104m, s.Last!.Value.Close);
    }

    [Fact]
    public void Append_rejects_out_of_order_and_duplicate_candles()
    {
        var s = new CandleSeries(new Symbol("BTC-USD"), Timeframe.M1, 10);
        s.Append(C(1));
        Assert.Throws<InvalidOperationException>(() => s.Append(C(1)));
        Assert.Throws<InvalidOperationException>(() => s.Append(C(0)));
        Assert.Throws<ArgumentException>(() => s.Append(T.Candle("BTC-USD", Timeframe.M5, T.Base.AddMinutes(5), 1, 1, 1, 1)));
    }

    [Fact]
    public void Snapshot_returns_ascending_copy_and_forming()
    {
        var s = new CandleSeries(new Symbol("BTC-USD"), Timeframe.M1, 4);
        for (var i = 0; i < 6; i++) s.Append(C(i, 100 + i));
        s.SetForming(C(6, 200));
        var snap = s.Snapshot(2);
        Assert.Equal([104m, 105m], snap.Closed.Select(c => c.Close));
        Assert.Equal(200m, snap.Forming!.Value.Close);
        Assert.Equal(105m, snap.Last!.Value.Close);
        Assert.Equal(4, s.Snapshot().Closed.Length);
    }

    [Fact]
    public void Reset_replaces_contents_and_truncates_to_capacity()
    {
        var s = new CandleSeries(new Symbol("BTC-USD"), Timeframe.M1, 3);
        s.Append(C(0));
        s.Reset(Enumerable.Range(10, 5).Select(i => C(i, i)).ToList());
        Assert.Equal(3, s.Count);
        Assert.Equal(12m, s[0].Close);
        Assert.Equal(14m, s[2].Close);
        Assert.Throws<ArgumentException>(() => s.Reset([C(2), C(1)]));
    }
}
