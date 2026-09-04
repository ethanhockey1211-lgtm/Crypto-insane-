using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Engine;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Engine;

public class SymbolStateTests
{
    private static SymbolState New() => new(new Symbol("BTC-USD"), new MarketDataOptions());

    [Fact]
    public void Trades_update_quote_and_build_all_timeframes()
    {
        var s = New();
        var closed = new List<Candle>();
        var id = 1L;
        for (var minute = 0; minute < 6; minute++)
        {
            s.OnTrade(T.Trade("BTC-USD", 100 + minute, 1m, T.Base.AddMinutes(minute).AddSeconds(10), id: id++), closed);
            s.OnTrade(T.Trade("BTC-USD", 100.5m + minute, 1m, T.Base.AddMinutes(minute).AddSeconds(40), id: id++), closed);
        }

        Assert.Equal(105.5m, s.Quote!.Price);
        Assert.Equal(12, s.TradesSeen);
        Assert.Equal(5, s.Series(Timeframe.M1).Count);
        Assert.Equal(1, s.Series(Timeframe.M3).Count);
        Assert.Equal(1, s.Series(Timeframe.M5).Count);
        Assert.Equal(0, s.Series(Timeframe.M15).Count);

        var m5 = s.Series(Timeframe.M5).Last!.Value;
        Assert.Equal(100m, m5.Open);
        Assert.Equal(104.5m, m5.Close);
        Assert.Equal(104.5m, m5.High);
        Assert.Equal(10m, m5.Volume);
        Assert.Equal(T.Base.AddMinutes(5), s.Series(Timeframe.M1).Forming!.Value.OpenTime);
        Assert.Equal(T.Base.AddMinutes(5), s.Series(Timeframe.M5).Forming!.Value.OpenTime);
        Assert.Equal(T.Base, s.Series(Timeframe.M15).Forming!.Value.OpenTime);
        Assert.Equal(12, s.Series(Timeframe.M15).Forming!.Value.TradeCount);

        Assert.Equal(5 + 1 + 1, closed.Count);
        Assert.Contains(closed, c => c.Timeframe == Timeframe.M3);
    }

    [Fact]
    public void Ticker_updates_bid_ask_and_stats_without_regressing_a_newer_trade_price()
    {
        var s = New();
        var closed = new List<Candle>();
        s.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(10)), closed);
        var older = new TickerUpdate(new Symbol("BTC-USD"), "test", "Test Exchange", 99m, 99.5m, 100.5m, 90m, 110m, 80m, 1234m, T.Base.AddSeconds(5), T.Base.AddSeconds(5));
        s.OnTicker(older);

        Assert.Equal(100m, s.Quote!.Price);
        Assert.Equal(T.Base.AddSeconds(10), s.Quote.ExchangeTime);
        Assert.Equal(99.5m, s.Quote.Bid);
        Assert.Equal(100.5m, s.Quote.Ask);
        Assert.Equal(90m, s.Stats!.Open24h);
        Assert.Equal(1234m, s.Stats.Volume24hBase);

        var newer = older with { LastPrice = 101m, ExchangeTime = T.Base.AddSeconds(20), ReceivedAt = T.Base.AddSeconds(20) };
        s.OnTicker(newer);
        Assert.Equal(101m, s.Quote!.Price);

        // Subsequent trade keeps the bid/ask from the ticker.
        s.OnTrade(T.Trade("BTC-USD", 102m, 1m, T.Base.AddSeconds(30)), closed);
        Assert.Equal(102m, s.Quote!.Price);
        Assert.Equal(99.5m, s.Quote.Bid);
    }

    [Fact]
    public void Clock_closes_quiet_bars_and_higher_timeframes_follow()
    {
        var s = New();
        var closed = new List<Candle>();
        s.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(10)), closed);
        s.OnClock(T.Base.AddMinutes(5).AddSeconds(2), closed);

        Assert.Equal(5, s.Series(Timeframe.M1).Count);
        Assert.Equal(4, closed.Count(c => c.Timeframe == Timeframe.M1 && c.Source == CandleSource.Synthetic));
        Assert.Equal(1, s.Series(Timeframe.M5).Count);
        Assert.Equal(1m, s.Series(Timeframe.M5).Last!.Value.Volume);
        Assert.Equal(CandleSource.Live, s.Series(Timeframe.M5).Last!.Value.Source);
    }

    [Fact]
    public void History_merge_prefers_history_up_to_the_first_live_bar_and_live_afterwards()
    {
        var s = New();
        var closed = new List<Candle>();
        // Live bars: minute 0 started mid-bucket (partial), minutes 1 and 2 full, minute 3 forming.
        s.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(50)), closed);
        s.OnTrade(T.Trade("BTC-USD", 101m, 1m, T.Base.AddMinutes(1).AddSeconds(5)), closed);
        s.OnTrade(T.Trade("BTC-USD", 102m, 1m, T.Base.AddMinutes(2).AddSeconds(5)), closed);
        s.OnTrade(T.Trade("BTC-USD", 103m, 1m, T.Base.AddMinutes(3).AddSeconds(5)), closed);

        // History fetched slightly later covers minutes -3 .. 1 (and erroneously the forming minute 3, which must be ignored).
        var history = new List<Candle>();
        for (var m = -3; m <= 1; m++) history.Add(T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(m), 50, 60, 40, 55, 7, CandleSource.Historical));
        history.Add(T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(3), 1, 1, 1, 1, 1, CandleSource.Historical));

        s.ApplyHistory(Timeframe.M1, history);

        var snap = s.Series(Timeframe.M1).Snapshot();
        Assert.Equal(6, snap.Closed.Length); // -3,-2,-1,0,1,2
        Assert.Equal(T.Base.AddMinutes(-3), snap.Closed[0].OpenTime);
        Assert.Equal(CandleSource.Historical, snap.Closed[3].Source); // minute 0: partial live bar replaced by history
        Assert.Equal(7m, snap.Closed[3].Volume);
        Assert.Equal(CandleSource.Live, snap.Closed[4].Source);       // minute 1: live wins over history
        Assert.Equal(101m, snap.Closed[4].Close);
        Assert.Equal(CandleSource.Live, snap.Closed[5].Source);
        Assert.Equal(T.Base.AddMinutes(3), snap.Forming!.Value.OpenTime);
        Assert.True(s.HistoryLoaded);

        // Live continues seamlessly after the merge.
        s.OnTrade(T.Trade("BTC-USD", 104m, 1m, T.Base.AddMinutes(4).AddSeconds(5)), closed);
        Assert.Equal(7, s.Series(Timeframe.M1).Count);
        Assert.Equal(103m, s.Series(Timeframe.M1).Last!.Value.Close);
    }

    [Fact]
    public void History_seeds_continuity_when_no_live_bars_exist_yet()
    {
        var s = New();
        var closed = new List<Candle>();
        var history = Enumerable.Range(-10, 10).Select(m => T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(m), 50, 60, 40, 55, 7, CandleSource.Historical)).ToList();
        s.ApplyHistory(Timeframe.M1, history);

        s.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddMinutes(2).AddSeconds(5)), closed);
        var snap = s.Series(Timeframe.M1).Snapshot();
        Assert.Equal(12, snap.Closed.Length); // 10 history + 2 synthetic (minutes 0 and 1)
        Assert.Equal(CandleSource.Synthetic, snap.Closed[^1].Source);
        Assert.Equal(55m, snap.Closed[^1].Close);
    }

    [Fact]
    public void RebuildDerived_creates_3m_30m_4h_from_history_and_primes_forming_bars()
    {
        var s = New();
        var closed = new List<Candle>();
        var m1 = Enumerable.Range(-30, 30).Select(m => T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(m), 50 + m, 60, 40, 55 + m, 1, CandleSource.Historical)).ToList();
        var m15 = new[] { -60, -45, -30, -15 }.Select(m => T.Candle("BTC-USD", Timeframe.M15, T.Base.AddMinutes(m), 1, 2, 0.5m, 1.5m, 15, CandleSource.Historical)).ToList();
        var h1 = Enumerable.Range(-8, 8).Select(h => T.Candle("BTC-USD", Timeframe.H1, T.Base.AddHours(h), 1, 2, 0.5m, 1.5m, 60, CandleSource.Historical)).ToList();

        // A couple of live minutes already built before history arrives.
        s.OnTrade(T.Trade("BTC-USD", 100m, 1m, T.Base.AddSeconds(5)), closed);
        s.OnTrade(T.Trade("BTC-USD", 101m, 1m, T.Base.AddMinutes(1).AddSeconds(5)), closed);

        s.ApplyHistory(Timeframe.M1, m1);
        s.ApplyHistory(Timeframe.M15, m15);
        s.ApplyHistory(Timeframe.H1, h1);
        closed.Clear();
        s.RebuildDerived(closed);

        Assert.Equal(10, s.Series(Timeframe.M3).Count);                 // 30 historical 1m bars -> 10 × 3m
        Assert.Equal(2, s.Series(Timeframe.M30).Count);                 // 4 × 15m -> 2 × 30m
        Assert.Equal(2, s.Series(Timeframe.H4).Count);                  // 8 × 1h starting at 04:00 -> 04:00 and 08:00 buckets
        Assert.Equal(T.Base.AddHours(-8), s.Series(Timeframe.H4).First().OpenTime);
        Assert.All(closed, c => Assert.NotEqual(Timeframe.M1, c.Timeframe));

        // Forming bars are primed from the merged 1m series: the 5m bucket at 12:00 contains the live minute 0.
        var forming5 = s.Series(Timeframe.M5).Forming!.Value;
        Assert.Equal(T.Base, forming5.OpenTime);
        Assert.Equal(100m, forming5.Open);
        Assert.Equal(2m, forming5.Volume); // closed live minute 0 + the forming minute 1
        // 15m forming at 12:00 too; 1h forming at 12:00; 4h forming at 12:00.
        Assert.Equal(T.Base, s.Series(Timeframe.H4).Forming!.Value.OpenTime);
        // The M3 forming bar covers 12:00-12:03 and includes live minute 0.
        Assert.Equal(T.Base, s.Series(Timeframe.M3).Forming!.Value.OpenTime);
    }

    [Fact]
    public void Synthetic_fill_after_history_does_not_reclose_buckets_the_higher_timeframe_history_already_holds()
    {
        // Production failure (VVV-USD, WIF-USD, SPX-USD): 5m history is complete through 12:05 but 1m history ends at
        // 12:06 (empty minutes omitted, newest minutes not yet published). No trade arrives; the clock fills 1m bars
        // from 12:07 on. The 5m aggregator must not rebuild and re-close the 12:05 bucket.
        var s = New();
        var closed = new List<Candle>();
        var m1 = Enumerable.Range(0, 7).Select(i => T.Candle("BTC-USD", Timeframe.M1, T.Base.AddMinutes(i), 100, 101, 99, 100, source: CandleSource.Historical)).ToList();
        var m5 = Enumerable.Range(0, 2).Select(i => T.Candle("BTC-USD", Timeframe.M5, T.Base.AddMinutes(5 * i), 100, 102, 98, 100, source: CandleSource.Historical)).ToList();
        s.ApplyHistory(Timeframe.M1, m1);
        s.ApplyHistory(Timeframe.M5, m5);
        s.RebuildDerived(closed);
        Assert.Equal(T.Base.AddMinutes(5), s.Series(Timeframe.M5).Last!.Value.OpenTime);

        closed.Clear();
        s.OnClock(T.Base.AddMinutes(12).AddSeconds(3), closed); // fills synthetic 12:07 .. 12:11

        Assert.Equal(5, closed.Count(c => c.Timeframe == Timeframe.M1 && c.IsSynthetic));
        Assert.DoesNotContain(closed, c => c.Timeframe == Timeframe.M5);
        Assert.Equal(2, s.Series(Timeframe.M5).Count);
        Assert.Equal(T.Base.AddMinutes(10), s.Series(Timeframe.M5).Forming!.Value.OpenTime);
        Assert.Equal(2, s.Series(Timeframe.M5).Forming!.Value.TradeCount + 2); // synthetic bars carry no trades

        // The 1m fill that started inside a bucket older than the newest 5m bar (WIF-USD variant) is ignored too.
        var w = New();
        w.ApplyHistory(Timeframe.M1, m1.Take(3).ToList());          // 1m ends at 12:02
        w.ApplyHistory(Timeframe.M5, m5);                           // 5m holds 12:00 and 12:05
        w.RebuildDerived(closed);
        closed.Clear();
        w.OnClock(T.Base.AddMinutes(11).AddSeconds(3), closed);     // fills 12:03 .. 12:10
        Assert.DoesNotContain(closed, c => c.Timeframe == Timeframe.M5);
        Assert.Equal(T.Base.AddMinutes(10), w.Series(Timeframe.M5).Forming!.Value.OpenTime);
        Assert.Equal(2, w.Series(Timeframe.M5).Count);
    }
}

file static class SeriesExt
{
    public static Candle First(this CandleSeries s) => s[0];
}
