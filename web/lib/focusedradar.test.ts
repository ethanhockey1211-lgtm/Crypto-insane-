import { describe, expect, it } from "vitest";
import { buildMarketRadar, radarQuoteIsFresh, type RadarInput } from "./radar";
import type { QuoteDto, ScannerRow, SymbolSummaryDto } from "./types";

const now = Date.parse("2026-09-08T12:00:00Z");
const quote = (symbol: string, changes: Partial<QuoteDto> = {}): QuoteDto => ({
  symbol, price: 100, bid: 99.99, ask: 100.01, exchangeTimeMs: now - 500,
  receivedAtMs: now - 500, ageMs: 500, stale: false, provider: "kraken", exchange: "Kraken", ...changes,
});
const summary = (symbol: string, changes: Partial<SymbolSummaryDto> = {}): SymbolSummaryDto => ({
  symbol, quote: quote(symbol), open24h: null, high24h: 105, low24h: 94, volume24hBase: 1_000,
  change24hPct: 5, tradesSeen: 0, historyLoaded: false, ...changes,
});
const row = (symbol: string, changes: Partial<ScannerRow> = {}): ScannerRow => ({
  rank: 1, symbol, score: 80, setup: "BreakoutRetest", confidence: "High", price: 100,
  entry: 100, stop: 98, target1: 106, rr: 3, r1m: 0.001, r5m: 0.02, r15m: 0.04, r1h: 0.05, r24h: 0.05,
  relVol: 2, breakout: "Confirmed", doNotChase: false, stale: false, vwapDev: 0.01, volume24h: 100_000,
  keyLevel: 99, trend: "Bullish", components: [], penalty: 0, entryState: "InZone", chaseCeiling: 102,
  executionStatus: "Watch", netRewardRatio: 2, assessedPrice: 100, entryLow: 99.5, entryHigh: 100.5,
  setupBias: "Bullish", trigger: "Hold the retest", ...changes,
});
const input = (changes: Partial<RadarInput> = {}): RadarInput => ({
  symbols: [summary("A-USD")], rows: [row("A-USD")], live: true,
  scannerAt: new Date(now - 1000).toISOString(), now, ...changes,
});

describe("Kraken discovery radar", () => {
  it("ranks 24h returns from the latest quote and only falls back when the open is missing", () => {
    const radar = buildMarketRadar(input({ rows: [], symbols: [
      summary("LATEST-USD", { open24h: 100, quote: quote("LATEST-USD", { price: 110, receivedAtMs: now - 1 }), change24hPct: 1 }),
      summary("FALLING-USD", { open24h: 100, quote: quote("FALLING-USD", { price: 99 }), change24hPct: 20 }),
      summary("FALLBACK-USD", { open24h: null, change24hPct: 12 }),
      summary("INVALID-USD", { open24h: 0, change24hPct: 50 }),
    ] }));
    expect(radar.daily.map(market => market.symbol)).toEqual(["FALLBACK-USD", "LATEST-USD"]);
    expect(radar.daily[1].value).toBeCloseTo(0.1);
    expect(radar.daily[1].price).toBe(110);
  });

  it("ranks the full catalog before warmup without inventing short-term returns or plans", () => {
    const radar = buildMarketRadar(input({ rows: [], scannerAt: null, symbols: [
      summary("A-USD", { change24hPct: 9 }), summary("B-USD", { change24hPct: 12.5 }),
      summary("USDT-USD", { change24hPct: 0.1 }), summary("UNKNOWN-USD", { change24hPct: null }),
    ] }));
    expect(radar.total).toBe(4);
    expect(radar.quoted).toBe(4);
    expect(radar.assessed).toBe(0);
    expect(radar.daily.map(market => market.symbol)).toEqual(["B-USD", "A-USD", "USDT-USD"]);
    expect(radar.daily[0].value).toBe(0.125);
    expect(radar.daily.every(market => market.assessed === false && market.row === undefined)).toBe(true);
    expect(radar.movers5m).toEqual([]);
    expect(radar.volume).toEqual([]);
    expect(radar.nearZone).toEqual([]);
  });

  it("rejects stale, crossed, zero, future, and invalid quotes from every leaderboard", () => {
    const changes: Partial<QuoteDto>[] = [
      { stale: true }, { price: 0 }, { price: NaN }, { bid: 0 }, { ask: -1 }, { bid: 102, ask: 101 },
      { receivedAtMs: now - 30_001 }, { receivedAtMs: now + 1 }, { ageMs: 30_001 }, { ageMs: -2001 }, { ageMs: NaN },
    ];
    for (const invalid of changes) {
      const badQuote = quote("A-USD", invalid);
      expect(radarQuoteIsFresh(badQuote, now)).toBe(false);
      const radar = buildMarketRadar(input({ symbols: [summary("A-USD", { quote: badQuote })] }));
      expect(radar.quoted).toBe(0);
      expect([...radar.daily, ...radar.movers5m, ...radar.volume, ...radar.nearZone]).toEqual([]);
    }
    expect(radarQuoteIsFresh(null, now)).toBe(false);
    expect(radarQuoteIsFresh(quote("A-USD", { receivedAtMs: now - 30_000, ageMs: 30_000 }), now)).toBe(true);
    expect(radarQuoteIsFresh(quote("A-USD", { ageMs: -250 }), now)).toBe(true);
    expect(radarQuoteIsFresh(quote("A-USD", { ageMs: -2000 }), now)).toBe(true);
  });

  it("pauses every ranking on disconnection and only assessment rankings on a frozen scanner", () => {
    const disconnected = buildMarketRadar(input({ live: false }));
    expect(disconnected.total).toBe(1);
    expect(disconnected.quoted).toBe(0);
    expect([...disconnected.daily, ...disconnected.movers5m, ...disconnected.volume, ...disconnected.nearZone]).toEqual([]);
    for (const scannerAt of [null, "invalid", new Date(now - 10_001).toISOString(), new Date(now + 3_000).toISOString()]) {
      const frozen = buildMarketRadar(input({ scannerAt }));
      expect(frozen.daily).toHaveLength(1);
      expect(frozen.daily[0].assessed).toBe(false);
      expect(frozen.assessed).toBe(0);
      expect([...frozen.movers5m, ...frozen.movers15m, ...frozen.volume, ...frozen.nearZone]).toEqual([]);
    }
  });

  it("keeps momentum and volume discovery independent of entry eligibility", () => {
    const rows = [row("GAIN-USD", { r5m: 0.04, r15m: 0.01, executionStatus: "Blocked" }),
      row("VOLUME-USD", { r5m: -0.03, r15m: 0.08, relVol: 5, executionStatus: "Blocked" }),
      row("QUIET-USD", { r5m: 0, r15m: null, relVol: 1.49 }),
      row("INVALID-USD", { r5m: NaN, r15m: Infinity, relVol: NaN })];
    const radar = buildMarketRadar(input({ rows, symbols: rows.map(item => summary(item.symbol)) }));
    expect(radar.movers5m.map(item => item.symbol)).toEqual(["GAIN-USD"]);
    expect(radar.movers15m.map(item => item.symbol)).toEqual(["VOLUME-USD", "GAIN-USD"]);
    expect(radar.volume.map(item => item.symbol)).toEqual(["VOLUME-USD", "GAIN-USD"]);
    expect(radar.nearZone.map(item => item.symbol)).not.toContain("GAIN-USD");
    expect(radar.nearZone.map(item => item.symbol)).not.toContain("VOLUME-USD");
  });

  it("only ranks complete passing bullish plans and preserves the assessed price for zone distance", () => {
    const rows = [row("ZONE-USD"), row("NEAR-USD", { assessedPrice: 99, entryState: "Watch" }),
      row("FAR-USD", { assessedPrice: 95, entryState: "Watch" }),
      row("CHASE-USD", { doNotChase: true }), row("BLOCKED-USD", { executionStatus: "Blocked" }),
      row("STALE-USD", { stale: true }), row("BEAR-USD", { setupBias: "Bearish" }),
      row("NO_PRICE-USD", { assessedPrice: undefined }), row("NO_ZONE-USD", { entryLow: null }),
      row("REVERSED-USD", { entryLow: 101, entryHigh: 99 }), row("BAD_STOP-USD", { stop: 99.6 }),
      row("BAD_TARGET-USD", { target1: 100.4 }), row("NO_REWARD-USD", { netRewardRatio: 0 })];
    const radar = buildMarketRadar(input({ rows, symbols: rows.map(item => summary(item.symbol, { quote: quote(item.symbol, { price: 105 }) })) }), 20);
    expect(radar.nearZone.map(item => item.symbol)).toEqual(["ZONE-USD", "NEAR-USD", "FAR-USD"]);
    expect(radar.nearZone[0].value).toBe(0);
    expect(radar.nearZone[1].value).toBeCloseTo(0.5 / 99);
    expect(radar.nearZone[0].price).toBe(105);
    expect(radar.nearZone[0].row?.assessedPrice).toBe(100);
  });

  it("counts each catalog pair once and breaks leaderboard ties deterministically", () => {
    const rows = [row("C-USD"), row("B-USD"), row("A-USD")];
    const radar = buildMarketRadar(input({ rows, symbols: [...rows.map(item => summary(item.symbol)), summary("A-USD")] }), 2);
    expect(radar.total).toBe(3);
    expect(radar.daily.map(item => item.symbol)).toEqual(["A-USD", "B-USD"]);
    expect(radar.movers5m.map(item => item.symbol)).toEqual(["A-USD", "B-USD"]);
    expect(radar.nearZone.map(item => item.symbol)).toEqual(["A-USD", "B-USD"]);
    expect(buildMarketRadar(input(), 0).daily).toEqual([]);
  });
});
