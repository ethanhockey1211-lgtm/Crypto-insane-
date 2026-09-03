using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.Universe;

namespace TradingScanner.MarketData.Coinbase;

/// <summary>
/// Coinbase Exchange public REST API (no authentication). Used for the product list, 24h stats, and candle history.
/// Reference: https://docs.cdp.coinbase.com/exchange/reference
/// </summary>
public sealed class CoinbaseRestClient
{
    private const int MaxCandlesPerRequest = 300;

    private readonly HttpClient _http;
    private readonly RequestRateLimiter _limiter;
    private readonly MarketDataMetrics _metrics;
    private readonly ILogger<CoinbaseRestClient> _logger;

    public CoinbaseRestClient(HttpClient http, CoinbaseOptions options, RequestRateLimiter limiter, MarketDataMetrics metrics, ILogger<CoinbaseRestClient> logger)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.RestUrl.TrimEnd('/') + "/");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _limiter = limiter;
        _metrics = metrics;
        _logger = logger;
    }

    public static int? Granularity(Timeframe tf) => tf switch
    {
        Timeframe.M1 => 60,
        Timeframe.M5 => 300,
        Timeframe.M15 => 900,
        Timeframe.H1 => 3600,
        _ => null,
    };

    /// <summary>GET /products — every product; volume is filled in separately by <see cref="EnrichWithStatsAsync"/>.</summary>
    public async Task<List<ProductInfo>> GetProductsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("products", ct).ConfigureAwait(false);
        var list = new List<ProductInfo>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var id = p.GetProperty("id").GetString();
            if (id is null || !id.Contains('-')) continue;
            var status = p.TryGetProperty("status", out var st) ? st.GetString() : null;
            var disabled = p.TryGetProperty("trading_disabled", out var td) && td.ValueKind == JsonValueKind.True;
            var isOnline = string.Equals(status, "online", StringComparison.OrdinalIgnoreCase) && !disabled;
            list.Add(new ProductInfo(
                new Symbol(id),
                id,
                p.GetProperty("base_currency").GetString() ?? id[..id.IndexOf('-')],
                p.GetProperty("quote_currency").GetString() ?? id[(id.IndexOf('-') + 1)..],
                isOnline,
                ParseDecimal(p, "quote_increment"),
                ParseDecimal(p, "base_increment"),
                null,
                null));
        }
        return list;
    }

    /// <summary>GET /products/{id}/stats for each product — 24h base volume × last price = 24h quote volume.</summary>
    public async Task<List<ProductInfo>> EnrichWithStatsAsync(IEnumerable<ProductInfo> products, int maxConcurrency, CancellationToken ct)
    {
        var results = new List<ProductInfo>();
        var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = products.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = await GetJsonAsync($"products/{p.ProviderProductId}/stats", ct).ConfigureAwait(false);
                var root = doc.RootElement;
                var volume = ParseDecimal(root, "volume");
                var last = ParseDecimal(root, "last");
                return p with { Volume24hQuote = volume * last, LastPrice = last };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Stats unavailable for {Product}; excluded from universe ranking", p.ProviderProductId);
                return p with { Volume24hQuote = null };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var t in tasks) results.Add(await t.ConfigureAwait(false));
        return results;
    }

    /// <summary>
    /// GET /products/{id}/candles. Response rows are [time, low, high, open, close, volume], newest first, max 300 per request.
    /// Pages backwards from <paramref name="to"/> until <paramref name="from"/>. Returns ascending closed candles.
    /// Quote volume is approximated as volume × typical price (the exchange does not provide it historically).
    /// </summary>
    public async Task<List<Candle>> GetCandlesAsync(Symbol symbol, Timeframe tf, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var gran = Granularity(tf) ?? throw new NotSupportedException($"Coinbase does not serve {tf} candles.");
        var result = new SortedDictionary<long, Candle>();
        var pageEnd = tf.BucketStart(to);
        var floor = tf.BucketStart(from);
        var pages = 0;
        while (pageEnd > floor && pages < 20)
        {
            var pageStart = pageEnd - tf.Duration() * MaxCandlesPerRequest;
            if (pageStart < floor) pageStart = floor;
            var url = $"products/{symbol.Value}/candles?granularity={gran}&start={pageStart.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&end={pageEnd.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var t = row[0].GetInt64();
                var low = row[1].GetDecimal();
                var high = row[2].GetDecimal();
                var open = row[3].GetDecimal();
                var close = row[4].GetDecimal();
                var volume = row[5].GetDecimal();
                var openTime = DateTimeOffset.FromUnixTimeSeconds(t);
                if (openTime < floor || openTime >= tf.BucketStart(to)) continue; // exclude the still-forming bucket
                var typical = (high + low + close) / 3m;
                result[t] = new Candle(symbol, tf, openTime, open, high, low, close, volume, volume * typical, 0m, 0m, 0, CandleSource.Historical);
                count++;
            }
            pages++;
            if (count == 0) break;
            pageEnd = pageStart;
        }
        return result.Values.ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken ct)
    {
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        _metrics.RestRequest();
        try
        {
            using var resp = await _http.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"Coinbase REST {relative} returned {(int)resp.StatusCode}: {Truncate(body)}", null, resp.StatusCode);
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _metrics.RestError();
            throw;
        }
    }

    private static decimal ParseDecimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m,
            _ => 0m,
        };
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
