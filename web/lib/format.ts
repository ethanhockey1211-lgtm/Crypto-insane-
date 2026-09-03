export function fmtPrice(p: number | null | undefined): string {
  if (p == null || !isFinite(p)) return "—";
  const digits = p >= 1000 ? 1 : p >= 100 ? 2 : p >= 1 ? 4 : p >= 0.01 ? 5 : 7;
  return p.toFixed(digits);
}

export function fmtPct(v: number | null | undefined, digits = 2): string {
  if (v == null || !isFinite(v)) return "—";
  const s = (v * 100).toFixed(digits);
  return (v > 0 ? "+" : "") + s + "%";
}

export function fmtX(v: number | null | undefined, digits = 1): string {
  return v == null || !isFinite(v) ? "—" : `${v.toFixed(digits)}×`;
}

export function fmtR(v: number | null | undefined): string {
  return v == null || !isFinite(v) ? "—" : `${v.toFixed(1)}R`;
}

export function fmtMoney(v: number | null | undefined, digits = 2): string {
  if (v == null || !isFinite(v)) return "—";
  return v.toLocaleString("en-US", { style: "currency", currency: "USD", minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function fmtVolume(v: number | null | undefined): string {
  if (v == null || !isFinite(v)) return "—";
  if (v >= 1e9) return `$${(v / 1e9).toFixed(2)}B`;
  if (v >= 1e6) return `$${(v / 1e6).toFixed(1)}M`;
  if (v >= 1e3) return `$${(v / 1e3).toFixed(0)}K`;
  return `$${v.toFixed(0)}`;
}

export function fmtAge(ms: number | null | undefined): string {
  if (ms == null) return "—";
  if (ms < 1000) return `${Math.max(0, Math.round(ms))}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.floor(ms / 60_000)}m ${Math.floor((ms % 60_000) / 1000)}s`;
}

export function fmtTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString("en-GB", { hour12: false });
}

export function signClass(v: number | null | undefined): string {
  if (v == null || !isFinite(v) || v === 0) return "text-ink-2";
  return v > 0 ? "up" : "down";
}

export const SETUP_ORDER = ["Breakout + Retest", "Range Breakout", "Breakout", "VWAP Reclaim", "Support Bounce", "Trend Pullback", "Momentum Continuation", "Reversal", "Volatility Expansion", "Volume Expansion", "None"];

/** Setup enum names from the API mapped to the labels the tape and scanner rows use. */
export const SETUP_LABELS: Record<string, string> = {
  None: "None", Breakout: "Breakout", BreakoutRetest: "Breakout + Retest", VwapReclaim: "VWAP Reclaim", SupportBounce: "Support Bounce",
  MomentumContinuation: "Momentum Continuation", RangeBreakout: "Range Breakout", TrendPullback: "Trend Pullback", Reversal: "Reversal",
  VolumeExpansion: "Volume Expansion", VolatilityExpansion: "Volatility Expansion",
};
export const setupLabel = (t: string): string => SETUP_LABELS[t] ?? t;
