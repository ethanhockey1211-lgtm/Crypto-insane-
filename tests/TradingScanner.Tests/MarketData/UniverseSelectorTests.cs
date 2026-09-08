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

    [Fact]
    public void All_pairs_mode_keeps_small_stable_wrapped_and_unmeasured_markets_without_a_size_cap()
    {
        var products = Enumerable.Range(0, 250).Select(i => P($"COIN{i}-USD", i))
            .Concat([P("USDT-USD", 900m), P("WBTC-USD", 1m), P("NEW-USD", null),
                P("BTC-USD", 500m), P("BTC-USD", 500m), P("ETH-EUR", 999m), P("OLD-USD", 999m, false)])
            .ToArray();
        var options = new MarketDataOptions { IncludeAllPairs = true, UniverseSize = 2, MinVolume24hQuote = 1_000_000m };
        var selected = UniverseSelector.Select(products, options);
        Assert.Equal(254, selected.Count);
        Assert.Equal("USDT-USD", selected[0].Symbol.Value);
        Assert.Contains(selected, p => p.Symbol.Value == "NEW-USD");
        Assert.Contains(selected, p => p.Symbol.Value == "WBTC-USD");
        Assert.Contains(selected, p => p.Symbol.Value == "COIN0-USD");
        Assert.Single(selected, p => p.Symbol.Value == "BTC-USD");
        Assert.DoesNotContain(selected, p => p.Symbol.Value is "ETH-EUR" or "OLD-USD");
    }
}
