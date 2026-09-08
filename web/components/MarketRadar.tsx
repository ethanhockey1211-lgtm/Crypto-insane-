"use client";
import { useEffect, useState, type ReactNode } from "react";
import { store, useAllOrder, useCycle, useDisplay } from "@/lib/display";
import { useFeed, useHub } from "@/lib/store";
import { fmtPct, fmtPrice, fmtR, fmtX, setupLabel } from "@/lib/format";
import { buildMarketRadar, type RadarMarket } from "@/lib/radar";

type RadarKind = "momentum" | "volume" | "zone" | "daily";

const icons: Record<RadarKind, ReactNode> = {
  momentum: <path d="m4 16 5-5 4 3 7-9m-6 0h6v6" />,
  volume: <path d="M4 17v-4m5 4V7m5 10v-7m5 7V3" />,
  zone: <><circle cx="12" cy="12" r="8" /><circle cx="12" cy="12" r="3" /><path d="M12 2v3m0 14v3M2 12h3m14 0h3" /></>,
  daily: <><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></>,
};

function RadarCard({ title, subtitle, kind, items, empty, accessory, onSelect }: {
  title: string; subtitle: string; kind: RadarKind; items: RadarMarket[]; empty: string;
  accessory?: ReactNode; onSelect: (market: RadarMarket) => void;
}) {
  return <article className="min-w-0 overflow-hidden rounded-xl border border-line-strong bg-navy/80">
    <div className="flex items-start justify-between gap-2 p-4 pb-3">
      <div className="min-w-0">
        <h3 className="flex items-center gap-2 text-[14px] font-semibold"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4 shrink-0 text-accent" aria-hidden="true">{icons[kind]}</svg>{title}</h3>
        <p className="mt-1.5 text-[11px] text-ink-3">{subtitle}</p>
      </div>
      {accessory}
    </div>
    {items.length > 0 ? <ol className="border-t border-line">
      {items.map((market, index) => {
        const secondary = kind === "volume" ? `5m ${fmtPct(market.row?.r5m)}` : kind === "zone" ? `${fmtR(market.row?.netRewardRatio)} net · last scan` : `$${fmtPrice(market.price)}`;
        const value = kind === "volume" ? fmtX(market.value) : kind === "zone" ? market.value === 0 ? "In zone" : `${(market.value * 100).toFixed(2)}% away` : fmtPct(market.value);
        const detail = kind === "zone" ? setupLabel(market.row?.setup ?? "None") : kind === "volume" ? "vs typical 5m volume" : kind === "daily" ? "over 24 hours" : "over the selected window";
        return <li key={market.symbol} className="border-b border-line last:border-b-0">
          <button type="button" onClick={() => onSelect(market)} aria-label={`Inspect ${market.symbol.replace("-", "/")}, ${value}, ${detail}`} className="group flex min-h-[65px] w-full items-center gap-2.5 px-4 py-3 text-left transition-colors hover:bg-navy-2 focus-visible:bg-navy-2">
            <span className="num w-3 shrink-0 text-[10px] text-ink-3">{index + 1}</span>
            <span className="min-w-0 flex-1"><span className="block truncate text-[13px] font-semibold group-hover:text-accent">{market.symbol.replace(/-USD$/, "")}<span className="ml-1 text-[10px] font-normal text-ink-3">USD</span></span><span className="num mt-1 block truncate text-[10px] text-ink-2">{secondary}</span></span>
            <span className="shrink-0 text-right"><span className={`num block text-[13px] font-medium ${kind === "zone" || kind === "volume" ? "text-accent" : "text-up"}`}>{value}</span><span className="mt-1 block text-[9px] text-ink-3">{kind === "zone" ? "Inspect trigger →" : "Inspect market →"}</span></span>
          </button>
        </li>;
      })}
    </ol> : <div className="flex min-h-[150px] items-center border-t border-line px-5 py-6"><p className="max-w-[28ch] text-[12px] leading-relaxed text-ink-2">{empty}</p></div>}
  </article>;
}

export function MarketRadar({ onOpen, onFilter }: { onOpen: (symbol: string) => void; onFilter?: (symbol: string) => void }) {
  const display = useDisplay();
  const order = useAllOrder();
  const feed = useFeed();
  const hub = useHub();
  const cycle = useCycle();
  const [window, setWindow] = useState<"5m" | "15m">("5m");
  const [, refreshClock] = useState(0);
  useEffect(() => { const timer = setInterval(() => refreshClock(value => value + 1), 1000); return () => clearInterval(timer); }, []);
  // A held view describes the captured instant, and is explicitly labeled as a reference snapshot.
  const now = display.paused ? display.capturedAt ?? Date.now() : Date.now();
  const live = hub === "connected" && feed?.live === true;
  const radar = buildMarketRadar({
    symbols: order.flatMap(symbol => { const summary = store.getSymbol(symbol); return summary ? [summary] : []; }),
    rows: order.flatMap(symbol => { const row = store.getRow(symbol); return row ? [row] : []; }),
    live, scannerAt: cycle.at, now,
  });
  const select = (market: RadarMarket) => {
    if (market.assessed) onOpen(market.symbol);
    else (onFilter ?? onOpen)(market.symbol);
  };
  const paused = !live ? "Rankings resume when the live feed reconnects." : !radar.scannerLive ? "Waiting for a fresh scanner cycle. The 24h list can update while analysis loads." : null;

  return <section className="border-b border-line bg-gradient-to-br from-navy-2/50 via-ground to-ground p-4 sm:p-5" aria-label="Kraken market radar">
    <div className="mb-4 flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
      <div><p className="eyebrow mb-1.5 text-accent">{display.paused ? "Paused snapshot" : "Market discovery"}</p><h2 className="text-[23px] font-semibold tracking-tight">Market radar</h2><p className="mt-1 text-[12px] text-ink-2">{display.paused ? "Rankings are held for reading. Refresh to inspect current movement." : "Find movement across Kraken. Open a market to inspect its setup and risks."}</p></div>
      <div className="flex flex-wrap items-center gap-4 rounded-lg border border-line bg-navy/70 px-3 py-2.5 text-[10px] text-ink-3">
        <div><span className="num mr-1.5 text-[16px] text-ink">{radar.total}</span>USD pairs</div>
        <span className="h-5 w-px bg-line" aria-hidden="true" />
        <div><span className={`num mr-1.5 text-[16px] ${live ? "text-accent" : "text-ink-2"}`}>{radar.quoted}</span>{display.paused ? "quotes at snapshot" : "fresh quotes"}</div>
        <div><span className="num mr-1.5 text-[16px] text-ink">{radar.assessed}</span>assessed</div>
      </div>
    </div>
    {paused && <p className="mb-3 rounded-md border border-line-strong bg-navy/70 px-3 py-2 text-[12px] text-ink-2" role="status">{paused}</p>}
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
      <RadarCard title="Fast movers" subtitle={`Strongest positive ${window} returns`} kind="momentum" items={window === "5m" ? radar.movers5m : radar.movers15m} empty={paused ?? `No positive ${window} moves with fresh analysis yet.`} onSelect={select}
        accessory={<div className="flex shrink-0 rounded-md border border-line bg-ground p-0.5" aria-label="Momentum window">{(["5m", "15m"] as const).map(value => <button key={value} type="button" aria-pressed={window === value} onClick={() => setWindow(value)} className={`rounded px-2 py-1 text-[10px] ${window === value ? "bg-navy-3 text-accent" : "text-ink-3 hover:text-ink"}`}>{value}</button>)}</div>} />
      <RadarCard title="Volume pulse" subtitle="5m volume · at least 1.5× typical" kind="volume" items={radar.volume} empty={paused ?? "No unusual volume among markets with fresh analysis."} onSelect={select} />
      <RadarCard title="Near the entry zone" subtitle="Passing plans · distance at last scan" kind="zone" items={radar.nearZone} empty={paused ?? "No fresh plan passes execution checks yet. Developing setups are below."} onSelect={select} />
      <RadarCard title="24h market leaders" subtitle="Full catalog · independent of warmup" kind="daily" items={radar.daily} empty={!live ? "Rankings resume when the live feed reconnects." : "Waiting for fresh quotes and positive 24h changes."} onSelect={select} />
    </div>
    <p className="mt-3 text-[10px] text-ink-3">Movers and volume highlight activity; they do not establish an entry. Every plan still requires its stated trigger.</p>
  </section>;
}
