using System.Net;
using System.Text.Json;
using TradingScanner.Api.PrizePicks;

namespace TradingScanner.Tests.PrizePicks;

public class NhlPickHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T15:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static string Results => JsonSerializer.Serialize(new { data = Enumerable.Range(1, 12)
        .Select(i => new { gameDate = Now.AddDays(-i).ToString("yyyy-MM-dd"), shots = i % 5, goals = 0, assists = 1, points = 1, blockedShots = 0, saves = 24, gamesStarted = i <= 10 ? 1 : 0 }) });
    private sealed class Handler(bool ambiguous = false, bool failOtherRoster = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (failOtherRoster && path.Contains("/roster/MTL")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = path.Contains("/schedule/") ? """
              {"gameWeek":[{"date":"2026-11-11","games":[{"startTimeUTC":"2026-11-12T01:00:00Z","awayTeam":{"placeName":{"default":"Edmonton"},"commonName":{"default":"Oilers"},"abbrev":"EDM"},"homeTeam":{"placeName":{"default":"Montréal"},"commonName":{"default":"Canadiens"},"abbrev":"MTL"}}]}]}
              """ : path.Contains("/roster/") ? path.Contains("EDM") || ambiguous ? """
              {"forwards":[{"id":8478402,"firstName":{"default":"Player"},"lastName":{"default":"One"}}],"defensemen":[],"goalies":[]}
              """ : "{\"forwards\":[],\"defensemen\":[],\"goalies\":[]}" : Results;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    [Fact]
    public async Task Exact_NHL_game_and_roster_match_enriches_real_PrizePicks_line()
    {
        var handler = new Handler(); var p = new PropLine("game", "icehockey_nhl", "Edmonton Oilers at Montreal Canadiens", Now.AddHours(10), "Player One", "player_shots_on_goal", 2.5, "prizepicks", Now, null, null);
        var lines = await new NhlPickHistory(new HttpClient(handler), new Clock()).EnrichAsync([p], default);
        Assert.Equal("EDM", lines[0].Team); Assert.Equal(12, lines[0].History!.Length);
        Assert.Contains(lines[0].History!, s => s.Value == 0);
        Assert.Contains(handler.Requests, r => r.Host == "api.nhle.com" && Uri.UnescapeDataString(r.Query).Contains("playerId=8478402 and seasonId=20262027 and gameTypeId=2"));
    }
    [Theory]
    [InlineData(true, "Edmonton Oilers at Montreal Canadiens")]
    [InlineData(false, "Other team at Montreal Canadiens")]
    public async Task Ambiguous_player_or_unmatched_game_has_no_history(bool ambiguous, string matchup)
    {
        var handler = new Handler(ambiguous); var p = new PropLine("game", "icehockey_nhl", matchup, Now.AddHours(10), "Player One", "player_shots_on_goal", 2.5, "prizepicks", Now, null, null);
        var lines = await new NhlPickHistory(new HttpClient(handler), new Clock()).EnrichAsync([p], default);
        Assert.Null(lines[0].History); Assert.DoesNotContain(handler.Requests, r => r.Host == "api.nhle.com");
    }
    [Fact]
    public async Task A_missing_roster_is_not_retried_for_every_line_or_treated_as_a_unique_match()
    {
        var handler = new Handler(failOtherRoster: true);
        var p = new PropLine("game", "icehockey_nhl", "Edmonton Oilers at Montreal Canadiens", Now.AddHours(10), "Player One", "player_shots_on_goal", 2.5, "prizepicks", Now, null, null);
        var result = await new NhlPickHistory(new HttpClient(handler), new Clock()).EnrichAsync(Enumerable.Repeat(p, 8).ToArray(), default);
        Assert.All(result, line => Assert.Null(line.History));
        Assert.Single(handler.Requests, r => r.AbsolutePath.Contains("/roster/MTL"));
        Assert.DoesNotContain(handler.Requests, r => r.Host == "api.nhle.com");
    }
    [Fact]
    public void Samples_preserve_zeroes_and_reject_old_future_or_missing_stats()
    {
        using var json = JsonDocument.Parse(Results);
        Assert.Equal(12, NhlPickHistory.ParseSample(json.RootElement, "player_shots_on_goal", Now).Length);
        Assert.All(NhlPickHistory.ParseSample(json.RootElement, "player_blocked_shots", Now), s => Assert.Equal(0, s.Value));
        Assert.Empty(NhlPickHistory.ParseSample(json.RootElement, "player_shots_on_goal", Now.AddDays(40)));
        Assert.Empty(NhlPickHistory.ParseSample(json.RootElement, "player_shots_on_goal", Now.AddDays(-30)));
        Assert.Empty(NhlPickHistory.ParseSample(json.RootElement, "fantasy_score", Now));
        using var missing = JsonDocument.Parse(Results.Replace("\"shots\"", "\"absent\""));
        Assert.Empty(NhlPickHistory.ParseSample(missing.RootElement, "player_shots_on_goal", Now));
    }
    [Fact]
    public void Goalie_saves_require_a_start()
    { using var json = JsonDocument.Parse(Results); Assert.Equal(10, NhlPickHistory.ParseSample(json.RootElement, "player_total_saves", Now).Length); }
}
