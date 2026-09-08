using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Kraken;
using TradingScanner.MarketData.Metrics;
using TradingScanner.Tests.Support;

namespace TradingScanner.Tests.Kraken;

public class KrakenProviderTests
{
    private static readonly Symbol[] Symbols = [new("BTC-USD")];

    [Fact]
    public async Task Trades_tickers_and_subscription_snapshot_normalize_without_counting_old_volume()
    {
        var socket = new ScriptedWebSocket([
            new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ack("ticker")),
            new SendStep(KrakenTestSupport.Trade(100, "snapshot")), new SendStep(KrakenTestSupport.Ticker),
            new SendStep(KrakenTestSupport.Trade(101, side: "sell")),
        ]);
        var provider = KrakenTestSupport.Provider(new ScriptedSocketFactory(socket));
        var channel = Channel.CreateUnbounded<MarketEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        try
        {
            var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
            Assert.Equal(FeedStatus.Connected, provider.Status);
            Assert.Single(events, e => e.Kind == MarketEventKind.QuoteOnly);
            var trade = Assert.Single(events, e => e.Kind == MarketEventKind.Trade).Trade;
            Assert.Equal(TradeSide.Sell, trade.TakerSide);
            Assert.Equal("kraken", trade.Provider);
            Assert.Equal("Kraken", trade.Exchange);
            Assert.Equal(0.25m, trade.Size);
            var ticker = Assert.Single(events, e => e.Kind == MarketEventKind.Ticker).Ticker;
            Assert.Equal(49990m, ticker.BestBid);
            Assert.Equal(50010m, ticker.BestAsk);
            Assert.Equal(49000m, ticker.Open24h);
            Assert.Equal(10m, ticker.Volume24h);
            Assert.Equal(2, socket.Sent.Count);
            using var tradeRequest = JsonDocument.Parse(socket.Sent[0]);
            Assert.False(tradeRequest.RootElement.GetProperty("params").GetProperty("snapshot").GetBoolean());
            Assert.Contains("bbo", socket.Sent[1]);
            Assert.DoesNotContain("BTC-USD", socket.Sent[0]);
        }
        finally { cts.Cancel(); await run; }
        Assert.Equal(FeedStatus.Disconnected, provider.Status);
    }

    [Fact]
    public async Task Reconnect_preserves_watermark_drops_replay_and_detects_missing_trades()
    {
        var first = new ScriptedWebSocket([
            new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ack("ticker")),
            new SendStep(KrakenTestSupport.Trade(100)), new CloseStep(),
        ]);
        var second = new ScriptedWebSocket([
            new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ack("ticker")),
            new SendStep(KrakenTestSupport.Trade(103, "snapshot")), // must not erase the gap since #100
            new SendStep(KrakenTestSupport.Trade(100)), new SendStep(KrakenTestSupport.Trade(104)),
        ]);
        var metrics = new MarketDataMetrics();
        var provider = KrakenTestSupport.Provider(new ScriptedSocketFactory(first, second), metrics: metrics);
        var channel = Channel.CreateUnbounded<MarketEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        try
        {
            var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade && x.Trade.TradeId == 104));
            Assert.Equal([100L, 104L], events.Where(e => e.Kind == MarketEventKind.Trade).Select(e => e.Trade.TradeId));
            var gap = Assert.Single(events, e => e.Kind == MarketEventKind.Gap).Gap!;
            Assert.Equal(101, gap.ExpectedTradeId);
            Assert.Equal(104, gap.ReceivedTradeId);
            Assert.Equal(1, metrics.Snapshot().Duplicates);
            Assert.Equal(1, metrics.Snapshot().WsReconnects);
            Assert.Equal(FeedStatus.Degraded, provider.Status);
            Assert.Equal(first.Sent, second.Sent);
            Assert.True(first.Aborted);
        }
        finally { cts.Cancel(); await run; }
    }

    [Fact]
    public async Task Partial_subscription_is_never_reported_connected()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new ScriptedWebSocket([
            new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ticker),
            new GateStep(release), new SendStep(KrakenTestSupport.Ack("ticker")),
        ]);
        var provider = KrakenTestSupport.Provider(new ScriptedSocketFactory(socket));
        var channel = Channel.CreateUnbounded<MarketEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        try
        {
            var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Ticker));
            Assert.Equal(FeedStatus.Connecting, provider.Status);
            Assert.DoesNotContain(events, e => e.Status?.Status == FeedStatus.Connected);
            release.SetResult();
            await T.CollectAsync(channel.Reader, e => e.Any(x => x.Status?.Status == FeedStatus.Connected));
            Assert.Equal(FeedStatus.Connected, provider.Status);
        }
        finally { cts.Cancel(); await run; }
    }

    [Theory]
    [InlineData("{\"method\":\"subscribe\",\"success\":false,\"error\":\"Currency pair not supported\"}")]
    [InlineData("{\"channel\":\"status\",\"data\":[{\"system\":\"maintenance\"}]}")]
    [InlineData("{broken json")]
    public async Task Rejected_subscriptions_maintenance_and_bad_data_reconnect(string bad)
    {
        var first = new ScriptedWebSocket([new SendStep(bad)]);
        var second = new ScriptedWebSocket([new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ack("ticker")), new SendStep(KrakenTestSupport.Trade(1))]);
        var metrics = new MarketDataMetrics();
        var provider = KrakenTestSupport.Provider(new ScriptedSocketFactory(first, second), metrics: metrics);
        var channel = Channel.CreateUnbounded<MarketEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        try
        {
            await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
            Assert.Equal(1, metrics.Snapshot().WsErrors);
            Assert.Equal(1, metrics.Snapshot().WsReconnects);
        }
        finally { cts.Cancel(); await run; }
    }

    [Fact]
    public async Task Silent_socket_times_out_and_resubscribes()
    {
        var first = new ScriptedWebSocket([]);
        var second = new ScriptedWebSocket([new SendStep(KrakenTestSupport.Ack("trade")), new SendStep(KrakenTestSupport.Ack("ticker")), new SendStep(KrakenTestSupport.Trade(1))]);
        var options = T.FastOptions();
        options.ReceiveTimeout = TimeSpan.FromMilliseconds(100);
        var factory = new ScriptedSocketFactory(first, second);
        var provider = KrakenTestSupport.Provider(factory, options);
        var channel = Channel.CreateUnbounded<MarketEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        try
        {
            await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
            Assert.Equal(2, factory.Created.Count);
        }
        finally { cts.Cancel(); await run; }
    }

    [Fact]
    public void Parser_preserves_Dogecoin_taker_side_and_rejects_invalid_prices()
    {
        var message = KrakenMessageParser.Parse(Encoding.UTF8.GetBytes(KrakenTestSupport.Trade(1, symbol: "DOGE/USD", side: "buy")), T.Base);
        Assert.Equal("DOGE-USD", Assert.Single(message.Trades).Symbol.Value);
        Assert.Equal(TradeSide.Buy, message.Trades[0].TakerSide);
        Assert.Throws<FormatException>(() => KrakenMessageParser.Parse(Encoding.UTF8.GetBytes(KrakenTestSupport.Trade(1).Replace("50000", "0")), T.Base));
    }
}
