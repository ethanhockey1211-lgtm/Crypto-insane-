import { describe, expect, it } from "vitest";
import { StockEntryAlertTracker } from "./stock-entry-alerts";

const NOW = Date.parse("2026-09-09T14:00:00Z");
const COOLDOWN = 600_000;
const DAY = 86_400_000;
const A = "NASDAQ:AAPL", B = "NYSE:UBER", C = "AMEX:SPY";
const encode = (cooldowns: unknown[]) => JSON.stringify({ version: 1, cooldowns });
const stored = (tracker: StockEntryAlertTracker): { symbol: string; at: number }[] => JSON.parse(tracker.serializeCooldowns()).cooldowns;

describe("stock entry alert transitions", () => {
  it("primes the first enabled observation and only announces newly entering symbols", () => {
    const tracker = new StockEntryAlertTracker();
    expect(tracker.update([A], true, NOW)).toEqual([]);
    expect(tracker.update([A, B, B], true, NOW + 1)).toEqual([B]);
    expect(tracker.update([B, A], true, NOW + 2)).toEqual([]);
    expect(stored(tracker)).toEqual([{ symbol: B, at: NOW + 1 }]);
  });

  it("an empty first observation lets the next entry announce", () => {
    const tracker = new StockEntryAlertTracker();
    expect(tracker.update([], true, NOW)).toEqual([]);
    expect(tracker.update([A], true, NOW + 1)).toEqual([A]);
  });

  it("requires a new visit after the cooldown, rather than repeating a continuously eligible stock", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    expect(tracker.update([A], true, NOW + 1)).toEqual([A]);
    tracker.update([], true, NOW + 2);
    expect(tracker.update([A], true, NOW + COOLDOWN)).toEqual([]);
    expect(tracker.update([A], true, NOW + COOLDOWN + 1)).toEqual([]);
    tracker.update([], true, NOW + COOLDOWN + 2);
    expect(tracker.update([A], true, NOW + COOLDOWN + 3)).toEqual([A]);
  });

  it("allows another visit at the exact cooldown boundary and treats symbols independently", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    expect(tracker.update([A], true, NOW + 1)).toEqual([A]);
    expect(tracker.update([B], true, NOW + 2)).toEqual([B]);
    expect(tracker.update([A, B, C], true, NOW + COOLDOWN + 1)).toEqual([A, C]);
  });

  it("shutdown and resume prime existing entries without clearing their cooldowns", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 1);
    expect(tracker.update([B], false, NOW + 2)).toEqual([]);
    expect(tracker.update([A, B], true, NOW + 3)).toEqual([]);
    expect(tracker.update([A, B, C], true, NOW + 4)).toEqual([C]);
    tracker.update([], true, NOW + 5);
    expect(tracker.update([A], true, NOW + 6)).toEqual([]);
  });

  it("availability/baseline resets suppress first-observation replay while keeping cooldowns", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 1);
    tracker.resetBaseline();
    expect(tracker.update([A, B], true, NOW + 2)).toEqual([]);
    tracker.update([], true, NOW + 3);
    expect(tracker.update([A, B], true, NOW + 4)).toEqual([B]);
  });

  it("accepts only already normalized US symbols and does not mutate the input", () => {
    const tracker = new StockEntryAlertTracker();
    const symbols = Object.freeze([A, "NASDAQ:BRK.B", "NYSE:BF-B", "NASDAQ:aapl", "nasdaq:AAPL", " AAPL ", "AAPL", "KRAKEN:BTC", "BTC/USD", "LSE:VOD", "NASDAQ:TOOLONG", "NYSE:A_B", "NYSE:JPM\n", "NYSE:JPM\r"]);
    tracker.update([], true, NOW);
    expect(tracker.update(symbols, true, NOW + 1)).toEqual([A, "NASDAQ:BRK.B", "NYSE:BF-B"]);
    expect(symbols).toHaveLength(14);
  });

  it("supports an explicit cooldown duration", () => {
    const tracker = new StockEntryAlertTracker(100);
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 1);
    tracker.update([], true, NOW + 2);
    expect(tracker.update([A], true, NOW + 100)).toEqual([]);
    tracker.update([], true, NOW + 100);
    expect(tracker.update([A], true, NOW + 101)).toEqual([A]);
  });

  it.each([NaN, Infinity, -1])("falls back to the default for invalid cooldown %s", cooldown => {
    const tracker = new StockEntryAlertTracker(cooldown);
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 1);
    tracker.update([], true, NOW + 2);
    expect(tracker.update([A], true, NOW + 3)).toEqual([]);
  });
});

describe("stock alert cooldown persistence", () => {
  it("survives workspace remount without replaying or bypassing cooldown", () => {
    const original = new StockEntryAlertTracker();
    original.update([], true, NOW);
    original.update([A], true, NOW + 1);
    const tracker = new StockEntryAlertTracker();
    tracker.restoreCooldowns(original.serializeCooldowns(), NOW + 2);
    expect(tracker.update([A], true, NOW + 2)).toEqual([]);
    tracker.update([], true, NOW + 3);
    expect(tracker.update([A], true, NOW + 4)).toEqual([]);
    tracker.update([], true, NOW + 5);
    expect(tracker.update([A], true, NOW + COOLDOWN + 1)).toEqual([A]);
  });

  it.each([null, "", "{bad", "null", "42", "[]", '{"version":2,"cooldowns":[]}', '{"version":1,"cooldowns":{}}', "x".repeat(32_769)])("ignores malformed, obsolete or excessive storage (%s)", raw => {
    const tracker = new StockEntryAlertTracker();
    expect(() => tracker.restoreCooldowns(raw, NOW)).not.toThrow();
    expect(stored(tracker)).toEqual([]);
    expect(tracker.update([A], true, NOW)).toEqual([]);
  });

  it("ignores invalid symbols, malformed rows, nonnumeric times and future/expired timestamps", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.restoreCooldowns(encode([
      null, false, [], { symbol: A }, { at: NOW },
      { symbol: "NASDAQ:aapl", at: NOW }, { symbol: "BTC/USD", at: NOW },
      { symbol: A, at: String(NOW) }, { symbol: A, at: null }, { symbol: A, at: -1 },
      { symbol: A, at: NOW + 1 }, { symbol: A, at: NOW - DAY - 1 },
      { symbol: B, at: NOW }, { symbol: C, at: NOW - DAY },
    ]), NOW);
    expect(stored(tracker)).toEqual([{ symbol: B, at: NOW }, { symbol: C, at: NOW - DAY }]);
    tracker.restoreCooldowns('{"version":1,"cooldowns":[{"symbol":"NASDAQ:AAPL","at":1e999}]}', NOW);
    expect(stored(tracker)).toEqual([]);
  });

  it("keeps the newest valid duplicate timestamp and expires records only after 24 hours", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.restoreCooldowns(encode([{ symbol: A, at: NOW - 1 }, { symbol: A, at: NOW - 2 }, { symbol: A, at: NOW + 1 }]), NOW);
    expect(stored(tracker)).toEqual([{ symbol: A, at: NOW - 1 }]);
    tracker.update([], false, NOW - 1 + DAY);
    expect(stored(tracker)).toHaveLength(1);
    tracker.update([], false, NOW + DAY);
    expect(stored(tracker)).toEqual([]);
  });

  it("bounds observations and persisted records to 100 symbols, retaining newest records on restore", () => {
    const symbols = Array.from({ length: 120 }, (_, index) => `NASDAQ:${String.fromCharCode(65 + Math.floor(index / 26))}${String.fromCharCode(65 + index % 26)}`);
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    expect(tracker.update(symbols, true, NOW + 1)).toHaveLength(100);
    expect(stored(tracker)).toHaveLength(100);
    tracker.restoreCooldowns(encode(symbols.map((symbol, index) => ({ symbol, at: NOW + index }))), NOW + 120);
    expect(stored(tracker)).toHaveLength(100);
    expect(stored(tracker)[0]).toEqual({ symbol: symbols[119], at: NOW + 119 });
    expect(stored(tracker).at(-1)).toEqual({ symbol: symbols[20], at: NOW + 20 });
    expect(tracker.serializeCooldowns()).toBe(tracker.serializeCooldowns());
  });
});

describe("stock alert clock integrity", () => {
  it.each([NaN, Infinity, -Infinity, -1, 8.64e15 + 1])("cannot emit at invalid time %s or replay entries when time recovers", now => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 1);
    expect(tracker.update([A, B], true, now)).toEqual([]);
    expect(tracker.update([A, B], true, NOW + 2)).toEqual([]);
    tracker.update([], true, NOW + 3);
    expect(tracker.update([A, B], true, NOW + 4)).toEqual([B]);
  });

  it("does not alert while the clock moves backward, even for previously unseen symbols", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 10);
    expect(tracker.update([B], true, NOW + 9)).toEqual([]);
    expect(tracker.update([C], true, NOW + 8)).toEqual([]);
    expect(tracker.update([A, B, C], true, NOW + 10)).toEqual([]);
    tracker.update([], true, NOW + 11);
    expect(tracker.update([A, B, C], true, NOW + 12)).toEqual([B, C]);
  });

  it("rejects restore attempts with invalid or reversed time without erasing active cooldowns", () => {
    const tracker = new StockEntryAlertTracker();
    tracker.update([], true, NOW);
    tracker.update([A], true, NOW + 10);
    for (const now of [NaN, Infinity, NOW + 9]) tracker.restoreCooldowns(null, now);
    expect(stored(tracker)).toEqual([{ symbol: A, at: NOW + 10 }]);
    expect(tracker.update([A], true, NOW + 11)).toEqual([]);
    tracker.update([], true, NOW + 12);
    expect(tracker.update([A], true, NOW + 13)).toEqual([]);
  });
});
