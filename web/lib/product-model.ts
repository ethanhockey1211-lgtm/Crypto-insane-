import type { ScannerRow } from "./types";

export function normalizeMarketSymbol(input: string): string {
  const symbol = input.trim().toUpperCase().replace(/\s/g, "").replace(/\//g, "-");
  return symbol.includes("-") ? symbol : `${symbol}-USD`;
}

export function snapshotIsFresh(at: string | null | undefined, now = Date.now()): boolean {
  if (!at) return false;
  const age = now - Date.parse(at);
  return Number.isFinite(age) && age >= -2000 && age <= 30_000;
}

export function qualifies(row: ScannerRow): boolean {
  return row.executionStatus === "Watch" && !row.stale && !row.doNotChase;
}
