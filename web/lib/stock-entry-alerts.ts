const DAY = 24 * 60 * 60_000;
const DEFAULT_COOLDOWN = 10 * 60_000;
const MAX_SYMBOLS = 100;
const MAX_STORAGE_LENGTH = 32_768;
const STOCK_SYMBOL = /^(?:NASDAQ|NYSE|AMEX):[A-Z]{1,5}(?:[.-][A-Z]{1,2})?$/;

function validSymbol(value: unknown): value is string {
  return typeof value === "string" && value === value.trim() && STOCK_SYMBOL.test(value);
}
function validTime(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 8.64e15;
}

/** Pure observed transitions. The caller supplies only currently eligible stock symbols. */
export class StockEntryAlertTracker {
  private previous: Set<string> | null = null;
  private sent = new Map<string, number>();
  private lastNow: number | null = null;
  private readonly cooldownMs: number;

  constructor(cooldownMs = DEFAULT_COOLDOWN) {
    this.cooldownMs = Number.isFinite(cooldownMs) && cooldownMs >= 0 ? cooldownMs : DEFAULT_COOLDOWN;
  }

  /** Pause/reconnect/availability changes must observe a new baseline before announcing entries. */
  resetBaseline(): void {
    this.previous = null;
  }

  update(symbols: readonly string[], enabled: boolean, now: number): string[] {
    // A clock adjustment cannot fabricate a new visit or erase the cooldown high-water mark.
    if (!validTime(now) || (this.lastNow !== null && now < this.lastNow)) {
      this.resetBaseline();
      return [];
    }
    this.lastNow = now;
    this.prune(now);
    if (!enabled) {
      this.resetBaseline();
      return [];
    }

    const current = new Set<string>();
    for (const symbol of symbols) {
      if (validSymbol(symbol)) current.add(symbol);
      if (current.size === MAX_SYMBOLS) break;
    }
    if (this.previous === null) {
      this.previous = current;
      return [];
    }

    const alerts: string[] = [];
    for (const symbol of current) {
      const sentAt = this.sent.get(symbol);
      if (this.previous.has(symbol) || (sentAt !== undefined && now - sentAt < this.cooldownMs)) continue;
      alerts.push(symbol);
      this.sent.set(symbol, now);
    }
    this.previous = current;
    this.prune(now);
    return alerts;
  }

  /** Replaces persisted cooldowns, never the active transition baseline. No storage API is used here. */
  restoreCooldowns(raw: string | null, now: number): void {
    this.resetBaseline();
    if (!validTime(now) || (this.lastNow !== null && now < this.lastNow)) return;
    this.lastNow = now;
    const restored = new Map<string, number>();
    if (typeof raw === "string" && raw.length <= MAX_STORAGE_LENGTH) {
      try {
        const parsed: unknown = JSON.parse(raw);
        if (parsed && typeof parsed === "object" && "version" in parsed && parsed.version === 1
          && "cooldowns" in parsed && Array.isArray(parsed.cooldowns)) {
          for (const row of parsed.cooldowns) {
            if (!row || typeof row !== "object" || !validSymbol(row.symbol) || !validTime(row.at)
              || row.at > now || now - row.at > DAY) continue;
            // Duplicate storage records retain the latest valid send time, never a shorter cooldown.
            restored.set(row.symbol, Math.max(restored.get(row.symbol) ?? 0, row.at));
          }
        }
      } catch { /* Corrupt or obsolete storage starts with no restored cooldowns. */ }
    }
    this.sent = restored;
    this.prune(now);
  }

  /** Versioned JSON for sessionStorage; at most 100 records, newest first. */
  serializeCooldowns(): string {
    if (this.lastNow !== null) this.prune(this.lastNow);
    return JSON.stringify({ version: 1, cooldowns: [...this.sent].map(([symbol, at]) => ({ symbol, at })) });
  }

  private prune(now: number): void {
    this.sent = new Map([...this.sent]
      .filter(([symbol, at]) => validSymbol(symbol) && validTime(at) && at <= now && now - at <= DAY)
      .sort(([aSymbol, aAt], [bSymbol, bAt]) => bAt - aAt || aSymbol.localeCompare(bSymbol))
      .slice(0, MAX_SYMBOLS));
  }
}
