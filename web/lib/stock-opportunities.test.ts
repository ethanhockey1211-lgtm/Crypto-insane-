import { describe, expect, it } from "vitest";
import { buildStockOpportunities, MIN_STOCK_NET_REWARD_RISK, rankStockOpportunities, type StockOpportunity, type StockOpportunitySettings } from "./stock-opportunities";
import type { StockMarketData, StockScanResponse, StockSetup } from "./stock-scanner";

const NOW = Date.parse("2026-09-09T14:05:05Z");
const iso = (value: number) => new Date(value).toISOString();
const SETTINGS: StockOpportunitySettings = { account: 10_000, cash: 5_000, riskPct: 1, costPerShare: 0.1 };
function setup(ticker = "AAPL", score = 80): StockSetup {
  return { symbol: `NASDAQ:${ticker}`, ticker, name: ticker, setup: "Breakout", state: "entry-zone", score,
    price: 99.9, changePct: 1, vwap: 99, ema9: 99.5, ema20: 99, atr: 1, relativeVolume: 2, spreadPct: 0.02,
    entry: 99.8, entryMax: 100, stop: 99, target: 102, rewardRisk: 2,
    barAt: "2026-09-09T14:04:00Z", quoteAt: iso(NOW), reasons: [], evidence: [] };
}
function row(ticker = "AAPL"): StockMarketData {
  return { ticker, bars: [], latestTrade: { price: 99.9, at: iso(NOW) },
    latestQuote: { bid: 99.89, ask: 99.91, bidSize: 10, askSize: 10, at: iso(NOW) },
    previousClose: 99, dayVolume: 10_000, historyComplete: true };
}
function response(tickers = ["AAPL"]): StockScanResponse {
  return { status: "ready", provider: "Alpaca", feed: "iex", asOf: iso(NOW), message: null, rows: tickers.map(row) };
}
function build(settings = SETTINGS, stock = setup(), data: StockScanResponse | null = response(), now = NOW): StockOpportunity {
  return buildStockOpportunities([stock], data, now, settings)[0];
}
function noEconomics(item: StockOpportunity): void {
  expect(item.eligible).toBe(false);
  expect([item.entry, item.target1, item.target2, item.riskPerShare, item.rewardPerShare, item.netRewardRisk,
    item.targetMovePct, item.breakEvenWinRate, item.position]).toEqual(Array(9).fill(null));
}

describe("stock opportunity economics", () => {
  it("uses the conservative entry ceiling and counts one round-trip cost in each outcome", () => {
    const item = build();
    expect(item.eligible).toBe(true);
    expect(item.entry).toBe(100);
    expect(item.target1).toBe(101);
    expect(item.target2).toBe(102);
    expect(item.riskPerShare).toBeCloseTo(1.1);
    expect(item.rewardPerShare).toBeCloseTo(1.9);
    expect(item.netRewardRisk).toBeCloseTo(1.9 / 1.1);
    expect(item.targetMovePct).toBeCloseTo(2);
    expect(item.breakEvenWinRate).toBeCloseTo(100 * 1.1 / 3);
  });

  it("sizes whole shares to the tighter cash limit while reserving the full cost buffer", () => {
    const item = build();
    expect(item.position?.valid).toBe(true);
    expect(item.position?.shares).toBe(49);
    expect(item.position?.capital).toBeCloseTo(49 * 100.1);
    expect(item.position?.risk).toBeCloseTo(49 * 1.1);
    expect(item.position?.reward).toBeCloseTo(49 * 1.9);
    expect(item.position!.capital).toBeLessThanOrEqual(SETTINGS.cash);
    expect(item.position!.risk).toBeLessThanOrEqual(100);
  });

  it("uses the risk limit when it is tighter than cash", () => {
    const item = build({ ...SETTINGS, cash: 50_000 });
    expect(item.position?.shares).toBe(90);
    expect(item.position!.risk).toBeLessThanOrEqual(100);
    expect(item.position!.shares * 1.1 + 1.1).toBeGreaterThan(100);
  });

  it("keeps a qualifying market setup visible when the budget cannot cover one whole share", () => {
    for (const settings of [{ ...SETTINGS, cash: 100 }, { ...SETTINGS, account: 1 }]) {
      const item = build(settings);
      expect(item.eligible).toBe(true);
      expect(item.position?.valid).toBe(false);
      expect(item.netRewardRisk).toBeGreaterThan(1.5);
      expect(item.reason).toContain("one whole share");
      expect(item.entry).toBe(100);
    }
  });

  it.each([
    { ...SETTINGS, account: 0, cash: 0 }, { ...SETTINGS, account: 0 },
    { ...SETTINGS, cash: 0 }, { ...SETTINGS, riskPct: 0 },
  ])("keeps per-share research when a budget field is not yet supplied: %j", settings => {
    const item = build(settings);
    expect(item.eligible).toBe(true);
    expect(item.position).toBeNull();
    expect(item.entry).toBe(100);
    expect(item.reason).toContain("Add account value, cash and risk");
  });

  it("fails closed on negative, nonfinite or out-of-range settings", () => {
    for (const key of ["account", "cash", "riskPct", "costPerShare"] as const) {
      for (const value of [-1, NaN, Infinity, -Infinity]) noEconomics(build({ ...SETTINGS, [key]: value }));
    }
    noEconomics(build({ ...SETTINGS, riskPct: 101 }));
  });

  it("applies the explicit 1.5 net reward/risk research filter after costs", () => {
    expect(MIN_STOCK_NET_REWARD_RISK).toBe(1.5);
    const below = build({ ...SETTINGS, costPerShare: 0.3 });
    expect(below.eligible).toBe(false);
    expect(below.reason).toContain("1.5:1");
    expect(below.netRewardRisk).toBeCloseTo(1.7 / 1.3);
    expect(below.position).toBeNull();
    expect(build({ ...SETTINGS, costPerShare: 0.2 }).eligible).toBe(true);
  });

  it("does not publish a break-even hit rate when costs consume the target gain", () => {
    for (const costPerShare of [2, 3]) {
      const item = build({ ...SETTINGS, costPerShare });
      expect(item.eligible).toBe(false);
      expect(item.reason).toContain("consumes");
      expect(item.breakEvenWinRate).toBeNull();
      expect(item.position).toBeNull();
    }
  });

  it("calculates a required hit rate independently of the heuristic score", () => {
    const low = build({ ...SETTINGS, costPerShare: 0 }, setup("AAPL", 25));
    const high = build({ ...SETTINGS, costPerShare: 0 }, setup("AAPL", 100));
    expect(low.breakEvenWinRate).toBeCloseTo(100 / 3);
    expect(high.breakEvenWinRate).toBe(low.breakEvenWinRate);
    expect(high.rankReason).toContain("not a win probability");
  });

  it("publishes no economics for failed scans, missing scans or non-entry states", () => {
    noEconomics(build(SETTINGS, setup(), null));
    noEconomics(build(SETTINGS, setup(), { ...response(), status: "error" }));
    noEconomics(build(SETTINGS, setup(), { ...response(), status: "not-configured" }));
    for (const state of ["blocked", "unavailable", "watch", "extended"] as const) {
      noEconomics(build(SETTINGS, { ...setup(), state, reasons: ["Wait for the required checks."] }));
    }
  });

  it("publishes no economics for stale or materially future source data", () => {
    noEconomics(build(SETTINGS, setup(), response(), NOW + 46_000));
    noEconomics(build(SETTINGS, setup(), { ...response(), asOf: iso(NOW - 61_000) }));
    noEconomics(build(SETTINGS, setup(), { ...response(), asOf: iso(NOW + 5_001) }));
    const futureQuote = response(); futureQuote.rows[0].latestQuote!.at = iso(NOW + 5_001);
    noEconomics(build(SETTINGS, setup(), futureQuote));
    noEconomics(build(SETTINGS, setup(), response(), NaN));
  });

  it("does not publish overflowed plan arithmetic", () => {
    const stock = { ...setup(), entry: 1e308, entryMax: 1.1e308, stop: 1, target: 1.7e308 };
    const data = response();
    data.rows[0].latestTrade!.price = 1.05e308;
    data.rows[0].latestQuote!.bid = 1.049e308; data.rows[0].latestQuote!.ask = 1.05e308;
    noEconomics(build(SETTINGS, stock, data));
  });

  it("does not mutate inputs or reorder setups while building economics", () => {
    const stocks = [setup("MSFT", 90), setup("AAPL", 100)];
    const data = response(["MSFT", "AAPL"]);
    const before = structuredClone({ stocks, data, settings: SETTINGS });
    const items = buildStockOpportunities(stocks, data, NOW, SETTINGS);
    expect(items.map(item => item.setup.ticker)).toEqual(["MSFT", "AAPL"]);
    expect({ stocks, data, settings: SETTINGS }).toEqual(before);
  });
});

describe("stock opportunity ranking", () => {
  function items(): StockOpportunity[] {
    return buildStockOpportunities([setup("AAPL", 90), setup("MSFT", 80), setup("NVDA", 70)], response(["AAPL", "MSFT", "NVDA"]), NOW, SETTINGS);
  }

  it("ranks quality by checklist points then after-cost ratio", () => {
    const values = items(); values[0].setup.score = 80; values[1].netRewardRisk = 2;
    expect(rankStockOpportunities(values, "quality").map(item => item.setup.ticker)).toEqual(["MSFT", "AAPL", "NVDA"]);
  });

  it("ranks sized target payoff without calling it expected profit", () => {
    const values = items(); values[2].position = { ...values[2].position!, reward: 500 };
    const ranked = rankStockOpportunities(values, "target-profit");
    expect(ranked[0].setup.ticker).toBe("NVDA");
    expect(ranked[0].rankReason).toContain("$500.00 if all");
    expect(ranked[0].rankReason).toContain("not expected profit");
  });

  it("ranks after-cost reward/risk ahead of score in ratio mode", () => {
    const values = items(); values[2].netRewardRisk = 2.5;
    const ranked = rankStockOpportunities(values, "net-rr");
    expect(ranked[0].setup.ticker).toBe("NVDA");
    expect(ranked[0].rankReason).toContain("does not establish a greater chance");
  });

  it.each(["quality", "target-profit", "net-rr"] as const)("never promotes an ineligible setup above an eligible one in %s mode", mode => {
    const values = items(); values[0].eligible = false; values[0].netRewardRisk = 100;
    values[0].position = { ...values[0].position!, reward: 1_000_000 };
    expect(rankStockOpportunities(values, mode).at(-1)?.setup.ticker).toBe("AAPL");
  });

  it("leaves unsized eligible research after sized eligible plans in target-dollar mode", () => {
    const values = items(); values[0].position = null;
    const ranked = rankStockOpportunities(values, "target-profit");
    expect(ranked.at(-1)?.setup.ticker).toBe("AAPL");
    expect(ranked.at(-1)?.rankReason).toContain("Add a complete budget");
  });

  it("preserves input order for ties and does not mutate items or their explanations", () => {
    const values = items().map(item => ({ ...item, setup: { ...item.setup, score: 80 } }));
    const before = structuredClone(values);
    for (const mode of ["quality", "target-profit", "net-rr"] as const) {
      expect(rankStockOpportunities(values, mode).map(item => item.setup.ticker)).toEqual(["AAPL", "MSFT", "NVDA"]);
    }
    expect(values).toEqual(before);
  });

  it("does not promote malformed nonfinite metrics", () => {
    const values = items(); values[0].setup.score = NaN; values[0].netRewardRisk = Infinity;
    values[0].position = { ...values[0].position!, reward: Infinity };
    for (const mode of ["quality", "target-profit", "net-rr"] as const) {
      expect(rankStockOpportunities(values, mode).at(-1)?.setup.ticker).toBe("AAPL");
    }
  });
});
