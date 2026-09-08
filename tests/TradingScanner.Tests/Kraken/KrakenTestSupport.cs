using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingScanner.Core;
using TradingScanner.MarketData.Kraken;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.Universe;
using TradingScanner.Tests.Support;

namespace TradingScanner.Tests.Kraken;

internal static class KrakenTestSupport
{
    public static string Catalog => JsonSerializer.Serialize(new
    {
        error = Array.Empty<string>(),
        result = new Dictionary<string, object>
        {
            ["XXBTZUSD"] = Pair("XBT/USD", "XBTUSD"),
            ["XDGUSD"] = Pair("XDG/USD", "XDGUSD"),
            ["XETHZUSD"] = Pair("ETH/USD", "ETHUSD"),
            ["XXBTZEUR"] = Pair("XBT/EUR", "XBTEUR"),
            ["ZEURZUSD"] = Pair("EUR/USD", "EURUSD"),
            ["AAPLXUSD"] = Pair("AAPLx/USD", "AAPLXUSD", assetClass: "tokenized_asset"),
            ["OFFUSD"] = Pair("OFF/USD", "OFFUSD", status: "cancel_only"),
            ["XBTUSD.d"] = Pair("XBT/USD", "XBTUSD.d"),
        },
    });

    private static object Pair(string name, string alt, string status = "online", string assetClass = "currency") => new
        { wsname = name, altname = alt, status, aclass_base = assetClass, aclass_quote = "currency", pair_decimals = 2, lot_decimals = 8, tick_size = "0.01" };

    public const string Tickers = """
        {"error":[],"result":{
          "XXBTZUSD":{"c":["50000","0.1"],"v":["1","10"],"p":["48000","49000"]},
          "XDGUSD":{"c":["0.10","10"],"v":["5","10000"],"p":["0.09","0.08"]},
          "XETHZUSD":{"c":["3000","1"],"v":["2","100"],"p":["2900","2800"]}
        }}
        """;

    public static StubHttpHandler Handler() => new StubHttpHandler().On("/0/public/AssetPairs", Catalog).On("/0/public/Ticker", Tickers);

    public static KrakenRestClient Rest(StubHttpHandler? handler = null, KrakenOptions? options = null, TimeProvider? time = null, MarketDataOptions? marketOptions = null) =>
        new(new HttpClient(handler ?? Handler()) { BaseAddress = new Uri("https://kraken.test/") }, options ?? new KrakenOptions(),
            marketOptions ?? new MarketDataOptions(), new RequestRateLimiter(1000, TimeSpan.FromSeconds(1)), new MarketDataMetrics(), NullLogger<KrakenRestClient>.Instance, time);

    public static KrakenExchangeProvider Provider(ScriptedSocketFactory factory, MarketDataOptions? options = null, MarketDataMetrics? metrics = null) =>
        new(new KrakenOptions(), options ?? T.FastOptions(), Rest(), factory, metrics ?? new MarketDataMetrics(),
            NullLogger<KrakenExchangeProvider>.Instance, random: new Random(1));

    public static string Ack(string channel, string symbol = "BTC/USD") =>
        JsonSerializer.Serialize(new { method = "subscribe", success = true, result = new { channel, symbol } });

    public static string Trade(long id, string type = "update", string symbol = "BTC/USD", string side = "buy") =>
        JsonSerializer.Serialize(new { channel = "trade", type, data = new[] { new { symbol, side, qty = 0.25m, price = 50000m, trade_id = id, timestamp = "2024-03-01T12:00:00.123456789Z" } } });

    public const string Ticker = """
        {"channel":"ticker","type":"snapshot","data":[{"symbol":"BTC/USD","last":50000,"bid":49990,"ask":50010,"change":1000,"high":51000,"low":48000,"volume":10,"vwap":49000,"timestamp":"2024-03-01T12:00:00Z"}]}
        """;
}
