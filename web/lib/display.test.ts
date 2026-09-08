import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DisplayStore, DISPLAY_INTERVAL_MS } from "./display";
import { EntryAlertTracker, type EntryNotice } from "./entry-alerts";
import { HiddenMarkets } from "./hidden-markets";
import { MarketStore } from "./store";
import type { AlertEvent, QuoteDto, ScannerRow, ScannerStream, SymbolSummaryDto, TapeEvent } from "./types";

const BASE = Date.parse("2026-09-08T12:00:00Z");
const row = (symbol: string, score = 80, changes: Partial<ScannerRow> = {}): ScannerRow => ({
  symbol, score, rank: 1, setup: "Breakout", confidence: "High", price: 100, assessedPrice: 100,
  entry: 100, entryLow: 99, entryHigh: 101, stop: 97, target1: 108, rr: 2, netRewardRatio: 2,
  executionStatus: "Watch", entryState: "InZone", setupBias: "Bullish", stale: false, doNotChase: false,
  r1m: null, r5m: null, r15m: null, r1h: null, r24h: null, relVol: null, breakout: null,
  vwapDev: null, volume24h: null, keyLevel: null, trend: null, components: [], penalty: 0,
  chaseCeiling: 103, ...changes,
});
const stream = (rows: ScannerRow[], cycleMs = 1): ScannerStream => ({
  at: new Date(Date.now()).toISOString(), universe: rows.length, cycleMs,
  rows: rows.map((r, i) => ({ ...r, rank: i + 1 })),
  market: {
    at: new Date(Date.now()).toISOString(), regime: "RiskOn", altsFavorable: true, btc: null, eth: null,
    breadthAboveVwap: 0.7, breadthPositive1h: 0.6, breadthBullishAlignment: 0.6, medianRelVolume: 1.5,
    symbolsEvaluated: rows.length, notes: [],
  },
});
const quote = (symbol: string, price = 100): QuoteDto => ({
  symbol, price, bid: price - 0.01, ask: price + 0.01, exchangeTimeMs: Date.now(), receivedAtMs: Date.now(),
  ageMs: 0, stale: false, provider: "kraken", exchange: "Kraken",
});
const summary = (symbol: string, q: QuoteDto | null = null): SymbolSummaryDto => ({
  symbol, quote: q, open24h: null, high24h: null, low24h: null, volume24hBase: null,
  change24hPct: null, tradesSeen: 0, historyLoaded: false,
});
const tape = (id: number, symbol: string | null): TapeEvent => ({
  id, symbol, at: new Date(Date.now()).toISOString(), kind: "SetupAppeared", severity: "Notice", text: `event ${id}`,
});

const cleanup: Array<() => void> = [];
function harness(seed = true) {
  const hidden = new HiddenMarkets();
  hidden.initialize(null);
  const raw = new MarketStore(hidden);
  if (seed) {
    raw.applySymbols([summary("A-USD", quote("A-USD")), summary("B-USD", quote("B-USD"))]);
    raw.applyScanner(stream([row("A-USD", 80), row("B-USD", 70)]));
    raw.pushTape([tape(1, "A-USD")]);
    raw.flush();
  }
  const display = new DisplayStore(raw, hidden);
  const stop = display.start();
  cleanup.push(stop);
  return { raw, hidden, display, stop };
}

beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(BASE); });
afterEach(() => {
  for (const stop of cleanup.splice(0)) stop();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("readable scanner display snapshots", () => {
  it("shows the first catalog immediately without inventing assessed rows", () => {
    const { raw, display } = harness(false);
    expect(display.getSnapshot().allOrder).toEqual([]);
    raw.applySymbols(Array.from({ length: 637 }, (_, i) => summary(`COIN${i}-USD`)));
    raw.flush();

    expect(display.getSnapshot().allOrder).toHaveLength(637);
    expect(display.getSnapshot().order).toEqual([]);
    expect(display.getRow("COIN636-USD")).toBeNull();
    expect(display.getSymbol("COIN636-USD")).toMatchObject({ quote: null, historyLoaded: false });
    expect(Date.now()).toBe(BASE);
  });

  it("holds rows, quotes, ranking, market and tape stable between five-second samples", () => {
    const { raw, display } = harness();
    const captured = display.getSnapshot();
    vi.advanceTimersByTime(DISPLAY_INTERVAL_MS - 1);
    const incoming = stream([row("B-USD", 92), row("A-USD", 75)], 8);
    raw.applyScanner(incoming);
    raw.applyQuotes([quote("B-USD", 101)]);
    raw.pushTape([tape(2, "B-USD")]);
    raw.flush();

    expect(display.getSnapshot()).toBe(captured);
    expect(display.getRow("B-USD")?.price).toBe(100);
    expect(display.getSymbol("B-USD")?.quote?.price).toBe(100);
    expect(raw.getRow("B-USD")?.price).toBe(101);
    expect(raw.getOrder()).toEqual(["B-USD", "A-USD"]);

    vi.advanceTimersByTime(1);
    const next = display.getSnapshot();
    expect(next.revision).toBe(captured.revision + 1);
    expect(next.capturedAt).toBe(BASE + DISPLAY_INTERVAL_MS);
    expect(next.order).toEqual(["B-USD", "A-USD"]);
    expect(display.getRow("B-USD")).toMatchObject({ price: 101, score: 92, rank: 1 });
    expect(display.getSymbol("B-USD")?.quote?.price).toBe(101);
    expect(next.market).toBe(incoming.market);
    expect(next.cycle).toEqual({ at: incoming.at, ms: 8 });
    expect(next.tape.map(event => event.id)).toEqual([2, 1]);
    expect(captured.order).toEqual(["A-USD", "B-USD"]);
    expect(captured.rows.get("B-USD")?.price).toBe(100);
  });

  it("keeps accepting raw rows and quotes throughout a paused display", () => {
    const { raw, display } = harness();
    display.setPaused(true);
    const frozen = display.getSnapshot();
    const changed = vi.fn();
    cleanup.push(display.subscribe(changed));
    for (let tick = 1; tick <= 20; tick++) {
      vi.advanceTimersByTime(1_000);
      raw.applyScanner(stream([row("B-USD", 70 + tick), row("A-USD", 60)]));
      raw.applyQuotes([quote("B-USD", 100 + tick)]);
    }
    raw.flush();

    expect(raw.getRow("B-USD")).toMatchObject({ score: 90, price: 120 });
    expect(raw.getSymbol("B-USD")?.quote?.price).toBe(120);
    expect(display.getSnapshot()).toBe(frozen);
    expect(display.getSnapshot().paused).toBe(true);
    expect(changed).not.toHaveBeenCalled();
  });

  it("manually captures the latest data once without leaving pause", () => {
    const { raw, display } = harness();
    display.setPaused(true);
    const previous = display.getSnapshot();
    vi.advanceTimersByTime(1_000);
    raw.applyScanner(stream([row("B-USD", 95)]));
    raw.applyQuotes([quote("B-USD", 102)]);
    const changed = vi.fn();
    cleanup.push(display.subscribe(changed));

    display.capture();
    const refreshed = display.getSnapshot();
    expect(changed).toHaveBeenCalledTimes(1);
    expect(refreshed.paused).toBe(true);
    expect(refreshed.revision).toBe(previous.revision + 1);
    expect(refreshed.order).toEqual(["B-USD"]);
    expect(display.getRow("B-USD")?.price).toBe(102);
    raw.applyQuotes([quote("B-USD", 103)]);
    vi.advanceTimersByTime(DISPLAY_INTERVAL_MS * 2);
    expect(display.getSnapshot()).toBe(refreshed);
  });

  it("resumes with one latest snapshot and never replays queued rankings", () => {
    const { raw, display } = harness();
    display.setPaused(true);
    for (let tick = 1; tick <= 3; tick++) {
      vi.advanceTimersByTime(DISPLAY_INTERVAL_MS);
      raw.applyScanner(stream(tick % 2 ? [row("B-USD", 70 + tick), row("A-USD")] : [row("A-USD"), row("B-USD")]));
    }
    const changed = vi.fn();
    cleanup.push(display.subscribe(changed));
    display.setPaused(false);
    const latest = display.getSnapshot();

    expect(changed).toHaveBeenCalledTimes(1);
    expect(latest.paused).toBe(false);
    expect(latest.order).toEqual(["B-USD", "A-USD"]);
    expect(display.getRow("B-USD")?.score).toBe(73);
    display.setPaused(false);
    expect(changed).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(DISPLAY_INTERVAL_MS - 1);
    expect(display.getSnapshot()).toBe(latest);
  });

  it("captures a coherent latest snapshot even before a queued browser frame is flushed", () => {
    const { raw, display } = harness();
    vi.stubGlobal("requestAnimationFrame", vi.fn(() => 1));
    vi.stubGlobal("cancelAnimationFrame", vi.fn());
    vi.advanceTimersByTime(100);
    const incoming = stream([row("C-USD", 95), row("B-USD", 90)], 9);
    raw.applySymbols([summary("C-USD", quote("C-USD", 105)), summary("B-USD")]);
    raw.applyScanner(incoming);

    display.capture();

    expect(display.getSnapshot().order).toEqual(["C-USD", "B-USD"]);
    expect(display.getSnapshot().allOrder).toEqual(["C-USD", "B-USD"]);
    expect(display.getRow("C-USD")).toMatchObject({ score: 95, rank: 1 });
    expect(display.getSymbol("C-USD")?.quote?.price).toBe(105);
    expect(display.getRow("A-USD")).toBeNull();
    expect(display.getSnapshot().market).toBe(incoming.market);
    expect(display.getSnapshot().cycle).toEqual({ at: incoming.at, ms: 9 });
  });

  it("keeps raw server alerts and entry-transition checks active while the display is paused", () => {
    const { raw, display } = harness();
    raw.applyScanner(stream([row("A-USD", 80, { entryState: "Watch" })]));
    display.capture();
    display.setPaused(true);
    const frozen = display.getSnapshot();
    raw.setHub("connected");
    const tracker = new EntryAlertTracker();
    const notices: EntryNotice[] = [];
    const observe = () => notices.push(...tracker.update(raw.getOrder().map(symbol => raw.getRow(symbol)!),
      raw.getHub() === "connected", raw.getCycle().at, Date.now()));
    observe();
    cleanup.push(raw.subscribeHeader(observe));
    const alertsChanged = vi.fn();
    cleanup.push(raw.subscribeAlerts(alertsChanged));

    vi.advanceTimersByTime(1_000);
    raw.applyScanner(stream([row("A-USD")]));
    vi.advanceTimersByTime(2_000);
    raw.applyScanner(stream([row("A-USD")]));
    const event: AlertEvent = {
      id: "alert-1", ruleId: "rule-1", ruleName: "Ready", at: new Date(Date.now()).toISOString(),
      symbol: "A-USD", message: "Current setup", values: {},
    };
    raw.pushAlerts([event]);
    raw.flush();

    expect(notices).toHaveLength(1);
    expect(notices[0].row.symbol).toBe("A-USD");
    expect(raw.getAlerts()).toEqual([event]);
    expect(alertsChanged).toHaveBeenCalledTimes(1);
    expect(display.getSnapshot()).toBe(frozen);
    expect(display.getRow("A-USD")?.entryState).toBe("Watch");
  });

  it("removes hidden coins immediately while paused and restores their captured data without refreshing", () => {
    const { raw, hidden, display } = harness();
    display.setPaused(true);
    const captured = display.getSnapshot();
    raw.applyQuotes([quote("A-USD", 110)]);
    raw.pushTape([tape(2, "A-USD")]);
    hidden.hide("A-USD");

    expect(display.getSnapshot().order).toEqual(["B-USD"]);
    expect(display.getSnapshot().allOrder).toEqual(["B-USD"]);
    expect(display.getSnapshot().tape).toEqual([]);
    hidden.restore("A-USD");

    expect(display.getSnapshot().order).toEqual(captured.order);
    expect(display.getRow("A-USD")?.price).toBe(100);
    expect(display.getSnapshot().tape.map(event => event.id)).toEqual([1]);
    expect(display.getSnapshot().capturedAt).toBe(captured.capturedAt);
    expect(display.getSnapshot().revision).toBe(captured.revision);
    expect(display.getSnapshot().paused).toBe(true);
  });

  it("retains hidden data in a manual paused capture so restoring never requires a refresh", () => {
    const { raw, hidden, display } = harness();
    display.setPaused(true);
    hidden.hide("A-USD");
    vi.advanceTimersByTime(1_000);
    raw.applyScanner(stream([row("B-USD", 95), row("A-USD", 85)]));
    raw.applyQuotes([quote("A-USD", 105)]);
    raw.pushTape([tape(2, "A-USD")]);
    display.capture();
    const captured = display.getSnapshot();
    raw.applyQuotes([quote("A-USD", 110)]);
    raw.pushTape([tape(3, "A-USD")]);

    hidden.restore("A-USD");

    expect(display.getSnapshot().order).toEqual(["B-USD", "A-USD"]);
    expect(display.getRow("A-USD")).toMatchObject({ price: 105, score: 85 });
    expect(display.getSymbol("A-USD")?.quote?.price).toBe(105);
    expect(display.getSnapshot().tape.map(event => event.id)).toEqual([2, 1]);
    expect(display.getSnapshot().capturedAt).toBe(captured.capturedAt);
    expect(display.getSnapshot().revision).toBe(captured.revision);
    expect(display.getSnapshot().paused).toBe(true);
  });

  it("stops the timer and removes source subscriptions on cleanup", () => {
    const { raw, hidden, display, stop } = harness(false);
    expect(vi.getTimerCount()).toBe(1);
    const captured = display.getSnapshot();
    const changed = vi.fn();
    cleanup.push(display.subscribe(changed));
    stop();
    expect(vi.getTimerCount()).toBe(0);
    raw.applySymbols([summary("A-USD")]);
    hidden.hide("A-USD");
    vi.advanceTimersByTime(DISPLAY_INTERVAL_MS * 3);

    expect(display.getSnapshot()).toBe(captured);
    expect(changed).not.toHaveBeenCalled();
  });
});
