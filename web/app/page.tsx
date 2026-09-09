"use client";
import { useCallback, useEffect, useState } from "react";
import dynamic from "next/dynamic";
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
import { EntryAlerts } from "@/components/EntryAlerts";
import { startHiddenMarkets } from "@/lib/hidden-markets";
import { store as displayStore } from "@/lib/display";
import { DisplayControls } from "@/components/DisplayControls";

const StockWorkspace = dynamic(() => import("@/components/StockWorkspace"), { ssr: false, loading: () => <div className="panel p-6">Opening stock trading desk…</div> });

type View = "scanner" | "heatmap" | "watchlist" | "alerts" | "paper" | "performance" | "backtest" | "tape";

export default function Page() {
  const [asset, setAsset] = useState<"crypto" | "stocks">("crypto");
  const [view, setView] = useState<View>("scanner");
  const [active, setActive] = useState<string | null>(null);
  const [list, add, remove] = useWatchlist();

  useEffect(() => {
    const syncAsset = () => setAsset(window.location.hash === "#stocks" ? "stocks" : "crypto");
    syncAsset();
    window.addEventListener("hashchange", syncAsset);
    return () => window.removeEventListener("hashchange", syncAsset);
  }, []);
  useEffect(() => {
    const stopVisibility = startHiddenMarkets();
    const stopDisplay = displayStore.start();
    void startConnection();
    const stopPolling = startFeedPolling();
    return () => { stopPolling(); stopVisibility(); stopDisplay(); };
  }, []);
  const open = useCallback((s: string) => { setActive(s); setAsset("crypto"); window.location.hash = "crypto"; }, []);
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
    <div className="h-dvh flex flex-col gap-2 p-2 sm:p-3 max-w-[2400px] mx-auto">
      <nav aria-label="Asset class" className="flex shrink-0 gap-2 items-center">
        <a href="#crypto" aria-current={asset === "crypto" ? "page" : undefined} className={`control-button ${asset === "crypto" ? "!border-accent text-accent" : "text-ink-2"}`} onClick={() => setAsset("crypto")}>Crypto scanner</a>
        <a href="#stocks" aria-current={asset === "stocks" ? "page" : undefined} className={`control-button ${asset === "stocks" ? "!border-accent text-accent" : "text-ink-2"}`} onClick={() => setAsset("stocks")}>Stocks & ETFs</a>
      </nav>
      {asset === "crypto" && <>
      <FeedBanner />
      <MarketHeader />
      <DisplayControls />
      </>}
      <div className="shrink-0" hidden={asset === "stocks"}><EntryAlerts onOpen={open} /></div>
      {asset === "stocks" ? <div className="flex-1 min-h-0"><StockWorkspace /></div> : <>
      <nav className="flex items-center gap-1 text-[14px] px-1 overflow-x-auto no-scrollbar shrink-0" aria-label="Views">
        {(["scanner", "heatmap", "watchlist", "alerts", "paper", "performance", "backtest", "tape"] as View[]).map((v) => (
          <button key={v} aria-current={view === v ? "page" : undefined} onClick={() => setView(v)} className={`shrink-0 px-3 py-2 rounded-[3px] ${view === v ? "bg-navy-3 text-ink" : "text-ink-2 hover:text-ink"}`}>{v === "scanner" ? "Find setups" : v === "performance" ? "Signal evidence" : v === "paper" ? "Paper trading" : v === "backtest" ? "Historical replay" : v}</button>
        ))}
        <span className="ml-auto text-ink-3 hidden lg:inline text-[11px] tracking-wider uppercase">Spot markets · USD</span>
      </nav>
      {/* Phones: one panel at a time (the tape has its own tab). Desktop: scanner, setup drawer, and the tape strip. */}
      <div className={`flex-1 min-h-0 grid gap-1.5 grid-cols-1 grid-rows-[minmax(0,1fr)] ${active ? "lg:grid-cols-[minmax(0,1fr)_minmax(0,560px)]" : ""}`}>
        <div className={`min-h-0 ${active ? "hidden lg:block" : ""}`}>{main}</div>
        <div className={`min-h-0 ${active ? "" : "hidden"}`}>
          {active ? (
            <TradeSetupDrawer symbol={active} onClose={close} watched={list.includes(active)} onWatch={toggleWatch} />
          ) : (
            <div className="panel h-full flex items-center justify-center text-ink-3 text-[12px] p-6 text-center">Select an opportunity to see its chart, plan, evidence, invalidation, and position size.</div>
          )}
        </div>
      </div>
      </>}
    </div>
  );
}
