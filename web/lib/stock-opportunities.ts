import { stockSetupIsCurrent, type StockScanResponse, type StockSetup } from "./stock-scanner";
import { sizeStockPlan, type StockSizingResult } from "./stocks";

export interface StockOpportunitySettings {
  account: number;
  cash: number;
  /** Percentage points: 1 means one percent of account value. Zero means not supplied. */
  riskPct: number;
  /** Total round-trip dollars per share for spread, slippage and fees, counted once per outcome. */
  costPerShare: number;
}

export interface StockOpportunity {
  setup: StockSetup;
  eligible: boolean;
  reason: string;
  entry: number | null;
  /** A gross 1R review checkpoint, not a price forecast or automatic partial exit. */
  target1: number | null;
  target2: number | null;
  /** Stop loss plus the round-trip cost buffer, per share. */
  riskPerShare: number | null;
  /** Target gain less the round-trip cost buffer, per share. */
  rewardPerShare: number | null;
  netRewardRisk: number | null;
  /** Gross percentage move from the conservative entry to target2. */
  targetMovePct: number | null;
  /** Required mathematical hit rate in percentage points, never an estimated win probability. */
  breakEvenWinRate: number | null;
  position: StockSizingResult | null;
  rankReason: string;
}

export type StockOpportunityRankMode = "quality" | "target-profit" | "net-rr";

/** A research filter, not a calibrated profitability threshold or a historical performance claim. */
export const MIN_STOCK_NET_REWARD_RISK = 1.5;

const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

function empty(setup: StockSetup, reason: string): StockOpportunity {
  return { setup, eligible: false, reason, entry: null, target1: null, target2: null,
    riskPerShare: null, rewardPerShare: null, netRewardRisk: null, targetMovePct: null,
    breakEvenWinRate: null, position: null, rankReason: reason };
}

function settingsError(settings: StockOpportunitySettings): string | null {
  const values = [settings.account, settings.cash, settings.riskPct, settings.costPerShare];
  if (!values.every(finite)) return "Enter finite numbers for account value, cash, risk and the round-trip cost buffer.";
  if (values.some(value => value < 0)) return "Account value, cash, risk and the round-trip cost buffer cannot be negative.";
  if (settings.riskPct > 100) return "Risk cannot exceed 100 percent of account value.";
  return null;
}

/**
 * Add explicit hypothetical trade economics to current, confirmed long setups. Entries use the
 * upper edge of the zone so sizing never depends on obtaining its cheapest fill. Empty budget
 * fields preserve per-share research; only a complete positive budget enables whole-share sizing.
 * No stale, failed or unconfirmed plan gets prices or actionable economics in this result.
 */
export function buildStockOpportunities(
  setups: readonly StockSetup[], response: StockScanResponse | null, nowMs: number, settings: StockOpportunitySettings,
): StockOpportunity[] {
  const invalidSettings = settingsError(settings);
  const hasBudget = settings.account > 0 && settings.cash > 0 && settings.riskPct > 0;
  return setups.map(setup => {
    if (!finite(nowMs) || !stockSetupIsCurrent(setup, response, nowMs)) {
      return empty(setup, setup.state === "entry-zone"
        ? "This entry is no longer current. Refresh the scan and wait for fresh trade and quote data."
        : setup.reasons.join(" ") || "Wait for a confirmed entry zone before reviewing trade economics.");
    }
    if (invalidSettings) return empty(setup, invalidSettings);

    const entry = setup.entryMax!, target2 = setup.target!;
    const grossRisk = entry - setup.stop!;
    const target1 = entry + grossRisk;
    const riskPerShare = grossRisk + settings.costPerShare;
    const rewardPerShare = target2 - entry - settings.costPerShare;
    const netRewardRisk = rewardPerShare / riskPerShare;
    const targetMovePct = ((target2 - entry) / entry) * 100;
    // Equivalent to risk / (risk + reward), without overflowing the dollar sum.
    const breakEvenWinRate = rewardPerShare > 0 ? (1 / (1 + netRewardRisk)) * 100 : null;
    if (![entry, target1, target2, grossRisk, riskPerShare, rewardPerShare, netRewardRisk, targetMovePct].every(finite)
      || grossRisk <= 0 || riskPerShare <= 0 || target1 <= entry
      || (breakEvenWinRate !== null && (!finite(breakEvenWinRate) || breakEvenWinRate <= 0 || breakEvenWinRate >= 100))) {
      return empty(setup, "Trade economics are outside the supported numeric range.");
    }

    const out: StockOpportunity = { setup, eligible: false, reason: "", entry, target1, target2,
      riskPerShare, rewardPerShare, netRewardRisk, targetMovePct, breakEvenWinRate, position: null, rankReason: "" };
    if (rewardPerShare <= 0) {
      out.reason = "The round-trip cost buffer consumes the gain to the target. Wait for a better setup.";
    } else if (netRewardRisk < MIN_STOCK_NET_REWARD_RISK) {
      out.reason = `After costs, this plan falls below the ${MIN_STOCK_NET_REWARD_RISK.toFixed(1)}:1 reward/risk research filter.`;
    } else {
      if (hasBudget) {
        out.position = sizeStockPlan({ ...settings, entry, stop: setup.stop!, target: target2 });
      }
      if (out.position && !out.position.valid) {
        out.reason = out.position.reason || "The supplied budget cannot support this position.";
      } else {
        out.eligible = true;
        out.reason = hasBudget
          ? "Current confirmed setup clears the cost filter and fits your whole-share risk and cash limits."
          : "Current confirmed setup clears the cost filter. Add account value, cash and risk to calculate shares and target dollars.";
      }
    }
    out.rankReason = qualityReason(out);
    return out;
  });
}

function qualityReason(item: StockOpportunity): string {
  if (!item.eligible) return item.reason;
  return `${finite(item.setup.score) ? item.setup.score : 0} checklist points; ${item.netRewardRisk!.toFixed(2)}:1 after-cost reward/risk. Points measure the current checklist, not a win probability.`;
}

function metric(value: number | null | undefined): number {
  return finite(value) ? value : Number.NEGATIVE_INFINITY;
}

function compareDescending(left: number, right: number): number {
  return left === right ? 0 : left > right ? -1 : 1;
}

/** Eligible plans always precede rejected plans. Equal comparisons retain the input reading order. */
export function rankStockOpportunities(items: readonly StockOpportunity[], mode: StockOpportunityRankMode): StockOpportunity[] {
  return items.map((item, index) => ({ item, index })).sort((left, right) => {
    const a = left.item, b = right.item;
    if (a.eligible !== b.eligible) return a.eligible ? -1 : 1;
    const byScore = compareDescending(metric(a.setup.score), metric(b.setup.score));
    const byRatio = compareDescending(metric(a.netRewardRisk), metric(b.netRewardRisk));
    if (mode === "target-profit") {
      const byReward = compareDescending(metric(a.position?.valid ? a.position.reward : null), metric(b.position?.valid ? b.position.reward : null));
      return byReward || byScore || byRatio || left.index - right.index;
    }
    if (mode === "net-rr") return byRatio || byScore || left.index - right.index;
    return byScore || byRatio || left.index - right.index;
  }).map(({ item }) => {
    let rankReason = qualityReason(item);
    if (item.eligible && mode === "target-profit") {
      rankReason = item.position?.valid
        ? `$${item.position.reward.toFixed(2)} if all ${item.position.shares} shares exit at the target, after the cost buffer; $${item.position.risk.toFixed(2)} planned stop loss. This ranks target payoff, not expected profit.`
        : "Add a complete budget to rank this setup by target dollars. Per-share research remains available.";
    } else if (item.eligible && mode === "net-rr") {
      rankReason = `${item.netRewardRisk!.toFixed(2)}:1 after-cost reward/risk, using the upper entry-zone price. A larger ratio does not establish a greater chance of reaching the target.`;
    }
    return { ...item, rankReason };
  });
}
