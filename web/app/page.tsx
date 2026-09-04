"use client";
import { useCallback, useEffect, useState } from "react";
import { startConnection, startFeedPolling } from "@/lib/connection";
import { FeedBanner } from "@/components/FeedBanner";
import { MarketHeader } from "@/components/MarketHeader";
import { OpportunityScanner } from "@/components/OpportunityScanner";
import { TradeSetupDrawer } from "@/components/TradeSetupDrawer";
import { MarketTape } from "@/components/MarketTape";
import { MarketHeatmap } from "@/components/MarketHeatmap";
import { Watchlist, useWatchlist } from "@/components/Watchlist";
import { AlertManager } from "@/components/AlertManager";
import { PaperTrading } from "@/components/PaperTrading";
import { Performance } from "@/components/Performance";
import { Backtest } from "@/components/Backtest";

type View = "scanner" | "heatmap" | "watchlist" | "alerts" | "paper" | "performance" | "backtest" | "tape";

export default function Page() {
  const [view, setView] = useState<View>("scanner");
  const [active, setActive] = useState<string | null>(null);
  const [list, add, remove] = useWatchlist();

  useEffect(() => { void startConnection(); return startFeedPolling(); }, []);
  const open = useCallback((s: string) => setActive(s), []);
  const close = useCallback(() => setActive(null), []);
  const toggleWatch = useCallback((s: string) => (list.includes(s) ? remove(s) : add(s)), [list, add, remove]);

  const main = view === "scanner" ? <OpportunityScanner active={active} onOpen={open} />
    : view === "heatmap" ? <MarketHeatmap onOpen={open} />
    : view === "watchlist" ? <Watchlist onOpen={open} list={list} add={add} remove={remove} />
    : view === "alerts" ? <AlertManager onOpen={open} presetSymbol={active} />
    : view === "paper" ? <PaperTrading onOpen={open} />
    : view === "performance" ? <Performance onOpen={open} />
    : view === "backtest" ? <Backtest onOpen={open} />
    : <MarketTape onOpen={open} />;

  return (
    <div className="h-dvh flex flex-col gap-1.5 p-1 sm:p-1.5">
      <FeedBanner />
      <MarketHeader />
      <nav className="flex items-center gap-1 text-[11px] px-1 overflow-x-auto no-scrollbar shrink-0" aria-label="Views">
        {(["scanner", "heatmap", "watchlist", "alerts", "paper", "performance", "backtest", "tape"] as View[]).map((v) => (
          <button key={v} onClick={() => setView(v)} className={`shrink-0 px-2.5 py-1.5 sm:py-1 rounded-[3px] uppercase tracking-[0.1em] ${view === v ? "bg-navy-3 text-ink" : "text-ink-3 hover:text-ink-2"}`}>{v}</button>
        ))}
        <span className="ml-auto text-ink-3 hidden md:inline">market → opportunities → setup → execution → risk</span>
      </nav>
      {/* Phones: one panel at a time (the tape has its own tab). Desktop: scanner, setup drawer, and the tape strip. */}
      <div className="flex-1 min-h-0 grid gap-1.5 grid-cols-1 grid-rows-[minmax(0,1fr)] lg:grid-cols-[minmax(0,1fr)_minmax(0,560px)] lg:grid-rows-[minmax(0,1fr)_180px]">
        <div className={`min-h-0 ${active ? "hidden lg:block" : ""}`}>{main}</div>
        <div className={`min-h-0 row-span-2 ${active ? "" : "hidden lg:block"}`}>
          {active ? (
            <TradeSetupDrawer symbol={active} onClose={close} watched={list.includes(active)} onWatch={toggleWatch} />
          ) : (
            <div className="panel h-full flex items-center justify-center text-ink-3 text-[12px] p-6 text-center">Select an opportunity to see its chart, plan, evidence, invalidation, and position size.</div>
          )}
        </div>
        <div className="min-h-0 hidden lg:block">{view === "tape" ? <MarketHeatmap onOpen={open} /> : <MarketTape onOpen={open} />}</div>
      </div>
    </div>
  );
}
