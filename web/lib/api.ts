import type { AlertEvent, AlertFieldInfo, AlertRule, AlertRuleRequest, CandlesResponse, FeedStatus, Opportunity, ScannerStream, TapeEvent } from "./types";

export const API_BASE = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080").replace(/\/$/, "");

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, { cache: "no-store" });
  if (!res.ok) throw new Error(`${path} → ${res.status}`);
  return (await res.json()) as T;
}

async function send<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, { method, headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!res.ok) {
    let detail = `${res.status}`;
    try { detail = (await res.text()).replace(/^"|"$/g, "") || detail; } catch { /* keep status */ }
    throw new Error(detail);
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
}

export const api = {
  scanner: () => get<ScannerStream>("/api/scanner"),
  opportunity: (symbol: string) => get<Opportunity>(`/api/scanner/${encodeURIComponent(symbol)}`),
  feed: () => get<FeedStatus>("/api/system/feed"),
  tape: (limit = 100) => get<TapeEvent[]>(`/api/scanner/tape?limit=${limit}`),
  candles: (symbol: string, tf: string, limit = 400) => get<CandlesResponse>(`/api/market/${encodeURIComponent(symbol)}/candles?tf=${tf}&limit=${limit}`),
  alerts: {
    list: () => get<AlertRule[]>("/api/alerts"),
    events: (limit = 100) => get<AlertEvent[]>(`/api/alerts/events?limit=${limit}`),
    fields: () => get<{ fields: AlertFieldInfo[]; operators: string[] }>("/api/alerts/fields"),
    create: (r: AlertRuleRequest) => send<AlertRule>("POST", "/api/alerts", r),
    update: (id: string, r: AlertRuleRequest) => send<AlertRule>("PUT", `/api/alerts/${id}`, r),
    remove: (id: string) => send<void>("DELETE", `/api/alerts/${id}`),
  },
};
