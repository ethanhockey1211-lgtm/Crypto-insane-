using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingScanner.Api.Contracts;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Api;

/// <summary>
/// Boots the real API host with a fake provider (the only swapped component) and verifies REST, health,
/// and SignalR output end to end. This is the seam the architecture promises: nothing else knows about Coinbase.
/// </summary>
public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.Factory>
{
    public sealed class FakeProvider : IMarketDataProvider
    {
        public string Name => "fake";
        public string Exchange => "Fake Exchange";
        public FeedStatus Status { get; private set; }
        public IReadOnlySet<Timeframe> HistoricalTimeframes { get; } = new HashSet<Timeframe>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProductInfo>>(
        [
            new ProductInfo(new Symbol("BTC-USD"), "BTC-USD", "BTC", "USD", true, 0.01m, 0.0001m, 900_000_000m, 50000m),
            new ProductInfo(new Symbol("ETH-USD"), "ETH-USD", "ETH", "USD", true, 0.01m, 0.001m, 300_000_000m, 3000m),
            new ProductInfo(new Symbol("XRP-USD"), "XRP-USD", "XRP", "USD", true, 0.0001m, 1m, 40_000_000m, 0.6m),
            new ProductInfo(new Symbol("USDT-USD"), "USDT-USD", "USDT", "USD", true, 0.0001m, 1m, 999_000_000m, 1m),
        ]);

        public Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Candle>>([]);

        public async Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct)
        {
            Status = FeedStatus.Connected;
            var now = DateTimeOffset.UtcNow;
            await output.WriteAsync(MarketEvent.FromStatus(new FeedStatusChange(Name, 0, FeedStatus.Connected, null, now)), ct);
            var id = 1L;
            foreach (var minute in Enumerable.Range(-3, 3))
            {
                var t = now.AddMinutes(minute);
                await output.WriteAsync(MarketEvent.FromTrade(new Trade(new Symbol("BTC-USD"), Name, Exchange, id++, 50000m + minute, 0.1m, TradeSide.Buy, t, t)), ct);
                await output.WriteAsync(MarketEvent.FromTrade(new Trade(new Symbol("ETH-USD"), Name, Exchange, id++, 3000m + minute, 1m, TradeSide.Sell, t, t)), ct);
            }
            await output.WriteAsync(MarketEvent.FromTicker(new TickerUpdate(new Symbol("BTC-USD"), Name, Exchange, 49997m, 49990m, 50000m, 48000m, 51000m, 47000m, 12345m, now, now)), ct);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { }
            Status = FeedStatus.Disconnected;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeProvider Provider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("MarketData:WarmUpHistory", "false");
            builder.UseSetting("MarketData:UniverseSize", "2");
            builder.UseSetting("MarketData:StaleQuoteThreshold", "00:10:00");
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMarketDataProvider>();
                services.AddSingleton<IMarketDataProvider>(Provider);
            });
        }
    }

    private readonly Factory _factory;
    public ApiIntegrationTests(Factory factory) => _factory = factory;

    private Task<HttpClient> ClientAsync() => ClientAsync(_factory);

    private static async Task<HttpClient> ClientAsync(Factory factory)
    {
        var client = factory.CreateClient();
        await factory.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Give the engine a moment to drain the channel.
        for (var i = 0; i < 100; i++)
        {
            var q = await client.GetFromJsonAsync<SymbolSummaryDto>("/api/market/BTC-USD/quote");
            if (q?.Quote is not null && q.Open24h is not null) break;
            await Task.Delay(20);
        }
        return client;
    }

    [Fact]
    public async Task Symbols_endpoint_lists_the_selected_universe_with_provenance()
    {
        var client = await ClientAsync();
        var symbols = await client.GetFromJsonAsync<List<SymbolSummaryDto>>("/api/market/symbols");
        Assert.NotNull(symbols);
        Assert.Equal(["BTC-USD", "ETH-USD"], symbols.Select(s => s.Symbol)); // USDT excluded, XRP outside top-2
        var btc = symbols[0];
        Assert.Equal(49997m, btc.Quote!.Price);
        Assert.Equal("fake", btc.Quote.Provider);
        Assert.Equal("Fake Exchange", btc.Quote.Exchange);
        Assert.False(btc.Quote.Stale);
        Assert.Equal(49990m, btc.Quote.Bid);
        Assert.Equal(48000m, btc.Open24h);
        Assert.NotNull(btc.Change24hPct);
        Assert.Equal(3, btc.TradesSeen);
    }

    [Fact]
    public async Task Candles_endpoint_returns_closed_and_forming_bars()
    {
        var client = await ClientAsync();
        var resp = await client.GetFromJsonAsync<CandlesResponse>("/api/market/ETH-USD/candles?tf=1m&limit=10");
        Assert.NotNull(resp);
        Assert.Equal("1m", resp.Timeframe);
        Assert.Equal(2, resp.Candles.Length); // minutes -3 and -2 closed; -1 forming
        Assert.NotNull(resp.Forming);
        Assert.Equal("live", resp.Candles[0].Src);

        var bad = await client.GetAsync("/api/market/ETH-USD/candles?tf=2m");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await client.GetAsync("/api/market/NOPE-USD/candles");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Analytics_endpoint_projects_indicators_at_the_live_price()
    {
        var client = await ClientAsync();
        var resp = await client.GetAsync("/api/market/BTC-USD/analytics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("BTC-USD", root.GetProperty("symbol").GetProperty("value").GetString());
        Assert.Equal(49997, root.GetProperty("price").GetDouble());
        var tfs = root.GetProperty("timeframes").EnumerateArray().ToList();
        Assert.Contains(tfs, t => t.GetProperty("timeframe").GetString() == "M1");
        Assert.Equal("Unknown", tfs[0].GetProperty("alignment").GetString());
        Assert.Equal(JsonValueKind.Null, tfs[0].GetProperty("ema9").ValueKind); // two closed bars: not enough history, never invented
        var momentum = root.GetProperty("momentum");
        Assert.NotEqual(JsonValueKind.Null, momentum.GetProperty("r1m").ValueKind);
        Assert.Equal(JsonValueKind.Null, momentum.GetProperty("r1h").ValueKind);
        Assert.True(root.GetProperty("vwap").GetProperty("above").ValueKind is JsonValueKind.True or JsonValueKind.False);

        var missing = await client.GetAsync("/api/market/XRP-USD/analytics");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Breakout state needs closed 5m bars. Depending on the wall clock the three fake 1m bars may or may not have
        // closed one, so either there is nothing yet (404) or an analysis with no levels; never an invented setup.
        var breakouts = await client.GetAsync("/api/market/BTC-USD/breakouts");
        if (breakouts.StatusCode == HttpStatusCode.OK)
        {
            using var b = JsonDocument.Parse(await breakouts.Content.ReadAsStringAsync());
            Assert.Equal(0, b.RootElement.GetProperty("levels").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, b.RootElement.GetProperty("bestUp").ValueKind);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, breakouts.StatusCode);
        }
    }

    [Fact]
    public async Task Feed_status_health_and_metrics_reflect_the_live_provider()
    {
        var client = await ClientAsync();
        var feed = await client.GetFromJsonAsync<FeedStatusDto>("/api/system/feed");
        Assert.NotNull(feed);
        Assert.Equal("fake", feed.Provider);
        Assert.Equal("Connected", feed.Status);
        Assert.True(feed.Live);
        Assert.Equal(2, feed.UniverseSize);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var doc = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var metrics = JsonDocument.Parse(await client.GetStringAsync("/api/system/metrics"));
        Assert.True(metrics.RootElement.GetProperty("candleCloses").GetInt64() >= 2);
    }

    [Fact]
    public async Task SignalR_clients_receive_batched_quotes()
    {
        // Own host: this test injects a trade and must not change what the shared-fixture tests observe.
        using var factory = new Factory();
        var client = await ClientAsync(factory);
        var received = new TaskCompletionSource<List<QuoteDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/hubs/market"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; // TestServer has no real socket
            })
            .Build();
        connection.On<List<QuoteDto>>("quotes", q => received.TrySetResult(q));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await connection.StartAsync(timeout.Token);
        try
        {
            await connection.InvokeAsync("SubscribeCandles", "BTC-USD", "1m", timeout.Token);
            // Inject a fresh trade through the engine by way of the provider's output channel: simplest is a new quote via the engine's channel.
            var channel = factory.Services.GetRequiredService<TradingScanner.MarketData.Engine.MarketEventChannel>();
            var now = DateTimeOffset.UtcNow;
            await channel.Writer.WriteAsync(MarketEvent.FromTrade(new Trade(new Symbol("BTC-USD"), "fake", "Fake Exchange", 999, 50123m, 0.5m, TradeSide.Buy, now, now)));

            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var btc = Assert.Single(batch, q => q.Symbol == "BTC-USD");
            Assert.Equal(50123m, btc.Price);
            Assert.Equal("fake", btc.Provider);
            Assert.True(btc.AgeMs >= 0);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
