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
  reasons: string[];
  evidence: string[];
}

const MINUTE = 60_000;
const MAX_SPREAD_PCT = 0.35;
const easternClock = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/New_York", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
});
type TimedBar = StockBar & { time: number };
type Trigger = { setup: StockPlan["setup"]; level: number; confirmed: boolean; volumeNeeded: number; explanation: string };

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

/** Recheck a displayed plan's eligibility without moving or reranking the user's reading view. */
export function stockSetupIsCurrent(setup: StockSetup, response: StockScanResponse | null, nowMs: number): boolean {
  if (setup.state !== "entry-zone" || !response || response.status !== "ready" || response.provider !== "Alpaca" || response.feed !== "iex"
    || getStockSession(new Date(nowMs)).phase !== "regular" || !fresh(response.asOf, nowMs, 60_000)
    || normalizeStockSymbol(setup.symbol) !== setup.symbol || setup.symbol.split(":")[1] !== setup.ticker) return false;
  if (![setup.entry, setup.entryMax, setup.stop, setup.target].every(positive)
    || setup.entryMax! <= setup.entry! || setup.stop! >= setup.entry! || setup.target! <= setup.entryMax!) return false;
  const barTime = timestamp(setup.barAt);
  const barAge = nowMs - (barTime + MINUTE);
  if (!Number.isFinite(barAge) || barTime % MINUTE !== 0 || barAge < 0 || barAge > 120_000) return false;
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

  const seen = new Set<string>();
  const result = watchlist.filter(item => { if (seen.has(item.symbol)) return false; seen.add(item.symbol); return true; }).map(item => {
    const out = base(item);
    const blocked = [...globalReasons];
    if (normalizeStockSymbol(item.symbol) !== item.symbol) blocked.push("The watchlist symbol must use a supported US exchange and stock ticker.");
    const available = item.availability === "confirmed";
    if (!available) out.reasons.push(item.availability === "unavailable" ? "Marked unavailable in your Kraken account." : "Confirm this stock is available in your Kraken account before an entry can qualify.");
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
    const last = bars.at(-1), previous = bars.at(-2);
    out.barAt = last?.at ?? null;
    if (clean.invalid) blocked.push("Invalid or conflicting minute bars were excluded; complete session calculations cannot be trusted.");
    if (bars.length < 21) blocked.push(`Need at least 21 valid completed regular-session one-minute bars; ${bars.length} available.`);
    if (!last || nowMs - (last.time + MINUTE) > 120_000) blocked.push("The latest completed one-minute bar is more than 120 seconds old or missing.");
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
    const bullish = last.close > last.open;
    const trend = out.ema9 > out.ema20 && last.close >= out.ema9;
    const priorBars = bars.slice(0, -1);
    const previousVwap = fullHistory ? estimatedVwap(priorBars) : null;
    const previousEma9 = ema(priorBars.map(bar => bar.close), 9);
    const previousEma20 = ema(priorBars.map(bar => bar.close), 20);
    const opening = bars.filter(bar => bar.time < start + 15 * MINUTE);
    const completeOpening = opening.length === 15 && opening.every((bar, index) => bar.time === start + index * MINUTE);
    const openingHigh = completeOpening ? Math.max(...opening.map(bar => bar.high)) : null;
    if (!completeOpening) out.evidence.push("Opening range is unconfirmed: all 09:30–09:44 ET minute bars are required for an opening-range breakout.");
    const resistance = Math.max(...priorBars.slice(-20).map(bar => bar.high));
    const candidates: Trigger[] = [];
    if (openingHigh !== null) candidates.push({ setup: "Opening range breakout", level: openingHigh, volumeNeeded: 1.1,
      confirmed: bullish && trend && previous.close <= openingHigh && last.close > openingHigh,
      explanation: "Completed candle crossed above the verified 15-minute opening-range high." });
    candidates.push({ setup: "VWAP reclaim", level: out.vwap, volumeNeeded: 1,
      confirmed: bullish && last.close > out.ema20 && positive(previousVwap) && previous.close <= previousVwap && last.close > out.vwap,
      explanation: "Completed candle reclaimed estimated session IEX VWAP from below and closed above EMA20." });
    candidates.push({ setup: "Pullback", level: previous.high, volumeNeeded: 1,
      confirmed: bullish && trend && positive(previousEma9) && positive(previousEma20) && previousEma9 > previousEma20
        && previous.low <= previousEma9 + 0.15 * atr && previous.close >= previousEma20 && last.close > previous.high,
      explanation: "Completed candle broke the prior pullback candle high after an EMA9-area touch in an upward EMA trend." });
    candidates.push({ setup: "Breakout", level: resistance, volumeNeeded: 1.1,
      confirmed: bullish && trend && last.close > resistance,
      explanation: "Completed candle closed above the preceding 20 observed bars' highs." });
    const reference = out.price !== null && quoteFresh ? Math.max(out.price, quote!.ask) : null;
    const structure = Math.min(...bars.slice(-3).map(bar => bar.low));
    const assessments = candidates.filter(candidate => candidate.confirmed).map(trigger => {
      const entry = trigger.level + 0.02 * atr, entryMax = entry + 0.25 * atr;
      const stop = Math.min(structure - 0.1 * atr, entry - 0.75 * atr);
      // A planned target of two gross R from the upper zone is not a price forecast.
      const target = entryMax + 2 * (entryMax - stop);
      const rewardRisk = reference === null ? null : finiteOrNull((target - reference) / (reference - stop));
      const validGeometry = [entry, entryMax, stop, target, rewardRisk].every(positive)
        && stop < entry && target > entryMax && rewardRisk! >= 2 - 1e-9;
      const inZone = reference !== null && out.price !== null && out.price >= entry && quote!.ask >= entry && reference <= entryMax;
      return { trigger, entry, entryMax, stop, target, rewardRisk, validGeometry, inZone, volumeConfirmed: out.relativeVolume! >= trigger.volumeNeeded };
    });
    // One incomplete preferred pattern must not hide a different independently qualified
    // setup. Keep the preferred trigger for a useful watch/extended explanation if none pass.
    const selected = assessments.find(candidate => candidate.inZone && candidate.volumeConfirmed && candidate.validGeometry) ?? assessments[0];
    const trigger = selected?.trigger;
    const entry = selected?.entry ?? null, entryMax = selected?.entryMax ?? null, inZone = selected?.inZone ?? false;
    const near = candidates.filter(candidate => Math.abs(last.close - candidate.level) <= 0.75 * atr)
      .sort((a, b) => Math.abs(last.close - a.level) - Math.abs(last.close - b.level))[0];
    out.setup = trigger?.setup ?? (trend || last.close > out.vwap ? near?.setup ?? null : null);

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
    out.evidence.push(trigger.explanation);
    if (entry !== null && entryMax !== null && reference !== null && reference > entryMax) {
      out.state = "extended";
      out.reasons.push(`Price or IEX ask is above the compact ${priceText(entry)}–${priceText(entryMax)} reference zone (0.25 ATR wide); wait for a fresh setup rather than chase.`);
      return out;
    }
    if (!inZone || out.relativeVolume < trigger.volumeNeeded) {
      out.state = "watch";
      if (!inZone) out.reasons.push("The latest trade and IEX ask have not held inside the completed trigger's entry zone.");
      if (out.relativeVolume < trigger.volumeNeeded) out.reasons.push(`Await IEX closed-bar volume of at least ${trigger.volumeNeeded.toFixed(2)}× the preceding 20 observed bars.`);
      return out;
    }
    if (!selected.validGeometry) {
      out.state = "blocked"; out.reasons.push("A finite positive long plan with a structural stop and at least 2R gross could not be constructed."); return out;
    }
    out.state = "entry-zone"; out.entry = entry; out.entryMax = entryMax; out.stop = selected.stop; out.target = selected.target; out.rewardRisk = selected.rewardRisk;
    out.reasons.push("Completed-bar setup is inside its reference entry zone. Verify the current Kraken quote and availability before deciding to trade.");
    out.evidence.push("Stop sits below recent three-bar structure with a 0.75 ATR minimum distance; target is 2R from the upper zone before costs. Prices and indicators are IEX references, not Kraken execution prices.");
    return out;
  });
  const order: Record<StockSetup["state"], number> = { "entry-zone": 0, watch: 1, extended: 2, blocked: 3, unavailable: 3 };
  return result.sort((a, b) => order[a.state] - order[b.state] || b.score - a.score || a.symbol.localeCompare(b.symbol));
}
