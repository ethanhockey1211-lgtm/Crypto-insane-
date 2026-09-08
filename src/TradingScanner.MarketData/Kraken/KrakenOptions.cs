namespace TradingScanner.MarketData.Kraken;

public sealed class KrakenOptions
{
    public const string SectionName = "Kraken";
    public string RestUrl { get; set; } = "https://api.kraken.com";
    public string WebSocketUrl { get; set; } = "wss://ws.kraken.com/v2";
    public string UserAgent { get; set; } = "TradingScanner/0.1 (+market-data)";

    /// <summary>Optional ISO country code used by Kraken to filter available pairs. Empty uses its global catalog.</summary>
    public string CountryCode { get; set; } = "";
}
