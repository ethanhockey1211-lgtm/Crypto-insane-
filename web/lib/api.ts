import type { CandlesResponse, FeedStatus, Opportunity, ScannerStream, TapeEvent } from "./types";

export const API_BASE = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080").replace(/\/$/, "");

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, { cache: "no-store" });
  if (!res.ok) throw new Error(`${path} → ${res.status}`);
  return (await res.json()) as T;
}

export const api = {
  scanner: () => get<ScannerStream>("/api/scanner"),
  opportunity: (symbol: string) => get<Opportunity>(`/api/scanner/${encodeURIComponent(symbol)}`),
  feed: () => get<FeedStatus>("/api/system/feed"),
  tape: (limit = 100) => get<TapeEvent[]>(`/api/scanner/tape?limit=${limit}`),
  candles: (symbol: string, tf: string, limit = 400) => get<CandlesResponse>(`/api/market/${encodeURIComponent(symbol)}/candles?tf=${tf}&limit=${limit}`),
};
