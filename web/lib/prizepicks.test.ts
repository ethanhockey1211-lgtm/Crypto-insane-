import { describe, expect, it } from "vitest";
import { americanDecimal, boardProjections, centralDate, entryAnalysis, historyEstimate, noVig, rankBoard, readEntries, stalePick, SPORTS, SPORT_STATS, type Pick, type PicksBoard, type PropLine } from "./prizepicks";

const now = Date.parse("2026-09-11T15:00:00Z");
const line: PropLine = { eventId: "game1", sport: "basketball_nba", matchup: "A at B", startsAt: "2026-09-11T23:00:00Z", player: "Player A", stat: "player_points", line: 24.5, bookmaker: "prizepicks", updatedAt: "2026-09-11T14:59:00Z", over: null, under: null };
const board = (lines: PropLine[] = [{ ...line }, { ...line, bookmaker: "fanduel", over: 1.5, under: 2.5 }, { ...line, bookmaker: "draftkings", over: 1.9, under: 1.9 }]): PicksBoard => ({ status: "ready", message: "", date: "2026-09-11", timeZone: "America/Chicago", asOf: "2026-09-11T15:00:00Z", eventsScanned: 1, eventsAvailable: 1, lines });
const pick = (patch: Partial<Pick> = {}): Pick => ({ ...rankBoard(board(), now)[0], estimate: { probability: .6, low: .55, high: .65, evidence: "fixture", conditional: false }, ...patch });

describe("PrizePicks evidence", () => {
  const nhl = (): PropLine => ({ ...line, sport: "icehockey_nhl", stat: "player_shots_on_goal", line: 2.5,
    history: Array.from({ length: 12 }, (_, i) => ({ date: `2026-09-${String(10 - Math.floor(i / 2)).padStart(2, "0")}`, value: i < 8 ? 4 : 0 }))
      .map((s, i) => ({ ...s, date: new Date(now - (i + 1) * 86400000).toISOString().slice(0, 10) })) });
  it("includes NHL and shows posted lines even without probability evidence", () => {
    expect(SPORTS.icehockey_nhl).toBe("NHL"); expect(SPORT_STATS.icehockey_nhl).toContain("player_total_saves");
    const p = { ...nhl(), history: null }; expect(rankBoard(board([p]), now)).toEqual([]);
    expect(boardProjections(board([p]), now)).toHaveLength(1);
  });
  it("recommends an actual NHL line using official recent results without odds", () => {
    const ranked = rankBoard(board([nhl()]), now);
    expect(ranked).toHaveLength(1); expect(ranked[0].side).toBe("More");
    expect(ranked[0].method).toBe("nhl-history"); expect(ranked[0].estimate.probability).toBeCloseTo(9 / 14);
    expect(ranked[0].estimate.evidence).toContain("8 hits / 12 games");
    expect(stalePick(ranked[0], now + 300001)).toBe(true);
    expect(rankBoard(board([nhl()]), now + 300001)).toEqual([]);
  });
  it("counts ties as non-hits on integer NHL projections", () => {
    const p = nhl(); p.line = 4;
    // Eight ties and four misses is insufficient for either direction to be recommended.
    expect(rankBoard(board([p]), now)).toEqual([]);
  });
  it("rejects short, stale, duplicate, missing or future NHL samples", () => {
    for (const history of [nhl().history!.slice(0, 5), nhl().history!.map(s => ({ ...s, date: "2026-08-01" })),
      nhl().history!.map(s => ({ ...s, date: "2026-09-12" })), nhl().history!.map(s => ({ ...s, value: NaN }))]) {
      expect(rankBoard(board([{ ...nhl(), history }]), now)).toEqual([]);
    }
  });
  it("removes each book's margin, then averages distinct books", () => {
    expect(noVig(americanDecimal("-110"), americanDecimal("-110"))).toBe(.5);
    expect(noVig(americanDecimal("-200"), americanDecimal("+150"))).toBeCloseTo(.625);
    expect(rankBoard(board(), now)[0].estimate.probability).toBeCloseTo(.5625);
  });
  it("does not count one duplicated book as multiple independent quotes", () => {
    const data = board(); data.lines[2] = { ...data.lines[1] };
    expect(rankBoard(data, now)).toEqual([]);
  });
  it.each(["line", "player", "eventId", "stat", "sport"])("requires exact %s matching", field => {
    const data = board(); data.lines[2] = { ...data.lines[2], [field]: field === "line" ? 25.5 : "different" };
    expect(rankBoard(data, now)).toEqual([]);
  });
  it("excludes integer and alternate fractional lines, missing pairs and conflicting books", () => {
    expect(rankBoard(board(board().lines.map(p => ({ ...p, line: 24 }))), now)).toEqual([]);
    expect(rankBoard(board(board().lines.map(p => ({ ...p, line: 24.25 }))), now)).toEqual([]);
    const data = board(); data.lines[2].under = null;
    expect(rankBoard(data, now)).toEqual([]);
    const conflict = board(); conflict.lines.push({ ...conflict.lines[1], over: 2.1 });
    expect(rankBoard(conflict, now)).toEqual([]);
  });
  it.each(["2026-09-11T14:54:59Z", "2026-09-11T15:02:00Z", "", "bad-date"])("rejects unusable source timestamp %s", updatedAt => {
    const data = board(); data.lines[0].updatedAt = updatedAt;
    expect(rankBoard(data, now)).toEqual([]);
  });
  it("drops started games and uses Central calendar day rather than UTC day", () => {
    expect(centralDate(Date.parse("2026-09-12T03:00:00Z"))).toBe("2026-09-11");
    expect(rankBoard(board(), Date.parse(line.startsAt))).toEqual([]);
    expect(rankBoard({ ...board(), date: "2026-09-10" }, now)).toEqual([]);
    expect(rankBoard(board(board().lines.map(p => ({ ...p, startsAt: "2026-09-12T06:00:00Z" }))), now)).toEqual([]);
  });
  it("never turns invalid inputs into zero-valued odds or a certain prediction", () => {
    for (const input of ["", " ", "NaN", "Infinity", "90", "0", "-99", "1e5"]) expect(americanDecimal(input)).toBeNull();
    expect(noVig(NaN, 2)).toBeNull();
    expect(noVig(1, 2)).toBeNull();
    expect(historyEstimate("1, 2, nope, 4, 5", 2.5, "More")).toBeTypeOf("string");
    expect(historyEstimate("1,2,3,4", 2.5, "More")).toBeTypeOf("string");
    const e = historyEstimate("30,30,30,30,30", 24.5, "More");
    expect(typeof e === "object" && e.probability).toBeCloseTo(6 / 7);
  });
  it("counts ties as non-hits and recomputes when side or line changes", () => {
    const more = historyEstimate("20,24,24,25,30", 24, "More");
    const less = historyEstimate("20,24,24,25,30", 24, "Less");
    expect(typeof more === "object" && more.probability).toBeCloseTo(3 / 7);
    expect(typeof less === "object" && less.probability).toBeCloseTo(2 / 7);
    expect(typeof more === "object" && more.evidence).toContain("2 ties");
  });
  it("keeps actual negative yardage in the historical sample", () => {
    const result = historyEstimate("-2, 0, 7, 12, 3", 3.5, "Less");
    expect(typeof result === "object" && result.probability).toBeCloseTo(4 / 7);
  });
});
describe("entry analysis and saved entries", () => {
  it("computes an independence estimate and dependence bounds for distinct games", () => {
    const result = entryAnalysis([pick(), pick({ id: "b", player: "Player B", eventId: "game2", matchup: "C at D" })], now);
    expect(result.probability).toBeCloseTo(.36);
    expect(result.low).toBeCloseTo(.2); expect(result.high).toBe(.6);
  });
  it("withholds a point estimate for same-game or unidentified games", () => {
    expect(entryAnalysis([pick(), pick({ player: "B" })], now).probability).toBeNull();
    expect(entryAnalysis([pick(), pick({ player: "B", eventId: "" })], now).probability).toBeNull();
  });
  it("detects the same game across daily provider IDs and custom labels", () => {
    const custom = pick({ player: "Player B", method: "history", eventId: "custom-id", matchup: "  a AT b  " });
    expect(entryAnalysis([pick(), custom], now).probability).toBeNull();
    expect(entryAnalysis([pick(), custom], now).dependence).toContain("Same-game");
  });
  it("does not multiply duplicates, expired odds or conditional no-tie probabilities", () => {
    expect(entryAnalysis([], now).probability).toBeNull();
    expect(entryAnalysis([pick(), pick()], now).low).toBeNull();
    expect(entryAnalysis([pick(), pick({ player: "B", eventId: "g2", updatedAt: "2026-09-11T14:00:00Z" })], now).probability).toBeNull();
    expect(entryAnalysis([pick(), pick({ player: "B", eventId: "g2", estimate: { ...pick().estimate, conditional: true } })], now).probability).toBeNull();
  });
  it("restores valid records but ignores malformed browser data", () => {
    const entries = [{ id: "a", savedAt: new Date(now).toISOString(), picks: [pick(), pick({ player: "B" })], result: "pending" }];
    expect(readEntries(JSON.stringify(entries))).toEqual(entries);
    expect(readEntries("{" )).toEqual([]);
    expect(readEntries(JSON.stringify([{ ...entries[0], picks: [{ ...pick(), estimate: null }] }]))).toEqual([]);
  });
});
