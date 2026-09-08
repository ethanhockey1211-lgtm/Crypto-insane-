import { describe, expect, it } from "vitest";
import { EntryAlertTracker } from "./entry-alerts";
import type { ScannerRow } from "./types";

const base = Date.parse("2026-09-08T12:00:00Z");
const row = (changes: Partial<ScannerRow> = {}): ScannerRow => ({
  symbol: "BTC-USD", rank: 1, score: 80, setup: "Breakout", confidence: "High", price: 100,
  assessedPrice: 100, entry: 100, entryLow: 99, entryHigh: 101, stop: 97, target1: 108, rr: 2,
  netRewardRatio: 2, executionStatus: "Watch", entryState: "InZone", setupBias: "Bullish",
  stale: false, doNotChase: false, r1m: null, r5m: null, r15m: null, r1h: null, r24h: null,
  relVol: null, breakout: null, vwapDev: null, volume24h: null, keyLevel: null, trend: null,
  components: [], penalty: 0, chaseCeiling: 103, ...changes,
});
const tick = (tracker: EntryAlertTracker, seconds: number, rows: ScannerRow[], connected = true) =>
  tracker.update(rows, connected, new Date(base + seconds * 1000).toISOString(), base + seconds * 1000);

describe("entry transition alerts", () => {
  it("silences initial in-zone setups and alerts only after a new visit holds across fresh cycles", () => {
    const t = new EntryAlertTracker();
    expect(tick(t, 0, [row()])).toEqual([]);
    expect(tick(t, 3, [row()])).toEqual([]);
    tick(t, 4, [row({ entryState: "Watch" })]);
    expect(tick(t, 5, [row()])).toEqual([]);
    expect(tick(t, 6, [row()])).toEqual([]);
    expect(tick(t, 7, [row()])).toHaveLength(1);
    expect(tick(t, 20, [row()])).toEqual([]);
  });
  it("requires continuous readiness, valid geometry, and the current price in the assessed zone", () => {
    for (const invalid of [{ stale: true }, { executionStatus: "Blocked" as const }, { doNotChase: true },
      { price: 105 }, { assessedPrice: 102 }, { entryLow: NaN }, { entryHigh: null }, { stop: 102 },
      { stop: 99.5 }, { target1: 100.5 }, { entry: 98 }]) {
      const t = new EntryAlertTracker(); tick(t, 0, []); tick(t, 1, [row()]);
      expect(tick(t, 3, [row(invalid)])).toEqual([]);
      expect(tick(t, 4, [row()])).toEqual([]);
      expect(tick(t, 6, [row()])).toHaveLength(1);
    }
  });
  it("does not fire on repeated, old, stale or disconnected snapshots and re-baselines after reconnect", () => {
    const t = new EntryAlertTracker(); tick(t, 0, []); tick(t, 1, [row()]);
    expect(t.update([row()], true, new Date(base + 1000).toISOString(), base + 5000)).toEqual([]);
    expect(t.update([row()], true, new Date(base + 1000).toISOString(), base + 15000)).toEqual([]);
    expect(tick(t, 16, [row()])).toEqual([]);
    tick(t, 17, [], false);
    expect(tick(t, 18, [row()])).toEqual([]);
    expect(tick(t, 21, [row()])).toEqual([]);
  });
  it("deduplicates each symbol for ten minutes without replaying a continuously ready setup", () => {
    const t = new EntryAlertTracker(); tick(t, 0, []); tick(t, 1, [row()]);
    expect(tick(t, 3, [row()])).toHaveLength(1);
    tick(t, 4, []); tick(t, 5, [row()]);
    expect(tick(t, 7, [row()])).toEqual([]);
    expect(tick(t, 610, [row()])).toEqual([]);
    tick(t, 611, []); tick(t, 612, [row()]);
    expect(tick(t, 614, [row()])).toHaveLength(1);
  });
  it("re-baselines after an unobserved gap instead of treating it as a continuous hold", () => {
    const t = new EntryAlertTracker(); tick(t, 0, []); tick(t, 1, [row()]);
    expect(tick(t, 30, [row()])).toEqual([]);
    expect(tick(t, 33, [row()])).toEqual([]);
    tick(t, 34, []); tick(t, 35, [row()]);
    expect(tick(t, 37, [row()])).toHaveLength(1);
  });
});
