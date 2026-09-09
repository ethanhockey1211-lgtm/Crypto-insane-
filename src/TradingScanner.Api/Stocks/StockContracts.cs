namespace TradingScanner.Api.Stocks;

public sealed class StockScannerOptions
{
    public const string SectionName = "Stocks";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";

    public bool Configured => Credential(ApiKey) && Credential(ApiSecret)
        && Credential(AccessToken) && AccessToken.Trim().Length >= 24
        && !string.Equals(AccessToken.Trim(), ApiKey.Trim(), StringComparison.Ordinal)
        && !string.Equals(AccessToken.Trim(), ApiSecret.Trim(), StringComparison.Ordinal);

    private static bool Credential(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Trim().Length <= 512 && !value.Any(char.IsControl);
}

public sealed record StockScannerStatus(bool Configured, bool AccessRequired, string Provider, string Feed,
    int MaxSymbols, int RefreshSeconds, string Message);
public sealed record StockMinuteBar(DateTimeOffset At, double Open, double High, double Low, double Close, double Volume, double? Vwap);
public sealed record StockLatestTrade(double Price, DateTimeOffset At);
public sealed record StockLatestQuote(double Bid, double Ask, double BidSize, double AskSize, DateTimeOffset At);
public sealed record StockMarketData(string Ticker, IReadOnlyList<StockMinuteBar> Bars, StockLatestTrade? LatestTrade,
    StockLatestQuote? LatestQuote, double? PreviousClose, double? DayVolume, bool HistoryComplete);
public sealed record StockScanResponse(string Status, string Provider, string Feed, DateTimeOffset AsOf,
    string? Message, IReadOnlyList<StockMarketData> Rows, string? ErrorCode = null);
public sealed record StockScanResult(int StatusCode, StockScanResponse Body);
