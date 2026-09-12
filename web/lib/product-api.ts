import { API_BASE } from "./api";
import type { MarketContext, ScannerRow, Opportunity } from "./types";

export interface Preferences {
  exchange: string; marketScope: string; conditions: string[]; pushEnabled: boolean;
  quietHoursEnabled: boolean; quietHoursStart: string; quietHoursEnd: string; timeZone: string;
  makerFeeBps: number; takerFeeBps: number; slippageBps: number; onboardingComplete: boolean;
}
export const defaultPreferences: Preferences = { exchange: "kraken", marketScope: "spot-usd", conditions: ["breakout", "trend"], pushEnabled: false, quietHoursEnabled: true, quietHoursStart: "22:00", quietHoursEnd: "08:00", timeZone: "UTC", makerFeeBps: 25, takerFeeBps: 40, slippageBps: 10, onboardingComplete: false };
export interface ProductMe {
  authenticated: boolean;
  user?: { id: string; email: string; displayName: string; emailConfirmed: boolean };
  preferences?: Preferences; watchlist: string[];
  subscription: { plan: "Free" | "Pro"; status: string; currentPeriodEnd?: string; cancelAtPeriodEnd: boolean };
  entitlements: { scanner: boolean; watchlist: boolean; alerts: boolean; backgroundPush: boolean; watchlistLimit: number; alertRuleLimit: number; historyDays: number };
  capabilities: { emailConfigured: boolean; billingConfigured: boolean; billingMode: string; liveChargingEnabled: boolean; supportedExchanges?: string[]; supportedMarketScopes?: string[] };
}
export interface Overview {
  mode: "demo" | "live" | "disconnected"; at: string;
  feed: { live: boolean; status: string; exchange: string; historyLoaded: number; historyTotal: number };
  market: MarketContext | null; rows: ScannerRow[]; reason: string; marketScope: string; limits?: unknown;
}
export interface ProductAlertRule { id: string; name: string; enabled: boolean; symbols: string[]; setupTypes: string[]; minimumScore: number; holdSeconds: number; cooldownMinutes: number; createdAt: string; }
export interface ProductAlertEvent {
  id: string; ruleId: string | null; ruleName: string; symbol: string; setupType: string;
  issuedAt: string; expiresAt: string; status: string; issueStatus: string; isTest: boolean; message: string; detailUrl: string;
  evidence?: { opportunity?: Opportunity };
  deliveries: { deviceId: string; state: string; attempts: number; lastAttemptAt: string | null; lastError: string | null }[];
  outcome: { status: string; initialPrice: number; targetPrice: number; stopPrice: number; windowEndsAt: string; observedAt: string | null; observedPrice: number | null; dataGap: boolean; interpretation: string } | null;
}
export interface PushDevice { id: string; deviceName: string; createdAt: string; lastAcceptedAt: string | null; lastTestAt: string | null; lastError: string | null; enabled: boolean; }
export interface ProductPushConfig {
  enabled: boolean; publicKey: string | null; limitations: string;
  ruleLimits: { minimumScore: number; maximumNameLength: number; maximumHoldSeconds: number; minimumCooldownMinutes: number; maximumCooldownMinutes: number };
}

export async function productRequest<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") {
    const csrf = await fetch(`${API_BASE}/api/product/auth/csrf`, { credentials: "include", cache: "no-store" });
    if (!csrf.ok) throw new Error("Could not establish a secure session. Check the connection and try again.");
    headers["X-CSRF-TOKEN"] = ((await csrf.json()) as { token: string }).token;
    headers["Content-Type"] = "application/json";
  }
  const response = await fetch(`${API_BASE}/api/product${path}`, { method, headers, credentials: "include", cache: "no-store", body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  let result: unknown;
  try { result = text ? JSON.parse(text) : undefined; } catch { result = undefined; }
  if (!response.ok) {
    const problem = result as { error?: string; detail?: string; title?: string; errors?: Record<string, string[]> } | undefined;
    throw new Error(problem?.error ?? problem?.detail ?? (problem?.errors ? Object.values(problem.errors).flat().join(" ") : undefined) ?? problem?.title ?? (response.status === 401 ? "Sign in to continue." : response.status === 403 ? "This feature requires an active Pro subscription." : `The request could not be completed (${response.status}).`));
  }
  return result as T;
}
