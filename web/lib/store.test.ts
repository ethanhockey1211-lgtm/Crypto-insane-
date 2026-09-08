import { describe, expect, it } from "vitest";
import { MarketStore, store } from "./store";
import type { QuoteDto, ScannerRow, ScannerStream, SymbolSummaryDto } from "./types";

const row = (symbol: string, score: number, price = 1): ScannerRow => ({
  rank: 0, symbol, score, setup: "None", confidence: "Low", price, entry: null, stop: null, target1: null, rr: null,
  r1m: null, r5m: null, r15m: null, r1h: null, r24h: null, relVol: null, breakout: null, doNotChase: false, stale: false,
  vwapDev: null, volume24h: null, keyLevel: null, trend: null, components: [0, 0, 0, 0, 0, 0, 0], penalty: 0, entryState: null, chaseCeiling: null,
});

const stream = (rows: ScannerRow[]): ScannerStream => ({
  at: "2024-03-01T00:00:00Z", universe: rows.length, cycleMs: 1,
  market: { at: "", regime: "Neutral", altsFavorable: false, btc: null, eth: null, breadthAboveVwap: 0, breadthPositive1h: 0, breadthBullishAlignment: 0, medianRelVolume: 0, symbolsEvaluated: 0, notes: [] },
  rows: rows.map((r, i) => ({ ...r, rank: i + 1 })),
});

describe("MarketStore", () => {
  it("notifies changed symbols and refreshes derived lists even when rank is unchanged", () => {
    const hits: string[] = [];
    const offA = store.subscribeSymbol("A-USD", () => hits.push("A"));
    const offB = store.subscribeSymbol("B-USD", () => hits.push("B"));
    const offL = store.subscribeList(() => hits.push("list"));

    store.applyScanner(stream([row("A-USD", 80), row("B-USD", 70)]));
    store.flush();
    expect(hits.sort()).toEqual(["A", "B", "list"]);

    hits.length = 0;
    store.applyScanner(stream([row("A-USD", 80), row("B-USD", 70)])); // identical
    store.flush();
    expect(hits).toEqual([]);

    store.applyScanner(stream([row("A-USD", 85), row("B-USD", 70)])); // only A changed, same order
    store.flush();
    expect(hits).toEqual(["A", "list"]);
    hits.length = 0;

    hits.length = 0;
    store.applyScanner(stream([row("B-USD", 90), row("A-USD", 85)])); // order flips: ranks change for both
    store.flush();
    expect(hits.sort()).toEqual(["A", "B", "list"]);
    expect(store.getOrder()).toEqual(["B-USD", "A-USD"]);

    offA(); offB(); offL();
  });

  it("applies live quotes to rows and coalesces bursts into one notification per animation frame", () => {
    // Node has no requestAnimationFrame; install one that only runs when flushed, like a browser between frames.
    const g = globalThis as unknown as { requestAnimationFrame?: (cb: () => void) => number; cancelAnimationFrame?: (id: number) => void };
    let pending: (() => void) | null = null;
    g.requestAnimationFrame = (cb) => { pending = cb; return 1; };
    g.cancelAnimationFrame = () => { pending = null; };
    try {
      store.applyScanner(stream([row("C-USD", 50, 1.0)]));
      store.flush();
      let n = 0;
      const off = store.subscribeSymbol("C-USD", () => n++);
      for (let i = 1; i <= 100; i++) store.applyQuotes([{ symbol: "C-USD", price: 1 + i / 1000, bid: 1, ask: 1, exchangeTimeMs: 0, receivedAtMs: 0, ageMs: 5, stale: false, provider: "t", exchange: "t" }]);
      expect(n).toBe(0);
      pending!();
      expect(n).toBe(1);
      expect(store.getRow("C-USD")?.price).toBeCloseTo(1.1, 9);
      off();
    } finally {
      delete g.requestAnimationFrame;
      delete g.cancelAnimationFrame;
    }
  });

  it("keeps the tape newest first and bounded", () => {
    const events = Array.from({ length: 350 }, (_, i) => ({ id: i + 1, at: "", symbol: null, kind: "x", severity: "Info" as const, text: `${i}` }));
    store.pushTape(events, true);
    store.flush();
    expect(store.getTape()).toHaveLength(300);
    expect(store.getTape()[0].id).toBe(350);
  });

  it("refreshes execution-only changes and removes symbols no longer in the universe", () => {
    const original = { ...row("CHECK-USD", 90), executionStatus: "Watch" as const };
    store.applyScanner(stream([original]));
    const before = store.getOrder();
    store.applyScanner(stream([{ ...original, executionStatus: "Blocked", entryState: "Chase" }]));
    expect(store.getRow("CHECK-USD")?.executionStatus).toBe("Blocked");
    expect(store.getOrder()).not.toBe(before);
    store.applyScanner(stream([]));
    expect(store.getRow("CHECK-USD")).toBeNull();
  });

  it("updates stale flags even when quote price does not move", () => {
    store.applyScanner(stream([row("STALE-USD", 80)]));
    store.applyQuotes([{ symbol: "STALE-USD", price: 1, bid: 1, ask: 1, exchangeTimeMs: 0, receivedAtMs: 0, ageMs: 40000, stale: true, provider: "t", exchange: "t" }]);
    expect(store.getRow("STALE-USD")?.stale).toBe(true);
  });

  it("updates changed entry zones, triggers, and bias even when score and midpoint stay the same", () => {
    const original = { ...row("PLAN-USD", 70), entry: 10, entryLow: 9, entryHigh: 11, trigger: "Wait for a close", setupBias: "Bullish" as const };
    store.applyScanner(stream([original]));
    const before = store.getOrder();
    store.applyScanner(stream([{ ...original, entryLow: 9.5, entryHigh: 10.5, trigger: "Wait for a retest", setupBias: "Neutral" }]));
    expect(store.getOrder()).not.toBe(before);
    expect(store.getRow("PLAN-USD")).toMatchObject({ entryLow: 9.5, entryHigh: 10.5, trigger: "Wait for a retest", setupBias: "Neutral" });
  });
});

const quote = (symbol: string, price = 1, receivedAtMs = 100): QuoteDto => ({
  symbol, price, bid: price, ask: price, exchangeTimeMs: receivedAtMs, receivedAtMs, ageMs: 0,
  stale: false, provider: "kraken", exchange: "Kraken",
});
const summary = (symbol: string, q: QuoteDto | null = null, historyLoaded = false): SymbolSummaryDto => ({
  symbol, quote: q, historyLoaded, open24h: null, high24h: null, low24h: null, volume24hBase: null, change24hPct: null, tradesSeen: 0,
});

describe("MarketStore complete catalog", () => {
  it("lists the entire catalog before quotes or history arrive without creating scored rows", () => {
    const market = new MarketStore();
    market.applySymbols(Array.from({ length: 650 }, (_, i) => summary(`COIN${i}-USD`)));
    expect(market.getAllOrder()).toHaveLength(650);
    expect(market.getAllOrder()).toContain("COIN649-USD");
    expect(market.getOrder()).toEqual([]);
    expect(market.getRow("COIN649-USD")).toBeNull();
    expect(market.getSymbol("COIN649-USD")).toMatchObject({ quote: null, historyLoaded: false });
  });

  it("merges ranked updates with pending pairs without duplicates or retaining removed assessments", () => {
    const market = new MarketStore();
    market.applySymbols([summary("A-USD"), summary("B-USD")]);
    market.applyScanner(stream([row("B-USD", 80)]));
    expect(market.getAllOrder()).toEqual(["B-USD", "A-USD"]);
    expect(market.getOrder()).toEqual(["B-USD"]);
    expect(market.getRow("A-USD")).toBeNull();
    market.applyScanner(stream([row("A-USD", 90), row("B-USD", 80)]));
    expect(market.getAllOrder()).toEqual(["A-USD", "B-USD"]);
    expect(market.getRow("A-USD")?.score).toBe(90);
    market.applyScanner(stream([]));
    expect(market.getAllOrder()).toEqual(["A-USD", "B-USD"]);
    expect(market.getRow("A-USD")).toBeNull();
  });

  it("updates real quotes on pending pairs and keeps a late catalog response from rewinding price", () => {
    const market = new MarketStore();
    market.applySymbols([summary("A-USD", quote("A-USD"))]);
    const order = market.getAllOrder();
    market.applyQuotes([quote("A-USD", 2, 200)]);
    expect(market.getAllOrder()).not.toBe(order);
    market.applySymbols([summary("A-USD", quote("A-USD", 1, 100), true)]);
    expect(market.getSymbol("A-USD")).toMatchObject({ quote: { price: 2, receivedAtMs: 200 }, historyLoaded: true });
    expect(market.getRow("A-USD")).toBeNull();
  });

  it("propagates quote staleness even without price changes and clears it only on a newer quote", () => {
    const market = new MarketStore();
    market.applySymbols([summary("A-USD", quote("A-USD"))]);
    market.applySymbols([summary("A-USD", { ...quote("A-USD"), stale: true, ageMs: 40000 })]);
    expect(market.getSymbol("A-USD")?.quote?.stale).toBe(true);
    market.applyQuotes([quote("A-USD")]);
    expect(market.getSymbol("A-USD")?.quote?.stale).toBe(true);
    market.applyQuotes([quote("A-USD", 1, 200)]);
    expect(market.getSymbol("A-USD")?.quote?.stale).toBe(false);
  });

  it("expires quiet pending quotes without waiting for a new server message and rejects future timestamps", () => {
    const market = new MarketStore();
    market.applySymbols([summary("A-USD", quote("A-USD", 1, 100_000)), summary("B-USD", quote("B-USD", 1, 200_000))]);
    const rankedOrder = market.getOrder();
    market.expireCatalogQuotes(100_000);
    expect(market.getSymbol("A-USD")?.quote?.stale).toBe(false);
    expect(market.getSymbol("B-USD")?.quote?.stale).toBe(true);
    market.expireCatalogQuotes(130_001);
    expect(market.getSymbol("A-USD")?.quote?.stale).toBe(true);
    expect(market.getOrder()).toBe(rankedOrder);
    expect(market.getRow("A-USD")).toBeNull();
  });

  it("removes catalog entries when the exchange universe changes and discards missing quotes", () => {
    const market = new MarketStore();
    market.applySymbols([summary("A-USD"), summary("B-USD", quote("B-USD"))]);
    market.applySymbols([summary("B-USD")]);
    expect(market.getAllOrder()).toEqual(["B-USD"]);
    expect(market.getSymbol("A-USD")).toBeNull();
    expect(market.getSymbol("B-USD")?.quote).toBeNull();
  });
});
