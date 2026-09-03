const KEY = "ts.watchlist.v1";

export function loadWatchlist(): string[] {
  try {
    const raw = typeof window !== "undefined" ? window.localStorage.getItem(KEY) : null;
    const parsed = raw ? (JSON.parse(raw) as unknown) : null;
    return Array.isArray(parsed) ? parsed.filter((s): s is string => typeof s === "string") : [];
  } catch {
    return [];
  }
}

export function saveWatchlist(list: string[]): void {
  try { window.localStorage.setItem(KEY, JSON.stringify(list)); } catch { /* storage unavailable: keep in memory */ }
}
