import { describe, expect, it } from "vitest";
import { decision, decisionSummary, executionBlockers, scannerIsFresh, shortlist } from "./decision";
import type { ScannerRow } from "./types";

const row = (changes: Partial<ScannerRow> = {}): ScannerRow => ({
  rank: 1, symbol: "TEST-USD", score: 90, setup: "Breakout", confidence: "High", price: 100,
  entry: 100, stop: 98, target1: 106, rr: 3, r1m: null, r5m: null, r15m: null, r1h: null, r24h: null,
  relVol: 2, breakout: "Confirmed", doNotChase: false, stale: false, vwapDev: null, volume24h: 1e7,
  keyLevel: 99, trend: null, components: [], penalty: 0, entryState: "InZone", chaseCeiling: 101,
  executionStatus: "Watch", netRewardRatio: 2, setupBias: "Bullish", ...changes,
});

describe("decision shortlist", () => {
  it("fails closed when data or the assessment is unavailable", () => {
    expect(decision(row(), false).state).toBe("unavailable");
    expect(decision(row({ stale: true }), true).state).toBe("unavailable");
    expect(decision(row({ executionStatus: undefined }), true).state).toBe("unavailable");
    expect(decision(row({ netRewardRatio: NaN }), true).state).toBe("unavailable");
    expect(decision(row({ entryState: null }), true).state).toBe("unavailable");
    expect(decision(row({ entry: null }), true).state).toBe("unavailable");
  });
  it("never turns breakout confirmation into an automatic buy instruction", () => {
    expect(decision(row(), true).label).toBe("Check confirmation");
    expect(decision(row({ entryState: "Watch" }), true).state).toBe("wait");
    expect(decision(row({ entryState: "Late" }), true).label).toBe("Wait for a pullback");
    expect(decision(row({ executionStatus: "Blocked" }), true).state).toBe("avoid");
    expect(decision(row({ entryState: "Chase" }), true).state).toBe("avoid");
  });
  it("caps the shortlist at three and prioritizes in-zone candidates without rescuing blocked scores", () => {
    const rows = [row({ symbol: "BLOCKED", score: 100, executionStatus: "Blocked" }),
      row({ symbol: "WAIT", score: 99, entryState: "Watch" }),
      ...["A", "B", "C", "D"].map(symbol => row({ symbol }))];
    expect(shortlist(rows, true).map(r => r.symbol)).toEqual(["A", "B", "C"]);
    expect(shortlist(rows, false)).toEqual([]);
  });
  it("expires frozen scanner snapshots and rejects invalid/future timestamps", () => {
    const now = Date.parse("2026-09-07T00:00:20Z");
    expect(scannerIsFresh("2026-09-07T00:00:15Z", now)).toBe(true);
    expect(scannerIsFresh("2026-09-07T00:00:00Z", now)).toBe(false);
    expect(scannerIsFresh("2026-09-07T00:00:30Z", now)).toBe(false);
    expect(scannerIsFresh("bad", now)).toBe(false);
  });
  it("counts actual decisions and separates blocked plans from eligible candidates", () => {
    const rows = [row({ symbol: "READY" }), row({ symbol: "WAIT", entryState: "Watch" }),
      row({ symbol: "LATE", entryState: "Late" }), row({ symbol: "STALE", stale: true }),
      row({ symbol: "MISSING", netRewardRatio: null }), row({ symbol: "CHASE", entryState: "Chase" }),
      row({ symbol: "BLOCKED", executionStatus: "Blocked", score: 99 })];
    const summary = decisionSummary(rows, true);
    expect(summary.ready.map(r => r.symbol)).toEqual(["READY"]);
    expect(summary.waiting.map(r => r.symbol)).toEqual(["LATE", "WAIT"]);
    expect(summary.blocked.map(r => r.symbol)).toEqual(["CHASE", "BLOCKED"]);
    expect(summary.unavailable.map(r => r.symbol)).toEqual(["STALE", "MISSING"]);
    expect(summary.developing.map(r => r.symbol)).toEqual(["BLOCKED", "CHASE"]);
    const disconnected = decisionSummary(rows, false);
    expect(disconnected.ready).toEqual([]);
    expect(disconnected.waiting).toEqual([]);
    expect(disconnected.developing).toEqual([]);
    expect(disconnected.blockers).toEqual([]);
    expect(disconnected.unavailable).toHaveLength(rows.length);
  });
  it("shows all distinct blockers, including checks after the first rejection", () => {
    const rows = [row({ executionStatus: "Blocked", executionReasons: ["Low score", "Wide spread", "Wide spread"] }),
      row({ symbol: "SECOND", executionStatus: "Blocked", executionReason: "Low score" })];
    expect(decisionSummary(rows, true).blockers).toEqual([["Low score", 2], ["Wide spread", 1]]);
    expect(executionBlockers(row({ entryState: "Chase", executionReason: "Checks passed" }))).toEqual(["Price is above the no-chase ceiling."]);
  });
  it("only develops fresh bullish plans and does not cap the eligible lists", () => {
    const ready = ["A", "B", "C", "D", "E"].map(symbol => row({ symbol }));
    const blocked = { executionStatus: "Blocked" as const };
    const rows = [...ready, row({ ...blocked, symbol: "BULL" }),
      row({ ...blocked, symbol: "BEAR", setupBias: "Bearish" }),
      row({ ...blocked, symbol: "UNKNOWN", setupBias: undefined }),
      row({ ...blocked, symbol: "STALE", stale: true }),
      row({ ...blocked, symbol: "NO_PLAN", entry: null }),
      row({ ...blocked, symbol: "BAD_PLAN", stop: 101 }),
      row({ ...blocked, symbol: "NONFINITE", target1: Infinity })];
    const summary = decisionSummary(rows, true);
    expect(summary.ready).toHaveLength(5);
    expect(summary.developing.map(r => r.symbol)).toEqual(["BULL"]);
  });
});
