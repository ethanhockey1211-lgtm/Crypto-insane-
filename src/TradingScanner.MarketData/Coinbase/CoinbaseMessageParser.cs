using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Coinbase;

/// <summary>
/// Allocation-light parser for the Coinbase Exchange WebSocket feed (wss://ws-feed.exchange.coinbase.com).
/// Reads the raw UTF-8 message once with Utf8JsonReader, regardless of property order.
/// Reference: https://docs.cdp.coinbase.com/exchange/docs/websocket-channels
/// </summary>
public static class CoinbaseMessageParser
{
    public static bool TryParse(ReadOnlySpan<byte> utf8, out CoinbaseMessage message, out string? error)
    {
        message = default;
        error = null;
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "Message is not a JSON object.";
                return false;
            }

            var type = CoinbaseMessageType.Unknown;
            string? productId = null;
            long sequence = 0, tradeId = 0, lastTradeId = 0;
            decimal price = 0, size = 0, bid = 0, ask = 0, open = 0, high = 0, low = 0, vol = 0;
            var makerSideSell = true; // default -> taker buy
            DateTimeOffset time = default;
            string? errMsg = null, errReason = null;
            var subscribedProducts = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    type = ReadType(ref reader);
                }
                else if (reader.ValueTextEquals("product_id"u8)) { reader.Read(); productId = reader.GetString(); }
                else if (reader.ValueTextEquals("sequence"u8)) { reader.Read(); sequence = ReadLong(ref reader); }
                else if (reader.ValueTextEquals("trade_id"u8)) { reader.Read(); tradeId = ReadLong(ref reader); }
                else if (reader.ValueTextEquals("last_trade_id"u8)) { reader.Read(); lastTradeId = ReadLong(ref reader); }
                else if (reader.ValueTextEquals("price"u8)) { reader.Read(); price = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("size"u8)) { reader.Read(); size = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("last_size"u8)) { reader.Read(); size = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("best_bid"u8)) { reader.Read(); bid = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("best_ask"u8)) { reader.Read(); ask = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("open_24h"u8)) { reader.Read(); open = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("high_24h"u8)) { reader.Read(); high = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("low_24h"u8)) { reader.Read(); low = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("volume_24h"u8)) { reader.Read(); vol = ReadDecimal(ref reader); }
                else if (reader.ValueTextEquals("side"u8)) { reader.Read(); makerSideSell = reader.ValueTextEquals("sell"u8); }
                else if (reader.ValueTextEquals("time"u8)) { reader.Read(); time = ReadTime(ref reader); }
                else if (reader.ValueTextEquals("message"u8)) { reader.Read(); errMsg = reader.GetString(); }
                else if (reader.ValueTextEquals("reason"u8)) { reader.Read(); errReason = reader.GetString(); }
                else if (reader.ValueTextEquals("channels"u8))
                {
                    reader.Read();
                    subscribedProducts = CountSubscribedProducts(ref reader);
                }
                else
                {
                    reader.Read();
                    reader.TrySkip();
                }
            }

            if (type is CoinbaseMessageType.Match or CoinbaseMessageType.LastMatch or CoinbaseMessageType.Ticker or CoinbaseMessageType.Heartbeat)
            {
                if (productId is null) { error = $"{type} without product_id."; return false; }
                if (time == default) { error = $"{type} without time."; return false; }
            }
            if (type is CoinbaseMessageType.Match or CoinbaseMessageType.LastMatch)
            {
                if (price <= 0 || size <= 0) { error = $"{type} with non-positive price/size."; return false; }
            }

            // Coinbase: "side" is the maker order side. Maker sell => taker bought (up-tick).
            var takerSide = makerSideSell ? TradeSide.Buy : TradeSide.Sell;

            message = new CoinbaseMessage(type, productId, sequence, tradeId, lastTradeId, price, size, bid, ask, open, high, low, vol,
                takerSide, time, errMsg, errReason, subscribedProducts);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static CoinbaseMessageType ReadType(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String) return CoinbaseMessageType.Unknown;
        if (reader.ValueTextEquals("match"u8)) return CoinbaseMessageType.Match;
        if (reader.ValueTextEquals("last_match"u8)) return CoinbaseMessageType.LastMatch;
        if (reader.ValueTextEquals("ticker"u8)) return CoinbaseMessageType.Ticker;
        if (reader.ValueTextEquals("heartbeat"u8)) return CoinbaseMessageType.Heartbeat;
        if (reader.ValueTextEquals("subscriptions"u8)) return CoinbaseMessageType.Subscriptions;
        if (reader.ValueTextEquals("error"u8)) return CoinbaseMessageType.Error;
        return CoinbaseMessageType.Unknown;
    }

    private static long ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt64();
        if (reader.TokenType == JsonTokenType.String && Utf8Parser.TryParse(reader.ValueSpan, out long v, out _)) return v;
        if (reader.TokenType == JsonTokenType.Null) return 0;
        throw new FormatException("Expected integer.");
    }

    private static decimal ReadDecimal(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (!reader.HasValueSequence && Utf8Parser.TryParse(reader.ValueSpan, out decimal v, out var consumed) && consumed == reader.ValueSpan.Length)
                return v;
            return decimal.Parse(reader.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (reader.TokenType == JsonTokenType.Number) return reader.GetDecimal();
        if (reader.TokenType == JsonTokenType.Null) return 0m;
        throw new FormatException("Expected decimal.");
    }

    internal static DateTimeOffset ReadTime(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String) throw new FormatException("Expected timestamp string.");
        if (!reader.HasValueSequence && Utf8Parser.TryParse(reader.ValueSpan, out DateTimeOffset dto, out var consumed, 'O') && consumed == reader.ValueSpan.Length)
            return dto.ToUniversalTime();
        // Coinbase emits variable-precision fractional seconds; fall back to the general parser.
        var s = reader.GetString()!;
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>Counts product_ids inside the "channels" array of a subscriptions confirmation, for logging/health.</summary>
    private static int CountSubscribedProducts(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.TrySkip(); return 0; }
        var max = 0;
        var depth = reader.CurrentDepth;
        while (reader.Read() && !(reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth))
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("product_ids"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) continue;
                var n = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) n++;
                if (n > max) max = n;
            }
        }
        return max;
    }

    /// <summary>Builds the subscribe request for the given product ids.</summary>
    public static byte[] BuildSubscribe(IEnumerable<string> productIds, params string[] channels)
    {
        var buffer = new ArrayBufferWriterCompat();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "subscribe");
            w.WriteStartArray("product_ids");
            foreach (var p in productIds) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("channels");
            foreach (var c in channels) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private sealed class ArrayBufferWriterCompat : System.Buffers.IBufferWriter<byte>
    {
        private readonly MemoryStream _ms = new();
        private byte[] _scratch = new byte[4096];
        private int _pending;

        public void Advance(int count) { _ms.Write(_scratch, 0, count); _pending = 0; }
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _scratch; }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _scratch; }
        private void Ensure(int hint) { if (hint > _scratch.Length) _scratch = new byte[Math.Max(hint, _scratch.Length * 2)]; _pending = hint; }
        public byte[] ToArray() => _ms.ToArray();
    }

    public static string DebugString(ReadOnlySpan<byte> utf8, int max = 300)
    {
        var s = Encoding.UTF8.GetString(utf8);
        return s.Length <= max ? s : s[..max] + "…";
    }
}
