using System.Text;
using System.Text.Json;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Coinbase;
using TradingScanner.Tests.Fixtures;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Coinbase;

public class CoinbaseMessageParserTests
{
    private static CoinbaseMessage Parse(string json)
    {
        Assert.True(CoinbaseMessageParser.TryParse(Encoding.UTF8.GetBytes(json), out var m, out var error), error);
        return m;
    }

    [Fact]
    public void Parses_match_and_flips_maker_side_to_taker_side()
    {
        var m = Parse(CoinbaseFixtures.Match);
        Assert.Equal(CoinbaseMessageType.Match, m.Type);
        Assert.Equal("BTC-USD", m.ProductId);
        Assert.Equal(10, m.TradeId);
        Assert.Equal(50, m.Sequence);
        Assert.Equal(400.23m, m.Price);
        Assert.Equal(5.23512m, m.Size);
        Assert.Equal(TradeSide.Buy, m.TakerSide); // maker sold => taker bought (up-tick)
        Assert.Equal(T.At("2014-11-07T08:19:27.028459Z"), m.Time);
        Assert.Equal(TimeSpan.Zero, m.Time.Offset);
    }

    [Fact]
    public void Maker_buy_means_taker_sell()
    {
        var m = Parse(CoinbaseFixtures.MatchMakerBuy);
        Assert.Equal(TradeSide.Sell, m.TakerSide);
        Assert.Equal(T.At("2014-11-07T08:19:27.5Z"), m.Time);
    }

    [Fact]
    public void Parses_ticker_with_bid_ask_and_24h_stats()
    {
        var m = Parse(CoinbaseFixtures.Ticker);
        Assert.Equal(CoinbaseMessageType.Ticker, m.Type);
        Assert.Equal("ETH-USD", m.ProductId);
        Assert.Equal(1285.22m, m.Price);
        Assert.Equal(1285.04m, m.BestBid);
        Assert.Equal(1285.27m, m.BestAsk);
        Assert.Equal(1310.79m, m.Open24h);
        Assert.Equal(1313.8m, m.High24h);
        Assert.Equal(1280.52m, m.Low24h);
        Assert.Equal(245532.79269678m, m.Volume24h);
        Assert.Equal(11.4396987m, m.Size);
        Assert.Equal(370843401, m.TradeId);
        Assert.Equal(T.At("2022-10-19T23:28:22.061769Z"), m.Time);
    }

    [Fact]
    public void Parses_heartbeat_last_match_subscriptions_and_error()
    {
        var hb = Parse(CoinbaseFixtures.Heartbeat);
        Assert.Equal(CoinbaseMessageType.Heartbeat, hb.Type);
        Assert.Equal(20, hb.LastTradeId);
        Assert.Equal("BTC-USD", hb.ProductId);

        var lm = Parse(CoinbaseFixtures.LastMatch);
        Assert.Equal(CoinbaseMessageType.LastMatch, lm.Type);
        Assert.Equal(9, lm.TradeId);
        Assert.Equal(T.At("2014-11-07T08:19:26Z"), lm.Time);

        var subs = Parse(CoinbaseFixtures.Subscriptions);
        Assert.Equal(CoinbaseMessageType.Subscriptions, subs.Type);
        Assert.Equal(2, subs.SubscribedProducts);

        var err = Parse(CoinbaseFixtures.Error);
        Assert.Equal(CoinbaseMessageType.Error, err.Type);
        Assert.Equal("Failed to subscribe", err.ErrorMessage);
        Assert.Equal("BTC-USDX is not a valid product", err.ErrorReason);
    }

    [Fact]
    public void Unknown_types_parse_as_unknown_not_as_errors()
    {
        var m = Parse(CoinbaseFixtures.Status);
        Assert.Equal(CoinbaseMessageType.Unknown, m.Type);
    }

    [Fact]
    public void Property_order_does_not_matter()
    {
        var reordered = """{"side":"buy","price":"1.5","product_id":"XRP-USD","size":"2","time":"2024-01-01T00:00:00.1Z","trade_id":5,"type":"match","sequence":1}""";
        var m = Parse(reordered);
        Assert.Equal(CoinbaseMessageType.Match, m.Type);
        Assert.Equal(1.5m, m.Price);
        Assert.Equal(TradeSide.Sell, m.TakerSide);
        Assert.Equal(T.At("2024-01-01T00:00:00.1Z"), m.Time);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"match","trade_id":1,"product_id":"BTC-USD","size":"1","price":"abc","side":"buy","time":"2024-01-01T00:00:00Z"}""")]
    [InlineData("""{"type":"match","trade_id":1,"size":"1","price":"1","side":"buy","time":"2024-01-01T00:00:00Z"}""")]
    [InlineData("""{"type":"match","trade_id":1,"product_id":"BTC-USD","size":"0","price":"1","side":"buy","time":"2024-01-01T00:00:00Z"}""")]
    [InlineData("""{"type":"ticker","product_id":"BTC-USD","price":"1"}""")]
    public void Malformed_or_incomplete_messages_are_rejected(string json)
    {
        Assert.False(CoinbaseMessageParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("2014-11-07T08:19:27.028459Z")]
    [InlineData("2014-11-07T08:19:27Z")]
    [InlineData("2014-11-07T08:19:27.1Z")]
    [InlineData("2014-11-07T08:19:27.1234567Z")]
    [InlineData("2014-11-07T08:19:27.028459+00:00")]
    public void Timestamps_with_any_fractional_precision_parse_to_utc(string ts)
    {
        var m = Parse($$"""{"type":"heartbeat","sequence":1,"last_trade_id":1,"product_id":"BTC-USD","time":"{{ts}}"}""");
        Assert.Equal(T.At(ts), m.Time);
        Assert.Equal(TimeSpan.Zero, m.Time.Offset);
    }

    [Fact]
    public void BuildSubscribe_emits_the_documented_request_shape()
    {
        var bytes = CoinbaseMessageParser.BuildSubscribe(["BTC-USD", "ETH-USD"], "matches", "ticker", "heartbeat");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal("subscribe", root.GetProperty("type").GetString());
        Assert.Equal(["BTC-USD", "ETH-USD"], root.GetProperty("product_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["matches", "ticker", "heartbeat"], root.GetProperty("channels").EnumerateArray().Select(e => e.GetString()));
    }
}
