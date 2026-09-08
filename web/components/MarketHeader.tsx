"use client";
import { useEffect, useState } from "react";
import { useCycle, useFeed, useHub, useMarket, useSymbol } from "@/lib/store";
import { scannerIsFresh } from "@/lib/decision";
import { radarQuoteIsFresh } from "@/lib/radar";
import { fmtPct, fmtPrice, signClass } from "@/lib/format";

const REGIME: Record<string, string> = { StrongRiskOn: "Strong risk-on", RiskOn: "Risk-on", Neutral: "Neutral", RiskOff: "Risk-off", StrongRiskOff: "Strong risk-off" };

function Asset({ symbol, live }: { symbol: string; live: boolean }) {
  const summary = useSymbol(symbol);
  const quote = summary?.quote;
  const fresh = live && radarQuoteIsFresh(quote, Date.now());
  const change = fresh && summary?.open24h && summary.open24h > 0 ? quote.price / summary.open24h - 1 : null;
  return <div className="min-w-0 px-3 py-2.5 border-l border-line"><p className="eyebrow">{symbol.replace("-USD", "")} <span className="text-ink-3">/ USD</span></p><div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 mt-1"><span className="num text-[14px] font-medium">{fresh ? fmtPrice(quote.price) : "—"}</span><span className={`num text-[10px] ${signClass(change)}`}>{fmtPct(change)} <span className="text-ink-3">24h</span></span></div></div>;
}

export function MarketHeader() {
  const market = useMarket(); const feed = useFeed(); const hub = useHub(); const cycle = useCycle();
  const [, refreshClock] = useState(0);
  useEffect(() => { const timer = setInterval(() => refreshClock(value => value + 1), 1000); return () => clearInterval(timer); }, []);
  const now = Date.now();
  const connected = hub === "connected" && feed?.live === true;
  const fresh = connected && scannerIsFresh(cycle.at, now);
  const regime = fresh && market ? REGIME[market.regime] : "Warming up";
  const regimeClass = !fresh ? "text-ink-3" : market?.regime.endsWith("On") ? "text-up" : market?.regime.endsWith("Off") ? "text-down" : "text-accent";
  const loaded = feed?.history.loaded ?? 0, total = feed?.history.total ?? 0;
  return <header className="rounded-xl border border-line-strong bg-navy overflow-hidden shrink-0">
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-b border-line">
      <div className="flex items-center gap-3"><div className="grid h-8 w-8 place-items-center rounded-lg bg-accent/10 border border-accent/20 text-accent" aria-hidden="true"><svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M3 17 8 12l4 3 8-11M15 4h5v5" /></svg></div><div><h1 className="text-[15px] font-semibold tracking-wide">KRAKEN <span className="font-normal text-ink-3">SCANNER</span></h1><p className="text-[10px] text-ink-3">Live markets. Clear plans.</p></div></div>
      <div className="flex items-center gap-2 text-[11px]"><span className={`h-1.5 w-1.5 rounded-full ${connected ? "bg-up" : "bg-warn"}`} /><span className={connected ? "text-up" : "text-warn"}>{connected ? "Exchange connected" : "Connecting to market"}</span><span className="hidden sm:inline text-ink-3 ml-2 num">{total} USD pairs</span></div>
    </div>
    <div className="grid grid-cols-2 sm:grid-cols-4 xl:grid-cols-[1fr_1.2fr_1.2fr_1fr_1.4fr]">
      <div className="px-4 py-2.5"><p className="eyebrow">Market regime</p><p className={`mt-1 text-[14px] font-medium ${regimeClass}`}>{regime}</p></div>
      <Asset symbol="BTC-USD" live={connected} /><Asset symbol="ETH-USD" live={connected} />
      <div className="px-3 py-2.5 border-l border-line"><p className="eyebrow">Above VWAP</p><p className="num text-[14px] mt-1">{fresh && market ? `${Math.round(market.breadthAboveVwap * 100)}%` : "—"}<span className="text-[10px] font-normal text-ink-3 ml-2">of assessed coins</span></p></div>
      <div className="col-span-2 sm:col-span-4 xl:col-span-1 px-4 py-2.5 border-t xl:border-t-0 xl:border-l border-line">
        <div className="flex justify-between gap-2 text-[10px]"><span className="text-ink-2">{feed?.history.complete ? "History ready" : "Loading analysis"}</span><span className="num text-ink-3">{loaded} / {total}</span></div>
        <div className="mt-2 h-1 rounded-full bg-ground overflow-hidden"><div className="h-full bg-accent/70 rounded-full transition-[width] duration-500" style={{ width: `${total ? Math.min(100, loaded / total * 100) : 0}%` }} /></div>
        {!!feed?.history.failed && <p className="text-warn text-[10px] mt-1">{feed.history.failed} markets waiting for complete history</p>}
      </div>
    </div>
  </header>;
}
