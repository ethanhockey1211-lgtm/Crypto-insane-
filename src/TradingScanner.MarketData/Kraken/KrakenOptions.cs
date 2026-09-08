namespace TradingScanner.MarketData.Kraken;

public sealed class KrakenOptions
{
    public const string SectionName = "Kraken";
    public string RestUrl { get; set; } = "https://api.kraken.com";
    public string WebSocketUrl { get; set; } = "wss://ws.kraken.com/v2";
    public string UserAgent { get; set; } = "TradingScanner/0.1 (+market-data)";

    /// <summary>Optional ISO country code used by Kraken to filter available pairs. Empty uses its global catalog.</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>Display context only. Kraken's public pair filter accepts countries, not US states or app accounts.</summary>
    public string Region { get; set; } = "";
    public string TradingVenue { get; set; } = "";

    /// <summary>User-reported app exclusions, applied before universe selection even in IncludeAllPairs mode.</summary>
    public string[] ExcludedAssets { get; set; } = [];

    public string[] NormalizedExcludedAssets() => ExcludedAssets
        .Where(a => !string.IsNullOrWhiteSpace(a))
        .Select(a => KrakenSymbols.NormalizeAsset(a.Trim()))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
}
