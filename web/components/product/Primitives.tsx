import type { ReactNode } from "react";
export function Icon({ name, size = 20, className = "" }: { name: string; size?: number; className?: string }) {
  const paths: Record<string, ReactNode> = {
    home: <><path d="m3 10 9-7 9 7v10H3z" /><path d="M9 20v-7h6v7" /></>,
    scan: <><path d="M3 8V3h5m8 0h5v5M3 16v5h5m8 0h5v-5M3 12h18" /><path d="m6 15 4-6 4 6 4-6" /></>,
    watch: <path d="M6 3h12v18l-6-4-6 4z" />,
    alerts: <><path d="M5 17h14l-2-3V9a5 5 0 0 0-10 0v5zM10 21h4M12 2v2" /></>,
    account: <><circle cx="12" cy="8" r="4" /><path d="M4 21v-2a8 8 0 0 1 16 0v2" /></>,
    arrow: <path d="M4 12h16m-6-6 6 6-6 6" />,
    arrowUp: <path d="M6 18 18 6M6 6h12v12" />,
    check: <path d="m5 12 4 4L19 6" />,
    plus: <path d="M12 5v14M5 12h14" />,
    close: <path d="m6 6 12 12M6 18 18 6" />,
    search: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></>,
    clock: <><circle cx="12" cy="12" r="9" /><path d="M12 6v6l4 2" /></>,
    shield: <><path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6z" /><path d="m8 11 3 3 5-6" /></>,
    chevron: <path d="m9 5 7 7-7 7" />,
    info: <><circle cx="12" cy="12" r="9" /><path d="M12 10v7M12 6v1" /></>,
    download: <path d="M12 3v12m-5-5 5 5 5-5M4 17v4h16v-4" />,
    external: <path d="M14 3h7v7m0-7L10 14M10 3H3v18h18v-7" />,
    refresh: <path d="M20 8a8 8 0 1 0 0 8M20 3v5h-5" />,
    moon: <path d="M20 15A9 9 0 0 1 9 4a9 9 0 1 0 11 11z" />,
    mail: <><rect x="3" y="5" width="18" height="14" rx="2" /><path d="m3 6 9 7 9-7" /></>,
    logout: <path d="M9 3H3v18h6M9 12h12m-5-5 5 5-5 5" />,
    filter: <><path d="M4 7h16M4 17h16" /><circle cx="9" cy="7" r="2" /><circle cx="15" cy="17" r="2" /></>,
    waves: <path d="M3 8c3-6 6 6 9 0s6 6 9 0M3 16c3-6 6 6 9 0s6 6 9 0" />,
  };
  return <svg aria-hidden="true" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" className={className}>{paths[name] ?? paths.info}</svg>;
}
export function Brand({ compact = false }: { compact?: boolean }) { return <a className="sw-brand" href="/" aria-label="Stillwatch home"><span className="sw-mark"><svg viewBox="0 0 32 32" fill="none" aria-hidden="true"><path d="M5 21V11m7 14V7m8 18V7m7 14V11" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" /></svg></span>{!compact && <span>stillwatch<span className="sw-brand-dot">.</span></span>}</a>; }
export function Sparkline({ variant = 0, muted = false }: { variant?: number; muted?: boolean }) {
  const lines = ["0,37 7,35 15,40 21,28 28,32 35,21 43,25 49,16 57,20 65,11 72,14 79,7 86,13 93,4 100,8", "0,25 8,30 14,23 21,34 28,28 36,31 43,17 50,21 57,16 64,24 72,11 79,14 85,8 93,11 100,3", "0,14 8,19 15,13 23,22 30,20 36,27 43,18 50,23 57,30 64,26 71,31 79,23 87,27 94,19 100,24"];
  return <svg viewBox="0 0 100 46" className={`sw-sparkline ${muted ? "muted" : ""}`} role="img" aria-label="Illustrative price movement"><polyline points={lines[variant % 3]} fill="none" stroke="currentColor" strokeWidth="1.5" vectorEffect="non-scaling-stroke" /></svg>;
}
export function ExampleChart() {
  return <div className="sw-example-chart" role="img" aria-label="Illustrative Bitcoin price chart with a conditional monitoring zone. Not live market data."><div className="sw-chart-grid" /><div className="sw-chart-zone"><span>Monitoring zone</span></div><svg viewBox="0 0 620 220" preserveAspectRatio="none"><defs><linearGradient id="chartFill" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#b7dca5" stopOpacity=".15" /><stop offset="100%" stopColor="#b7dca5" stopOpacity="0" /></linearGradient></defs><path d="M0 176 12 165 23 171 35 150 45 157 59 139 68 151 80 136 92 149 104 120 116 135 128 108 141 119 152 99 166 107 178 88 190 100 201 81 214 95 227 69 238 88 251 84 265 101 277 85 289 92 301 74 313 80 326 62 339 76 352 49 366 65 379 46 394 61 408 43 422 57 435 42 447 54 461 41 475 48 488 32 501 39 515 28 528 42 541 31 554 40 568 26 581 32 595 20 608 30 620 19V220H0Z" fill="url(#chartFill)" /><path d="M0 176 12 165 23 171 35 150 45 157 59 139 68 151 80 136 92 149 104 120 116 135 128 108 141 119 152 99 166 107 178 88 190 100 201 81 214 95 227 69 238 88 251 84 265 101 277 85 289 92 301 74 313 80 326 62 339 76 352 49 366 65 379 46 394 61 408 43 422 57 435 42 447 54 461 41 475 48 488 32 501 39 515 28 528 42 541 31 554 40 568 26 581 32 595 20 608 30 620 19" fill="none" stroke="#b7dca5" strokeWidth="2" vectorEffect="non-scaling-stroke" /></svg><div className="sw-chart-times"><span>08:00</span><span>10:00</span><span>12:00</span><span>14:00</span></div></div>;
}
export function EmptyState({ icon = "scan", title, children, action }: { icon?: string; title: string; children: ReactNode; action?: ReactNode }) { return <div className="sw-empty"><span className="sw-empty-icon"><Icon name={icon} size={24} /></span><h3>{title}</h3><p>{children}</p>{action}</div>; }
export function StatusPill({ children, tone = "neutral" }: { children: ReactNode; tone?: "neutral" | "green" | "amber" }) { return <span className={`sw-pill ${tone}`}><span />{children}</span>; }
export function money(value?: number | null) { return value == null ? "—" : value.toLocaleString("en-US", { style: "currency", currency: "USD", maximumFractionDigits: value < 1 ? 6 : 2 }); }
export function displaySymbol(symbol: string) { return symbol.replace(/[/\-_]?USD[T]?$/, "").replace(/^XBT$/, "BTC"); }
export function timeAgo(at?: string | null) { if (!at) return "Time unavailable"; const seconds = Math.max(0, Math.floor((Date.now() - new Date(at).getTime()) / 1000)); return seconds < 60 ? `${seconds}s ago` : seconds < 3600 ? `${Math.floor(seconds / 60)}m ago` : new Date(at).toLocaleString(); }
