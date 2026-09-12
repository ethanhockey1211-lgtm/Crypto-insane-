import { describe, expect, it } from "vitest";
import { normalizeMarketSymbol, qualifies, snapshotIsFresh } from "./product-model";
import type { ScannerRow } from "./types";

describe("customer market presentation", () => {
  it.each(["btc", "BTC-USD", "btc/usd", " btc / usd "])("normalizes %s to the engine's canonical symbol", input => {
    expect(normalizeMarketSymbol(input)).toBe("BTC-USD");
  });
  it("does not classify high-score but cost-blocked markets as qualifying", () => {
    const row = { score: 99, stale: false, doNotChase: false, executionStatus: "Blocked" } as ScannerRow;
    expect(qualifies(row)).toBe(false);
    expect(qualifies({ ...row, executionStatus: "Watch" })).toBe(true);
    expect(qualifies({ ...row, executionStatus: "Watch", stale: true })).toBe(false);
  });
  it("expires cached rows from their snapshot time, even if the overview poll succeeds", () => {
    const now = Date.parse("2026-09-12T12:00:00Z");
    expect(snapshotIsFresh("2026-09-12T11:59:29Z", now)).toBe(false);
    expect(snapshotIsFresh("2026-09-12T11:59:50Z", now)).toBe(true);
    expect(snapshotIsFresh("2026-09-12T12:00:03Z", now)).toBe(false);
    expect(snapshotIsFresh(null, now)).toBe(false);
  });
});
