/**
 * Position sizing and P/L math. Pure functions; the same numbers the UI shows.
 * Long-only for now (the scanner is long-biased); every value is derived from the user's own inputs.
 */
export interface PositionInputs {
  accountBalance: number;
  maxPositionUsd: number;
  /** Maximum risk per trade as a fraction of the account (0.01 = 1%). */
  maxRiskPct: number;
  entry: number;
  stop: number;
  targets: number[];
  /** Round-trip fee estimate as a fraction of notional (0.001 = 10 bps). */
  feePct?: number;
}

export interface PositionResult {
  valid: boolean;
  reason?: string;
  riskPerUnit: number;
  maxRiskUsd: number;
  quantity: number;
  capitalDeployed: number;
  dollarRisk: number;
  riskPctOfAccount: number;
  positionPctOfAccount: number;
  /** Which constraint bound the size: "risk" or "position". */
  boundBy: "risk" | "position" | "none";
  targets: { price: number; profitUsd: number; rewardRatio: number; pct: number }[];
  fees: number;
}

export function sizePosition(i: PositionInputs): PositionResult {
  const empty: PositionResult = { valid: false, riskPerUnit: 0, maxRiskUsd: 0, quantity: 0, capitalDeployed: 0, dollarRisk: 0, riskPctOfAccount: 0, positionPctOfAccount: 0, boundBy: "none", targets: [], fees: 0 };
  if (!(i.entry > 0)) return { ...empty, reason: "Entry must be above zero." };
  if (!(i.stop > 0)) return { ...empty, reason: "Stop must be above zero." };
  if (i.stop >= i.entry) return { ...empty, reason: "Stop must be below entry for a long." };
  if (!(i.accountBalance > 0)) return { ...empty, reason: "Account balance must be above zero." };
  const riskPerUnit = i.entry - i.stop;
  const maxRiskUsd = i.accountBalance * Math.max(0, i.maxRiskPct);
  const byRisk = maxRiskUsd / riskPerUnit;
  const byPosition = i.maxPositionUsd > 0 ? i.maxPositionUsd / i.entry : Number.POSITIVE_INFINITY;
  const quantity = Math.max(0, Math.min(byRisk, byPosition));
  const boundBy: PositionResult["boundBy"] = quantity === 0 ? "none" : byPosition < byRisk ? "position" : "risk";
  const capitalDeployed = quantity * i.entry;
  const feePct = i.feePct ?? 0;
  const fees = capitalDeployed * feePct;
  const dollarRisk = quantity * riskPerUnit + fees;
  const targets = i.targets.filter((t) => isFinite(t) && t > 0).map((t) => ({
    price: t,
    profitUsd: quantity * (t - i.entry) - fees,
    rewardRatio: (t - i.entry) / riskPerUnit,
    pct: (t - i.entry) / i.entry,
  }));
  return {
    valid: quantity > 0,
    reason: quantity > 0 ? undefined : "Risk budget is zero.",
    riskPerUnit,
    maxRiskUsd,
    quantity,
    capitalDeployed,
    dollarRisk,
    riskPctOfAccount: i.accountBalance > 0 ? dollarRisk / i.accountBalance : 0,
    positionPctOfAccount: i.accountBalance > 0 ? capitalDeployed / i.accountBalance : 0,
    boundBy,
    targets,
    fees,
  };
}
