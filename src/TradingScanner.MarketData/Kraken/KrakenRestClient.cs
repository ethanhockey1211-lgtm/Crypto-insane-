using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.Universe;

namespace TradingScanner.MarketData.Kraken;

/// <summary>Kraken public spot catalog, bulk 24h ticker, and closed OHLC history. No account credentials or orders.</summary>
public sealed class KrakenRestClient
{
    private static readonly HashSet<string> FiatBases = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "EUR", "GBP", "CAD", "AUD", "CHF", "JPY", "NZD", "BRL", "ARS", "MXN", "AED", "TRY", "PLN", "SGD", "HKD", "CNY", "KRW", "INR", "ZAR" };
    private readonly HttpClient _http;
    private readonly KrakenOptions _options;
    private readonly MarketDataOptions _marketOptions;
    private readonly RequestRateLimiter _limiter;
    private readonly MarketDataMetrics _metrics;
    private readonly ILogger<KrakenRestClient> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private IReadOnlyDictionary<Symbol, ProductInfo> _products = new Dictionary<Symbol, ProductInfo>();

    public KrakenRestClient(HttpClient http, KrakenOptions options, MarketDataOptions marketOptions,
        RequestRateLimiter limiter, MarketDataMetrics metrics, ILogger<KrakenRestClient> logger, TimeProvider? time = null)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.RestUrl.TrimEnd('/') + "/");
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        _options = options;
        _marketOptions = marketOptions;
        _limiter = limiter;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static int? IntervalMinutes(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => 1, Timeframe.M5 => 5, Timeframe.M15 => 15,
        Timeframe.M30 => 30, Timeframe.H1 => 60, Timeframe.H4 => 240, _ => null,
    };

    public async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct)
    {
        await _catalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = "0/public/AssetPairs?aclass_base=currency";
            if (!string.IsNullOrWhiteSpace(_options.CountryCode)) path += "&country_code=" + Uri.EscapeDataString(_options.CountryCode.Trim().ToUpperInvariant());
            using var catalog = await GetJsonAsync(path, ct).ConfigureAwait(false);
            using var tickers = await GetJsonAsync("0/public/Ticker", ct).ConfigureAwait(false);
            var stats = tickers.RootElement.GetProperty("result");
            var products = new Dictionary<Symbol, ProductInfo>();
            var excludedAssets = _options.NormalizedExcludedAssets().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog.RootElement.GetProperty("result").EnumerateObject())
            {
                var p = entry.Value;
                if (Text(p, "aclass_base") != "currency" || Text(p, "aclass_quote") != "currency" ||
                    !string.Equals(Text(p, "status"), "online", StringComparison.OrdinalIgnoreCase) ||
                    Text(p, "wsname") is not { } wsname || entry.Name.Contains('.')) continue;
                var symbol = KrakenSymbols.FromWebSocket(wsname);
                var currencies = symbol.Value.Split('-');
                if (!string.Equals(currencies[1], _marketOptions.QuoteCurrency, StringComparison.OrdinalIgnoreCase) ||
                    FiatBases.Contains(currencies[0]) || excludedAssets.Contains(currencies[0])) continue;
                decimal? volumeQuote = null, last = null;
                if (stats.TryGetProperty(entry.Name, out var ticker) ||
                    (Text(p, "altname") is { } alt && stats.TryGetProperty(alt, out ticker)))
                {
                    last = Decimal(ticker.GetProperty("c")[0]);
                    // Index 1 is the rolling 24h window. Quote turnover is base volume × actual 24h VWAP.
                    volumeQuote = Decimal(ticker.GetProperty("v")[1]) * Decimal(ticker.GetProperty("p")[1]);
                }
                var increment = p.TryGetProperty("tick_size", out var tick) ? Decimal(tick) : Precision(p.GetProperty("pair_decimals").GetInt32());
                products[symbol] = new ProductInfo(symbol, entry.Name, currencies[0], currencies[1], true,
                    increment, Precision(p.GetProperty("lot_decimals").GetInt32()), volumeQuote, last);
            }
            Volatile.Write(ref _products, products);
            _logger.LogInformation("Kraken catalog: {Count} online crypto {Quote} pairs; country {Country}; app exclusions {Excluded}",
                products.Count, _marketOptions.QuoteCurrency,
                string.IsNullOrWhiteSpace(_options.CountryCode) ? "global" : _options.CountryCode.Trim().ToUpperInvariant(),
                string.Join(",", excludedAssets));
            return products.Values.ToArray();
        }
        finally { _catalogGate.Release(); }
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var interval = IntervalMinutes(timeframe) ?? throw new NotSupportedException($"Kraken does not serve {timeframe} candles.");
        if (from >= to) return [];
        if (!Volatile.Read(ref _products).TryGetValue(symbol, out var product))
        {
            await GetProductsAsync(ct).ConfigureAwait(false);
            if (!Volatile.Read(ref _products).TryGetValue(symbol, out product))
                throw new InvalidOperationException($"{symbol} is not an online Kraken crypto {_marketOptions.QuoteCurrency} pair.");
        }
        var floor = timeframe.BucketStart(from);
        var end = timeframe.BucketStart(to < _time.GetUtcNow() ? to : _time.GetUtcNow());
        using var doc = await GetJsonAsync($"0/public/OHLC?pair={Uri.EscapeDataString(product.ProviderProductId)}&interval={interval}&since={floor.ToUnixTimeSeconds()}", ct).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("result");
        var rows = result.EnumerateObject().FirstOrDefault(p => p.Name != "last" && p.Value.ValueKind == JsonValueKind.Array).Value;
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"Kraken returned no OHLC array for {symbol}.");
        var candles = new SortedDictionary<DateTimeOffset, Candle>();
        // Kraken only returns its latest 720 entries, regardless of since; paging cannot retrieve older bars.
        // The final entry is ALWAYS uncommitted, even if the requested end is in the future.
        for (var i = 0; i < rows.GetArrayLength() - 1; i++)
        {
            var row = rows[i];
            var openTime = DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64());
            if (openTime < from || openTime >= end) continue;
            var volume = Decimal(row[6]);
            candles[openTime] = new Candle(symbol, timeframe, openTime, Decimal(row[1]), Decimal(row[2]), Decimal(row[3]), Decimal(row[4]),
                volume, volume * Decimal(row[5]), 0m, 0m, row[7].GetInt32(), CandleSource.Historical);
        }
        return candles.Values.ToArray();
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _limiter.WaitAsync(ct).ConfigureAwait(false);
            _metrics.RestRequest();
            try
            {
                using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var errors = doc.RootElement.GetProperty("error");
                if (errors.GetArrayLength() > 0)
                {
                    var message = string.Join("; ", errors.EnumerateArray().Select(e => e.GetString()));
                    doc.Dispose();
                    if (message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase) || message.Contains("Throttled", StringComparison.OrdinalIgnoreCase))
                        throw new HttpRequestException("Kraken: " + message, null, HttpStatusCode.TooManyRequests);
                    throw new InvalidOperationException("Kraken: " + message);
                }
                return doc;
            }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode is null or HttpStatusCode.TooManyRequests || (int)ex.StatusCode >= 500))
            {
                _metrics.RestError();
                _logger.LogWarning("Kraken REST transient error; retry {Attempt}: {Message}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), _time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics.RestError();
                throw;
            }
        }
    }

    internal static decimal Decimal(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? value.GetDecimal() : decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? field.GetString() : null;
    private static decimal Precision(int decimals)
    {
        if (decimals is < 0 or > 28) throw new FormatException("Invalid Kraken decimal precision.");
        var increment = 1m;
        for (var i = 0; i < decimals; i++) increment /= 10m;
        return increment;
    }
}
