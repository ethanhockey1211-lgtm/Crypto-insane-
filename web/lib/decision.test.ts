import { describe, expect, it } from "vitest";
import { decision, scannerIsFresh, shortlist } from "./decision";
import type { ScannerRow } from "./types";

const row = (changes: Partial<ScannerRow> = {}): ScannerRow => ({
  rank: 1, symbol: "TEST-USD", score: 90, setup: "Breakout", confidence: "High", price: 100,
  entry: 100, stop: 98, target1: 106, rr: 3, r1m: null, r5m: null, r15m: null, r1h: null, r24h: null,
  relVol: 2, breakout: "Confirmed", doNotChase: false, stale: false, vwapDev: null, volume24h: 1e7,
  keyLevel: 99, trend: null, components: [], penalty: 0, entryState: "InZone", chaseCeiling: 101,
  executionStatus: "Watch", netRewardRatio: 2, ...changes,
});

describe("decision shortlist", () => {
  it("fails closed when data or the assessment is unavailable", () => {
    expect(decision(row(), false).state).toBe("unavailable");
    expect(decision(row({ stale: true }), true).state).toBe("unavailable");
    expect(decision(row({ executionStatus: undefined }), true).state).toBe("unavailable");
    expect(decision(row({ netRewardRatio: NaN }), true).state).toBe("unavailable");
  });
  it("never turns breakout confirmation into an automatic buy instruction", () => {
    expect(decision(row(), true).label).toBe("Check confirmation");
    expect(decision(row({ entryState: "Watch" }), true).state).toBe("wait");
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
});
