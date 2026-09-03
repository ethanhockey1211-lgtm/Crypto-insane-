using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Universe;
using Xunit;

namespace TradingScanner.Tests.MarketData;

public class UniverseSelectorTests
{
    private static ProductInfo P(string id, decimal? vol, bool online = true) =>
        new(new Symbol(id), id, id[..id.IndexOf('-')], id[(id.IndexOf('-') + 1)..], online, 0.01m, 0.001m, vol, 1m);

    [Fact]
    public void Filters_quote_status_and_stablecoins_then_ranks_by_volume()
    {
        var products = new[]
        {
            P("BTC-USD", 500_000_000m), P("ETH-USD", 200_000_000m), P("SOL-USD", 50_000_000m),
            P("USDT-USD", 900_000_000m), P("ETH-BTC", 100_000_000m), P("OLD-USD", 10_000_000m, online: false),
            P("DUST-USD", 5_000m), P("XRP-USD", 60_000_000m),
        };
        var options = new MarketDataOptions { UniverseSize = 3, MinVolume24hQuote = 1_000_000m, AlwaysInclude = [] };
        var selected = UniverseSelector.Select(products, options);
        Assert.Equal(["BTC-USD", "ETH-USD", "XRP-USD"], selected.Select(p => p.Symbol.Value));
    }

    [Fact]
    public void Always_include_symbols_are_added_even_when_outside_the_top_n()
    {
        var products = new[] { P("AAA-USD", 10m), P("BBB-USD", 9m), P("ETH-USD", 1m), P("BTC-USD", 0.5m) };
        var options = new MarketDataOptions { UniverseSize = 2, MinVolume24hQuote = 0m };
        var selected = UniverseSelector.Select(products, options);
        Assert.Equal(4, selected.Count);
        Assert.Contains(selected, p => p.Symbol.Value == "BTC-USD");
        Assert.Equal("AAA-USD", selected[0].Symbol.Value);
    }

    [Fact]
    public void Products_without_volume_are_not_ranked()
    {
        var products = new[] { P("AAA-USD", null), P("BBB-USD", 5m) };
        var options = new MarketDataOptions { UniverseSize = 5, MinVolume24hQuote = 1m, AlwaysInclude = [] };
        var selected = UniverseSelector.Select(products, options);
        Assert.Equal(["BBB-USD"], selected.Select(p => p.Symbol.Value));
    }
}
