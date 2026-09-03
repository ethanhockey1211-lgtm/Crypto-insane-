using TradingScanner.Core.Market;
using TradingScanner.MarketData.Candles;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.MarketData;

public class TimeframeAggregatorTests
{
    private static readonly Symbol Btc = new("BTC-USD");

    private static Candle M1(int minute, decimal o, decimal h, decimal l, decimal c, decimal v = 1m, CandleSource src = CandleSource.Live) =>
        T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(minute), o, h, l, c, v, src);

    [Fact]
    public void Five_one_minute_bars_close_exactly_one_five_minute_bar()
    {
        var agg = new TimeframeAggregator(Btc, Timeframe.M1, Timeframe.M5);
        var closed = new List<Candle>();
        agg.OnBaseCandleClosed(M1(0, 10, 12, 9, 11), closed);
        agg.OnBaseCandleClosed(M1(1, 11, 15, 10, 14), closed);
        agg.OnBaseCandleClosed(M1(2, 14, 14, 8, 9), closed);
        agg.OnBaseCandleClosed(M1(3, 9, 10, 9, 10), closed);
        Assert.Empty(closed);
        Assert.Equal(15m, agg.Forming()!.Value.High);

        agg.OnBaseCandleClosed(M1(4, 10, 11, 10, 10.5m), closed);
        var c = Assert.Single(closed);
        Assert.Equal(Timeframe.M5, c.Timeframe);
        Assert.Equal(T.Base, c.OpenTime);
        Assert.Equal(10m, c.Open);
        Assert.Equal(15m, c.High);
        Assert.Equal(8m, c.Low);
        Assert.Equal(10.5m, c.Close);
        Assert.Equal(5m, c.Volume);
        Assert.Equal(15, c.TradeCount);
        Assert.Equal(CandleSource.Live, c.Source);
        Assert.Null(agg.Forming());
    }

    [Fact]
    public void Bars_align_to_epoch_buckets_not_to_the_first_bar_seen()
    {
        var agg = new TimeframeAggregator(Btc, Timeframe.M1, Timeframe.M5);
        var closed = new List<Candle>();
        // Start at 12:03 -> first 5m bucket (12:00) closes after 12:04, containing only two bars.
        agg.OnBaseCandleClosed(M1(3, 1, 1, 1, 1), closed);
        agg.OnBaseCandleClosed(M1(4, 1, 1, 1, 1), closed);
        var c = Assert.Single(closed);
        Assert.Equal(T.Base, c.OpenTime);
        Assert.Equal(2m, c.Volume);
    }

    [Fact]
    public void Source_is_synthetic_only_when_every_constituent_is_synthetic()
    {
        var agg = new TimeframeAggregator(Btc, Timeframe.M1, Timeframe.M3);
        var closed = new List<Candle>();
        agg.OnBaseCandleClosed(M1(0, 1, 1, 1, 1, 0, CandleSource.Synthetic), closed);
        agg.OnBaseCandleClosed(M1(1, 1, 1, 1, 1, 0, CandleSource.Synthetic), closed);
        agg.OnBaseCandleClosed(M1(2, 1, 1, 1, 1, 0, CandleSource.Synthetic), closed);
        Assert.Equal(CandleSource.Synthetic, closed[0].Source);

        agg.OnBaseCandleClosed(M1(3, 1, 1, 1, 1, 0, CandleSource.Synthetic), closed);
        agg.OnBaseCandleClosed(M1(4, 1, 1, 1, 1, 1, CandleSource.Historical), closed);
        agg.OnBaseCandleClosed(M1(5, 1, 1, 1, 1, 1, CandleSource.Live), closed);
        Assert.Equal(CandleSource.Live, closed[1].Source);
    }

    [Fact]
    public void AggregateAll_drops_the_trailing_partial_bucket()
    {
        var bars = Enumerable.Range(0, 13).Select(i => M1(i, 1, 2, 0.5m, 1.5m)).ToList();
        var fives = TimeframeAggregator.AggregateAll(Btc, Timeframe.M1, Timeframe.M5, bars);
        Assert.Equal(2, fives.Count);
        Assert.Equal(T.Base, fives[0].OpenTime);
        Assert.Equal(T.Base.AddMinutes(5), fives[1].OpenTime);
    }

    [Fact]
    public void Rejects_invalid_timeframe_pairs()
    {
        Assert.Throws<ArgumentException>(() => new TimeframeAggregator(Btc, Timeframe.M5, Timeframe.M3));
        Assert.Throws<ArgumentException>(() => new TimeframeAggregator(Btc, Timeframe.M5, Timeframe.M5));
    }
}

public class TimeframeAggregatorFormingTests
{
    private static readonly Symbol Btc = new("BTC-USD");
    private static Candle M1(int minute, decimal o, decimal h, decimal l, decimal c, decimal v = 1m) =>
        T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(minute), o, h, l, c, v);

    [Fact]
    public void FormingWith_folds_the_partial_base_bar_into_the_current_bucket()
    {
        var agg = new TimeframeAggregator(Btc, Timeframe.M1, Timeframe.M5);
        var closed = new List<Candle>();
        agg.OnBaseCandleClosed(M1(0, 10, 12, 9, 11), closed);
        agg.OnBaseCandleClosed(M1(1, 11, 13, 10, 12), closed);

        var f = agg.FormingWith(M1(2, 12, 20, 5, 15, 3))!.Value;
        Assert.Equal(T.Base, f.OpenTime);
        Assert.Equal(10m, f.Open);
        Assert.Equal(20m, f.High);
        Assert.Equal(5m, f.Low);
        Assert.Equal(15m, f.Close);
        Assert.Equal(5m, f.Volume);
        Assert.Equal(9, f.TradeCount);
        Assert.Equal(13m, agg.Forming()!.Value.High); // aggregator state itself untouched
    }

    [Fact]
    public void FormingWith_starts_a_new_bucket_from_the_base_bar_when_nothing_is_forming()
    {
        var agg = new TimeframeAggregator(Btc, Timeframe.M1, Timeframe.M5);
        Assert.Null(agg.FormingWith(null));
        var f = agg.FormingWith(M1(7, 1, 2, 0.5m, 1.5m))!.Value;
        Assert.Equal(Timeframe.M5, f.Timeframe);
        Assert.Equal(T.Base.AddMinutes(5), f.OpenTime);
        Assert.Equal(2m, f.High);
    }
}
