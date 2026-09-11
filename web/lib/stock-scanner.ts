import { getStockSession } from "./stock-session";
import { normalizeStockSymbol, type StockItem, type StockPlan } from "./stocks";

export interface StockBar {
  at: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  vwap: number | null;
}
export interface StockMarketData {
  ticker: string;
  bars: StockBar[];
  latestTrade: { price: number; at: string } | null;
  latestQuote: { bid: number; ask: number; bidSize: number; askSize: number; at: string } | null;
  previousClose: number | null;
  dayVolume: number | null;
  historyComplete: boolean;
}
export interface StockScanResponse {
  status: "ready" | "error" | "not-configured";
  provider: "Alpaca";
  feed: "iex";
  asOf: string;
  message: string | null;
  rows: StockMarketData[];
}
export interface StockScannerStatus {
  configured: boolean;
  accessRequired: boolean;
  provider: string;
  feed: string;
  maxSymbols: number;
  refreshSeconds: number;
  message: string;
}
export interface StockSetupDetails {
  thesis: string;
  triggerPrice: number | null;
  /** A completed-bar pattern can be confirmed while other entry checks still fail. */
  triggerConfirmed: boolean;
  confirmation: string;
  invalidation: string;
  cautions: string[];
  scoreFactors: { label: string; earned: number; possible: number; detail: string }[];
  levels: { label: string; price: number; kind: "support" | "resistance" | "reference" }[];
  trendLabel: string;
  target1: number | null;
  target2: number | null;
}
export interface StockSetup {
  symbol: string;
  ticker: string;
  name: string;
  setup: StockPlan["setup"] | null;
  state: "entry-zone" | "watch" | "extended" | "unavailable" | "blocked";
  /** Explicit point checklist, never a probability or historical win rate. */
  score: number;
  price: number | null;
  changePct: number | null;
  vwap: number | null;
  ema9: number | null;
  ema20: number | null;
  atr: number | null;
  relativeVolume: number | null;
  spreadPct: number | null;
  entry: number | null;
  entryMax: number | null;
  stop: number | null;
  target: number | null;
  rewardRisk: number | null;
  barAt: string | null;
  quoteAt: string | null;
  /** Original completed trigger, retained for at most three elapsed minutes. */
  triggerAt?: string;
  reasons: string[];
  evidence: string[];
  details?: StockSetupDetails;
}

const MINUTE = 60_000;
const MAX_SPREAD_PCT = 0.35;
const easternClock = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/New_York", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
});
type TimedBar = StockBar & { time: number };
type Trigger = { setup: StockPlan["setup"]; level: number; confirmed: boolean; volumeNeeded: number; explanation: string;
  at: string; close: number; atr: number; structure: number; relativeVolume: number };
type DetailContext = {
  bars: TimedBar[];
  start: number;
  tradeFresh: boolean;
  quoteFresh: boolean;
  scored?: boolean;
  trend?: boolean;
  trigger?: Trigger;
  near?: Trigger;
  inZone?: boolean;
};

const positive = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value) && value > 0;
const finiteOrNull = (value: number): number | null => Number.isFinite(value) ? value : null;
function timestamp(value: unknown): number {
  return typeof value === "string" && /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? Date.parse(value) : NaN;
}
function fresh(at: unknown, now: number, maxAge: number): boolean {
  const age = now - timestamp(at);
  return Number.isFinite(age) && age >= -5_000 && age <= maxAge;
}
function average(values: number[]): number {
  // Divide before summing to avoid overflowing a sum of individually valid values.
  return values.reduce((sum, value) => sum + value / values.length, 0);
}
function ema(values: number[], period: number): number | null {
  if (values.length < period) return null;
  let value = average(values.slice(0, period));
  const weight = 2 / (period + 1);
  for (let i = period; i < values.length; i++) value = values[i] * weight + value * (1 - weight);
  return finiteOrNull(value);
}
function atr14(bars: TimedBar[]): number | null {
  if (bars.length < 15) return null;
  const ranges = bars.slice(1).map((bar, i) => Math.max(bar.high - bar.low, Math.abs(bar.high - bars[i].close), Math.abs(bar.low - bars[i].close)));
  let value = average(ranges.slice(0, 14));
  for (const range of ranges.slice(14)) value = value * (13 / 14) + range / 14;
  return positive(value) ? value : null;
}
function estimatedVwap(bars: TimedBar[]): number | null {
  const volume = bars.reduce((sum, bar) => sum + bar.volume, 0);
  if (!positive(volume)) return null;
  const value = bars.reduce((sum, bar) => sum + (bar.vwap ?? (bar.high / 3 + bar.low / 3 + bar.close / 3)) * (bar.volume / volume), 0);
  return positive(value) ? value : null;
}

/** Evaluate each trigger using only observations available when its candle closed. */
function findTriggers(bars: TimedBar[], start: number): Trigger[] {
  if (bars.length < 21) return [];
  const last = bars.at(-1)!, previous = bars.at(-2)!;
  if (last.time - previous.time !== MINUTE || last.time - bars.at(-21)!.time > 30 * MINUTE) return [];
  const priorBars = bars.slice(0, -1), closes = bars.map(bar => bar.close);
  const fast = ema(closes, 9), slow = ema(closes, 20), atr = atr14(bars), vwap = estimatedVwap(bars);
  const priorVolume = average(priorBars.slice(-20).map(bar => bar.volume));
  if (!positive(fast) || !positive(slow) || !positive(atr) || !positive(vwap) || !positive(priorVolume)) return [];
  const bullish = last.close > last.open, trend = fast > slow && last.close >= fast;
  const previousVwap = estimatedVwap(priorBars);
  const previousEma9 = ema(priorBars.map(bar => bar.close), 9), previousEma20 = ema(priorBars.map(bar => bar.close), 20);
  const opening = bars.filter(bar => bar.time < start + 15 * MINUTE);
  const completeOpening = opening.length === 15 && opening.every((bar, index) => bar.time === start + index * MINUTE);
  const common = { at: last.at, close: last.close, atr, structure: Math.min(...bars.slice(-3).map(bar => bar.low)), relativeVolume: last.volume / priorVolume };
  const candidates: Trigger[] = [];
  if (completeOpening) {
    const level = Math.max(...opening.map(bar => bar.high));
    candidates.push({ ...common, setup: "Opening range breakout", level, volumeNeeded: 1.1,
      confirmed: bullish && trend && previous.close <= level && last.close > level,
      explanation: "Completed candle crossed above the verified 15-minute opening-range high." });
  }
  candidates.push({ ...common, setup: "VWAP reclaim", level: vwap, volumeNeeded: 1,
    confirmed: bullish && last.close > slow && positive(previousVwap) && previous.close <= previousVwap && last.close > vwap,
    explanation: "Completed candle reclaimed estimated session IEX VWAP from below and closed above EMA20." });
  candidates.push({ ...common, setup: "Pullback", level: previous.high, volumeNeeded: 1,
    confirmed: bullish && trend && positive(previousEma9) && positive(previousEma20) && previousEma9 > previousEma20
      && previous.low <= previousEma9 + 0.15 * atr && previous.close >= previousEma20 && last.close > previous.high,
    explanation: "Completed candle broke the prior pullback candle high after an EMA9-area touch in an upward EMA trend." });
  const resistance = Math.max(...priorBars.slice(-20).map(bar => bar.high));
  candidates.push({ ...common, setup: "Breakout", level: resistance, volumeNeeded: 1.1,
    confirmed: bullish && trend && last.close > resistance,
    explanation: "Completed candle closed above the preceding 20 observed bars' highs." });
  return candidates;
}
function validBar(bar: StockBar): boolean {
  return [bar.open, bar.high, bar.low, bar.close, bar.volume].every(positive)
    && bar.high >= Math.max(bar.open, bar.close) && bar.low <= Math.min(bar.open, bar.close)
    && (bar.vwap === null || positive(bar.vwap));
}
function equalBar(a: TimedBar, b: TimedBar): boolean {
  return a.open === b.open && a.high === b.high && a.low === b.low && a.close === b.close && a.volume === b.volume && a.vwap === b.vwap;
}
function cleanBars(input: StockBar[], start: number, end: number, now: number): { bars: TimedBar[]; invalid: boolean } {
  const byTime = new Map<number, TimedBar>();
  const conflicts = new Set<number>();
  let invalid = false;
  for (const bar of input) {
    const time = timestamp(bar?.at);
    if (!Number.isFinite(time)) { invalid = true; continue; }
    // Alpaca minute timestamps mark the beginning of the bar. Never use the forming minute,
    // premarket, after-hours, or another day's bars as completed regular-session evidence.
    if (time < start || time >= end || time + MINUTE > now) continue;
    if (time % MINUTE !== 0 || !validBar(bar)) { invalid = true; continue; }
    const candidate = { ...bar, time };
    const prior = byTime.get(time);
    if (prior && !equalBar(prior, candidate)) { conflicts.add(time); invalid = true; }
    else byTime.set(time, candidate);
  }
  for (const time of conflicts) byTime.delete(time);
  return { bars: [...byTime.values()].sort((a, b) => a.time - b.time), invalid };
}
function base(item: StockItem): StockSetup {
  return { symbol: item.symbol, ticker: item.symbol.split(":")[1] ?? item.symbol, name: item.name,
    setup: null, state: "blocked", score: 0, price: null, changePct: null, vwap: null, ema9: null, ema20: null,
    atr: null, relativeVolume: null, spreadPct: null, entry: null, entryMax: null, stop: null, target: null,
    rewardRisk: null, barAt: null, quoteAt: null, reasons: [], evidence: [] };
}
function priceText(value: number): string {
  return value >= 1 ? value.toFixed(2) : value.toPrecision(4);
}

const scoreRubric = [
  ["Upward EMA trend", 20], ["Above estimated VWAP", 15], ["IEX relative volume", 10],
  ["Completed setup trigger", 15], ["Fresh trade and quote", 15], ["IEX quoted spread", 10],
  ["Trade and ask in entry zone", 15],
] as const;

function explainScore(out: StockSetup, context?: DetailContext): StockSetupDetails["scoreFactors"] {
  // The existing scanner deliberately leaves the score at zero if the inputs needed for
  // its checklist are incomplete. Do not award explanation points to those early exits.
  if (!context?.scored) return scoreRubric.map(([label, possible]) => ({ label, possible, earned: 0,
    detail: "Not assessed: complete, valid price and indicator inputs are required before this checklist is scored." }));
  const last = context.bars.at(-1)!;
  const trend = context.trend === true;
  const volume = out.relativeVolume!;
  const trigger = context.trigger;
  const near = context.near && out.setup ? context.near : undefined;
  const spreadPasses = out.spreadPct !== null && out.spreadPct <= MAX_SPREAD_PCT;
  const values: [number, string][] = [
    [trend ? 20 : 0, `EMA9 ${priceText(out.ema9!)} ${out.ema9! > out.ema20! ? "is above" : "is not above"} EMA20 ${priceText(out.ema20!)}; completed close ${priceText(last.close)} ${last.close >= out.ema9! ? "is at or above" : "is below"} EMA9. Both checks are required for 20 points.`],
    [last.close > out.vwap! ? 15 : 0, `Completed close ${priceText(last.close)} ${last.close > out.vwap! ? "is above" : "is not above"} the estimated IEX session VWAP of ${priceText(out.vwap!)}. A close above earns 15 points.`],
    [volume >= 1.1 ? 10 : volume >= 1 ? 5 : 0, `Latest completed IEX bar volume is ${volume.toFixed(2)}× the preceding 20 observed bars' average. At least 1.10× earns 10 points; at least 1.00× but below 1.10× earns 5; below 1.00× earns 0.`],
    [trigger ? 15 : near ? 10 : 0, trigger ? `${trigger.setup} has a completed-bar trigger at ${priceText(trigger.level)} (15 points). Other entry checks are separate.`
      : near ? `${near.setup} is within 0.75 ATR of ${priceText(near.level)} (10 proximity points); a completed trigger is still missing.`
        : "No completed pattern or qualifying setup level within 0.75 ATR (0 points). A completed trigger earns 15; proximity alone earns 10."],
    [context.tradeFresh && context.quoteFresh ? 15 : 0, `Trade ${context.tradeFresh ? "passes" : "fails"} and two-sided quote ${context.quoteFresh ? "passes" : "fails"} the 45-second freshness check. Both are required for 15 points; up to 5 seconds of upstream clock skew is tolerated.`],
    [spreadPasses ? out.spreadPct! <= 0.15 ? 10 : 5 : 0, `${out.spreadPct === null ? "A valid fresh quoted spread is unavailable" : `IEX quoted spread is ${out.spreadPct.toFixed(3)}%`}. At most 0.15% earns 10 points; above 0.15% through 0.35% earns 5; a wider or unavailable spread earns 0.`],
    [trigger && context.inZone ? 15 : 0, trigger && context.inZone
      ? "Both the fresh IEX trade and ask are inside the completed trigger's compact reference zone (15 points)."
      : "Both a completed trigger and the fresh trade and ask inside its reference zone are required for 15 points; this check is unmet."],
  ];
  return scoreRubric.map(([label, possible], index) => ({ label, possible, earned: values[index][0], detail: values[index][1] }));
}

function triggerRequirement(trigger: Trigger): string {
  const level = priceText(trigger.level);
  switch (trigger.setup) {
    case "Opening range breakout": return `a bullish completed candle crossing from a prior close at or below the verified 09:30–09:44 ET high of ${level} to a close above it, with EMA9 above EMA20 and price at or above EMA9`;
    case "VWAP reclaim": return `a bullish completed candle moving from a prior close at or below its estimated IEX session VWAP to a close above current estimated VWAP (${level}) and EMA20`;
    case "Pullback": return `a bullish completed candle above the prior candle high of ${level}, after that candle touched the EMA9 area and held EMA20, with the upward EMA trend intact`;
    case "Breakout": return `a bullish completed close above the preceding 20 observed bars' high of ${level}, with EMA9 above EMA20 and price at or above EMA9`;
    default: return `a qualifying completed candle above the observed setup reference of ${level}`;
  }
}

function explainSetup(out: StockSetup, context?: DetailContext): StockSetupDetails {
  const bars = context?.bars ?? [];
  const last = bars.at(-1);
  const prior = bars.slice(0, -1);
  const trigger = context?.trigger;
  const reference = trigger ?? (out.setup ? context?.near : undefined);
  const levels: StockSetupDetails["levels"] = [];
  const addLevel = (label: string, price: number | null, kind: StockSetupDetails["levels"][number]["kind"]) => {
    if (positive(price)) levels.push({ label, price, kind });
  };
  const structure = bars.length >= 3 ? Math.min(...bars.slice(-3).map(bar => bar.low)) : null;
  addLevel("Recent three completed IEX bars' low", structure, "support");
  if (prior.length >= 20) addLevel("Preceding 20 observed IEX bars' high", Math.max(...prior.slice(-20).map(bar => bar.high)), "resistance");
  const opening = context ? bars.filter(bar => bar.time < context.start + 15 * MINUTE) : [];
  const completeOpening = !!context && opening.length === 15 && opening.every((bar, index) => bar.time === context.start + index * MINUTE);
  if (completeOpening) addLevel("Verified 09:30–09:44 ET opening-range high", Math.max(...opening.map(bar => bar.high)), "resistance");
  addLevel("Estimated IEX session VWAP", out.vwap, "reference");
  addLevel("EMA9 of completed observed IEX bars", out.ema9, "reference");
  addLevel("EMA20 of completed observed IEX bars", out.ema20, "reference");
  if (reference?.setup === "Pullback") addLevel("Prior completed IEX candle high", reference.level, "reference");
  const priorHigh = prior.length ? Math.max(...prior.map(bar => bar.high)) : null;
  addLevel("Earlier observed IEX session high", priorHigh, "reference");

  const trendLabel = !last || !positive(out.ema9) || !positive(out.ema20) ? "Trend not assessed"
    : out.ema9 > out.ema20 && last.close >= out.ema9 ? "Upward: EMA9 above EMA20, close at or above EMA9"
      : out.ema9 > out.ema20 ? "EMA9 above EMA20, close below EMA9"
        : "EMA9 is not above EMA20";
  const thesis = trigger && last
    ? `${out.ticker}: ${trigger.explanation} The last completed close was ${priceText(last.close)}, versus the ${priceText(trigger.level)} trigger reference; the trigger candle at ${trigger.at} had ${trigger.relativeVolume.toFixed(2)}× its preceding 20 observed bars' average.`
    : reference && last ? `${out.ticker} is being watched for ${reference.setup.toLowerCase()} around the ${priceText(reference.level)} observed reference. The last completed close was ${priceText(last.close)}; the required completed-bar pattern has not confirmed.`
      : last ? `${out.ticker} has no completed long setup selected. The last completed close was ${priceText(last.close)}. ${trendLabel}; wait for the stated pattern and data checks to align.`
        : `${out.ticker} has no assessable completed-bar setup in this scan. Valid current-session IEX observations are needed before a trade thesis can be formed.`;
  const confirmation = reference
    ? `${trigger ? "Completed-bar pattern confirmed" : "Still required"}: ${triggerRequirement(reference)}. The pattern needs IEX closed-bar relative volume of at least ${reference.volumeNeeded.toFixed(2)}×.${out.relativeVolume === null ? "" : ` Latest bar: ${out.relativeVolume.toFixed(2)}×.${trigger ? ` Trigger bar: ${trigger.relativeVolume.toFixed(2)}× at ${trigger.at}; valid for up to three minutes after its close.` : ""}`} ${out.state === "entry-zone" ? "The latest IEX trade and ask also pass the entry-zone checks." : "Entry eligibility still requires all listed checks to pass; pattern confirmation alone does not qualify an entry."}`
    : "Wait for a qualifying completed-bar pattern, sufficient IEX volume, fresh two-sided pricing, and all stated data checks. No conditional trigger has been selected.";
  const invalidation = out.state === "entry-zone" && positive(out.stop) && positive(structure)
    ? `This reference plan is invalidated at the stop of ${priceText(out.stop)}, below the trigger’s three-bar low of ${priceText(trigger?.structure ?? structure)}. The lower-zone entry-to-stop distance is at least 0.75 ATR measured at confirmation. A stop order's execution price is not guaranteed.`
    : out.state === "extended" ? "The compact entry condition has failed because the latest trade or IEX ask is above its reference zone. No active entry plan: wait for a fresh qualifying setup and avoid carrying forward the old trigger as an entry."
      : `No active entry plan. ${out.reasons[0] ?? "The required completed-bar pattern has not confirmed."} Reassess after the missing checks pass; any conditional reference must be recomputed with a fresh scan.`;
  const cautions = ["IEX is a single-exchange feed: observed prices, spread, and volume are not the consolidated US market or a Kraken execution quote."];
  cautions.push(...out.reasons.filter(reason => out.state !== "entry-zone" || reason.includes("availability")));
  if (out.vwap !== null) cautions.push("Session VWAP is estimated from available IEX minute bars; missing minute VWAP uses typical OHLC prices.");
  if (context && !completeOpening) cautions.push("The 15-minute opening range is unconfirmed because at least one 09:30–09:44 ET IEX bar is missing; no opening-range level is supplied.");
  const recent = bars.slice(-21);
  if (recent.length > 1) {
    const slots = (recent.at(-1)!.time - recent[0].time) / MINUTE + 1;
    if (slots > recent.length) cautions.push(`Sparse IEX observations: ${recent.length} completed bars cover ${slots} one-minute slots. Missing minutes are not filled with zero volume.`);
  }
  const observedPrice = out.price ?? last?.close;
  if (positive(priorHigh) && positive(observedPrice) && priorHigh > observedPrice) cautions.push(`An earlier observed IEX session high at ${priceText(priorHigh)} is above the latest ${out.price === null ? "completed close" : "trade"}. It is an observed reference, not a guaranteed price barrier.`);
  return { thesis, triggerPrice: reference?.level ?? null, triggerConfirmed: !!trigger, confirmation, invalidation,
    cautions: [...new Set(cautions)], scoreFactors: explainScore(out, context), levels, trendLabel,
    target1: out.state === "entry-zone" && positive(out.entryMax) && positive(out.stop) ? finiteOrNull(out.entryMax + (out.entryMax - out.stop)) : null,
    target2: out.state === "entry-zone" ? out.target : null };
}

/** Recheck a displayed plan's eligibility without moving or reranking the user's reading view. */
export function stockSetupIsCurrent(setup: StockSetup, response: StockScanResponse | null, nowMs: number): boolean {
  if (setup.state !== "entry-zone" || !response || response.status !== "ready" || response.provider !== "Alpaca" || response.feed !== "iex"
    || getStockSession(new Date(nowMs)).phase !== "regular" || !fresh(response.asOf, nowMs, 60_000)
    || normalizeStockSymbol(setup.symbol) !== setup.symbol || setup.symbol.split(":")[1] !== setup.ticker) return false;
  if (![setup.entry, setup.entryMax, setup.stop, setup.target].every(positive)
    || setup.entryMax! <= setup.entry! || setup.stop! >= setup.entry! || setup.target! <= setup.entryMax!) return false;
  const barTime = timestamp(setup.barAt);
  const barAge = nowMs - (barTime + MINUTE);
  if (!Number.isFinite(barAge) || barTime % MINUTE !== 0 || barAge < 0 || barAge > 60_000) return false;
  if (setup.triggerAt) {
    const triggerTime = timestamp(setup.triggerAt), triggerAge = nowMs - triggerTime - MINUTE;
    if (!Number.isFinite(triggerAge) || triggerTime % MINUTE !== 0 || triggerAge < 0 || triggerAge > 3 * MINUTE) return false;
  }
  const matching = response.rows.filter(row => row.ticker === setup.ticker);
  if (matching.length !== 1) return false;
  const data = matching[0], trade = data.latestTrade, quote = data.latestQuote;
  if (data.historyComplete !== true || !positive(trade?.price) || !fresh(trade?.at, nowMs, 45_000) || !quote
    || ![quote.bid, quote.ask, quote.bidSize, quote.askSize].every(positive) || quote.ask < quote.bid || !fresh(quote.at, nowMs, 45_000)) return false;
  const spread = ((quote.ask - quote.bid) / (quote.ask / 2 + quote.bid / 2)) * 100;
  return Number.isFinite(spread) && spread <= MAX_SPREAD_PCT
    && trade!.price >= setup.entry! && trade!.price <= setup.entryMax! && quote.ask >= setup.entry! && quote.ask <= setup.entryMax!;
}

/**
 * Long-only research from Alpaca's limited IEX feed. The checklist is intentionally explicit;
 * no trade execution, broker eligibility lookup, consolidated volume, or NBBO is implied.
 * Minute bars can be sparse. Require 21 observed bars within 30 elapsed minutes and adjacent
 * trigger/confirmation bars, but never fill missing IEX minutes with fabricated zero-volume bars.
 */
export function scanStockSetups(response: StockScanResponse, watchlist: StockItem[], nowMs: number): StockSetup[] {
  const session = getStockSession(new Date(nowMs));
  const globalReasons: string[] = [];
  if (response.status !== "ready") globalReasons.push(response.message || (response.status === "not-configured" ? "Connect Alpaca market data to scan stocks." : "Alpaca market data is unavailable."));
  if (response.provider !== "Alpaca" || response.feed !== "iex") globalReasons.push("Expected Alpaca IEX data; this feed cannot be assessed.");
  if (!fresh(response.asOf, nowMs, 60_000)) globalReasons.push("Scan response is stale or has an invalid/future timestamp (maximum age 60 seconds).");
  if (session.phase !== "regular") globalReasons.push(`${session.label}: entries require the scheduled regular stock session. ${session.detail}`);

  const parts = Number.isFinite(nowMs) ? Object.fromEntries(easternClock.formatToParts(new Date(nowMs)).map(part => [part.type, part.value])) : null;
  const minute = parts ? Number(parts.hour) * 60 + Number(parts.minute) : NaN;
  const start = Math.floor(nowMs / MINUTE) * MINUTE - (minute - 570) * MINUTE;
  const end = start + (getStockSession(new Date(start + 210 * MINUTE)).phase === "regular" ? 390 : 210) * MINUTE;
  const matches = new Map<string, StockMarketData[]>();
  for (const row of response.rows ?? []) {
    if (!row || typeof row.ticker !== "string") continue;
    matches.set(row.ticker, [...(matches.get(row.ticker) ?? []), row]);
  }

  const detailContexts = new Map<string, DetailContext>();
  const seen = new Set<string>();
  const result = watchlist.filter(item => { if (seen.has(item.symbol)) return false; seen.add(item.symbol); return true; }).map(item => {
    const out = base(item);
    const blocked = [...globalReasons];
    if (normalizeStockSymbol(item.symbol) !== item.symbol) blocked.push("The watchlist symbol must use a supported US exchange and stock ticker.");
    const available = item.availability !== "unavailable";
    if (!available) out.reasons.push("Marked unavailable in your Kraken account.");
    else if (item.availability !== "confirmed") out.reasons.push("Kraken availability is unconfirmed. You can research this setup; verify your account's Buy list before using an entry plan.");
    const matched = matches.get(out.ticker) ?? [];
    if (matched.length !== 1) blocked.push(matched.length > 1 ? "Conflicting duplicate market rows prevent a reliable symbol match." : `No matching Alpaca IEX data for ${out.ticker}.`);
    if (blocked.length && (matched.length !== 1 || response.status !== "ready" || !Number.isFinite(start) || normalizeStockSymbol(item.symbol) !== item.symbol)) {
      out.state = available ? "blocked" : "unavailable"; out.reasons.push(...blocked); return out;
    }
    const data = matched[0];
    const tradeFresh = positive(data.latestTrade?.price) && fresh(data.latestTrade?.at, nowMs, 45_000);
    if (tradeFresh) out.price = data.latestTrade!.price;
    else blocked.push("A positive IEX trade no older than 45 seconds is required; missing, stale, or future trades are not current prices.");
    const quote = data.latestQuote;
    const validQuote = !!quote && [quote.bid, quote.ask, quote.bidSize, quote.askSize].every(positive) && quote.ask >= quote.bid;
    const quoteFresh = validQuote && fresh(quote!.at, nowMs, 45_000);
    if (quoteFresh) {
      out.quoteAt = quote!.at;
      out.spreadPct = finiteOrNull(((quote!.ask - quote!.bid) / (quote!.ask / 2 + quote!.bid / 2)) * 100);
      if (out.spreadPct === null || out.spreadPct > MAX_SPREAD_PCT) blocked.push(`IEX quoted spread exceeds the ${MAX_SPREAD_PCT.toFixed(2)}% limit.`);
    } else blocked.push("A fresh two-sided IEX quote with positive sizes is required (45 seconds maximum age; not an NBBO quote).");
    if (out.price !== null && positive(data.previousClose)) out.changePct = finiteOrNull((out.price / data.previousClose - 1) * 100);

    const clean = cleanBars(Array.isArray(data.bars) ? data.bars : [], start, end, nowMs);
    const bars = clean.bars;
    const detailContext: DetailContext = { bars, start, tradeFresh, quoteFresh };
    detailContexts.set(out.symbol, detailContext);
    const last = bars.at(-1), previous = bars.at(-2);
    out.barAt = last?.at ?? null;
    if (clean.invalid) blocked.push("Invalid or conflicting minute bars were excluded; complete session calculations cannot be trusted.");
    if (bars.length < 21) blocked.push(`Need at least 21 valid completed regular-session one-minute bars; ${bars.length} available.`);
    if (!last || nowMs - (last.time + MINUTE) > 60_000) blocked.push("The latest completed IEX minute is missing (last bar closed over 60 seconds ago). Wait for complete recent bars before retaining a setup.");
    const recent = bars.slice(-21);
    const adjacent = !!last && !!previous && last.time - previous.time === MINUTE;
    const boundedHistory = recent.length === 21 && recent[20].time - recent[0].time <= 30 * MINUTE;
    if (last && previous && !adjacent) blocked.push("The latest two completed bars are not adjacent minutes; wait for a fresh consecutive confirmation.");
    if (recent.length === 21 && !boundedHistory) blocked.push("Sparse IEX history: the latest 21 observed bars span more than 30 minutes.");
    // A complete request from 09:30 can legitimately have no 09:30 IEX prints. Only the
    // verified opening range needs every opening minute; never manufacture missing bars.
    const fullHistory = data.historyComplete === true && !clean.invalid;
    if (!fullHistory) blocked.push("Complete available IEX session bar history is required; the history is missing, invalid, or truncated.");
    const closes = bars.map(bar => bar.close);
    out.ema9 = ema(closes, 9); out.ema20 = ema(closes, 20); out.atr = atr14(bars);
    if (fullHistory) out.vwap = estimatedVwap(bars);
    if (recent.length === 21) {
      const priorVolume = average(recent.slice(0, 20).map(bar => bar.volume));
      if (positive(priorVolume)) out.relativeVolume = finiteOrNull(recent[20].volume / priorVolume);
    }
    if (bars.length >= 21 && (!positive(out.ema9) || !positive(out.ema20) || !positive(out.atr) || !positive(out.vwap) || !positive(out.relativeVolume))) blocked.push("Valid finite trend, volatility, session VWAP, and volume estimates are required.");
    out.evidence.push("IEX-only observations: prices, quoted spread, and volume do not represent every US exchange or Kraken execution.");
    if (out.vwap !== null) out.evidence.push(`Session VWAP is a bar-based IEX estimate${bars.some(bar => bar.vwap === null) ? "; missing minute VWAP uses typical (high + low + close) / 3 prices" : "; minute VWAP and volume may cover different eligible trades"}.`);
    if (out.relativeVolume !== null) out.evidence.push(`IEX relative volume ${out.relativeVolume.toFixed(2)}×: last completed bar versus the preceding 20 observed bars; no zero filling or consolidated-volume claim.`);
    if (recent.length === 21) out.evidence.push(`Recent coverage: 21 observed bars across ${(recent[20].time - recent[0].time) / MINUTE + 1} elapsed one-minute slots.`);

    if (!last || !previous || !positive(out.ema9) || !positive(out.ema20) || !positive(out.atr) || !positive(out.vwap) || !positive(out.relativeVolume)) {
      out.state = available ? "blocked" : "unavailable"; out.reasons.push(...blocked); return out;
    }
    const atr = out.atr;
    const trend = out.ema9 > out.ema20 && last.close >= out.ema9;
    const candidates = findTriggers(bars, start);
    if (!candidates.some(candidate => candidate.setup === "Opening range breakout")) out.evidence.push("Opening range is unconfirmed: all 09:30–09:44 ET minute bars are required for an opening-range breakout.");
    const reference = out.price !== null && quoteFresh ? Math.max(out.price, quote!.ask) : null;
    const recentTriggers: Trigger[] = [];
    for (let endIndex = bars.length; endIndex >= 21; endIndex--) {
      const triggerBar = bars[endIndex - 1];
      if (nowMs - (triggerBar.time + MINUTE) > 3 * MINUTE) break;
      const continuation = bars.slice(endIndex);
      if (!continuation.every((bar, index) => bar.time === triggerBar.time + (index + 1) * MINUTE)) continue;
      for (const trigger of (endIndex === bars.length ? candidates : findTriggers(bars.slice(0, endIndex), start))) {
        if (!trigger.confirmed) continue;
        const stop = Math.min(trigger.structure - 0.1 * trigger.atr, trigger.level + 0.02 * trigger.atr - 0.75 * trigger.atr);
        if (continuation.some(bar => bar.low <= stop || bar.close < trigger.level)) continue;
        recentTriggers.push(trigger);
      }
    }
    const assessments = recentTriggers.map(trigger => {
      const entry = trigger.level + 0.02 * trigger.atr;
      // The crossing candle has already closed. Give its observed close a small fixed
      // follow-through allowance; never recenter this ceiling on subsequent live prices.
      const entryMax = Math.max(entry, trigger.close) + 0.25 * trigger.atr;
      const stop = Math.min(trigger.structure - 0.1 * trigger.atr, entry - 0.75 * trigger.atr);
      const target = entryMax + 2 * (entryMax - stop);
      const rewardRisk = reference === null ? null : finiteOrNull((target - reference) / (reference - stop));
      const oversized = trigger.close > trigger.level + trigger.atr;
      const validGeometry = [entry, entryMax, stop, target, rewardRisk].every(positive)
        && stop < entry && target > entryMax && rewardRisk! >= 2 - 1e-9 && !oversized;
      const inZone = reference !== null && out.price !== null && out.price >= entry && quote!.ask >= entry && reference <= entryMax;
      return { trigger, entry, entryMax, stop, target, rewardRisk, validGeometry, inZone, oversized, volumeConfirmed: trigger.relativeVolume >= trigger.volumeNeeded };
    });
    // One incomplete preferred pattern must not hide a different independently qualified
    // setup. Keep the preferred trigger for a useful watch/extended explanation if none pass.
    const selected = assessments.find(candidate => candidate.inZone && candidate.volumeConfirmed && candidate.validGeometry) ?? assessments[0];
    const trigger = selected?.trigger;
    const entry = selected?.entry ?? null, entryMax = selected?.entryMax ?? null, inZone = selected?.inZone ?? false;
    const near = candidates.filter(candidate => Math.abs(last.close - candidate.level) <= 0.75 * atr)
      .sort((a, b) => Math.abs(last.close - a.level) - Math.abs(last.close - b.level))[0];
    out.triggerAt = trigger?.at;
    out.setup = trigger?.setup ?? (trend || last.close > out.vwap ? near?.setup ?? null : null);
    Object.assign(detailContext, { scored: true, trend, trigger, near, inZone });

    const points: string[] = [];
    const add = (value: number, reason: string) => { out.score += value; points.push(`+${value} ${reason}`); };
    if (trend) add(20, "upward EMA trend");
    if (last.close > out.vwap) add(15, "close above estimated VWAP");
    if (out.relativeVolume >= 1.1) add(10, "IEX bar volume ≥1.10×"); else if (out.relativeVolume >= 1) add(5, "IEX bar volume ≥1.00×");
    if (trigger) add(15, "completed-bar trigger"); else if (near && out.setup) add(10, "near a setup level");
    if (tradeFresh && quoteFresh) add(15, "fresh IEX trade and quote");
    if (out.spreadPct !== null && out.spreadPct <= MAX_SPREAD_PCT) add(out.spreadPct <= 0.15 ? 10 : 5, "IEX spread passes");
    if (trigger && inZone) add(15, "trade and IEX ask within entry zone");
    out.evidence.push(`Checklist score ${out.score}/100: ${points.join("; ") || "no checklist points"}. Heuristic ranking, not a win probability.`);

    if (blocked.length || !available) {
      out.state = available ? "blocked" : "unavailable"; out.reasons.push(...blocked); return out;
    }
    if (!trigger) {
      out.state = "watch";
      out.reasons.push(out.setup && near ? `Watching ${out.setup.toLowerCase()} near ${priceText(near.level)}; wait for a qualifying completed candle and volume confirmation.` : "No completed long setup: wait for price structure and trend to improve.");
      if (!trend) out.reasons.push("The fast EMA trend is not yet aligned upward with price above EMA9.");
      return out;
    }
    out.evidence.push(`${trigger.explanation} Confirmed ${trigger.at} at ${trigger.relativeVolume.toFixed(2)}× IEX volume. Expires three minutes after that candle closes; later candles must hold the trigger and original stop.`);
    if (selected.oversized || (entry !== null && entryMax !== null && reference !== null && reference > entryMax)) {
      out.state = "extended";
      out.reasons.push(selected.oversized ? "The confirming candle closed more than 1 ATR above its trigger; wait for a fresh setup rather than chase." : `Price or IEX ask is above the fixed ${priceText(entry!)}–${priceText(entryMax!)} reference zone; wait for a fresh setup rather than chase.`);
      return out;
    }
    if (!inZone || !selected.volumeConfirmed) {
      out.state = "watch";
      if (!inZone) out.reasons.push("The latest trade and IEX ask have not held inside the completed trigger's entry zone.");
      if (!selected.volumeConfirmed) out.reasons.push(`Await IEX closed-bar volume of at least ${trigger.volumeNeeded.toFixed(2)}× the preceding 20 observed bars on a new trigger. This trigger measured ${trigger.relativeVolume.toFixed(2)}×.`);
      return out;
    }
    if (!selected.validGeometry) {
      out.state = "blocked"; out.reasons.push("A finite positive long plan with a structural stop and at least 2R gross could not be constructed."); return out;
    }
    out.state = "entry-zone"; out.entry = entry; out.entryMax = entryMax; out.stop = selected.stop; out.target = selected.target; out.rewardRisk = selected.rewardRisk;
    out.reasons.push("Completed-bar setup is inside its reference entry zone. Verify the current Kraken quote and availability before deciding to trade.");
    out.evidence.push("Stop sits below the trigger candle's three-bar structure with a 0.75 ATR minimum distance; target is 2R from the upper zone before costs. Prices and indicators are IEX references, not Kraken execution prices.");
    return out;
  });
  const order: Record<StockSetup["state"], number> = { "entry-zone": 0, watch: 1, extended: 2, blocked: 3, unavailable: 3 };
  return result.map(out => ({ ...out, details: explainSetup(out, detailContexts.get(out.symbol)) }))
    .sort((a, b) => order[a.state] - order[b.state] || b.score - a.score || a.symbol.localeCompare(b.symbol));
}
