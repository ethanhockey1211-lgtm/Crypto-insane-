using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingScanner.Api.PrizePicks;

namespace TradingScanner.Tests.PrizePicks;

public class PrizePicksFeedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T15:00:00Z");
    private const string Token = "fixture-access-code-24-characters";
    private class Clock : TimeProvider { public DateTimeOffset At = Now; public override DateTimeOffset GetUtcNow() => At; }
    private class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            Assert.False(request.Headers.Contains("X-PrizePicks-Access-Token"));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("/odds") ? Odds() : """[{"id":"game1","commence_time":"2026-09-11T23:00:00Z"}]""", Encoding.UTF8, "application/json") });
        }
    }
    private static string Odds(string timestamp = "2026-09-11T14:59:00Z", string stat = "player_points") => $$"""
        {"id":"game1","commence_time":"2026-09-11T23:00:00Z","away_team":"A","home_team":"B","bookmakers":[
        {"key":"prizepicks","markets":[{"key":"{{stat}}","last_update":"{{timestamp}}","outcomes":[{"name":"Over","description":"Player A","point":24.5,"price":1.01},{"name":"Under","description":"Player A","point":24.5,"price":1.01}]}]},
        {"key":"fanduel","markets":[{"key":"{{stat}}","last_update":"{{timestamp}}","outcomes":[{"name":"Over","description":"Player A","point":24.5,"price":1.5},{"name":"Under","description":"Player A","point":24.5,"price":2.5}]}]}]}
        """;
    private static PrizePicksFeed Feed(Handler handler, Clock? clock = null, PrizePicksOptions? options = null) => new(new HttpClient(handler), Options.Create(options ?? new PrizePicksOptions { ApiKey = "fixture-provider-key", AccessToken = Token }), clock ?? new Clock());

    [Fact]
    public async Task Requires_configuration_and_access_before_spending_provider_quota()
    {
        var handler = new Handler();
        using var missing = Feed(handler, options: new());
        Assert.Equal(503, (await missing.ScanAsync("basketball_nba", Token, default)).StatusCode);
        using var configured = Feed(handler);
        Assert.Equal(401, (await configured.ScanAsync("basketball_nba", "wrong", default)).StatusCode);
        Assert.Equal(400, (await configured.ScanAsync("../../unknown", Token, default)).StatusCode);
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Fetches_today_then_event_odds_and_shares_cache_only_after_authorization()
    {
        var handler = new Handler(); var clock = new Clock(); using var feed = Feed(handler, clock);
        var response = await feed.ScanAsync("basketball_nba", Token, default);
        Assert.Equal(200, response.StatusCode); Assert.Equal("2026-09-11", response.Body.Date);
        Assert.Equal(2, response.Body.Lines.Length); Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("commenceTimeFrom=2026-09-11T05:00:00Z", handler.Requests[0].Query);
        Assert.Contains("commenceTimeTo=2026-09-12T04:59:59Z", handler.Requests[0].Query);
        Assert.Contains("bookmakers=prizepicks,draftkings,fanduel,betmgm", handler.Requests[1].Query);
        Assert.Contains("oddsFormat=decimal", handler.Requests[1].Query);
        Assert.All(handler.Requests, uri => Assert.Equal("api.the-odds-api.com", uri.Host));
        await feed.ScanAsync("basketball_nba", Token, default);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(401, (await feed.ScanAsync("basketball_nba", "wrong", default)).StatusCode);
        clock.At = Now.AddMinutes(3); await feed.ScanAsync("basketball_nba", Token, default);
        Assert.Equal(4, handler.Requests.Count);
        var body = JsonSerializer.Serialize(response.Body);
        Assert.DoesNotContain("fixture-provider-key", body); Assert.DoesNotContain(Token, body);
    }
    [Fact]
    public void Parses_market_timestamps_and_ignores_PrizePicks_placeholder_prices()
    {
        using var json = JsonDocument.Parse(Odds());
        var lines = PrizePicksFeed.ParseLines(json.RootElement, "basketball_nba", Now);
        Assert.Null(lines[0].Over); Assert.Null(lines[0].Under);
        Assert.Equal(1.5, lines[1].Over); Assert.Equal(2.5, lines[1].Under);
        Assert.Equal("Player A", lines[0].Player);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T14:59:00Z"), lines[0].UpdatedAt);
    }
    [Theory]
    [InlineData("2026-09-11T14:54:59Z")]
    [InlineData("2026-09-11T15:02:00Z")]
    [InlineData("")]
    public void Does_not_use_old_missing_or_future_quotes(string timestamp)
    {
        using var json = JsonDocument.Parse(Odds(timestamp));
        Assert.Empty(PrizePicksFeed.ParseLines(json.RootElement, "basketball_nba", Now));
    }
    [Fact]
    public void Excludes_alternate_markets_and_started_games()
    {
        using var alternate = JsonDocument.Parse(Odds(stat: "player_points_alternate"));
        Assert.Empty(PrizePicksFeed.ParseLines(alternate.RootElement, "basketball_nba", Now));
        using var standard = JsonDocument.Parse(Odds());
        Assert.Empty(PrizePicksFeed.ParseLines(standard.RootElement, "basketball_nba", Now.AddHours(8)));
    }
    [Fact]
    public void Requires_both_PrizePicks_directions_before_offering_a_comparison()
    {
        var oneSided = Odds().Replace(",{\"name\":\"Under\",\"description\":\"Player A\",\"point\":24.5,\"price\":1.01}", "");
        using var json = JsonDocument.Parse(oneSided);
        Assert.DoesNotContain(PrizePicksFeed.ParseLines(json.RootElement, "basketball_nba", Now), p => p.Bookmaker == "prizepicks");
    }
    [Fact]
    public async Task Does_not_leak_provider_errors_or_repeatedly_spend_quota_after_failure()
    {
        var handler = new Handler { Status = HttpStatusCode.TooManyRequests }; using var feed = Feed(handler);
        var response = await feed.ScanAsync("basketball_nba", Token, default);
        Assert.Equal(503, response.StatusCode); Assert.Empty(response.Body.Lines);
        Assert.Contains("quota", response.Body.Message);
        Assert.DoesNotContain("apiKey", JsonSerializer.Serialize(response));
        await feed.ScanAsync("basketball_nba", Token, default); Assert.Single(handler.Requests);
    }
}
