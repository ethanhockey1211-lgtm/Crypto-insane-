using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingScanner.Api.Stocks;
using Xunit;

namespace TradingScanner.Tests.Stocks;

public class AlpacaStockScannerTests
{
    private const string Key = "test-provider-key", Secret = "test-provider-secret", Token = "test-stock-access-code-at-least-24";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T14:00:30Z");
    private static StockScannerOptions Config() => new() { ApiKey = Key, ApiSecret = Secret, AccessToken = Token };
    private static string Bar(string at = "2026-09-09T13:30:00Z", double price = 100) =>
        JsonSerializer.Serialize(new { t = at, o = price, h = price + 1, l = price - 1, c = price, v = 20, vw = price });
    private static string Bars(string values, string? token = null) =>
        $$"""{"bars":{"NVDA":[{{values}}]},"next_page_token":{{JsonSerializer.Serialize(token)}}} """;
    private const string Snapshot = """
        {"NVDA":{"latestTrade":{"t":"2026-09-09T14:00:29Z","p":105},
        "latestQuote":{"t":"2026-09-09T14:00:29Z","bp":104.99,"ap":105.01,"bs":2,"as":3},
        "dailyBar":{"t":"2026-09-09T04:00:00Z","v":900},
        "prevDailyBar":{"t":"2026-09-08T04:00:00Z","c":99}},
        "UNREQUESTED":{"latestTrade":{"t":"2026-09-09T14:00:29Z","p":999}}}
        """;

    private sealed record Request(Uri Uri, string? Key, string? Secret, bool AccessHeader);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(new(request.RequestUri!, request.Headers.TryGetValues("APCA-API-KEY-ID", out var key) ? key.Single() : null,
                request.Headers.TryGetValues("APCA-API-SECRET-KEY", out var secret) ? secret.Single() : null,
                request.Headers.Contains(StockEndpoints.AccessHeader)));
            return respond?.Invoke(request, ct) ?? Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot : Bars(Bar())));
        }
    }
    private class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }
    private sealed class ShortTimeoutClock(DateTimeOffset at) : Clock(at)
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(20), period);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static AlpacaStockScanner Scanner(Handler handler, Clock? clock = null, StockScannerOptions? options = null) =>
        new(new HttpClient(handler), Options.Create(options ?? Config()), clock ?? new Clock(Now));

    [Fact]
    public async Task Fetches_bulk_snapshots_and_every_page_with_secret_headers_and_closed_session_bars_only()
    {
        var page = 0;
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot :
            ++page == 1 ? Bars(string.Join(',', Bar("2026-09-08T13:30:00Z"), Bar("2026-09-09T13:29:00Z"), Bar()), "page/+ token")
                : Bars(string.Join(',', Bar("2026-09-09T13:32:00Z", 102), Bar("2026-09-09T14:00:00Z"), Bar("2026-09-09T20:00:00Z"))))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("nvda, aapl", Token, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("ready", result.Body.Status);
        Assert.Equal(Now, result.Body.AsOf);
        Assert.Equal(["AAPL", "NVDA"], result.Body.Rows.Select(row => row.Ticker));
        var nvda = result.Body.Rows[1];
        Assert.Equal([100d, 102d], nvda.Bars.Select(bar => bar.Close));
        Assert.True(nvda.HistoryComplete); // Sparse IEX minutes are legitimate; all API pages are present.
        Assert.Equal(105, nvda.LatestTrade!.Price);
        Assert.Equal(104.99, nvda.LatestQuote!.Bid);
        Assert.Equal(2, nvda.LatestQuote.BidSize);
        Assert.Equal(99, nvda.PreviousClose);
        Assert.Equal(900, nvda.DayVolume);
        var absent = result.Body.Rows[0];
        Assert.Empty(absent.Bars);
        Assert.False(absent.HistoryComplete);
        Assert.Null(absent.LatestTrade);
        Assert.Null(absent.LatestQuote);
        Assert.Null(absent.PreviousClose);
        Assert.Null(absent.DayVolume);
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal("https", request.Uri.Scheme); Assert.Equal("data.alpaca.markets", request.Uri.Host);
            Assert.Equal(Key, request.Key); Assert.Equal(Secret, request.Secret); Assert.False(request.AccessHeader);
            Assert.Contains("symbols=AAPL%2CNVDA", request.Uri.Query);
            Assert.Contains("feed=iex", request.Uri.Query);
        });
        Assert.Contains("timeframe=1Min", requests[1].Uri.Query);
        Assert.Contains("adjustment=raw", requests[1].Uri.Query);
        Assert.Contains("sort=asc", requests[1].Uri.Query);
        Assert.Contains("limit=10000", requests[1].Uri.Query);
        Assert.Contains("start=2026-09-09T13%3A30", requests[1].Uri.Query);
        Assert.Contains("page_token=page%2F%2B%20token", requests[2].Uri.Query);
        var serialized = JsonSerializer.Serialize(result.Body);
        Assert.DoesNotContain(Key, serialized); Assert.DoesNotContain(Secret, serialized); Assert.DoesNotContain(Token, serialized);
    }

    [Theory]
    [InlineData("2026-09-09T13:29:59Z")]
    [InlineData("2026-09-09T13:30:59Z")]
    [InlineData("2026-09-12T15:00:00Z")]
    public async Task Before_first_completed_regular_bar_and_weekends_do_not_fetch_or_invent_previous_day_history(string at)
    {
        var handler = new Handler();
        using var scanner = Scanner(handler, new Clock(DateTimeOffset.Parse(at)));
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Equal(200, result.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Body.Rows[0].Bars);
        Assert.False(result.Body.Rows[0].HistoryComplete);
    }

    [Theory]
    [InlineData("2026-01-08T15:00:00Z", "2026-01-08T14%3A30")]
    [InlineData("2026-09-09T14:00:00Z", "2026-09-09T13%3A30")]
    public async Task Session_start_tracks_New_York_daylight_saving_time(string at, string start)
    {
        var handler = new Handler();
        using var scanner = Scanner(handler, new Clock(DateTimeOffset.Parse(at)));
        await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Contains("start=" + start, handler.Requests.Last().Uri.Query);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    public async Task Pagination_is_bounded_and_a_truncated_or_repeated_token_never_claims_complete_history(bool repeatToken, int expectedRequests)
    {
        var page = 0;
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot : Bars(Bar(), repeatToken ? "same" : $"p{++page}"))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Equal(expectedRequests, handler.Requests.Count); // One snapshot plus at most three pages.
        Assert.Single(result.Body.Rows[0].Bars); // Overlapping page buckets are deduplicated.
        Assert.False(result.Body.Rows[0].HistoryComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_minutes_across_pages_are_deduplicated_but_conflicting_values_invalidate_history(bool conflict)
    {
        var page = 0;
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot :
            ++page == 1 ? Bars(Bar(), "second-page") : Bars(Bar(price: conflict ? 102 : 100)))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(row.Bars);
        Assert.Equal(!conflict, row.HistoryComplete);
    }

    [Theory]
    [InlineData("2026-09-09T13:30:00")]
    [InlineData("2026-09-09 13:30:00Z")]
    [InlineData("09/09/2026 13:30:00 +00:00")]
    [InlineData("2026-09-09T13:30:00+0000")]
    [InlineData("2026-09-09T13:30:00.0000000000Z")]
    public async Task Non_ISO_or_timezone_less_timestamps_cannot_become_valid_market_records(string at)
    {
        var snapshot = JsonSerializer.Serialize(new { NVDA = new {
            latestTrade = new { t = at, p = 105 }, latestQuote = new { t = at, bp = 104, ap = 105, bs = 1, @as = 1 },
            dailyBar = new { t = at, v = 900 } } });
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? snapshot : Bars(Bar() + "," + Bar(at)))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Single(row.Bars);
        Assert.False(row.HistoryComplete);
        Assert.Null(row.LatestTrade);
        Assert.Null(row.LatestQuote);
        Assert.Null(row.DayVolume);
    }

    [Theory]
    [InlineData("2026-09-09T14:00:29.123456789Z", "2026-09-09T13:30:00.000000000Z")]
    [InlineData("2026-09-09T10:00:29.123456789-04:00", "2026-09-09T09:30:00.000000000-04:00")]
    public async Task Explicit_ISO_offsets_and_Alpaca_nanoseconds_are_supported_without_rounding_forward(string at, string barAt)
    {
        var snapshot = Snapshot.Replace("2026-09-09T14:00:29Z", at);
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? snapshot : Bars(Bar(barAt)))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        var expected = DateTimeOffset.Parse("2026-09-09T14:00:29.1234567Z");
        Assert.Equal(expected, row.LatestTrade!.At);
        Assert.Equal(expected, row.LatestQuote!.At);
        Assert.Equal(DateTimeOffset.Parse("2026-09-09T13:30:00Z"), Assert.Single(row.Bars).At);
        Assert.True(row.HistoryComplete);
    }

    [Fact]
    public async Task Sub_tick_fractional_minute_bars_cannot_be_truncated_into_valid_exact_minutes()
    {
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot :
            Bars(Bar() + "," + Bar("2026-09-09T13:31:00.000000001Z")))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Single(row.Bars);
        Assert.False(row.HistoryComplete);
    }

    [Fact]
    public async Task After_close_retains_only_current_day_regular_session_minutes()
    {
        var open = DateTimeOffset.Parse("2026-09-09T13:30:00Z");
        var values = string.Join(',', Enumerable.Range(-2, 405).Select(minute => Bar(open.AddMinutes(minute).ToString("O"))));
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot : Bars(values))));
        using var scanner = Scanner(handler, new Clock(DateTimeOffset.Parse("2026-09-09T21:00:00Z")));
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Equal(390, row.Bars.Count);
        Assert.Equal(open, row.Bars[0].At);
        Assert.Equal(open.AddMinutes(389), row.Bars[^1].At);
        Assert.Contains("end=2026-09-09T20%3A00", handler.Requests.Last().Uri.Query);
        Assert.True(row.HistoryComplete);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"bars\":[]}")]
    [InlineData("{\"bars\":{},\"next_page_token\":123}")]
    public async Task Malformed_history_payloads_fail_closed_without_echoing_provider_content(string body)
    {
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot : body)));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("error", result.Body.Status);
        Assert.Empty(result.Body.Rows);
        Assert.DoesNotContain(body, JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Invalid_bar_or_nonfinite_snapshot_values_are_not_published_as_prices()
    {
        var malformedSnapshot = """{"NVDA":{"latestTrade":{"t":"2026-09-09T14:00:29Z","p":1e309},"latestQuote":{"t":"2026-09-09T14:00:29Z","bp":106,"ap":105,"bs":1,"as":1},"dailyBar":{"t":"2026-09-08T04:00:00Z","v":900}}} """;
        var bad = Bar("2026-09-09T13:31:00Z").Replace("\"h\":101", "\"h\":-1");
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? malformedSnapshot : Bars(Bar() + "," + bad))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Single(row.Bars);
        Assert.False(row.HistoryComplete);
        Assert.Null(row.LatestTrade);
        Assert.Null(row.LatestQuote);
        Assert.Null(row.DayVolume);
        Assert.DoesNotContain("Infinity", JsonSerializer.Serialize(result.Body));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("NVDA,NVDA")] [InlineData("nvda,NVDA")]
    [InlineData("BTC-USD")] [InlineData("NASDAQ:NVDA")] [InlineData("NVDA,,AAPL")] [InlineData("NVDA&feed=sip")]
    public async Task Invalid_or_duplicate_symbols_are_rejected_without_upstream_calls(string? symbols)
    {
        var handler = new Handler(); using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync(symbols, Token, CancellationToken.None);
        Assert.Equal(400, result.StatusCode); Assert.Empty(handler.Requests); Assert.Empty(result.Body.Rows);
    }

    [Fact]
    public async Task More_than_forty_symbols_and_invalid_access_are_rejected_before_upstream()
    {
        var handler = new Handler(); using var scanner = Scanner(handler);
        Assert.Equal(400, (await scanner.ScanAsync(string.Join(',', Enumerable.Range(0, 41).Select(i => "A" + (char)('A' + i / 26) + (char)('A' + i % 26))), Token, CancellationToken.None)).StatusCode);
        Assert.Equal(401, (await scanner.ScanAsync("NVDA", "incorrect", CancellationToken.None)).StatusCode);
        Assert.Equal(401, (await scanner.ScanAsync("NVDA", null, CancellationToken.None)).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("key")] [InlineData("secret")] [InlineData("token")] [InlineData("short-token")]
    public async Task All_three_server_settings_are_required_and_public_status_never_exposes_values(string missing)
    {
        var options = Config();
        if (missing == "key") options.ApiKey = "";
        if (missing == "secret") options.ApiSecret = "";
        if (missing == "token") options.AccessToken = "";
        if (missing == "short-token") options.AccessToken = "too-short";
        var handler = new Handler(); using var scanner = Scanner(handler, options: options);
        Assert.False(scanner.GetStatus().Configured);
        Assert.True(scanner.GetStatus().AccessRequired);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Equal(503, result.StatusCode); Assert.Equal("not-configured", result.Body.Status); Assert.Empty(handler.Requests);
        var json = JsonSerializer.Serialize(scanner.GetStatus());
        Assert.DoesNotContain(Key, json); Assert.DoesNotContain(Secret, json); Assert.DoesNotContain(Token, json);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("secret")]
    [InlineData("trimmed-short")]
    public async Task Browser_access_code_must_be_distinct_from_provider_credentials_and_long_after_trimming(string invalid)
    {
        var options = Config();
        if (invalid == "key") options.ApiKey = " " + Token + " ";
        if (invalid == "secret") options.ApiSecret = Token;
        if (invalid == "trimmed-short") options.AccessToken = "        short-code          ";
        var handler = new Handler(); using var scanner = Scanner(handler, options: options);
        Assert.False(scanner.GetStatus().Configured);
        Assert.Equal(503, (await scanner.ScanAsync("NVDA", Token, CancellationToken.None)).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Future_quotes_and_trades_are_unavailable_and_future_bars_are_never_published()
    {
        var future = Snapshot.Replace("2026-09-09T14:00:29Z", "2026-09-09T14:05:00Z");
        var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots")
            ? future : Bars(Bar() + "," + Bar("2026-09-09T14:05:00Z")))));
        using var scanner = Scanner(handler);
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        var row = Assert.Single(result.Body.Rows);
        Assert.Null(row.LatestTrade); Assert.Null(row.LatestQuote);
        Assert.Single(row.Bars);
        Assert.True(row.Bars[0].At.AddMinutes(1) <= Now);
    }

    [Theory]
    [InlineData(401, 502)] [InlineData(403, 502)] [InlineData(429, 429)] [InlineData(500, 502)] [InlineData(302, 502)]
    public async Task Upstream_errors_never_reflect_provider_body_or_return_stale_rows(int upstream, int expected)
    {
        var failing = false;
        var handler = new Handler((request, _) => Task.FromResult(failing ? Json(Key + Secret + Token, (HttpStatusCode)upstream)
            : Json(request.RequestUri!.AbsolutePath.EndsWith("snapshots") ? Snapshot : Bars(Bar()))));
        var clock = new Clock(Now); using var scanner = Scanner(handler, clock);
        Assert.Equal(200, (await scanner.ScanAsync("NVDA", Token, CancellationToken.None)).StatusCode);
        clock.At += TimeSpan.FromSeconds(30); failing = true;
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        Assert.Equal(expected, result.StatusCode); Assert.Equal("error", result.Body.Status); Assert.Empty(result.Body.Rows);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.DoesNotContain(Key, json); Assert.DoesNotContain(Secret, json); Assert.DoesNotContain(Token, json);
    }

    [Fact]
    public async Task Cache_normalizes_symbol_sets_expires_at_thirty_seconds_and_holds_at_most_eight_sets()
    {
        var handler = new Handler(); var clock = new Clock(Now); using var scanner = Scanner(handler, clock);
        var first = await scanner.ScanAsync("NVDA,AAPL", Token, CancellationToken.None);
        var cached = await scanner.ScanAsync(" aapl,nvda ", Token, CancellationToken.None);
        Assert.Same(first.Body, cached.Body); Assert.Equal(2, handler.Requests.Count);
        clock.At += TimeSpan.FromSeconds(29);
        Assert.Same(first.Body, (await scanner.ScanAsync("NVDA,AAPL", Token, CancellationToken.None)).Body);
        clock.At += TimeSpan.FromSeconds(1);
        Assert.NotSame(first.Body, (await scanner.ScanAsync("NVDA,AAPL", Token, CancellationToken.None)).Body);
        Assert.Equal(4, handler.Requests.Count);
        for (var i = 0; i < 8; i++) await scanner.ScanAsync("Z" + (char)('A' + i), Token, CancellationToken.None);
        Assert.Equal(20, handler.Requests.Count);
        await scanner.ScanAsync("NVDA,AAPL", Token, CancellationToken.None);
        Assert.Equal(22, handler.Requests.Count);
    }

    [Fact]
    public async Task Simultaneous_cache_misses_for_one_set_share_a_single_fetch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("snapshots")) { started.TrySetResult(); await release.Task.WaitAsync(ct); return Json(Snapshot); }
            return Json(Bars(Bar()));
        });
        using var scanner = Scanner(handler);
        var first = scanner.ScanAsync("NVDA", Token, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = scanner.ScanAsync("nvda", Token, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(results[0].Body, results[1].Body); Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Distinct_symbol_set_churn_cannot_exceed_the_reserved_provider_quota()
    {
        var handler = new Handler(); using var scanner = Scanner(handler);
        StockScanResult? last = null;
        for (var i = 0; i < 91; i++) last = await scanner.ScanAsync("A" + (char)('A' + i / 26) + (char)('A' + i % 26), Token, CancellationToken.None);
        Assert.Equal(180, handler.Requests.Count);
        Assert.Equal(429, last!.StatusCode); Assert.Empty(last.Body.Rows);
    }

    [Fact]
    public async Task Linked_timeout_is_generic_and_client_cancellation_is_propagated()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        using var scanner = Scanner(handler, new ShortTimeoutClock(Now));
        var result = await scanner.ScanAsync("NVDA", Token, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(502, result.StatusCode); Assert.Empty(result.Body.Rows);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync("NVDA", Token, cancelled.Token));
    }

    [Fact]
    public async Task Real_endpoint_contract_is_public_status_but_header_protected_scan_with_no_store()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Stocks:ApiKey"] = Key, ["Stocks:ApiSecret"] = Secret, ["Stocks:AccessToken"] = Token });
        builder.Services.AddStockScanner(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(new Clock(Now));
        var handler = new Handler();
        builder.Services.AddHttpClient(StockEndpoints.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("api", limit =>
        { limit.PermitLimit = 100; limit.Window = TimeSpan.FromMinutes(1); }));
        await using var app = builder.Build();
        app.UseRouting(); app.UseRateLimiter(); app.MapStockEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var status = await client.GetFromJsonAsync<StockScannerStatus>("/api/stocks/status");
        Assert.True(status!.Configured); Assert.Equal(40, status.MaxSymbols); Assert.Equal(30, status.RefreshSeconds);
        Assert.Empty(handler.Requests);
        using var unauthorized = await client.GetAsync("/api/stocks/scan?symbols=NVDA");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.True(unauthorized.Headers.CacheControl!.NoStore);
        Assert.True(unauthorized.Headers.CacheControl.Private);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stocks/scan?symbols=NVDA&accessToken=" + Token)).StatusCode);
        client.DefaultRequestHeaders.Add(StockEndpoints.AccessHeader, Token);
        using var response = await client.GetAsync("/api/stocks/scan?symbols=NVDA");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.True(response.Headers.CacheControl.Private);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"provider\":\"Alpaca\"", json); Assert.Contains("\"feed\":\"iex\"", json);
        Assert.DoesNotContain(Key, json); Assert.DoesNotContain(Secret, json); Assert.DoesNotContain(Token, json);
    }
}
