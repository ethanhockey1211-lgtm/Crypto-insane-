using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Kraken;

public static class KrakenSymbols
{
    // REST wsname uses the legacy names; WebSocket v2 uses the display names.
    public static string NormalizeAsset(string asset) => asset.ToUpperInvariant() switch
    {
        "XBT" => "BTC",
        "XDG" => "DOGE",
        var name => name,
    };

    public static Symbol FromWebSocket(string pair)
    {
        var parts = pair.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new FormatException($"Invalid Kraken symbol: {pair}");
        return new Symbol($"{NormalizeAsset(parts[0])}-{NormalizeAsset(parts[1])}");
    }

    public static string ToWebSocket(Symbol symbol) => symbol.Value.Replace('-', '/');
}
