using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Coinbase;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.Universe;

namespace TradingScanner.Tests.Support;

public static class T
{
    public static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    public static readonly DateTimeOffset Base = At("2024-03-01T12:00:00Z");

    public static Trade Trade(string symbol, decimal price, decimal size, DateTimeOffset time, TradeSide side = TradeSide.Buy, long id = 1) =>
        new(new Symbol(symbol), "test", "Test Exchange", id, price, size, side, time, time + TimeSpan.FromMilliseconds(40));

    public static Candle Candle(string symbol, Timeframe tf, DateTimeOffset open, decimal o, decimal h, decimal l, decimal c, decimal v = 1m, CandleSource source = CandleSource.Live) =>
        new(new Symbol(symbol), tf, open, o, h, l, c, v, v * (h + l + c) / 3m, source == CandleSource.Live ? v / 2 : 0m, source == CandleSource.Live ? v / 2 : 0m, source == CandleSource.Live ? 3 : 0, source);

    public static MarketDataOptions FastOptions() => new()
    {
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
        ReconnectMaxDelay = TimeSpan.FromMilliseconds(40),
        ReceiveTimeout = TimeSpan.FromSeconds(5),
        SymbolsPerConnection = 100,
        CandleCloseGrace = TimeSpan.FromSeconds(2),
    };

    public static CoinbaseRestClient RestClient(HttpMessageHandler? handler = null, MarketDataMetrics? metrics = null)
    {
        var http = new HttpClient(handler ?? new StubHttpHandler()) { BaseAddress = new Uri("https://api.exchange.test/") };
        return new CoinbaseRestClient(http, new CoinbaseOptions(), new RequestRateLimiter(1000, TimeSpan.FromSeconds(1)), metrics ?? new MarketDataMetrics(), NullLogger<CoinbaseRestClient>.Instance);
    }

    public static CoinbaseExchangeProvider Provider(ScriptedSocketFactory sockets, MarketDataOptions? options = null, MarketDataMetrics? metrics = null, ILogger<CoinbaseExchangeProvider>? logger = null)
    {
        metrics ??= new MarketDataMetrics();
        return new CoinbaseExchangeProvider(new CoinbaseOptions { WebSocketUrl = "wss://test.invalid/" }, options ?? FastOptions(), RestClient(metrics: metrics), sockets, metrics, logger ?? NullLogger<CoinbaseExchangeProvider>.Instance, random: new Random(1));
    }

    public static IOptions<MarketDataOptions> Opts(MarketDataOptions? o = null) => Options.Create(o ?? new MarketDataOptions());

    /// <summary>Read events until <paramref name="until"/> returns true or the timeout elapses. Returns everything read.</summary>
    public static async Task<List<MarketEvent>> CollectAsync(ChannelReader<MarketEvent> reader, Func<List<MarketEvent>, bool> until, TimeSpan? timeout = null)
    {
        var events = new List<MarketEvent>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (!until(events))
            {
                var evt = await reader.ReadAsync(cts.Token);
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Condition not met after {events.Count} events: " + string.Join(", ", events.Select(Describe)));
        }
        return events;
    }

    public static string Describe(MarketEvent e) => e.Kind switch
    {
        MarketEventKind.Trade => $"Trade({e.Trade.Symbol}#{e.Trade.TradeId}@{e.Trade.Price})",
        MarketEventKind.QuoteOnly => $"QuoteOnly({e.Trade.Symbol}@{e.Trade.Price})",
        MarketEventKind.Ticker => $"Ticker({e.Ticker.Symbol})",
        MarketEventKind.Status => $"Status({e.Status!.ConnectionIndex}:{e.Status.Status})",
        MarketEventKind.Gap => $"Gap({e.Gap!.Symbol}:{e.Gap.ExpectedTradeId}->{e.Gap.ReceivedTradeId})",
        _ => e.Kind.ToString(),
    };
}

/// <summary>Routes GET requests by path (with query) to canned JSON. Records requests.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> match, Func<Uri, (HttpStatusCode, string)> respond)> _routes = new();
    public List<Uri> Requests { get; } = new();

    public StubHttpHandler On(string pathPrefix, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((u => u.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal), _ => (status, json)));
        return this;
    }

    public StubHttpHandler On(Func<Uri, bool> match, Func<Uri, string> json)
    {
        _routes.Add((match, u => (HttpStatusCode.OK, json(u))));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        foreach (var (match, respond) in _routes)
        {
            if (!match(request.RequestUri!)) continue;
            var (status, body) = respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"NotFound\"}") });
    }
}
