using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData;
using TradingScanner.MarketData.Coinbase;
using TradingScanner.MarketData.Kraken;
using TradingScanner.Tests.Support;

namespace TradingScanner.Tests.Kraken;

public class KrakenRestClientTests
{
    [Fact]
    public async Task Discovers_only_online_crypto_USD_and_normalizes_legacy_symbols_with_actual_quote_turnover()
    {
        var handler = KrakenTestSupport.Handler();
        var rest = KrakenTestSupport.Rest(handler, new KrakenOptions { CountryCode = "us" });
        var products = await rest.GetProductsAsync(CancellationToken.None);
        Assert.Equal(["BTC-USD", "DOGE-USD", "ETH-USD"], products.Select(p => p.Symbol.Value).Order().ToArray());
        var btc = Assert.Single(products, p => p.BaseCurrency == "BTC");
        Assert.Equal("XXBTZUSD", btc.ProviderProductId);
        Assert.Equal(490000m, btc.Volume24hQuote); // rolling base volume 10 × VWAP 49000 (not last 50000)
        Assert.Equal(50000m, btc.LastPrice);
        Assert.Equal(0.01m, btc.PriceIncrement);
        Assert.Equal(0.00000001m, btc.SizeIncrement);
        Assert.Equal(2, handler.Requests.Count); // bulk ticker avoids hundreds of per-pair stats requests
        Assert.Contains("aclass_base=currency", handler.Requests[0].Query);
        Assert.Contains("country_code=US", handler.Requests[0].Query);
        Assert.Equal("DOGE/USD", KrakenSymbols.ToWebSocket(Assert.Single(products, p => p.BaseCurrency == "DOGE").Symbol));
    }

    [Fact]
    public async Task Closed_history_drops_final_uncommitted_row_filters_bounds_and_uses_VWAP_quote_volume()
    {
        var t = T.Base.ToUnixTimeSeconds();
        var handler = KrakenTestSupport.Handler().On("/0/public/OHLC", $$"""
            {"error":[],"result":{"XXBTZUSD":[
              [{{t - 60}},"9","12","8","11","10","2",3],
              [{{t}},"10","13","9","12","11","3",4],
              [{{t + 60}},"12","14","11","13","12","4",5],
              [{{t + 120}},"13","15","12","14","13","5",6]
            ],"last":{{t + 120}} } }
            """);
        var rest = KrakenTestSupport.Rest(handler);
        await rest.GetProductsAsync(CancellationToken.None);
        var bars = await rest.GetCandlesAsync(new Symbol("BTC-USD"), Timeframe.M1, T.Base, T.Base.AddMinutes(10), CancellationToken.None);
        Assert.Equal(2, bars.Count);
        Assert.Equal([T.Base, T.Base.AddMinutes(1)], bars.Select(c => c.OpenTime));
        Assert.Equal(33m, bars[0].QuoteVolume);
        Assert.Equal(4, bars[0].TradeCount);
        Assert.Equal(CandleSource.Historical, bars[0].Source);
        Assert.Equal(0m, bars[0].BuyVolume);
        Assert.Contains("pair=XXBTZUSD", handler.Requests[^1].Query);
        Assert.Equal(3, handler.Requests.Count); // never pretends paging can retrieve more than Kraken's 720-bar limit
    }

    [Fact]
    public async Task Historical_to_boundary_is_exclusive()
    {
        var t = T.Base.ToUnixTimeSeconds();
        var handler = KrakenTestSupport.Handler().On("/0/public/OHLC", $$"""
            {"error":[],"result":{"XXBTZUSD":[
              [{{t}},"10","13","9","12","11","3",4],
              [{{t + 60}},"12","14","11","13","12","4",5],
              [{{t + 120}},"13","15","12","14","13","5",6]
            ],"last":{{t + 120}} } }
            """);
        var bars = await KrakenTestSupport.Rest(handler).GetCandlesAsync(new Symbol("BTC-USD"), Timeframe.M1, T.Base, T.Base.AddMinutes(1), CancellationToken.None);
        Assert.Equal(T.Base, Assert.Single(bars).OpenTime);
    }

    [Fact]
    public async Task Api_error_envelopes_are_failures_even_with_HTTP_200()
    {
        var handler = new StubHttpHandler().On("/0/public/AssetPairs", """{"error":["EQuery:Unknown asset pair"],"result":{}}""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KrakenTestSupport.Rest(handler).GetProductsAsync(CancellationToken.None));
        Assert.Contains("Unknown asset pair", error.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("kraken", typeof(KrakenExchangeProvider))]
    [InlineData("coinbase", typeof(CoinbaseExchangeProvider))]
    public void Dependency_injection_honors_selected_provider(string provider, Type expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MarketData:Provider"] = provider }).Build();
        var services = new ServiceCollection().AddLogging().AddMarketData(config).BuildServiceProvider();
        Assert.IsType(expected, services.GetRequiredService<IMarketDataProvider>());
    }

    [Fact]
    public void Unknown_provider_fails_clearly_instead_of_silently_using_Coinbase()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MarketData:Provider"] = "typo" }).Build();
        var services = new ServiceCollection().AddLogging().AddMarketData(config).BuildServiceProvider();
        Assert.Contains("Unsupported", Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IMarketDataProvider>()).Message);
    }
}
