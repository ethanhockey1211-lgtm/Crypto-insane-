using TradingScanner.Core.Market;
using TradingScanner.MarketData.Candles;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.MarketData;

public class CandleBuilderTests
{
    private static readonly Symbol Btc = new("BTC-USD");
    private static CandleBuilder NewBuilder(int graceSeconds = 2) => new(Btc, Timeframe.M1, TimeSpan.FromSeconds(graceSeconds));

    [Fact]
    public void Accumulates_ohlcv_and_taker_side_volume_within_a_bucket()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(1), TradeSide.Buy), closed);
        b.OnTrade(T.Trade("BTC-USD", 105m, 2m, T.Base.AddSeconds(20), TradeSide.Sell), closed);
        b.OnTrade(T.Trade("BTC-USD", 95m, 0.5m, T.Base.AddSeconds(40), TradeSide.Buy), closed);
        b.OnTrade(T.Trade("BTC-USD", 101m, 1m, T.Base.AddSeconds(59), TradeSide.Buy), closed);

        Assert.Empty(closed);
        var f = b.Forming()!.Value;
        Assert.Equal(T.Base, f.OpenTime);
        Assert.Equal(100m, f.Open);
        Assert.Equal(105m, f.High);
        Assert.Equal(95m, f.Low);
        Assert.Equal(101m, f.Close);
        Assert.Equal(4.5m, f.Volume);
        Assert.Equal(100m * 1 + 105m * 2 + 95m * 0.5m + 101m * 1, f.QuoteVolume);
        Assert.Equal(2.5m, f.BuyVolume);
        Assert.Equal(2m, f.SellVolume);
        Assert.Equal(4, f.TradeCount);
        Assert.Equal(CandleSource.Live, f.Source);
    }

    [Fact]
    public void Trade_in_next_bucket_closes_the_previous_bar()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(30)), closed);
        b.OnTrade(T.Trade("BTC-USD", 102m, 1m, T.Base.AddSeconds(61)), closed);

        var c = Assert.Single(closed);
        Assert.Equal(T.Base, c.OpenTime);
        Assert.Equal(100m, c.Close);
        Assert.Equal(T.Base.AddMinutes(1), b.Forming()!.Value.OpenTime);
        Assert.Equal(102m, b.Forming()!.Value.Open);
    }

    [Fact]
    public void Empty_buckets_between_trades_are_filled_with_synthetic_flat_candles()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(10)), closed);
        b.OnTrade(T.Trade("BTC-USD", 110m, 1m, T.Base.AddMinutes(3).AddSeconds(5)), closed);

        Assert.Equal(3, closed.Count);
        Assert.Equal(CandleSource.Live, closed[0].Source);
        Assert.Equal(CandleSource.Synthetic, closed[1].Source);
        Assert.Equal(CandleSource.Synthetic, closed[2].Source);
        Assert.Equal(T.Base.AddMinutes(1), closed[1].OpenTime);
        Assert.Equal(T.Base.AddMinutes(2), closed[2].OpenTime);
        Assert.All(closed.Skip(1), c => { Assert.Equal(100m, c.Open); Assert.Equal(100m, c.Close); Assert.Equal(0m, c.Volume); Assert.Equal(0, c.TradeCount); });
        Assert.Equal(2, b.SyntheticCandles);
    }

    [Fact]
    public void Late_trades_for_closed_buckets_are_counted_and_ignored()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(10)), closed);
        b.OnTrade(T.Trade("BTC-USD", 101m, 1m, T.Base.AddSeconds(70)), closed);
        b.OnTrade(T.Trade("BTC-USD", 999m, 1m, T.Base.AddSeconds(50)), closed); // belongs to already-closed first minute

        Assert.Single(closed);
        Assert.Equal(1, b.LateTrades);
        Assert.Equal(101m, b.Forming()!.Value.High);
    }

    [Fact]
    public void Clock_closes_a_forming_bar_only_after_bucket_end_plus_grace()
    {
        var b = NewBuilder(graceSeconds: 2);
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(30)), closed);

        b.OnClock(T.Base.AddSeconds(60), closed);
        Assert.Empty(closed);
        b.OnClock(T.Base.AddSeconds(61.9), closed);
        Assert.Empty(closed);
        b.OnClock(T.Base.AddSeconds(62), closed);
        var c = Assert.Single(closed);
        Assert.Equal(T.Base, c.OpenTime);
        Assert.False(b.HasForming);
    }

    [Fact]
    public void Clock_fills_synthetic_bars_for_quiet_minutes_without_trades()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(30)), closed);
        b.OnClock(T.Base.AddMinutes(3).AddSeconds(2), closed);

        // minute 0 closed by clock, minutes 1 and 2 are complete with no trades -> synthetic
        Assert.Equal(3, closed.Count);
        Assert.Equal(CandleSource.Live, closed[0].Source);
        Assert.Equal(T.Base.AddMinutes(1), closed[1].OpenTime);
        Assert.Equal(T.Base.AddMinutes(2), closed[2].OpenTime);
        Assert.All(closed.Skip(1), c => Assert.Equal(CandleSource.Synthetic, c.Source));

        // No further fill until the next bucket fully completes.
        b.OnClock(T.Base.AddMinutes(3).AddSeconds(30), closed);
        Assert.Equal(3, closed.Count);
        b.OnClock(T.Base.AddMinutes(4).AddSeconds(2), closed);
        Assert.Equal(4, closed.Count);
    }

    [Fact]
    public void Clock_does_nothing_without_a_reference_price()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.OnClock(T.Base.AddHours(1), closed);
        Assert.Empty(closed);
    }

    [Fact]
    public void Seed_provides_continuity_from_history()
    {
        var b = NewBuilder();
        var closed = new List<Candle>();
        b.Seed(T.Base.AddMinutes(-3), 90m);
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(5)), closed);

        Assert.Equal(2, closed.Count); // minutes -2 and -1 synthetic at 90
        Assert.All(closed, c => { Assert.Equal(CandleSource.Synthetic, c.Source); Assert.Equal(90m, c.Close); });
        Assert.Equal(T.Base, b.Forming()!.Value.OpenTime);

        // A trade for a bucket at or before the seeded close is late.
        b.OnTrade(T.Trade("BTC-USD", 1m, 1m, T.Base.AddMinutes(-3).AddSeconds(30)), closed);
        Assert.Equal(1, b.LateTrades);
    }

    [Fact]
    public void Synthetic_fill_is_capped_for_very_old_history()
    {
        var b = new CandleBuilder(Btc, Timeframe.M1, TimeSpan.FromSeconds(2), maxSyntheticFill: 5);
        var closed = new List<Candle>();
        b.Seed(T.Base.AddHours(-10), 50m);
        b.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(5)), closed);
        Assert.Equal(5, closed.Count);
        Assert.Equal(T.Base.AddMinutes(-1), closed[^1].OpenTime);
    }
}
