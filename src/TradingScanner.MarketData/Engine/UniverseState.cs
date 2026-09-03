using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.MarketData.Engine;

/// <summary>The currently selected tradable universe, published by the orchestrator.</summary>
public sealed class UniverseState
{
    private IReadOnlyList<ProductInfo> _products = [];
    public IReadOnlyList<ProductInfo> Products => Volatile.Read(ref _products);
    public IReadOnlyList<Symbol> Symbols => Products.Select(p => p.Symbol).ToArray();
    public DateTimeOffset? SelectedAt { get; private set; }

    public void Set(IReadOnlyList<ProductInfo> products, DateTimeOffset at)
    {
        Volatile.Write(ref _products, products);
        SelectedAt = at;
    }
}
