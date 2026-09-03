using TradingScanner.Core.Market;

namespace TradingScanner.Core.Providers;

/// <summary>Tradable product as advertised by the exchange, normalized.</summary>
public sealed record ProductInfo(
    Symbol Symbol,
    string ProviderProductId,
    string BaseCurrency,
    string QuoteCurrency,
    bool IsOnline,
    decimal PriceIncrement,
    decimal SizeIncrement,
    decimal? Volume24hQuote,
    decimal? LastPrice);
