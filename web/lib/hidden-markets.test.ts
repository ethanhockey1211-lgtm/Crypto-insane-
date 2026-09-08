import { describe, expect, it } from "vitest";
import { HIDDEN_MARKETS_KEY, HiddenMarkets, normalizeHiddenSymbol, parseHiddenSymbols, startHiddenMarkets } from "./hidden-markets";
import { MarketStore } from "./store";
import { EntryAlertTracker } from "./entry-alerts";
import type { AlertEvent, QuoteDto, ScannerRow, ScannerStream, SymbolSummaryDto, TapeEvent } from "./types";

function memoryStorage(initial: string | null = null) {
  const values = new Map<string, string>(initial == null ? [] : [[HIDDEN_MARKETS_KEY, initial]]);
  let writes = 0;
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { writes++; values.set(key, value); }, writes: () => writes };
}

const row = (symbol: string, price = 100): ScannerRow => ({
  symbol, price, assessedPrice: price, rank: 1, score: 80, setup: "Breakout", confidence: "High",
  entry: 100, entryLow: 99, entryHigh: 101, stop: 97, target1: 108, rr: 2, netRewardRatio: 2,
  executionStatus: "Watch", entryState: "InZone", setupBias: "Bullish", stale: false, doNotChase: false,
  r1m: null, r5m: 0.01, r15m: 0.02, r1h: null, r24h: null, relVol: 2, breakout: null,
  vwapDev: null, volume24h: 100_000, keyLevel: null, trend: null, components: [], penalty: 0, chaseCeiling: 103,
});
const quote = (symbol: string, price = 100, receivedAtMs = 100): QuoteDto => ({
  symbol, price, bid: price, ask: price, exchangeTimeMs: receivedAtMs, receivedAtMs,
  ageMs: 0, stale: false, provider: "kraken", exchange: "Kraken",
});
const summary = (symbol: string, price = 100): SymbolSummaryDto => ({
  symbol, quote: quote(symbol, price), open24h: 95, high24h: 105, low24h: 90,
  volume24hBase: 1000, change24hPct: 5, tradesSeen: 0, historyLoaded: false,
});
const stream = (rows: ScannerRow[], at = "2026-09-08T12:00:00Z"): ScannerStream => ({
  at, universe: rows.length, cycleMs: 1, rows,
  market: { at, regime: "Neutral", altsFavorable: false, btc: null, eth: null, breadthAboveVwap: 0,
    breadthPositive1h: 0, breadthBullishAlignment: 0, medianRelVolume: 0, symbolsEvaluated: 0, notes: [] },
});

describe("browser hidden-market preferences", () => {
  it("normalizes, deduplicates and rejects malformed storage without browser globals", () => {
    expect(parseHiddenSymbols('[" btc/usd ","BTC-USD","1inch-usd",null,12,"<script>-USD","BTC-EUR"]')).toEqual(["1INCH-USD", "BTC-USD"]);
    expect(parseHiddenSymbols("{broken")).toEqual([]);
    expect(parseHiddenSymbols('{"symbols":["BTC-USD"]}')).toEqual([]);
    expect(normalizeHiddenSymbol("../../BTC-USD")).toBeNull();
    expect(() => startHiddenMarkets()()).not.toThrow();
  });

  it("persists hides and individual/all restores across a fresh page instance", () => {
    const storage = memoryStorage();
    const first = new HiddenMarkets(); first.initialize(storage);
    first.hide(" btc/usd "); first.hide("ETH-USD"); first.hide("BTC-USD");
    expect(storage.writes()).toBe(2);
    const reloaded = new HiddenMarkets(); reloaded.initialize(storage);
    expect(reloaded.getSnapshot()).toEqual({ symbols: ["BTC-USD", "ETH-USD"], persistence: "browser" });
    reloaded.restore("btc/usd");
    expect(JSON.parse(storage.getItem(HIDDEN_MARKETS_KEY)!)).toEqual(["ETH-USD"]);
    reloaded.restoreAll();
    expect(storage.getItem(HIDDEN_MARKETS_KEY)).toBe("[]");
  });

  it("keeps in-memory preferences and reports session-only behavior when storage throws", () => {
    const hidden = new HiddenMarkets();
    hidden.initialize({ getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("quota"); } });
    hidden.hide("BTC-USD");
    expect(hidden.getSnapshot()).toEqual({ symbols: ["BTC-USD"], persistence: "session" });
    hidden.initialize(null);
    expect(hidden.isHidden("BTC/USD")).toBe(true);
    hidden.restore("BTC-USD");
    expect(hidden.getSnapshot().symbols).toEqual([]);
  });

  it("applies cross-tab changes without writing back and keeps snapshot references stable", () => {
    const storage = memoryStorage(); const hidden = new HiddenMarkets(); hidden.initialize(storage);
    let changes = 0; hidden.subscribe(() => changes++);
    hidden.receiveStorage('["ETH-USD"]');
    const snapshot = hidden.getSnapshot();
    hidden.receiveStorage('["ETH-USD","ETH-USD"]');
    expect(hidden.getSnapshot()).toBe(snapshot);
    expect(changes).toBe(1);
    expect(storage.writes()).toBe(0);
    hidden.receiveStorage(null);
    expect(hidden.isHidden("ETH-USD")).toBe(false);
    expect(changes).toBe(2);
  });
});

describe("hidden markets across discovery snapshots", () => {
  it("filters assessed and pending lists through snapshots/reconnects while retaining current canonical data", () => {
    const hidden = new HiddenMarkets(); const market = new MarketStore(hidden);
    hidden.hide("BTC-USD"); hidden.hide("PENDING-USD");
    market.applySymbols([summary("BTC-USD"), summary("ETH-USD"), summary("PENDING-USD")]);
    market.applyScanner(stream([row("BTC-USD"), row("ETH-USD")]));
    expect(market.getOrder()).toEqual(["ETH-USD"]);
    expect(market.getAllOrder()).toEqual(["ETH-USD"]);
    market.setHub("reconnecting");
    market.applySymbols([summary("BTC-USD"), summary("ETH-USD"), summary("PENDING-USD")]);
    market.applyScanner(stream([row("BTC-USD", 101), row("ETH-USD")]));
    market.applyQuotes([quote("BTC-USD", 102, 200), quote("PENDING-USD", 2, 200)]);
    market.setHub("connected");
    expect(market.getOrder()).toEqual(["ETH-USD"]);
    expect(market.getRow("BTC-USD")?.price).toBe(102);
    expect(market.getSymbol("PENDING-USD")?.quote?.price).toBe(2);
    hidden.restoreAll();
    expect(market.getOrder()).toEqual(["BTC-USD", "ETH-USD"]);
    expect(market.getAllOrder()).toEqual(["BTC-USD", "ETH-USD", "PENDING-USD"]);
    expect(market.getSymbol("BTC-USD")?.quote?.price).toBe(102);
  });

  it("immediately filters existing and new tape/alert events while preserving global events and restore history", () => {
    const hidden = new HiddenMarkets(); const market = new MarketStore(hidden);
    const tape: TapeEvent[] = [
      { id: 1, at: "", symbol: "BTC-USD", kind: "Breakout", severity: "Notice", text: "BTC setup" },
      { id: 2, at: "", symbol: null, kind: "Regime", severity: "Info", text: "Market context" },
    ];
    const alert: AlertEvent = { id: "a", at: "2026-09-08T12:00:00Z", ruleId: "r", ruleName: "Entry", symbol: "BTC-USD", message: "Setup", values: {} };
    market.pushTape(tape, true); market.pushAlerts([alert], true);
    hidden.hide("BTC-USD");
    expect(market.getTape().map(event => event.id)).toEqual([2]);
    expect(market.getAlerts()).toEqual([]);
    market.pushTape([{ ...tape[0], id: 3 }]); market.pushAlerts([{ ...alert, id: "b" }]);
    expect(market.getTape()).toHaveLength(1);
    expect(market.getAlerts()).toEqual([]);
    hidden.restore("BTC-USD");
    expect(market.getTape().map(event => event.id)).toEqual([3, 2, 1]);
    expect(market.getAlerts()).toHaveLength(2);
  });

  it("applies visibility synchronously before alerts re-baseline, even with queued animation frames", () => {
    const hidden = new HiddenMarkets(); const market = new MarketStore(hidden);
    market.applyScanner(stream([row("BTC-USD")]));
    const previousFrame = globalThis.requestAnimationFrame, previousCancel = globalThis.cancelAnimationFrame;
    globalThis.requestAnimationFrame = () => 1; globalThis.cancelAnimationFrame = () => undefined;
    try {
      hidden.hide("BTC-USD");
      expect(market.getOrder()).toEqual([]);
      hidden.restore("BTC-USD");
      expect(market.getOrder()).toEqual(["BTC-USD"]);
      const tracker = new EntryAlertTracker();
      const rows = () => market.getOrder().flatMap(symbol => { const value = market.getRow(symbol); return value ? [value] : []; });
      const base = Date.parse("2026-09-08T12:00:00Z");
      tracker.update([], true, new Date(base).toISOString(), base);
      tracker.pause(); // The component resets on visibility changes before re-baselining.
      expect(tracker.update(rows(), true, new Date(base + 1000).toISOString(), base + 1000)).toEqual([]);
      expect(tracker.update(rows(), true, new Date(base + 4000).toISOString(), base + 4000)).toEqual([]);
    } finally {
      if (previousFrame) globalThis.requestAnimationFrame = previousFrame; else Reflect.deleteProperty(globalThis, "requestAnimationFrame");
      if (previousCancel) globalThis.cancelAnimationFrame = previousCancel; else Reflect.deleteProperty(globalThis, "cancelAnimationFrame");
    }
  });
});
