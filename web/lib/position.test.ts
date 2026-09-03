import { describe, expect, it } from "vitest";
import { sizePosition } from "./position";

describe("sizePosition", () => {
  it("sizes by risk budget: $3,000 account, 1% risk, entry 1.405, stop 1.395", () => {
    const r = sizePosition({ accountBalance: 3000, maxPositionUsd: 10_000, maxRiskPct: 0.01, entry: 1.405, stop: 1.395, targets: [1.43] });
    expect(r.valid).toBe(true);
    expect(r.riskPerUnit).toBeCloseTo(0.01, 9);
    expect(r.maxRiskUsd).toBe(30);
    expect(r.quantity).toBeCloseTo(3000, 6);          // $30 / $0.01
    expect(r.capitalDeployed).toBeCloseTo(4215, 6);   // 3000 × 1.405
    expect(r.dollarRisk).toBeCloseTo(30, 6);
    expect(r.riskPctOfAccount).toBeCloseTo(0.01, 9);
    expect(r.boundBy).toBe("risk");
    expect(r.targets[0].rewardRatio).toBeCloseTo(2.5, 6);
    expect(r.targets[0].profitUsd).toBeCloseTo(75, 6);
  });

  it("caps at the maximum position when the risk budget would allow more", () => {
    const r = sizePosition({ accountBalance: 3000, maxPositionUsd: 3000, maxRiskPct: 0.01, entry: 1.405, stop: 1.395, targets: [1.43, 1.45] });
    expect(r.boundBy).toBe("position");
    expect(r.capitalDeployed).toBeCloseTo(3000, 6);
    expect(r.quantity).toBeCloseTo(3000 / 1.405, 6);
    expect(r.dollarRisk).toBeLessThan(30);
    expect(r.targets).toHaveLength(2);
  });

  it("includes fees in risk and subtracts them from profit", () => {
    const r = sizePosition({ accountBalance: 10_000, maxPositionUsd: 100_000, maxRiskPct: 0.01, entry: 100, stop: 98, targets: [104], feePct: 0.001 });
    expect(r.quantity).toBe(50);
    expect(r.fees).toBeCloseTo(5, 9);            // 5000 notional × 0.1%
    expect(r.dollarRisk).toBeCloseTo(105, 9);
    expect(r.targets[0].profitUsd).toBeCloseTo(195, 9);
  });

  it("rejects stops at or above entry and non-positive inputs with a reason", () => {
    expect(sizePosition({ accountBalance: 1000, maxPositionUsd: 1000, maxRiskPct: 0.01, entry: 10, stop: 10, targets: [] }).reason).toMatch(/below entry/);
    expect(sizePosition({ accountBalance: 0, maxPositionUsd: 1000, maxRiskPct: 0.01, entry: 10, stop: 9, targets: [] }).reason).toMatch(/Account/);
    expect(sizePosition({ accountBalance: 1000, maxPositionUsd: 1000, maxRiskPct: 0, entry: 10, stop: 9, targets: [] }).valid).toBe(false);
  });
});
