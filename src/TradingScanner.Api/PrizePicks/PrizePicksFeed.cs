using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.PrizePicks;

public sealed class PrizePicksOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed record PropLine(string EventId, string Sport, string Matchup, DateTimeOffset StartsAt,
    string Player, string Stat, double Line, string Bookmaker, DateTimeOffset UpdatedAt, double? Over, double? Under);
public sealed record PicksBoard(string Status, string Message, DateTimeOffset AsOf, string Date,
    string TimeZone, int EventsScanned, int EventsAvailable, PropLine[] Lines);
public sealed record PicksResponse(int StatusCode, PicksBoard Body);

// Read-only provider adapter. Never submits entries or accepts the provider key from a browser.
public sealed class PrizePicksFeed(HttpClient http, IOptions<PrizePicksOptions> options, TimeProvider clock) : IDisposable
{
    public static readonly IReadOnlyDictionary<string, string> Sports = new Dictionary<string, string>
    {
        ["basketball_nba"] = "player_points,player_rebounds,player_assists,player_threes,player_points_rebounds_assists",
        ["basketball_wnba"] = "player_points,player_rebounds,player_assists,player_threes,player_points_rebounds_assists",
        ["americanfootball_nfl"] = "player_pass_yds,player_rush_yds,player_reception_yds,player_receptions,player_pass_tds",
        ["baseball_mlb"] = "pitcher_strikeouts,batter_hits,batter_total_bases",
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, PicksResponse Response)> cache = new();
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
    private const int MaxEvents = 16;
    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<PicksResponse> ScanAsync(string sport, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var date = TimeZoneInfo.ConvertTime(now, Central).ToString("yyyy-MM-dd");
        PicksResponse Empty(int code, string status, string message) => new(code, new(status, message, now, date, "America/Chicago", 0, 0, []));
        if (!Sports.TryGetValue(sport, out var markets)) return Empty(400, "invalid", "Choose NBA, WNBA, NFL or MLB.");
        if (!Configured) return Empty(503, "not_configured", "Daily picks need a connected sports feed. Custom analysis is available below.");
        await gate.WaitAsync(ct);
        try
        {
            var cacheKey = sport + date;
            if (cache.TryGetValue(cacheKey, out var saved) && saved.Expires > now) return saved.Response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var localStart = DateTime.SpecifyKind(DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
            var start = TimeZoneInfo.ConvertTimeToUtc(localStart, Central);
            var end = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), Central).AddSeconds(-1);
            using var events = await GetAsync($"/v4/sports/{sport}/events?commenceTimeFrom={start:yyyy-MM-ddTHH:mm:ssZ}&commenceTimeTo={end:yyyy-MM-ddTHH:mm:ssZ}", timeout.Token);
            if (events.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            var upcoming = events.RootElement.EnumerateArray().Where(e => Timestamp(e, "commence_time") > now)
                .OrderBy(e => Timestamp(e, "commence_time")).ToArray();
            var lines = new List<PropLine>();
            var failures = 0;
            var scanned = 0;
            foreach (var item in upcoming.Take(MaxEvents))
            {
                var id = Text(item, "id");
                if (id is null || id.Length > 100 || !id.All(char.IsAsciiLetterOrDigit)) { failures++; continue; }
                try
                {
                    using var odds = await GetAsync($"/v4/sports/{sport}/events/{id}/odds?bookmakers=prizepicks,draftkings,fanduel,betmgm&markets={markets}&oddsFormat=decimal", timeout.Token);
                    if (Text(odds.RootElement, "id") != id) throw new JsonException();
                    lines.AddRange(ParseLines(odds.RootElement, sport, now));
                    scanned++;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) { failures++; }
                catch (JsonException) { failures++; }
            }
            var partial = failures > 0 || upcoming.Length > MaxEvents;
            var response = new PicksResponse(200, new(partial ? "partial" : "ready",
                partial ? $"Partial coverage: {scanned} of {upcoming.Length} upcoming events scanned. Rankings only cover the returned lines."
                    : upcoming.Length == 0 ? "No upcoming games for this league today (Central time)."
                    : "Current standard lines from The Odds API. Rankings require matching sportsbook evidence.",
                now, date, "America/Chicago", scanned, upcoming.Length, lines.ToArray()));
            cache.ClearIfLarge();
            cache[cacheKey] = (clock.GetUtcNow().AddMinutes(2), response);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return RememberFailure(Empty(504, "unavailable", "The sports feed timed out. Try again shortly.")); }
        catch (HttpRequestException ex)
        {
            return RememberFailure(Empty(503, "unavailable", ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The sports provider rejected its API key or plan. Check the server feed configuration.",
                HttpStatusCode.TooManyRequests => "The sports provider quota is exhausted or rate limited. Try again later.",
                _ => "The sports provider is unavailable. No old picks are being presented as current.",
            }));
        }
        catch (JsonException) { return RememberFailure(Empty(502, "unavailable", "The sports provider returned an unreadable board. Try again later.")); }
        finally { gate.Release(); }

        PicksResponse RememberFailure(PicksResponse response)
        {
            cache.ClearIfLarge();
            cache[sport + date] = (clock.GetUtcNow().AddMinutes(1), response);
            return response;
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        // Query credentials are required by this provider. The named client's HTTP logging is removed below.
        using var response = await http.GetAsync("https://api.the-odds-api.com" + path + "&apiKey=" + Uri.EscapeDataString(options.Value.ApiKey), ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static PropLine[] ParseLines(JsonElement root, string sport, DateTimeOffset now)
    {
        if (!Sports.ContainsKey(sport)) return [];
        var id = Text(root, "id"); var at = Timestamp(root, "commence_time");
        if (id is null || at is null || at <= now || !root.TryGetProperty("bookmakers", out var books) || books.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PropLine>();
        foreach (var book in books.EnumerateArray())
        {
            var name = Text(book, "key");
            if (name is not ("prizepicks" or "draftkings" or "fanduel" or "betmgm") || !book.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array) continue;
            foreach (var market in markets.EnumerateArray())
            {
                var stat = Text(market, "key"); var updated = Timestamp(market, "last_update");
                if (stat is null || !Sports[sport].Split(',').Contains(stat) || updated is null || updated < now.AddMinutes(-5) || updated > now.AddMinutes(1)
                    || !market.TryGetProperty("outcomes", out var outcomes) || outcomes.ValueKind != JsonValueKind.Array) continue;
                var valid = outcomes.EnumerateArray().Where(o => Text(o, "description") is { Length: > 0 and <= 160 }
                    && Number(o, "point") is >= 0 and <= 10000 && Text(o, "name") is "Over" or "Under");
                foreach (var group in valid.GroupBy(o => (Player: Text(o, "description")!, Line: Number(o, "point")!.Value)))
                {
                    // Standard comparisons require both available directions; never infer the missing PP side.
                    if (name == "prizepicks" && (!group.Any(o => Text(o, "name") == "Over") || !group.Any(o => Text(o, "name") == "Under"))) continue;
                    double? Price(string side)
                    {
                        var values = group.Where(o => Text(o, "name") == side).Select(o => Number(o, "price")).Distinct().ToArray();
                        return values.Length == 1 && values[0] is > 1 and < 1001 ? values[0] : null;
                    }
                    result.Add(new(id, sport, $"{Text(root, "away_team")} at {Text(root, "home_team")}", at.Value,
                        group.Key.Player, stat, group.Key.Line, name, updated.Value, name == "prizepicks" ? null : Price("Over"), name == "prizepicks" ? null : Price("Under")));
                }
            }
        }
        return result.ToArray();
    }
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static DateTimeOffset? Timestamp(JsonElement e, string key) => DateTimeOffset.TryParse(Text(e, key), out var date) ? date : null;
    public void Dispose() => gate.Dispose();
}

internal static class PicksCacheExtensions
{
    public static void ClearIfLarge<T>(this Dictionary<string, T> cache) { if (cache.Count >= 8) cache.Clear(); }
}
