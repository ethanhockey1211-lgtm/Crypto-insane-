import { describe, expect, it } from "vitest";
import {
  defaultStockState, normalizeStockSymbol, parseStockState, SEED_STOCKS, sizeStockPlan,
  STOCK_STORAGE_KEY, tradePnl, tradeR, type StockExchange, type StockPlan, type StockSizingInput, type StockTrade,
} from "./stocks";

const sizing: StockSizingInput = { entry: 100, stop: 98, target: 105, account: 10_000, riskPct: 1, cash: 10_000, costPerShare: 0.5 };
const plan = (changes: Partial<StockPlan> = {}): StockPlan => ({
  id: "plan-1", symbol: "NASDAQ:NVDA", setup: "Pullback", ...sizing,
  notes: "Wait for the stated trigger", createdAt: "2026-09-09T15:00:00.000Z", ...changes,
});
const trade = (changes: Partial<StockTrade> = {}): StockTrade => ({
  id: "trade-1", planId: "plan-1", symbol: "NASDAQ:NVDA", setup: "Pullback",
  entry: 100, stop: 98, exit: 105, shares: 40, costPerShare: 0.5,
  notes: "Manual fill", closedAt: "2026-09-09T16:00:00.000Z", ...changes,
});

describe("US stock symbol normalization", () => {
  it("normalizes tickers and explicit US venues without guessing broker eligibility", () => {
    expect(normalizeStockSymbol(" nvda ")).toBe("NASDAQ:NVDA");
    expect(normalizeStockSymbol("spy", "AMEX")).toBe("AMEX:SPY");
    expect(normalizeStockSymbol(" nyse:brk.b ")).toBe("NYSE:BRK.B");
    expect(normalizeStockSymbol("bf-b", "NYSE")).toBe("NYSE:BF-B");
    expect(normalizeStockSymbol("nyse:jpm", "NASDAQ")).toBe("NYSE:JPM");
    expect(normalizeStockSymbol("nasdaq:googl")).toBe("NASDAQ:GOOGL");
  });

  it.each(["", "NASDAQ:", "NYSE:", "BTC-USD", "ETH/USDT", "BTCUSD", "BTCUSDT", "NYSE:SOL-USD",
    "NASDAQ:ETH-BTC", "BINANCE:BTCUSDT", "COINBASE:BTCUSD", "OTC:ABC", "NASDAQ:AAPL:USD", "NASDAQ: AAPL",
    "A APL", "<script>", "__proto__", "https://example.com", "ABC_", "ABC..A", "123", "NASDAQ:BRK.BB.B"])
    ("rejects pairs, unsupported venues and malformed symbol %s", value => {
      expect(normalizeStockSymbol(value)).toBeNull();
    });

  it("validates the exchange argument at runtime too", () => {
    expect(normalizeStockSymbol("AAPL", "BINANCE" as StockExchange)).toBeNull();
  });
});

describe("whole-share stock position sizing", () => {
  it("charges the total round-trip buffer once and sizes within the actual risk budget", () => {
    const result = sizeStockPlan(sizing);
    expect(result).toEqual({ valid: true, shares: 40, capital: 4_020, risk: 100, reward: 180, rewardRisk: 1.8, riskBudget: 100 });
    expect(tradePnl(trade({ shares: result.shares, exit: sizing.target }))).toBe(result.reward);
    expect(tradeR(trade({ shares: result.shares, exit: sizing.target }))).toBe(result.rewardRisk);
  });

  it("reserves the cost buffer within cash and leaves unaffordable fractional shares unbought", () => {
    const result = sizeStockPlan({ ...sizing, cash: 1_000 });
    expect(result.valid).toBe(true);
    expect(result.shares).toBe(9);
    expect(result.capital).toBe(904.5);
    expect(result.risk).toBe(22.5);
    expect(result.reward).toBe(40.5);
    expect(result.capital).toBeLessThanOrEqual(1_000);
  });

  it("allows a zero buffer without silently assuming a commission rate", () => {
    const result = sizeStockPlan({ ...sizing, costPerShare: 0 });
    expect(result).toMatchObject({ valid: true, shares: 50, capital: 5_000, risk: 100, reward: 250, rewardRisk: 2.5 });
  });

  it("rejects inadequate budgets and targets whose profit is consumed by costs", () => {
    expect(sizeStockPlan({ ...sizing, cash: 100 }).valid).toBe(false);
    expect(sizeStockPlan({ ...sizing, account: 1 }).valid).toBe(false);
    expect(sizeStockPlan({ ...sizing, target: 100.5 })).toMatchObject({ valid: false, shares: 0 });
    expect(sizeStockPlan({ ...sizing, target: 100.25 }).reason).toMatch(/consumes/);
  });

  it.each([
    { entry: 0 }, { stop: 0 }, { stop: -1 }, { stop: 100 }, { stop: 101 }, { target: 100 },
    { account: 0 }, { cash: -1 }, { riskPct: 0 }, { riskPct: -0.1 }, { riskPct: 100.1 }, { costPerShare: -0.01 },
  ])("rejects invalid long-trade geometry and budgets: %j", change => {
    const result = sizeStockPlan({ ...sizing, ...change });
    expect(result.valid).toBe(false);
    expect(result.reason).toBeTruthy();
    expect(result.shares).toBe(0);
    expect(Object.values(result).filter(value => typeof value === "number").every(Number.isFinite)).toBe(true);
  });

  it("rejects every nonfinite or nonnumeric field instead of allowing comparisons to fall through", () => {
    for (const key of Object.keys(sizing)) for (const bad of [NaN, Infinity, -Infinity, "10", null, undefined]) {
      const result = sizeStockPlan({ ...sizing, [key]: bad } as StockSizingInput);
      expect(result.valid, `${key}: ${String(bad)}`).toBe(false);
      expect(result.shares).toBe(0);
    }
  });

  it("does not round a floating-point overspend up to a whole share", () => {
    // 1.503 / 0.501 rounds to 3, but 3 * 0.501 is 1.5030000000000001.
    const result = sizeStockPlan({ ...sizing, entry: 0.501, stop: 0.4, target: 1, costPerShare: 0, cash: 1.503 });
    expect(result.valid).toBe(true);
    expect(result.shares).toBe(2);
    expect(result.capital).toBeLessThanOrEqual(1.503);
  });

  it("keeps cash and risk within budget across varied decimal price and buffer combinations", () => {
    for (let index = 1; index <= 300; index++) {
      const entry = 0.13 * index + 0.1;
      const input = { entry, stop: entry * 0.95, target: entry * 1.2, account: 1_000 + index * 13,
        riskPct: 0.1 + index % 20 / 10, cash: index * 10.01, costPerShare: index % 7 * 0.001 };
      const result = sizeStockPlan(input);
      if (!result.valid) continue;
      expect(Number.isSafeInteger(result.shares)).toBe(true);
      expect(result.shares).toBeGreaterThan(0);
      expect(result.capital).toBeLessThanOrEqual(input.cash);
      expect(result.risk).toBeLessThanOrEqual(result.riskBudget);
      expect(result.rewardRisk).toBeCloseTo(result.reward / result.risk, 12);
    }
  });

  it("rejects overflow and underflow without returning nonfinite result fields", () => {
    for (const input of [
      { ...sizing, entry: 1e308, stop: 1e307, target: 1.7e308, costPerShare: 1e308 },
      { ...sizing, target: 1e308, account: 1e308, cash: 1e308, riskPct: 100 },
      { ...sizing, account: Number.MIN_VALUE, riskPct: Number.MIN_VALUE },
    ]) {
      const result = sizeStockPlan(input);
      expect(result.valid).toBe(false);
      expect(Object.values(result).filter(value => typeof value === "number").every(Number.isFinite)).toBe(true);
    }
  });
});

describe("manual stock journal results", () => {
  it("subtracts costs from winning and losing results and measures risk from the original stop", () => {
    expect(tradePnl(trade())).toBe(180);
    expect(tradeR(trade())).toBe(1.8);
    expect(tradePnl(trade({ exit: 98 }))).toBe(-100);
    expect(tradeR(trade({ exit: 98 }))).toBe(-1);
    expect(tradePnl(trade({ exit: 100 }))).toBe(-20);
    expect(tradePnl(trade({ exit: 100.5 }))).toBe(0);
    expect(tradeR(trade({ exit: 100.5 }))).toBe(0);
    expect(tradePnl(trade({ exit: 0 }))).toBe(-4_020);
  });

  it("accepts actual fractional fills even though planned sizes use whole shares", () => {
    const fractional = trade({ shares: 0.125 });
    expect(tradePnl(fractional)).toBe(0.5625);
    expect(tradeR(fractional)).toBe(1.8);
    expect(tradeR({ ...fractional, exit: 98 })).toBe(-1);
  });

  it("returns an unavailable result for invalid direct inputs instead of fabricated break-even", () => {
    for (const change of [{ entry: NaN }, { stop: Infinity }, { exit: -1 }, { shares: 0 }, { shares: -1 },
      { stop: 100 }, { costPerShare: -1 }, { shares: 1e308, exit: 1e308 }]) {
      expect(Number.isNaN(tradePnl(trade(change)))).toBe(true);
      expect(Number.isNaN(tradeR(trade(change)))).toBe(true);
    }
  });
});

describe("stock workspace persistence", () => {
  it("starts with 20 independent unconfirmed watchlist items and no invented plans or trades", () => {
    const first = defaultStockState(), second = defaultStockState();
    expect(STOCK_STORAGE_KEY).toBe("kraken.stocks.workspace.v1");
    expect(first.watchlist).toHaveLength(20);
    expect(new Set(first.watchlist.map(item => item.symbol)).size).toBe(20);
    expect(first.watchlist.every(item => item.availability === "unconfirmed" && normalizeStockSymbol(item.symbol) === item.symbol)).toBe(true);
    expect(first.plans).toEqual([]);
    expect(first.journal).toEqual([]);
    expect(first.selected).toBe("NASDAQ:NVDA");
    first.watchlist[0].availability = "confirmed";
    first.watchlist.pop();
    expect(second.watchlist).toHaveLength(20);
    expect(SEED_STOCKS[0].availability).toBe("unconfirmed");
  });

  it.each([null, "", "{broken", "null", "[]", "42", '"text"'])
    ("uses defaults for malformed or nonobject persistence: %s", raw => {
      expect(parseStockState(raw)).toEqual(defaultStockState());
    });

  it("round-trips manual availability, selected symbol, saved plans and fractional trade fills", () => {
    const value = { watchlist: [
      { symbol: "NYSE:JPM", name: "JPMorgan Chase", availability: "confirmed" },
      { symbol: "NASDAQ:NVDA", name: "NVIDIA", availability: "unavailable" },
    ], selected: "NYSE:JPM", plans: [plan()], journal: [trade({ shares: 0.125 })] };
    expect(parseStockState(JSON.stringify(value))).toEqual(value);
  });

  it("preserves an explicitly empty watchlist and records for symbols removed from it", () => {
    const result = parseStockState(JSON.stringify({ watchlist: [], selected: "NASDAQ:NVDA", plans: [plan()], journal: [trade()] }));
    expect(result.watchlist).toEqual([]);
    expect(result.selected).toBe("");
    expect(result.plans).toEqual([plan()]);
    expect(result.journal).toEqual([trade()]);
  });

  it("keeps unavailable stocks unselected after reload and falls back to the first visible item", () => {
    const watchlist = [
      { symbol: "NASDAQ:NVDA", name: "NVIDIA", availability: "unavailable" },
      { symbol: "NYSE:JPM", name: "JPMorgan Chase", availability: "unconfirmed" },
    ];
    const restored = parseStockState(JSON.stringify({ watchlist, selected: "NASDAQ:NVDA" }));
    expect(restored.watchlist).toEqual(watchlist);
    expect(restored.selected).toBe("NYSE:JPM");
    const allHidden = parseStockState(JSON.stringify({ watchlist: [watchlist[0]], selected: "NASDAQ:NVDA" }));
    expect(allHidden.watchlist).toHaveLength(1);
    expect(allHidden.selected).toBe("");
  });

  it("recovers valid records independently, normalizes symbols and defaults unknown availability to unconfirmed", () => {
    const result = parseStockState(JSON.stringify({
      watchlist: [null, { symbol: "BTC-USD" }, { symbol: " nyse:jpm ", name: "  JPMorgan  ", availability: "verified-by-broker" },
        { symbol: "NYSE:JPM", name: "duplicate", availability: "confirmed" }],
      selected: "NASDAQ:AAPL", plans: [null, plan({ id: "bad", target: 90 }), plan()],
      journal: [trade({ id: "bad", shares: -1 }), trade(), trade({ exit: 1 })],
    }));
    expect(result.watchlist).toEqual([{ symbol: "NYSE:JPM", name: "JPMorgan", availability: "unconfirmed" }]);
    expect(result.selected).toBe("NYSE:JPM");
    expect(result.plans).toEqual([plan()]);
    expect(result.journal).toEqual([trade()]);
  });

  it("rejects stored number strings, overflows, invalid dates and setups while capping notes", () => {
    const result = parseStockState(JSON.stringify({
      plans: [plan({ id: "long-notes", notes: "n".repeat(2_100) }), { ...plan(), id: "string-entry", entry: "100" },
        plan({ id: "bad-date", createdAt: "2026-02-30T16:00:00.000Z" }), { ...plan(), id: "bad-setup", setup: "Guaranteed win" },
        plan({ id: "bad-cost", costPerShare: -1 })],
      journal: [trade({ notes: "n".repeat(2_100) }), { ...trade(), id: "overflow", shares: 1e308, exit: 1e308 },
        trade({ id: "bad-date", closedAt: "yesterday" }), trade({ id: "empty-id", planId: "" })],
    }));
    expect(result.plans).toHaveLength(1);
    expect(result.plans[0].notes).toHaveLength(2_000);
    expect(result.journal).toHaveLength(1);
    expect(result.journal[0].notes).toHaveLength(2_000);
    const overflowJson = JSON.stringify({ plans: [plan()] }).replace('"entry":100', '"entry":1e309');
    expect(parseStockState(overflowJson).plans).toEqual([]);
  });

  it("limits recovered valid entries rather than allowing invalid leading rows to hide later records", () => {
    const letters = (index: number) => String.fromCharCode(65 + Math.floor(index / 26), 65 + index % 26);
    const result = parseStockState(JSON.stringify({
      watchlist: [null, ...Array.from({ length: 105 }, (_, i) => ({ symbol: `NYSE:${letters(i)}`, name: `Stock ${i}` }))],
      plans: [null, ...Array.from({ length: 105 }, (_, i) => plan({ id: `p${i}` }))],
      journal: [null, ...Array.from({ length: 505 }, (_, i) => trade({ id: `t${i}` }))],
    }));
    expect(result.watchlist).toHaveLength(100);
    expect(result.plans).toHaveLength(100);
    expect(result.journal).toHaveLength(500);
    expect(result.plans[99].id).toBe("p99");
    expect(result.journal[499].id).toBe("t499");
  });

  it("whitelists record fields instead of spreading arbitrary persisted objects", () => {
    const raw = '{"watchlist":[{"symbol":"NASDAQ:AAPL","name":"Apple","__proto__":{"polluted":true}}],"plans":[],"journal":[]}';
    const result = parseStockState(raw);
    expect(result.watchlist[0]).toEqual({ symbol: "NASDAQ:AAPL", name: "Apple", availability: "unconfirmed" });
    expect(Object.prototype.hasOwnProperty.call(result.watchlist[0], "__proto__")).toBe(false);
    expect(({} as Record<string, unknown>).polluted).toBeUndefined();
  });
});
