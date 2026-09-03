namespace TradingScanner.MarketData.Coinbase;

public sealed class CoinbaseOptions
{
    public const string SectionName = "Coinbase";
    public string WebSocketUrl { get; set; } = "wss://ws-feed.exchange.coinbase.com";
    public string RestUrl { get; set; } = "https://api.exchange.coinbase.com";
    public string UserAgent { get; set; } = "TradingScanner/0.1 (+market-data)";
}
