export const PICK_STORAGE_KEY = "prizepicks-notebook-v1";
export const SPORTS = { icehockey_nhl: "NHL", americanfootball_nfl: "NFL", basketball_nba: "NBA", basketball_wnba: "WNBA", baseball_mlb: "MLB" } as const;
export const STATS: Record<string, string> = {
  player_points: "Points", player_rebounds: "Rebounds", player_assists: "Assists", player_threes: "3-pointers made",
  player_points_rebounds_assists: "Points + rebounds + assists", player_pass_yds: "Passing yards", player_rush_yds: "Rushing yards",
  player_reception_yds: "Receiving yards", player_receptions: "Receptions", player_pass_tds: "Passing TDs",
  pitcher_strikeouts: "Pitcher strikeouts", batter_hits: "Hits", batter_total_bases: "Total bases",
  player_shots_on_goal: "Shots on goal", player_goals: "Goals", player_blocked_shots: "Blocked shots", player_total_saves: "Goalie saves",
};
export const SPORT_STATS: Record<string, string[]> = {
  icehockey_nhl: ["player_shots_on_goal", "player_points", "player_assists", "player_goals", "player_blocked_shots", "player_total_saves"],
  basketball_nba: ["player_points", "player_rebounds", "player_assists", "player_threes", "player_points_rebounds_assists"],
  basketball_wnba: ["player_points", "player_rebounds", "player_assists", "player_threes", "player_points_rebounds_assists"],
  americanfootball_nfl: ["player_pass_yds", "player_rush_yds", "player_reception_yds", "player_receptions", "player_pass_tds"],
  baseball_mlb: ["pitcher_strikeouts", "batter_hits", "batter_total_bases"],
};
export type Side = "More" | "Less";
export interface PropLine {
  eventId: string; sport: string; matchup: string; startsAt: string; player: string; stat: string; line: number;
  bookmaker: string; updatedAt: string; over: number | null; under: number | null;
  projectionId?: string | null; team?: string | null; history?: { date: string; value: number }[] | null;
}
export interface PicksBoard {
  status: string; message: string; asOf: string; date: string; timeZone: string;
  eventsScanned: number; eventsAvailable: number; lines: PropLine[];
  source?: string; evidenceMessage?: string | null;
}
export interface Estimate { probability: number; low: number; high: number; evidence: string; conditional: boolean; }
export interface Pick {
  id: string; player: string; stat: string; line: number; side: Side; sport: string;
  eventId: string; matchup: string; startsAt: string; updatedAt: string;
  method: "market" | "history" | "manual-odds" | "nhl-history"; estimate: Estimate;
}
export interface RankedPick extends Pick { books: string[]; }
export interface SavedEntry { id: string; savedAt: string; picks: Pick[]; result: "pending" | "hit" | "miss" | "void"; }

export const percent = (p: number) => `${Math.round(p * 100)}%`;
export function riskLabel(p: number) { return p >= 0.65 ? "Lower relative risk" : p >= 0.55 ? "Moderate risk" : p >= 0.4 ? "High risk" : "Very high risk"; }
export function numeric(value: string): number | null {
  if (!value.trim() || !/^[+-]?(?:\d+\.?\d*|\.\d+)$/.test(value.trim())) return null;
  const n = Number(value); return Number.isFinite(n) ? n : null;
}
export function noVig(over: number | null, under: number | null): number | null {
  if (over === null || under === null || !Number.isFinite(over) || !Number.isFinite(under) || over <= 1 || under <= 1 || over > 1000 || under > 1000) return null;
  return (1 / over) / (1 / over + 1 / under);
}
export function americanDecimal(raw: string): number | null {
  const n = numeric(raw);
  return n === null || Math.abs(n) < 100 || Math.abs(n) > 100000 ? null : n < 0 ? 1 + 100 / -n : 1 + n / 100;
}
export function historyEstimate(raw: string, line: number, side: Side): Estimate | string {
  const pieces = raw.trim().split(/[\s,;]+/).filter(Boolean);
  const values = pieces.map(numeric);
  if (!Number.isFinite(line) || line < 0) return "Enter a valid non-negative line.";
  if (values.some(n => n === null || n < -10000 || n > 10000)) return "Use actual game results from -10,000 to 10,000 separated by commas or spaces.";
  if (values.length < 5 || values.length > 200) return "Enter 5–200 completed game results for this exact stat. Exclude DNPs.";
  const scores = values as number[];
  const wins = scores.filter(n => side === "More" ? n > line : n < line).length;
  const ties = scores.filter(n => n === line).length;
  const n = scores.length, p = wins / n, z = 1.96, denominator = 1 + z * z / n;
  const middle = (p + z * z / (2 * n)) / denominator;
  const width = z * Math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denominator;
  return { probability: (wins + 1) / (n + 2), low: Math.max(0, middle - width), high: Math.min(1, middle + width), conditional: false,
    evidence: `${wins} hits / ${n} games · ${ties} ties · ${n - wins - ties} misses. ${n < 20 ? "Limited sample. " : ""}Smoothed history estimate; ties count as non-hits. Range is a 95% historical sampling interval, not a forecast interval.` };
}
const playerKey = (s: string) => s.normalize("NFKC").trim().toLocaleLowerCase("en-US").replace(/\s+/g, " ");
const lineKey = (p: Pick | PropLine) => JSON.stringify([p.eventId, p.sport, playerKey(p.player), p.stat, p.line]);
export function centralDate(now: number) { return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Chicago", year: "numeric", month: "2-digit", day: "2-digit" }).format(now); }
export function fresh(at: string, now: number) { const n = Date.parse(at); return Number.isFinite(n) && n <= now + 60000 && now - n <= 300000; }
export function boardProjections(board: PicksBoard | null, now: number): PropLine[] {
  if (!board || !["ready", "partial"].includes(board.status) || !Array.isArray(board.lines) || board.date !== centralDate(now)) return [];
  return board.lines.filter(p => p && p.bookmaker === "prizepicks" && typeof p.player === "string" && typeof p.stat === "string"
    && Number.isFinite(p.line) && p.line >= 0 && fresh(p.updatedAt, now) && Date.parse(p.startsAt) > now && centralDate(Date.parse(p.startsAt)) === board.date);
}
function nhlEstimate(p: PropLine, now: number): { side: Side; estimate: Estimate } | null {
  if (p.sport !== "icehockey_nhl" || !SPORT_STATS.icehockey_nhl.includes(p.stat) || !Array.isArray(p.history) || p.history.length < 10 || p.history.length > 20) return null;
  const samples = p.history;
  if (samples.some(s => !s || !Number.isFinite(s.value) || s.value < 0 || !Number.isFinite(Date.parse(s.date))
    || s.date >= centralDate(now) || now - Date.parse(s.date) > 121 * 86400000) || new Set(samples.map(s => s.date)).size !== samples.length
    || now - Math.max(...samples.map(s => Date.parse(s.date))) > 31 * 86400000) return null;
  const raw = samples.map(s => s.value).join(",");
  const more = historyEstimate(raw, p.line, "More"), less = historyEstimate(raw, p.line, "Less");
  if (typeof more === "string" || typeof less === "string") return null;
  const side: Side = more.probability >= less.probability ? "More" : "Less";
  const estimate = side === "More" ? more : less;
  if (estimate.probability <= .5) return null;
  const dates = samples.map(s => s.date).sort();
  return { side, estimate: { ...estimate, evidence: `Official NHL regular-season results, ${dates[0]}–${dates.at(-1)}. ${estimate.evidence}` } };
}
export function rankBoard(board: PicksBoard | null, now: number): RankedPick[] {
  if (!board || !["ready", "partial"].includes(board.status) || !Array.isArray(board.lines) || board.date !== centralDate(now)) return [];
  const lines = board.lines.filter(p => p && typeof p.player === "string" && typeof p.eventId === "string" && typeof p.stat === "string"
    && Number.isFinite(p.line) && p.line >= 0 && p.line <= 10000 && fresh(p.updatedAt, now) && Date.parse(p.startsAt) > now && centralDate(Date.parse(p.startsAt)) === board.date);
  const result: RankedPick[] = [];
  const seen = new Set<string>();
  for (const projection of lines.filter(p => p.bookmaker === "prizepicks")) {
    const key = lineKey(projection);
    // All supported stats are integer-valued. Standard half-lines cannot push.
    if (seen.has(key)) continue;
    seen.add(key);
    const matching = lines.filter(p => ["draftkings", "fanduel", "betmgm"].includes(p.bookmaker) && lineKey(p) === key);
    const books: { name: string; probability: number; updatedAt: string }[] = [];
    for (const name of new Set(matching.map(p => p.bookmaker))) {
      const quotes = matching.filter(p => p.bookmaker === name);
      const probabilities = quotes.map(p => noVig(p.over, p.under));
      if (probabilities.some(p => p === null) || new Set(probabilities).size !== 1) continue;
      books.push({ name, probability: probabilities[0]!, updatedAt: quotes.map(q => q.updatedAt).sort()[0] });
    }
    if (books.length < 2 || projection.line % 1 !== 0.5) {
      const historical = nhlEstimate(projection, now);
      if (historical) result.push({ id: key, player: projection.player, stat: projection.stat, line: projection.line,
        side: historical.side, sport: projection.sport, eventId: projection.eventId, matchup: projection.matchup,
        startsAt: projection.startsAt, updatedAt: projection.updatedAt, method: "nhl-history", books: ["Official NHL game results"], estimate: historical.estimate });
      continue;
    }
    const over = books.reduce((sum, b) => sum + b.probability, 0) / books.length;
    const side: Side = over >= 0.5 ? "More" : "Less";
    const probabilities = books.map(b => side === "More" ? b.probability : 1 - b.probability);
    result.push({ id: key, player: projection.player, stat: projection.stat, line: projection.line, side, sport: projection.sport,
      eventId: projection.eventId, matchup: projection.matchup, startsAt: projection.startsAt,
      updatedAt: [projection.updatedAt, ...books.map(b => b.updatedAt)].sort()[0], method: "market", books: books.map(b => b.name),
      estimate: { probability: side === "More" ? over : 1 - over, low: Math.min(...probabilities), high: Math.max(...probabilities), conditional: false,
        evidence: `${books.length} sportsbooks, matched player/stat/line. Each book's margin removed before averaging. Range shows book disagreement, not confidence.` } });
  }
  return result.sort((a, b) => b.estimate.probability - a.estimate.probability || a.player.localeCompare(b.player));
}
export function stalePick(p: Pick, now: number) { return Date.parse(p.startsAt) <= now || (p.method !== "history" && !fresh(p.updatedAt, now)); }
export function entryAnalysis(picks: Pick[], now: number) {
  const warnings: string[] = [];
  if (picks.length < 2 || picks.length > 6) warnings.push("Build an entry with 2–6 different players.");
  if (new Set(picks.map(p => playerKey(p.player))).size !== picks.length) warnings.push("Repeated player: use only one selection per player.");
  if (picks.some(p => stalePick(p, now))) warnings.push("A pick has started or its odds are over 5 minutes old. Refresh its evidence.");
  if (picks.some(p => p.estimate.conditional)) warnings.push("A line can tie. Its odds estimate is conditional on no tie, so an all-hit estimate is unavailable.");
  const sameGame = picks.some((p, i) => picks.some((q, j) => j < i && ((p.eventId && p.eventId === q.eventId)
    || (p.sport === q.sport && p.matchup.trim() && playerKey(p.matchup) === playerKey(q.matchup)
      && Number.isFinite(Date.parse(p.startsAt)) && Number.isFinite(Date.parse(q.startsAt)) && centralDate(Date.parse(p.startsAt)) === centralDate(Date.parse(q.startsAt))))));
  const unknownGame = picks.some(p => !p.eventId);
  const values = picks.map(p => p.estimate.probability);
  const valid = picks.length >= 2 && warnings.length === 0 && values.every(p => Number.isFinite(p) && p >= 0 && p <= 1);
  return { probability: valid && !sameGame && !unknownGame ? values.reduce((a, b) => a * b, 1) : null,
    low: valid ? Math.max(0, values.reduce((a, b) => a + b, 0) - (values.length - 1)) : null,
    high: valid ? Math.min(...values) : null, warnings,
    dependence: sameGame ? "Same-game picks may move together. Showing mathematical bounds only; there is no correlation model."
      : unknownGame ? "Game identity is missing. Add the same game label for related custom picks; showing mathematical bounds only."
      : "All-hit estimate assumes independent outcomes. Shared news, players and game conditions can change the true chance." };
}
export function readEntries(raw: string | null): SavedEntry[] {
  if (!raw) return [];
  try {
    const data = JSON.parse(raw);
    if (!Array.isArray(data) || data.length > 100) return [];
    return data.filter((entry: SavedEntry) => entry && typeof entry.id === "string" && Number.isFinite(Date.parse(entry.savedAt))
      && ["pending", "hit", "miss", "void"].includes(entry.result) && Array.isArray(entry.picks) && entry.picks.length >= 2 && entry.picks.length <= 6
      && entry.picks.every(p => p && typeof p.id === "string" && typeof p.player === "string" && typeof p.stat === "string" && typeof p.eventId === "string"
        && typeof p.matchup === "string" && typeof p.sport === "string" && typeof p.updatedAt === "string" && typeof p.startsAt === "string"
        && ["More", "Less"].includes(p.side) && ["market", "history", "manual-odds", "nhl-history"].includes(p.method) && Number.isFinite(p.line) && p.line >= 0
        && p.estimate && typeof p.estimate.evidence === "string" && typeof p.estimate.conditional === "boolean"
        && [p.estimate.probability, p.estimate.low, p.estimate.high].every(n => typeof n === "number" && Number.isFinite(n) && n >= 0 && n <= 1)));
  } catch { return []; }
}
