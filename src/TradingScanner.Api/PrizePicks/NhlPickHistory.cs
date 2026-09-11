using System.Text.Json;

namespace TradingScanner.Api.PrizePicks;

public sealed class NhlPickHistory(HttpClient http, TimeProvider clock)
{
    // Limit supplemental requests. Uncovered projections remain visible without an invented estimate.
    public async Task<PropLine[]> EnrichAsync(PropLine[] lines, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var result = lines.ToArray();
        // Match the Odds API game's two full team names and start time to an official NHL game.
        // Both rosters are searched; a same-name ambiguity produces no automatic estimate.
        JsonElement schedule;
        // A UTC evening can fall on the next UTC date. The previous date's week includes the whole Central slate.
        try { schedule = await Get($"https://api-web.nhle.com/v1/schedule/{now.AddDays(-1):yyyy-MM-dd}", ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { return result; }
        if (!schedule.TryGetProperty("gameWeek", out var week) || week.ValueKind != JsonValueKind.Array) return result;
        var games = week.EnumerateArray().Where(day => day.ValueKind == JsonValueKind.Object)
            .SelectMany(day => day.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array ? games.EnumerateArray().ToArray() : []).ToArray();
        var rosters = new Dictionary<string, JsonElement>();
        var failedRosters = new HashSet<string>();
        foreach (var p in lines.Where(p => p.Bookmaker == "prizepicks" && p.Sport == "icehockey_nhl"))
        {
            var matched = games.Where(g => g.ValueKind == JsonValueKind.Object
                && DateTimeOffset.TryParse(Text(g, "startTimeUTC"), out var start) && Math.Abs((start - p.StartsAt).TotalMinutes) <= 5
                && g.TryGetProperty("awayTeam", out var away) && g.TryGetProperty("homeTeam", out var home)
                && Name($"{TeamName(away)} at {TeamName(home)}") == Name(p.Matchup)).ToArray();
            if (matched.Length != 1) continue;
            var candidates = new List<string>();
            var bothRostersAvailable = true;
            foreach (var side in new[] { "awayTeam", "homeTeam" })
            {
                var team = Text(matched[0].GetProperty(side), "abbrev");
                if (team is not { Length: 2 or 3 } || !team.All(char.IsAsciiLetter)) { bothRostersAvailable = false; continue; }
                if (failedRosters.Contains(team)) { bothRostersAvailable = false; continue; }
                try
                {
                    if (!rosters.TryGetValue(team, out var roster)) rosters[team] = roster = await Get($"https://api-web.nhle.com/v1/roster/{team}/current", ct);
                    if (RosterPlayers(roster).Count(player => Name($"{Localized(player, "firstName")} {Localized(player, "lastName")}") == Name(p.Player)) == 1) candidates.Add(team);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
                { failedRosters.Add(team); bothRostersAvailable = false; if (ct.IsCancellationRequested) return result; }
            }
            if (bothRostersAvailable && candidates.Count == 1)
                for (var i = 0; i < result.Length; i++) if (ReferenceEquals(result[i], p)) result[i] = p with { Team = candidates[0] };
        }
        var groups = result.Where(p => p.Bookmaker == "prizepicks" && p.Sport == "icehockey_nhl" && p.Team is { Length: 2 or 3 }
            && p.Team.All(char.IsAsciiLetter)).GroupBy(p => (p.Team, p.Player)).Take(24).ToArray();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (group, _) =>
        {
            if (ct.IsCancellationRequested || !rosters.TryGetValue(group.Key.Team!, out var roster)) return;
            string FullName(JsonElement p) => Name($"{Localized(p, "firstName")} {Localized(p, "lastName")}");
            var matches = RosterPlayers(roster).Where(p => FullName(p) == Name(group.Key.Player)).ToArray();
            if (matches.Length != 1 || Number(matches[0], "id") is not { } playerId || playerId % 1 != 0 || playerId <= 0) return;
            // One roster-confirmed player, explicit regular-season game type, newest 20 completed games.
            var year = now.Month >= 9 ? now.Year : now.Year - 1;
            foreach (var report in group.GroupBy(p => p.Stat == "player_total_saves" ? "goalie/summary" : p.Stat == "player_blocked_shots" ? "skater/realtime" : "skater/summary"))
            {
                try
                {
                    var query = Uri.EscapeDataString($"playerId={playerId:0} and seasonId={year}{year + 1} and gameTypeId=2");
                    var json = await Get($"https://api.nhle.com/stats/rest/en/{report.Key}?isAggregate=false&isGame=true&start=0&limit=20&sort=%5B%7B%22property%22%3A%22gameDate%22%2C%22direction%22%3A%22DESC%22%7D%5D&cayenneExp={query}", ct);
                    foreach (var p in report)
                    {
                        var sample = ParseSample(json, p.Stat, now);
                        for (var i = 0; i < result.Length; i++) if (ReferenceEquals(result[i], p)) result[i] = p with { History = sample };
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { }
            }
        });
        return result;
    }

    public static GameSample[] ParseSample(JsonElement root, string stat, DateTimeOffset now)
    {
        var field = stat switch { "player_points" => "points", "player_assists" => "assists", "player_goals" => "goals",
            "player_shots_on_goal" => "shots", "player_total_saves" => "saves", "player_blocked_shots" => "blockedShots", _ => "" };
        if (field == "" || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var rows = new List<GameSample>();
        foreach (var row in data.EnumerateArray())
        {
            if (!DateOnly.TryParse(Text(row, "gameDate")?.Split('T')[0], out var day) || day >= DateOnly.FromDateTime(now.UtcDateTime)
                || day < DateOnly.FromDateTime(now.AddDays(-120).UtcDateTime) || Number(row, field) is not { } value || value < 0
                || (stat == "player_total_saves" && Number(row, "gamesStarted") != 1)) continue;
            rows.Add(new(day.ToString("yyyy-MM-dd"), value));
        }
        var sample = rows.GroupBy(r => r.Date).Where(g => g.Count() == 1).Select(g => g.Single()).OrderByDescending(r => r.Date).Take(20).ToArray();
        return sample.Length >= 10 && DateOnly.Parse(sample[0].Date) >= DateOnly.FromDateTime(now.AddDays(-30).UtcDateTime) ? sample : [];
    }
    private static string? Localized(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? Text(value, "default") : null;
    private static string TeamName(JsonElement team) => $"{Localized(team, "placeName")} {Localized(team, "commonName")}";
    private static IEnumerable<JsonElement> RosterPlayers(JsonElement roster) => new[] { "forwards", "defensemen", "goalies" }
        .SelectMany(role => roster.TryGetProperty(role, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [])
        .Where(p => p.ValueKind == JsonValueKind.Object);
    private static string Name(string name)
    {
        // This application runs with invariant globalization; Unicode decomposition can be a no-op.
        name = name.Replace("Montréal", "Montreal", StringComparison.OrdinalIgnoreCase);
        var asciiAccents = new string(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return string.Join(' ', asciiAccents.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private async Task<JsonElement> Get(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        return json.RootElement.Clone();
    }
}
