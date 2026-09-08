import { decision, scannerIsFresh } from "./decision";
import type { ScannerRow } from "./types";

export interface EntryNotice { id: string; at: number; row: ScannerRow }

/** Observed zone transitions only: never replay an existing setup on enable or reconnect. */
export class EntryAlertTracker {
  private previous: Set<string> | null = null;
  private pending = new Map<string, number>();
  private sent = new Map<string, number>();
  private lastCycle = -Infinity;

  constructor(private holdMs = 2000, private cooldownMs = 10 * 60_000) {}

  pause(): void { this.previous = null; this.pending.clear(); this.lastCycle = -Infinity; }

  update(rows: ScannerRow[], connected: boolean, at: string | null, now: number): EntryNotice[] {
    if (!connected || !scannerIsFresh(at, now)) { this.pause(); return []; }
    const cycle = Date.parse(at!);
    if (cycle <= this.lastCycle) return [];
    if (cycle - this.lastCycle > 10_000) { this.previous = null; this.pending.clear(); }
    this.lastCycle = cycle;
    const ready = rows.filter(row => decision(row, true).state === "watch" &&
      row.setupBias === "Bullish" && row.entryLow != null && row.entryHigh != null &&
      Number.isFinite(row.entryLow) && Number.isFinite(row.entryHigh) && row.entryLow > 0 && row.entryHigh >= row.entryLow &&
      row.entry != null && row.entry >= row.entryLow && row.entry <= row.entryHigh &&
      row.stop != null && row.stop < row.entryLow && row.target1 != null && row.target1 > row.entryHigh &&
      row.assessedPrice != null && row.assessedPrice >= row.entryLow && row.assessedPrice <= row.entryHigh &&
      Number.isFinite(row.price) && row.price >= row.entryLow && row.price <= row.entryHigh);
    const current = new Set(ready.map(row => row.symbol));
    if (this.previous === null) { this.previous = current; return []; }
    for (const symbol of this.pending.keys()) if (!current.has(symbol)) this.pending.delete(symbol);
    for (const [symbol, sent] of this.sent) if (now - sent > this.cooldownMs) this.sent.delete(symbol);
    const notices: EntryNotice[] = [];
    for (const row of ready) {
      if (!this.previous.has(row.symbol)) this.pending.set(row.symbol, cycle);
      const since = this.pending.get(row.symbol);
      if (since == null || cycle - since < this.holdMs) continue;
      this.pending.delete(row.symbol);
      if (this.sent.has(row.symbol)) continue;
      this.sent.set(row.symbol, now);
      notices.push({ id: `${row.symbol}:${cycle}`, at: now, row: { ...row } });
    }
    this.previous = current;
    return notices;
  }
}
