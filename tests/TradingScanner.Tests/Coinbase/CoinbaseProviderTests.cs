using System.Net.WebSockets;
using System.Threading.Channels;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Metrics;
using TradingScanner.Tests.Fixtures;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Coinbase;

/// <summary>
/// Drives the provider against scripted sockets that speak the documented Coinbase protocol.
/// The sandbox has no exchange connectivity, so this is the contract test for connection handling.
/// </summary>
public class CoinbaseProviderTests
{
    private static readonly Symbol[] Symbols = [new("BTC-USD"), new("ETH-USD")];

    private static (Channel<MarketEvent> channel, CancellationTokenSource cts) Harness()
        => (Channel.CreateUnbounded<MarketEvent>(), new CancellationTokenSource(TimeSpan.FromSeconds(10)));

    [Fact]
    public async Task Subscribes_then_normalizes_matches_tickers_and_last_match()
    {
        var socket = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.LastMatch),
            new SendStep(CoinbaseFixtures.Match),
            new SendStep(CoinbaseFixtures.Heartbeat),
            new SendStep(CoinbaseFixtures.Ticker),
            new SendStep(CoinbaseFixtures.MatchMakerBuy),
        ]);
        var factory = new ScriptedSocketFactory(socket);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, metrics: metrics);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Count(x => x.Kind == MarketEventKind.Trade) == 2);

        var sent = Assert.Single(socket.Sent);
        Assert.Contains("\"type\":\"subscribe\"", sent);
        Assert.Contains("\"BTC-USD\"", sent);
        Assert.Contains("\"matches\"", sent);

        Assert.Equal([FeedStatus.Connecting, FeedStatus.Connected], events.Where(e => e.Kind == MarketEventKind.Status).Select(e => e.Status!.Status));
        Assert.Equal(FeedStatus.Connected, provider.Status);

        var quoteOnly = Assert.Single(events, e => e.Kind == MarketEventKind.QuoteOnly);
        Assert.Equal(400.00m, quoteOnly.Trade.Price);

        var trades = events.Where(e => e.Kind == MarketEventKind.Trade).Select(e => e.Trade).ToList();
        Assert.Equal(10, trades[0].TradeId);
        Assert.Equal(400.23m, trades[0].Price);
        Assert.Equal(TradeSide.Buy, trades[0].TakerSide);
        Assert.Equal("coinbase", trades[0].Provider);
        Assert.Equal("Coinbase Exchange", trades[0].Exchange);
        Assert.Equal(11, trades[1].TradeId);
        Assert.Equal(TradeSide.Sell, trades[1].TakerSide);

        var ticker = Assert.Single(events, e => e.Kind == MarketEventKind.Ticker).Ticker;
        Assert.Equal("ETH-USD", ticker.Symbol.Value);
        Assert.Equal(1285.04m, ticker.BestBid);
        Assert.Equal(1285.27m, ticker.BestAsk);

        Assert.DoesNotContain(events, e => e.Kind == MarketEventKind.Gap);
        Assert.Equal(6, metrics.Snapshot().WsMessages);
        Assert.Equal(2, metrics.Snapshot().Trades);

        cts.Cancel();
        await run;
        Assert.Equal(FeedStatus.Disconnected, provider.Status);
    }

    [Fact]
    public async Task Reconnects_with_backoff_and_resubscribes_after_remote_close()
    {
        var first = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 100, "2024-03-01T12:00:00.5Z", "50000", "0.1")),
            new CloseStep(),
        ]);
        var second = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 101, "2024-03-01T12:00:03Z", "50010", "0.2")),
        ]);
        var factory = new ScriptedSocketFactory(first, second);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, metrics: metrics);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade && x.Trade.TradeId == 101));

        var statuses = events.Where(e => e.Kind == MarketEventKind.Status).Select(e => e.Status!.Status).ToList();
        Assert.Equal([FeedStatus.Connecting, FeedStatus.Connected, FeedStatus.Reconnecting, FeedStatus.Connecting, FeedStatus.Connected], statuses);
        Assert.Single(first.Sent);
        Assert.Single(second.Sent);
        Assert.Equal(first.Sent[0], second.Sent[0]);
        Assert.True(first.Aborted);
        Assert.Equal(1, metrics.Snapshot().WsReconnects);
        // trade ids 100 -> 101 are contiguous across the reconnect: no gap.
        Assert.DoesNotContain(events, e => e.Kind == MarketEventKind.Gap);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Reconnects_when_the_socket_throws_or_connect_fails()
    {
        var failing = new ScriptedWebSocket([]) { ConnectException = new WebSocketException("dns failure") };
        var throwing = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new ThrowStep(new WebSocketException("connection reset")),
        ]);
        var healthy = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.Match),
        ]);
        var factory = new ScriptedSocketFactory(failing, throwing, healthy);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, metrics: metrics);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));

        Assert.Equal(3, factory.Created.Count);
        Assert.Equal(2, metrics.Snapshot().WsReconnects);
        Assert.Equal(2, metrics.Snapshot().WsErrors);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Silence_beyond_receive_timeout_forces_a_reconnect()
    {
        var silent = new ScriptedWebSocket([new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD"))]);
        var healthy = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.Match),
        ]);
        var factory = new ScriptedSocketFactory(silent, healthy);
        var options = T.FastOptions();
        options.ReceiveTimeout = TimeSpan.FromMilliseconds(150);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, options, metrics);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));

        Assert.Contains(events, e => e.Kind == MarketEventKind.Status && e.Status!.Status == FeedStatus.Reconnecting);
        Assert.Equal(1, metrics.Snapshot().WsReconnects);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Feed_error_messages_trigger_a_reconnect()
    {
        var bad = new ScriptedWebSocket([new SendStep(CoinbaseFixtures.Error)]);
        var good = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.Match),
        ]);
        var factory = new ScriptedSocketFactory(bad, good);
        var provider = T.Provider(factory);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
        Assert.Equal(2, factory.Created.Count);
        Assert.Contains(events, e => e.Kind == MarketEventKind.Status && e.Status!.Status == FeedStatus.Reconnecting);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Detects_missed_trades_via_trade_id_continuity_and_drops_duplicates()
    {
        var socket = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 10, "2024-03-01T12:00:00Z", "1", "1")),
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 11, "2024-03-01T12:00:01Z", "1", "1")),
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 11, "2024-03-01T12:00:01Z", "1", "1")), // duplicate
            new SendStep(CoinbaseFixtures.MatchAt("ETH-USD", 500, "2024-03-01T12:00:01Z", "1", "1")), // other product, own sequence
            new SendStep(CoinbaseFixtures.MatchAt("BTC-USD", 15, "2024-03-01T12:00:02Z", "1", "1")), // 12,13,14 missed
            new SendStep(CoinbaseFixtures.MatchAt("ETH-USD", 501, "2024-03-01T12:00:02Z", "1", "1")),
        ]);
        var factory = new ScriptedSocketFactory(socket);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, metrics: metrics);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade && x.Trade.TradeId == 501));

        var gap = Assert.Single(events, e => e.Kind == MarketEventKind.Gap).Gap!;
        Assert.Equal("BTC-USD", gap.Symbol.Value);
        Assert.Equal(12, gap.ExpectedTradeId);
        Assert.Equal(15, gap.ReceivedTradeId);
        Assert.Equal(3, gap.MissedTrades);
        Assert.Equal([10L, 11L, 500L, 15L, 501L], events.Where(e => e.Kind == MarketEventKind.Trade).Select(e => e.Trade.TradeId));
        Assert.Equal(1, metrics.Snapshot().Duplicates);
        Assert.Equal(1, metrics.Snapshot().Gaps);
        Assert.Equal(FeedStatus.Degraded, provider.Status);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Last_match_seeds_continuity_so_the_first_live_match_is_not_a_gap()
    {
        var socket = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep(CoinbaseFixtures.LastMatch), // trade_id 9
            new SendStep(CoinbaseFixtures.Match),     // trade_id 10
        ]);
        var provider = T.Provider(new ScriptedSocketFactory(socket));
        var (channel, cts) = Harness();
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
        Assert.DoesNotContain(events, e => e.Kind == MarketEventKind.Gap);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Malformed_messages_are_counted_and_skipped_without_dropping_the_connection()
    {
        var socket = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD", "ETH-USD")),
            new SendStep("{not json"),
            new SendStep(CoinbaseFixtures.Match),
        ]);
        var factory = new ScriptedSocketFactory(socket);
        var metrics = new MarketDataMetrics();
        var provider = T.Provider(factory, metrics: metrics);
        var (channel, cts) = Harness();
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
        Assert.Equal(1, metrics.Snapshot().ParseErrors);
        Assert.Single(factory.Created);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Symbols_are_sharded_across_connections_and_status_aggregates()
    {
        var options = T.FastOptions();
        options.SymbolsPerConnection = 1;
        var a = new ScriptedWebSocket([new SendStep(CoinbaseFixtures.SubscriptionsFor("BTC-USD"))]);
        var b = new ScriptedWebSocket([new SendStep(CoinbaseFixtures.SubscriptionsFor("ETH-USD"))]);
        var factory = new ScriptedSocketFactory(a, b);
        var provider = T.Provider(factory, options);
        var (channel, cts) = Harness();

        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        await T.CollectAsync(channel.Reader, e => e.Count(x => x.Kind == MarketEventKind.Status && x.Status!.Status == FeedStatus.Connected) == 2);

        Assert.Equal(2, factory.Created.Count);
        Assert.Contains("\"BTC-USD\"", a.Sent[0]);
        Assert.DoesNotContain("\"ETH-USD\"", a.Sent[0]);
        Assert.Contains("\"ETH-USD\"", b.Sent[0]);
        Assert.Equal(FeedStatus.Connected, provider.Status);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Large_messages_split_across_frames_are_reassembled()
    {
        // 64KB receive buffer: a subscriptions confirmation with many products exceeds it.
        var products = Enumerable.Range(0, 6000).Select(i => $"P{i:D5}-USD").ToArray();
        var socket = new ScriptedWebSocket([
            new SendStep(CoinbaseFixtures.SubscriptionsFor(products)),
            new SendStep(CoinbaseFixtures.Match),
        ]);
        var provider = T.Provider(new ScriptedSocketFactory(socket));
        var (channel, cts) = Harness();
        var run = provider.RunAsync(Symbols, channel.Writer, cts.Token);
        var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade));
        Assert.Contains(events, e => e.Kind == MarketEventKind.Status && e.Status!.Status == FeedStatus.Connected);
        cts.Cancel();
        await run;
    }
}
