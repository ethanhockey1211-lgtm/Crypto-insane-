using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Stocks;

/// <summary>Authenticated, bounded IEX market data only. No account, order, or broker trading endpoint exists here.</summary>
public sealed class AlpacaStockScanner : IDisposable
{
    public const int MaxSymbols = 40;
    public const int RefreshSeconds = 30;
    private const int MaxPages = 3, MaxCacheEntries = 8, MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeZoneInfo Eastern = FindEastern();
    private static readonly Regex TickerSyntax = new("^[A-Z]{1,5}(?:[.-][A-Z]{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex TimestampSyntax = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.(?<fraction>[0-9]{1,9}))?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant);
    private readonly HttpClient _http;
    private readonly StockScannerOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _requests = new();

    public AlpacaStockScanner(HttpClient http, IOptions<StockScannerOptions> options, TimeProvider? time = null)
    {
        _http = http; _options = options.Value; _time = time ?? TimeProvider.System;
    }

    public StockScannerStatus GetStatus() => new(_options.Configured, true, "Alpaca", "iex", MaxSymbols, RefreshSeconds,
        _options.Configured
            ? "IEX data is configured. A stock access code is required. IEX covers one exchange, not the consolidated US market."
            : "Set Stocks:ApiKey, Stocks:ApiSecret and Stocks:AccessToken (at least 24 characters) on the server.");

    public async Task<StockScanResult> ScanAsync(string? rawSymbols, string? accessToken, CancellationToken ct)
    {
        if (!_options.Configured) return Failure(503, "not-configured", GetStatus().Message);
        if (!Authorized(accessToken)) return Failure(401, "error", "A valid stock access code is required.");
        if (!TrySymbols(rawSymbols, out var symbols)) return Failure(400, "error", "Request 1 to 40 distinct US stock tickers separated by commas.");
        var key = string.Join(',', symbols);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var entered = false;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var now = _time.GetUtcNow();
            foreach (var expired in _cache.Where(pair => now - pair.Value.At >= TimeSpan.FromSeconds(RefreshSeconds) || now < pair.Value.At).Select(pair => pair.Key).ToArray())
                _cache.Remove(expired);
            if (_cache.TryGetValue(key, out var cached)) return new(200, cached.Response);
            var response = await FetchAsync(symbols, now, linked.Token).ConfigureAwait(false);
            if (_cache.Count >= MaxCacheEntries) _cache.Remove(_cache.MinBy(pair => pair.Value.At).Key);
            _cache[key] = new(now, response);
            return new(200, response);
        }
        catch (ProviderFailure failure) { return Failure(failure.StatusCode, "error", failure.SafeMessage); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Failure(502, "error", "Stock data did not respond in time. Try again shortly."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or InvalidOperationException or IOException or ArgumentException)
        { return Failure(502, "error", "Stock data is temporarily unavailable. Check the server configuration or try again shortly."); }
        finally { if (entered) _gate.Release(); }
    }

    private bool Authorized(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(_options.AccessToken.Trim())));
    }

    private static bool TrySymbols(string? raw, out string[] symbols)
    {
        symbols = [];
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024) return false;
        var parsed = raw.Split(',').Select(value => value.Trim().ToUpperInvariant()).ToArray();
        if (parsed.Length is < 1 or > MaxSymbols || parsed.Any(value => !TickerSyntax.IsMatch(value))
            || parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Length) return false;
        symbols = parsed.Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    private async Task<StockScanResponse> FetchAsync(string[] symbols, DateTimeOffset now, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(now, Eastern);
        var open = ToUtc(local.Date.AddHours(9.5));
        var close = ToUtc(local.Date.AddHours(16));
        var end = now < close ? now : close;
        var sessionStarted = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && now >= open.AddMinutes(1);
        var encoded = Uri.EscapeDataString(string.Join(',', symbols));
        using var snapshots = await GetJsonAsync($"/v2/stocks/snapshots?symbols={encoded}&feed=iex", ct).ConfigureAwait(false);
        if (snapshots.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var bars = symbols.ToDictionary(symbol => symbol, _ => new SortedDictionary<DateTimeOffset, StockMinuteBar>(), StringComparer.Ordinal);
        var invalidHistory = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        if (sessionStarted)
        {
            string? token = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < MaxPages; page++)
            {
                var path = $"/v2/stocks/bars?symbols={encoded}&timeframe=1Min&start={Uri.EscapeDataString(open.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}&adjustment=raw&feed=iex&sort=asc&limit=10000";
                if (token is not null) path += "&page_token=" + Uri.EscapeDataString(token);
                using var response = await GetJsonAsync(path, ct).ConfigureAwait(false);
                var root = response.RootElement;
                if (!root.TryGetProperty("bars", out var pageBars) || pageBars.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new JsonException();
                if (pageBars.ValueKind == JsonValueKind.Object)
                {
                    foreach (var symbol in symbols)
                    {
                        if (!pageBars.TryGetProperty(symbol, out var data)) continue;
                        if (data.ValueKind != JsonValueKind.Array) { invalidHistory.Add(symbol); continue; }
                        foreach (var item in data.EnumerateArray())
                        {
                            var at = Timestamp(item, "t", wholeMinute: true);
                            if (at is null) { invalidHistory.Add(symbol); continue; }
                            if (at < open || at >= close || at.Value.AddMinutes(1) > end) continue;
                            var parsed = ParseBar(item, at.Value);
                            if (parsed is null) { invalidHistory.Add(symbol); continue; }
                            if (bars[symbol].TryGetValue(at.Value, out var existing) && existing != parsed)
                                invalidHistory.Add(symbol);
                            bars[symbol][at.Value] = parsed;
                        }
                    }
                }
                token = null;
                if (root.TryGetProperty("next_page_token", out var next) && next.ValueKind != JsonValueKind.Null)
                {
                    if (next.ValueKind != JsonValueKind.String) throw new JsonException();
                    token = next.GetString();
                    if (string.IsNullOrEmpty(token)) token = null;
                    else if (token.Length > 4096) throw new JsonException();
                }
                if (token is null) break;
                if (page == MaxPages - 1 || !seenTokens.Add(token)) { truncated = true; break; }
            }
        }

        var rows = new List<StockMarketData>(symbols.Length);
        foreach (var symbol in symbols)
        {
            snapshots.RootElement.TryGetProperty(symbol, out var snapshot);
            var ordered = bars[symbol].Values.TakeLast(400).ToArray();
            // IEX legitimately omits minutes without qualifying trades. Completeness means the
            // paginated response is intact; setup rules must separately require the bars they use.
            var complete = sessionStarted && ordered.Length > 0 && !truncated && !invalidHistory.Contains(symbol);
            rows.Add(new(symbol, ordered, ParseTrade(snapshot, now), ParseQuote(snapshot, now),
                PreviousClose(snapshot, open), DayVolume(snapshot, local.Date), complete));
        }
        return new("ready", "Alpaca", "iex", now,
            sessionStarted ? null : "Current-day regular-session history begins after 09:30 New York time. Entries require complete, fresh data.", rows);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        while (_requests.TryPeek(out var at) && now - at >= TimeSpan.FromMinutes(1)) _requests.Dequeue();
        if (_requests.Count >= 180) throw new ProviderFailure(429, "The stock data request limit was reached. Wait a minute before refreshing.");
        _requests.Enqueue(now);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://data.alpaca.markets" + path));
        request.Headers.Add("APCA-API-KEY-ID", _options.ApiKey.Trim());
        request.Headers.Add("APCA-API-SECRET-KEY", _options.ApiSecret.Trim());
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new ProviderFailure(429, "The stock data provider is rate limited. Wait before refreshing.");
        if (!response.IsSuccessStatusCode) throw new ProviderFailure(502, "Stock data is unavailable. Check the server credentials and IEX data access.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new JsonException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxResponseBytes) throw new JsonException();
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static StockMinuteBar? ParseBar(JsonElement item, DateTimeOffset at)
    {
        double? open = Positive(item, "o"), high = Positive(item, "h"), low = Positive(item, "l"), close = Positive(item, "c"), volume = Nonnegative(item, "v");
        if (open is null || high is null || low is null || close is null || volume is null || at.Second != 0 || at.Ticks % TimeSpan.TicksPerSecond != 0
            || high < Math.Max(open.Value, close.Value) || low > Math.Min(open.Value, close.Value)) return null;
        var vwap = Positive(item, "vw");
        if (item.TryGetProperty("vw", out var raw) && raw.ValueKind != JsonValueKind.Null && vwap is null) return null;
        return new(at, open.Value, high.Value, low.Value, close.Value, volume.Value, vwap);
    }

    private static StockLatestTrade? ParseTrade(JsonElement snapshot, DateTimeOffset now)
    {
        var trade = Child(snapshot, "latestTrade");
        var price = Positive(trade, "p"); var at = Timestamp(trade, "t");
        return price is { } p && at is { } t && t <= now.AddSeconds(2) ? new(p, t) : null;
    }

    private static StockLatestQuote? ParseQuote(JsonElement snapshot, DateTimeOffset now)
    {
        var quote = Child(snapshot, "latestQuote");
        double? bid = Positive(quote, "bp"), ask = Positive(quote, "ap"), bidSize = Nonnegative(quote, "bs"), askSize = Nonnegative(quote, "as");
        var at = Timestamp(quote, "t");
        return bid is { } b && ask is { } a && a >= b && bidSize is { } bs && askSize is { } ass && at is { } t && t <= now.AddSeconds(2)
            ? new(b, a, bs, ass, t) : null;
    }

    private static double? PreviousClose(JsonElement snapshot, DateTimeOffset open)
    {
        var bar = Child(snapshot, "prevDailyBar"); var at = Timestamp(bar, "t");
        return at is { } t && t < open && TimeZoneInfo.ConvertTime(t, Eastern).Date < TimeZoneInfo.ConvertTime(open, Eastern).Date ? Positive(bar, "c") : null;
    }
    private static double? DayVolume(JsonElement snapshot, DateTime localDate)
    {
        var bar = Child(snapshot, "dailyBar"); var at = Timestamp(bar, "t");
        return at is { } t && TimeZoneInfo.ConvertTime(t, Eastern).Date == localDate ? Nonnegative(bar, "v") : null;
    }
    private static JsonElement Child(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    private static double? Number(JsonElement value, string key)
    {
        var child = Child(value, key);
        return child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    }
    private static double? Positive(JsonElement value, string key) => Number(value, key) is > 0 and var number ? number : null;
    private static double? Nonnegative(JsonElement value, string key) => Number(value, key) is >= 0 and var number ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement value, string key, bool wholeMinute = false)
    {
        var text = Child(value, key);
        if (text.ValueKind != JsonValueKind.String) return null;
        var raw = text.GetString()!;
        if (raw.Length is < 20 or > 35) return null;
        var match = TimestampSyntax.Match(raw);
        if (!match.Success) return null;
        var fraction = match.Groups["fraction"];
        // Truncation must not make a fractional minute bar look exactly aligned.
        if (wholeMinute && fraction.Value.Any(digit => digit != '0')) return null;
        // Alpaca emits nanoseconds; DateTimeOffset keeps seven fractional digits.
        // Truncate explicitly because its permissive parser otherwise rounds forward.
        if (fraction.Length > 7) raw = raw.Remove(fraction.Index + 7, fraction.Length - 7);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }
    private StockScanResult Failure(int statusCode, string status, string message) => new(statusCode, new(status, "Alpaca", "iex", _time.GetUtcNow(), message, []));
    private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Eastern));
    private static TimeZoneInfo FindEastern()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
    private sealed record CacheEntry(DateTimeOffset At, StockScanResponse Response);
    private sealed class ProviderFailure(int statusCode, string safeMessage) : Exception
    { public int StatusCode { get; } = statusCode; public string SafeMessage { get; } = safeMessage; }
}
