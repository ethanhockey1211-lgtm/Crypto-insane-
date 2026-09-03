using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.MarketData.Universe;

/// <summary>
/// Pure selection of the tradable universe from exchange products. Ranked by 24h quote volume,
/// filtered to the configured quote currency, online status, and non-stablecoin bases.
/// </summary>
public static class UniverseSelector
{
    public static IReadOnlyList<ProductInfo> Select(IReadOnlyList<ProductInfo> products, MarketDataOptions options)
    {
        var excluded = new HashSet<string>(options.ExcludedBases, StringComparer.OrdinalIgnoreCase);
        var always = new HashSet<string>(options.AlwaysInclude, StringComparer.OrdinalIgnoreCase);

        var eligible = products
            .Where(p => p.IsOnline)
            .Where(p => string.Equals(p.QuoteCurrency, options.QuoteCurrency, StringComparison.OrdinalIgnoreCase))
            .Where(p => !excluded.Contains(p.BaseCurrency))
            .ToList();

        var ranked = eligible
            .Where(p => (p.Volume24hQuote ?? 0m) >= options.MinVolume24hQuote)
            .OrderByDescending(p => p.Volume24hQuote ?? 0m)
            .ThenBy(p => p.Symbol.Value, StringComparer.Ordinal)
            .Take(options.UniverseSize)
            .ToList();

        var chosen = new Dictionary<Symbol, ProductInfo>();
        foreach (var p in eligible.Where(p => always.Contains(p.Symbol.Value))) chosen[p.Symbol] = p;
        foreach (var p in ranked) chosen.TryAdd(p.Symbol, p);

        return chosen.Values
            .OrderByDescending(p => p.Volume24hQuote ?? 0m)
            .ThenBy(p => p.Symbol.Value, StringComparer.Ordinal)
            .ToList();
    }
}
