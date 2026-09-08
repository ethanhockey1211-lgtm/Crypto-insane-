import { decision, scannerIsFresh } from "./decision";
import type { QuoteDto, ScannerRow, SymbolSummaryDto } from "./types";

export interface RadarMarket {
  symbol: string;
  price: number;
  assessed: boolean;
  value: number;
  row?: ScannerRow;
}

export interface RadarInput {
  symbols: readonly SymbolSummaryDto[];
  rows: readonly ScannerRow[];
  /** A connected browser hub and a live exchange feed are both required. */
  live: boolean;
  scannerAt: string | null;
  now: number;
}

const positive = (value: number | null | undefined): value is number => value != null && Number.isFinite(value) && value > 0;

/** A quiet, crossed, or invalid quote must never win a live leaderboard. */
export function radarQuoteIsFresh(quote: QuoteDto | null | undefined, now: number): quote is QuoteDto {
  if (!quote || quote.stale || !positive(quote.price) || !positive(quote.bid) || !positive(quote.ask) || quote.ask < quote.bid) return false;
  const age = now - quote.receivedAtMs;
  return Number.isFinite(age) && age >= 0 && age <= 30_000 &&
    // Exchange clocks may lead reception slightly; match the execution policy's 2s skew allowance.
    Number.isFinite(quote.ageMs) && quote.ageMs >= -2000 && quote.ageMs <= 30_000;
}

function zoneDistance(row: ScannerRow): number | null {
  const state = decision(row, true).state;
  const low = row.entryLow, high = row.entryHigh, price = row.assessedPrice;
  if ((state !== "watch" && state !== "wait") || row.setupBias !== "Bullish" ||
    !positive(low) || !positive(high) || high < low || !positive(price) ||
    !positive(row.entry) || row.entry < low || row.entry > high ||
    !positive(row.stop) || row.stop >= low || !positive(row.target1) || row.target1 <= high) return null;
  // Use the assessed price so a later tick cannot silently revalidate an older plan.
  return price < low ? (low - price) / price : price > high ? (price - high) / price : 0;
}

/** Discovery is deliberately separate from execution eligibility; a mover is not an entry. */
export function buildMarketRadar(input: RadarInput, limit = 4) {
  const count = Number.isFinite(limit) ? Math.max(0, Math.floor(limit)) : 4;
  const scannerLive = input.live && scannerIsFresh(input.scannerAt, input.now);
  const catalog = new Map(input.symbols.map(summary => [summary.symbol, summary]));
  const quoted = [...catalog.values()].filter(summary => input.live && radarQuoteIsFresh(summary.quote, input.now));
  const quotes = new Map(quoted.map(summary => [summary.symbol, summary.quote!]));
  const assessed = new Map(input.rows.filter(row => scannerLive && !row.stale && positive(row.price) && quotes.has(row.symbol)).map(row => [row.symbol, row]));
  const rows = [...assessed.values()];
  const asMarket = (row: ScannerRow, value: number): RadarMarket => ({ symbol: row.symbol, price: quotes.get(row.symbol)!.price, assessed: true, value, row });
  const descending = (a: RadarMarket, b: RadarMarket) => b.value - a.value || a.symbol.localeCompare(b.symbol);
  const movers = (field: "r5m" | "r15m") => rows.filter(row => positive(row[field])).map(row => asMarket(row, row[field]!)).sort(descending).slice(0, count);
  const volume = rows.filter(row => positive(row.relVol) && row.relVol >= 1.5).map(row => asMarket(row, row.relVol!)).sort(descending).slice(0, count);
  const nearZone = rows.flatMap(row => {
    const distance = zoneDistance(row);
    return distance == null ? [] : [asMarket(row, distance)];
  }).sort((a, b) => a.value - b.value || (b.row?.score ?? 0) - (a.row?.score ?? 0) || a.symbol.localeCompare(b.symbol)).slice(0, count);
  const daily = quoted.flatMap(summary => {
    // Recalculate with the latest quote instead of freezing changes between catalog polls.
    // REST catalog changes are percentage points; all radar returns are fractions.
    const value = positive(summary.open24h) ? summary.quote!.price / summary.open24h - 1
      : summary.open24h == null && summary.change24hPct != null ? summary.change24hPct / 100 : null;
    return positive(value) ? [{
      symbol: summary.symbol, price: summary.quote!.price, assessed: assessed.has(summary.symbol),
      value, row: assessed.get(summary.symbol),
    }] : [];
  }).sort(descending).slice(0, count);
  return { total: catalog.size, quoted: quoted.length, assessed: assessed.size, scannerLive, movers5m: movers("r5m"), movers15m: movers("r15m"), volume, nearZone, daily };
}
