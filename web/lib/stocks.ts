/** Manual stock workspace only: these symbols and calculations do not verify broker availability. */
export type StockExchange = "NASDAQ" | "NYSE" | "AMEX";
export type StockItem = { symbol: string; name: string; availability: "unconfirmed" | "confirmed" | "unavailable" };

export const STOCK_STORAGE_KEY = "kraken.stocks.workspace.v1";
export const SEED_STOCKS: StockItem[] = [
  { symbol: "NASDAQ:NVDA", name: "NVIDIA", availability: "unconfirmed" },
  { symbol: "NASDAQ:AAPL", name: "Apple", availability: "unconfirmed" },
  { symbol: "NASDAQ:MSFT", name: "Microsoft", availability: "unconfirmed" },
  { symbol: "NASDAQ:AMZN", name: "Amazon", availability: "unconfirmed" },
  { symbol: "NASDAQ:META", name: "Meta Platforms", availability: "unconfirmed" },
  { symbol: "NASDAQ:GOOGL", name: "Alphabet", availability: "unconfirmed" },
  { symbol: "NASDAQ:TSLA", name: "Tesla", availability: "unconfirmed" },
  { symbol: "NASDAQ:AMD", name: "Advanced Micro Devices", availability: "unconfirmed" },
  { symbol: "NASDAQ:PLTR", name: "Palantir Technologies", availability: "unconfirmed" },
  { symbol: "NASDAQ:COIN", name: "Coinbase Global", availability: "unconfirmed" },
  { symbol: "AMEX:SPY", name: "SPDR S&P 500 ETF Trust", availability: "unconfirmed" },
  { symbol: "NASDAQ:QQQ", name: "Invesco QQQ", availability: "unconfirmed" },
  { symbol: "AMEX:IWM", name: "iShares Russell 2000 ETF", availability: "unconfirmed" },
  { symbol: "AMEX:DIA", name: "SPDR Dow Jones Industrial Average ETF Trust", availability: "unconfirmed" },
  { symbol: "NASDAQ:NFLX", name: "Netflix", availability: "unconfirmed" },
  { symbol: "NASDAQ:AVGO", name: "Broadcom", availability: "unconfirmed" },
  { symbol: "NASDAQ:INTC", name: "Intel", availability: "unconfirmed" },
  { symbol: "NASDAQ:HOOD", name: "Robinhood Markets", availability: "unconfirmed" },
  { symbol: "NYSE:UBER", name: "Uber Technologies", availability: "unconfirmed" },
  { symbol: "NYSE:JPM", name: "JPMorgan Chase", availability: "unconfirmed" },
];

const EXCHANGES: readonly string[] = ["NASDAQ", "NYSE", "AMEX"];

/** Syntax validation, not a listing lookup. Pair separators and non-US widget exchanges are rejected. */
export function normalizeStockSymbol(input: string, exchange: StockExchange = "NASDAQ"): string | null {
  if (typeof input !== "string" || !EXCHANGES.includes(exchange)) return null;
  const value = input.trim().toUpperCase();
  const parts = value.split(":");
  if (parts.length > 2) return null;
  const venue = parts.length === 2 ? parts[0] : exchange;
  const ticker = parts.length === 2 ? parts[1] : parts[0];
  // US tickers, optionally a class/warrant suffix (BRK.B, BF-B). USD/USDT crypto pairs do not fit.
  if (!EXCHANGES.includes(venue) || !/^[A-Z]{1,5}(?:[.-][A-Z]{1,2})?$/.test(ticker)) return null;
  return `${venue}:${ticker}`;
}

export type StockPlan = {
  id: string;
  symbol: string;
  setup: "Opening range breakout" | "VWAP reclaim" | "Breakout" | "Pullback";
  entry: number;
  stop: number;
  target: number;
  account: number;
  /** Percentage points: 1 means one percent of account value. */
  riskPct: number;
  cash: number;
  /** Total round-trip buffer per share for spread, slippage and fees; not a commission quote. */
  costPerShare: number;
  notes: string;
  createdAt: string;
};

export type StockSizingInput = Pick<StockPlan, "entry" | "stop" | "target" | "account" | "riskPct" | "cash" | "costPerShare">;
export type StockSizingResult = {
  valid: boolean;
  reason?: string;
  shares: number;
  /** Entry notional plus the full round-trip buffer reserved in cash. */
  capital: number;
  risk: number;
  reward: number;
  rewardRisk: number;
  riskBudget: number;
};

export function sizeStockPlan(input: StockSizingInput): StockSizingResult {
  const invalid = (reason: string, riskBudget = 0): StockSizingResult =>
    ({ valid: false, reason, shares: 0, capital: 0, risk: 0, reward: 0, rewardRisk: 0, riskBudget });
  const { entry, stop, target, account, riskPct, cash, costPerShare } = input;
  if (![entry, stop, target, account, riskPct, cash, costPerShare].every(finite)) return invalid("Every input must be a finite number.");
  if (entry <= 0) return invalid("Entry must be above zero.");
  if (stop <= 0 || stop >= entry) return invalid("Stop must be above zero and below entry for a long trade.");
  if (target <= entry) return invalid("Target must be above entry.");
  if (account <= 0 || cash <= 0) return invalid("Account value and available cash must be above zero.");
  if (riskPct <= 0 || riskPct > 100) return invalid("Risk must be greater than zero and no more than 100 percent.");
  if (costPerShare < 0) return invalid("The round-trip cost buffer cannot be negative.");

  const riskBudget = account * (riskPct / 100);
  const perShareRisk = entry - stop + costPerShare;
  const perShareReward = target - entry - costPerShare;
  const perShareCash = entry + costPerShare;
  if (![riskBudget, perShareRisk, perShareReward, perShareCash].every(finite)) return invalid("Calculated values are outside the supported numeric range.");
  if (perShareReward <= 0) return invalid("The round-trip cost buffer consumes the profit to the target.", riskBudget);
  if (riskBudget <= 0 || perShareRisk <= 0) return invalid("The risk budget is too small to represent safely.", riskBudget);

  let shares = Math.floor(Math.min(Number.MAX_SAFE_INTEGER, riskBudget / perShareRisk, cash / perShareCash));
  // Division can round a just-under-integer quotient up. Verify the actual products before
  // publishing a size, without rounding costs down or adding an over-budget epsilon.
  if (shares > 0 && (shares * perShareRisk > riskBudget || shares * perShareCash > cash)) shares--;
  if (shares < 1) return invalid("The risk budget or available cash does not cover one whole share including the cost buffer.", riskBudget);
  const risk = shares * perShareRisk, reward = shares * perShareReward, capital = shares * perShareCash;
  const rewardRisk = reward / risk;
  if (![risk, reward, capital, rewardRisk].every(finite) || risk <= 0 || reward <= 0 || risk > riskBudget || capital > cash)
    return invalid("Calculated values are outside the supported numeric range.", riskBudget);
  return { valid: true, shares, capital, risk, reward, rewardRisk, riskBudget };
}

export type StockTrade = {
  id: string;
  planId: string;
  symbol: string;
  setup: StockPlan["setup"];
  entry: number;
  stop: number;
  exit: number;
  shares: number;
  costPerShare: number;
  notes: string;
  closedAt: string;
};

function validTradeNumbers(trade: StockTrade): boolean {
  return [trade.entry, trade.stop, trade.exit, trade.shares, trade.costPerShare].every(finite)
    && trade.entry > 0 && trade.stop > 0 && trade.stop < trade.entry && trade.exit >= 0
    && trade.shares > 0 && trade.costPerShare >= 0;
}

/** Net result after one total round-trip buffer. Invalid direct inputs return NaN, never a made-up zero. */
export function tradePnl(trade: StockTrade): number {
  if (!validTradeNumbers(trade)) return Number.NaN;
  const result = (trade.exit - trade.entry - trade.costPerShare) * trade.shares;
  return finite(result) ? result : Number.NaN;
}

/** Net P/L divided by the original stop loss including the same round-trip buffer. */
export function tradeR(trade: StockTrade): number {
  if (!validTradeNumbers(trade)) return Number.NaN;
  const risk = (trade.entry - trade.stop + trade.costPerShare) * trade.shares;
  const result = tradePnl(trade) / risk;
  return risk > 0 && finite(risk) && finite(result) ? result : Number.NaN;
}

export type StockWorkspaceState = { watchlist: StockItem[]; selected: string; plans: StockPlan[]; journal: StockTrade[] };

export function defaultStockState(): StockWorkspaceState {
  return { watchlist: SEED_STOCKS.map(stock => ({ ...stock })), selected: SEED_STOCKS[0].symbol, plans: [], journal: [] };
}

const SETUPS: readonly StockPlan["setup"][] = ["Opening range breakout", "VWAP reclaim", "Breakout", "Pullback"];
const NOTES_LIMIT = 2_000;
const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const record = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const identifier = (value: unknown): value is string => typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,119}$/.test(value);
const setupName = (value: unknown): value is StockPlan["setup"] => typeof value === "string" && SETUPS.includes(value as StockPlan["setup"]);
const notes = (value: unknown): string => typeof value === "string" ? value.slice(0, NOTES_LIMIT) : "";

function timestamp(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?(?:Z|[+-]\d{2}:\d{2})$/.test(value)) return false;
  const year = Number(value.slice(0, 4)), month = Number(value.slice(5, 7)), day = Number(value.slice(8, 10));
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (month < 1 || month > 12 || day < 1 || day > days[month - 1] || Number(value.slice(11, 13)) > 23
    || Number(value.slice(14, 16)) > 59 || Number(value.slice(17, 19)) > 59) return false;
  const date = Date.parse(value);
  return Number.isFinite(date);
}

function parseItem(value: unknown): StockItem | null {
  if (!record(value) || typeof value.symbol !== "string") return null;
  const symbol = normalizeStockSymbol(value.symbol);
  if (!symbol) return null;
  return {
    symbol,
    name: typeof value.name === "string" && value.name.trim() ? value.name.trim().slice(0, 120) : symbol.split(":")[1],
    availability: value.availability === "confirmed" || value.availability === "unavailable" ? value.availability : "unconfirmed",
  };
}

function parsePlan(value: unknown): StockPlan | null {
  if (!record(value) || !identifier(value.id) || typeof value.symbol !== "string" || !setupName(value.setup) || !timestamp(value.createdAt)) return null;
  const symbol = normalizeStockSymbol(value.symbol);
  if (!symbol || ![value.entry, value.stop, value.target, value.account, value.riskPct, value.cash, value.costPerShare].every(finite)) return null;
  const plan: StockPlan = {
    id: value.id, symbol, setup: value.setup, entry: value.entry as number, stop: value.stop as number,
    target: value.target as number, account: value.account as number, riskPct: value.riskPct as number,
    cash: value.cash as number, costPerShare: value.costPerShare as number, notes: notes(value.notes), createdAt: value.createdAt,
  };
  return sizeStockPlan(plan).valid ? plan : null;
}

function parseTrade(value: unknown): StockTrade | null {
  if (!record(value) || !identifier(value.id) || !identifier(value.planId) || typeof value.symbol !== "string" || !setupName(value.setup) || !timestamp(value.closedAt)) return null;
  const symbol = normalizeStockSymbol(value.symbol);
  if (!symbol || ![value.entry, value.stop, value.exit, value.shares, value.costPerShare].every(finite)) return null;
  const trade: StockTrade = {
    id: value.id, planId: value.planId, symbol, setup: value.setup, entry: value.entry as number,
    stop: value.stop as number, exit: value.exit as number, shares: value.shares as number,
    costPerShare: value.costPerShare as number, notes: notes(value.notes), closedAt: value.closedAt,
  };
  return finite(tradePnl(trade)) && finite(tradeR(trade)) ? trade : null;
}

function recover<T>(values: unknown, parse: (value: unknown) => T | null, key: (value: T) => string, limit: number): T[] {
  if (!Array.isArray(values)) return [];
  const result: T[] = [], seen = new Set<string>();
  for (const value of values) {
    const item = parse(value);
    if (!item || seen.has(key(item))) continue;
    result.push(item); seen.add(key(item));
    if (result.length === limit) break;
  }
  return result;
}

/** Recover valid records independently. Empty lists are intentional; saved records need not remain watched. */
export function parseStockState(raw: string | null): StockWorkspaceState {
  const fallback = defaultStockState();
  if (typeof raw !== "string" || raw.length > 2_000_000) return fallback;
  try {
    const value: unknown = JSON.parse(raw);
    if (!record(value)) return fallback;
    const recovered = recover(value.watchlist, parseItem, item => item.symbol, 100);
    const watchlist = Array.isArray(value.watchlist) && (value.watchlist.length === 0 || recovered.length > 0) ? recovered : fallback.watchlist;
    const selected = typeof value.selected === "string" ? normalizeStockSymbol(value.selected) : null;
    return {
      watchlist,
      selected: selected && watchlist.some(item => item.symbol === selected && item.availability !== "unavailable")
        ? selected : watchlist.find(item => item.availability !== "unavailable")?.symbol ?? "",
      plans: recover(value.plans, parsePlan, plan => plan.id, 100),
      journal: recover(value.journal, parseTrade, trade => trade.id, 500),
    };
  } catch { return fallback; }
}
