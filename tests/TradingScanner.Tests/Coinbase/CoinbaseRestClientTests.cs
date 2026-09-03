using System.Net;
using TradingScanner.Core.Market;
using TradingScanner.Tests.Fixtures;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Coinbase;

public class CoinbaseRestClientTests
{
    [Fact]
    public async Task Products_are_normalized_with_online_status()
    {
        var handler = new StubHttpHandler().On("/products", CoinbaseFixtures.Products);
        var client = T.RestClient(handler);
        var products = await client.GetProductsAsync(CancellationToken.None);

        Assert.Equal(7, products.Count);
        var btc = products.Single(p => p.Symbol.Value == "BTC-USD");
        Assert.True(btc.IsOnline);
        Assert.Equal("BTC", btc.BaseCurrency);
        Assert.Equal("USD", btc.QuoteCurrency);
        Assert.Equal(0.01m, btc.PriceIncrement);
        Assert.Equal(0.00000001m, btc.SizeIncrement);
        Assert.False(products.Single(p => p.Symbol.Value == "OLD-USD").IsOnline);
        Assert.False(products.Single(p => p.Symbol.Value == "HALT-USD").IsOnline, "trading_disabled must mark a product offline");
    }

    [Fact]
    public async Task Stats_enrichment_computes_quote_volume_and_tolerates_failures()
    {
        var handler = new StubHttpHandler()
            .On("/products/BTC-USD/stats", CoinbaseFixtures.Stats("100", "50000"))
            .On("/products/ETH-USD/stats", "{\"message\":\"NotFound\"}", HttpStatusCode.NotFound);
        var client = T.RestClient(handler);
        var products = new[]
        {
            new TradingScanner.Core.Providers.ProductInfo(new Symbol("BTC-USD"), "BTC-USD", "BTC", "USD", true, 0.01m, 0.001m, null, null),
            new TradingScanner.Core.Providers.ProductInfo(new Symbol("ETH-USD"), "ETH-USD", "ETH", "USD", true, 0.01m, 0.001m, null, null),
        };
        var enriched = await client.EnrichWithStatsAsync(products, 4, CancellationToken.None);
        Assert.Equal(5_000_000m, enriched.Single(p => p.Symbol.Value == "BTC-USD").Volume24hQuote);
        Assert.Equal(50000m, enriched.Single(p => p.Symbol.Value == "BTC-USD").LastPrice);
        Assert.Null(enriched.Single(p => p.Symbol.Value == "ETH-USD").Volume24hQuote);
    }

    [Fact]
    public async Task Candles_are_returned_ascending_with_the_forming_bucket_excluded()
    {
        var to = T.At("2024-03-01T12:00:30Z");   // inside the 12:00 bucket, which is still forming
        var from = to.AddMinutes(-5);
        // Coinbase returns [time, low, high, open, close, volume], newest first, and may include the in-progress bucket.
        var rows = new List<string>();
        for (var m = 0; m >= -5; m--)
        {
            var t = T.At("2024-03-01T12:00:00Z").AddMinutes(m).ToUnixTimeSeconds();
            rows.Add($"[{t},{100 + m},{110 + m},{105 + m},{108 + m},{2}]");
        }
        var handler = new StubHttpHandler().On("/products/BTC-USD/candles", "[" + string.Join(",", rows) + "]");
        var client = T.RestClient(handler);

        var candles = await client.GetCandlesAsync(new Symbol("BTC-USD"), Timeframe.M1, from, to, CancellationToken.None);

        Assert.Equal(5, candles.Count);
        Assert.Equal(T.At("2024-03-01T11:55:00Z"), candles[0].OpenTime);
        Assert.Equal(T.At("2024-03-01T11:59:00Z"), candles[^1].OpenTime);
        Assert.True(candles.Zip(candles.Skip(1)).All(p => p.First.OpenTime < p.Second.OpenTime));
        var last = candles[^1];
        Assert.Equal(104m, last.Open);
        Assert.Equal(109m, last.High);
        Assert.Equal(99m, last.Low);
        Assert.Equal(107m, last.Close);
        Assert.Equal(2m, last.Volume);
        Assert.Equal(2m * (109m + 99m + 107m) / 3m, last.QuoteVolume);
        Assert.Equal(CandleSource.Historical, last.Source);
        var req = Assert.Single(handler.Requests);
        Assert.Contains("granularity=60", req.Query);
        Assert.Contains("start=2024-03-01T11:55:00Z", req.Query);
        Assert.Contains("end=2024-03-01T12:00:00Z", req.Query);
    }

    [Fact]
    public async Task Candles_page_backwards_in_300_bar_requests()
    {
        var to = T.At("2024-03-02T00:00:00Z");
        var from = to.AddMinutes(-400);
        var handler = new StubHttpHandler().On(u => u.AbsolutePath.EndsWith("/candles"), u =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            var start = T.At(q["start"]!);
            var end = T.At(q["end"]!);
            var rows = new List<string>();
            for (var t = end.AddMinutes(-1); t >= start; t = t.AddMinutes(-1))
                rows.Add($"[{t.ToUnixTimeSeconds()},1,2,1.5,1.7,1]");
            return "[" + string.Join(",", rows) + "]";
        });
        var client = T.RestClient(handler);

        var candles = await client.GetCandlesAsync(new Symbol("BTC-USD"), Timeframe.M1, from, to, CancellationToken.None);

        Assert.Equal(400, candles.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(from, candles[0].OpenTime);
        Assert.Equal(to.AddMinutes(-1), candles[^1].OpenTime);
        Assert.Equal(400, candles.Select(c => c.OpenTime).Distinct().Count());
    }

    [Fact]
    public async Task Unsupported_timeframes_throw_instead_of_guessing()
    {
        var client = T.RestClient(new StubHttpHandler());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetCandlesAsync(new Symbol("BTC-USD"), Timeframe.M3, T.Base.AddHours(-1), T.Base, CancellationToken.None));
    }

    [Fact]
    public async Task Http_errors_surface_with_status_and_body()
    {
        var handler = new StubHttpHandler().On("/products", "{\"message\":\"rate limited\"}", HttpStatusCode.TooManyRequests);
        var client = T.RestClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProductsAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Contains("rate limited", ex.Message);
    }
}
