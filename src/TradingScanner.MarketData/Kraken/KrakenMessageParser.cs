using System.Globalization;
using System.Text.Json;
using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Kraken;

public sealed record KrakenMessage(string? Channel, string? Type, string? AcknowledgedSymbol,
    bool Subscription, string? Error, IReadOnlyList<Trade> Trades, IReadOnlyList<TickerUpdate> Tickers);

/// <summary>Public WebSocket v2 protocol. Trades carry taker side and per-book sequence IDs.</summary>
public static class KrakenMessageParser
{
    public static KrakenMessage Parse(ReadOnlyMemory<byte> utf8, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(utf8);
        var root = doc.RootElement;
        var channel = KrakenRestClient.Text(root, "channel");
        var type = KrakenRestClient.Text(root, "type");
        if (KrakenRestClient.Text(root, "method") == "subscribe")
        {
            if (!root.GetProperty("success").GetBoolean())
                // Kraken identifies a rejected pair at the top level, outside result.
                return new(null, null, KrakenRestClient.Text(root, "symbol"), true,
                    KrakenRestClient.Text(root, "error") ?? "Subscription rejected", [], []);
            var result = root.GetProperty("result");
            return new(KrakenRestClient.Text(result, "channel"), null, KrakenRestClient.Text(result, "symbol"), true, null, [], []);
        }
        if (channel == "status")
        {
            var status = KrakenRestClient.Text(root.GetProperty("data")[0], "system");
            return new(channel, type, null, false, status == "online" ? null : $"Kraken system is {status ?? "unknown"}", [], []);
        }
        if (channel is not ("trade" or "ticker")) return new(channel, type, null, false, null, [], []);
        if (type is not ("snapshot" or "update")) throw new FormatException("Kraken market data missing snapshot/update type.");
        var trades = new List<Trade>();
        var tickers = new List<TickerUpdate>();
        foreach (var data in root.GetProperty("data").EnumerateArray())
        {
            var symbol = KrakenSymbols.FromWebSocket(data.GetProperty("symbol").GetString()!);
            var timestamp = DateTimeOffset.Parse(data.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (channel == "trade")
            {
                var price = KrakenRestClient.Decimal(data.GetProperty("price"));
                var size = KrakenRestClient.Decimal(data.GetProperty("qty"));
                var id = data.GetProperty("trade_id").GetInt64();
                var side = KrakenRestClient.Text(data, "side") switch
                {
                    "buy" => TradeSide.Buy, "sell" => TradeSide.Sell,
                    _ => throw new FormatException("Kraken trade has an invalid taker side."),
                };
                if (price <= 0 || size <= 0 || id <= 0) throw new FormatException("Kraken trade has a non-positive price, size or ID.");
                trades.Add(new Trade(symbol, KrakenExchangeProvider.ProviderName, KrakenExchangeProvider.ExchangeName,
                    id, price, size, side, timestamp, receivedAt));
            }
            else
            {
                var last = KrakenRestClient.Decimal(data.GetProperty("last"));
                var bid = KrakenRestClient.Decimal(data.GetProperty("bid"));
                var ask = KrakenRestClient.Decimal(data.GetProperty("ask"));
                // Quiet/new listings can have no recent trade or an empty book side. Kraken only
                // guarantees last when traded within 24h. Keep their catalog rows, but do not
                // invent a price or reconnect every other market sharing this socket.
                if (last == 0m || bid == 0m || ask == 0m) continue;
                // The documented rolling 24h absolute change is named change; older v2 deployments use price_change.
                var change = data.TryGetProperty("change", out var field) || data.TryGetProperty("price_change", out field)
                    ? KrakenRestClient.Decimal(field) : 0m;
                if (last <= 0 || bid <= 0 || ask < bid) throw new FormatException("Kraken ticker has invalid prices.");
                tickers.Add(new TickerUpdate(symbol, KrakenExchangeProvider.ProviderName, KrakenExchangeProvider.ExchangeName,
                    last, bid, ask, last - change, KrakenRestClient.Decimal(data.GetProperty("high")),
                    KrakenRestClient.Decimal(data.GetProperty("low")), KrakenRestClient.Decimal(data.GetProperty("volume")), timestamp, receivedAt));
            }
        }
        return new(channel, type, null, false, null, trades, tickers);
    }

    public static byte[] BuildSubscribe(IEnumerable<Symbol> symbols, string channel) => channel switch
    {
        "trade" => JsonSerializer.SerializeToUtf8Bytes(new { method = "subscribe", @params = new { channel, symbol = symbols.Select(KrakenSymbols.ToWebSocket).ToArray(), snapshot = false } }),
        // Best-bid-offer updates keep the execution spread current between trades.
        "ticker" => JsonSerializer.SerializeToUtf8Bytes(new { method = "subscribe", @params = new { channel, symbol = symbols.Select(KrakenSymbols.ToWebSocket).ToArray(), snapshot = true, event_trigger = "bbo" } }),
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}
