"use client";
import { memo, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { stockWidgetDefinition, TRADINGVIEW_DATA_FAQ, validWidgetSymbol, type StockChartInterval,
  type StockNewsMode, type StockScreen, type StockWidgetDefinition, type StockWidgetTab } from "@/lib/tradingview-widgets";

const TABS: { id: StockWidgetTab; label: string; note: string }[] = [
  { id: "chart", label: "Chart", note: "Read the structure" },
  { id: "screener", label: "Screener", note: "Find movement" },
  { id: "heatmap", label: "Heatmap", note: "See the market" },
  { id: "news", label: "News", note: "Check the catalyst" },
];
const INTERVALS: { value: StockChartInterval; label: string }[] = [
  { value: "1", label: "1m" }, { value: "5", label: "5m" }, { value: "15", label: "15m" }, { value: "60", label: "1h" }, { value: "D", label: "1D" },
];
const SCREENS: { value: StockScreen; label: string }[] = [
  { value: "top_gainers", label: "Gainers" }, { value: "top_losers", label: "Losers" }, { value: "volume_leaders", label: "Most active" },
];

/** Only this isolated host is modified by TradingView; React never reconciles its children. */
const TradingViewFrame = memo(function TradingViewFrame({ definition, retry }: { definition: StockWidgetDefinition; retry: number }) {
  const host = useRef<HTMLDivElement>(null);
  const [status, setStatus] = useState<"loading" | "embedded" | "slow" | "failed">("loading");
  const json = JSON.stringify(definition.config);
  const { script: source, title, externalUrl, attributionUrl, attribution } = definition;

  useEffect(() => {
    const mount = host.current;
    if (!mount) return;
    let active = true;
    let frame: HTMLIFrameElement | null = null;
    setStatus("loading");
    const wrapper = document.createElement("div");
    wrapper.className = "tradingview-widget-container";
    wrapper.style.height = "100%"; wrapper.style.width = "100%";
    const canvas = document.createElement("div");
    canvas.className = "tradingview-widget-container__widget";
    canvas.style.height = "calc(100% - 32px)"; canvas.style.width = "100%";
    const copyright = document.createElement("div");
    copyright.className = "tradingview-widget-copyright";
    copyright.style.cssText = "min-height:32px;display:flex;align-items:center;justify-content:center;gap:4px;font-size:11px;";
    const link = document.createElement("a");
    link.href = attributionUrl; link.target = "_blank"; link.rel = "noopener noreferrer nofollow";
    const credit = document.createElement("span"); credit.className = "blue-text"; credit.textContent = attribution;
    link.appendChild(credit);
    const trademark = document.createElement("span"); trademark.className = "trademark"; trademark.textContent = "by TradingView";
    copyright.append(link, trademark); wrapper.append(canvas, copyright); mount.appendChild(wrapper);

    const loaded = () => { if (active) setStatus("embedded"); };
    const watchFrame = () => {
      const found = wrapper.querySelector("iframe");
      if (found && found !== frame) {
        frame?.removeEventListener("load", loaded);
        frame = found; frame.title = title;
        frame.addEventListener("load", loaded);
      }
    };
    const observer = new MutationObserver(watchFrame);
    observer.observe(wrapper, { childList: true, subtree: true });
    const script = document.createElement("script");
    script.type = "text/javascript"; script.src = source; script.async = true; script.textContent = json;
    script.onerror = () => { if (active) setStatus("failed"); };
    wrapper.appendChild(script);
    // A one-time loading hint only. There is no automatic reload or remount timer.
    const timeout = setTimeout(() => { if (active) setStatus(current => current === "loading" ? "slow" : current); }, 15_000);
    return () => {
      active = false; clearTimeout(timeout); observer.disconnect();
      frame?.removeEventListener("load", loaded); script.onerror = null;
      wrapper.remove();
    };
  }, [source, json, title, attributionUrl, attribution, retry]);

  return <div className="flex h-full min-w-0 flex-col">
    <div ref={host} className="min-h-0 min-w-0 flex-1" aria-label={title} />
    <div className="flex min-h-8 shrink-0 items-center gap-2 border-t border-line px-3 py-1.5 text-[11px]" role="status" aria-live="polite">
      <span className={status === "failed" || status === "slow" ? "text-warn" : "text-ink-3"}>{status === "failed" ? "TradingView could not load. Your browser or network may be blocking it."
        : status === "slow" ? "Still waiting for TradingView. If the view stays blank, retry or open it directly."
          : status === "loading" ? "Opening TradingView…" : "External TradingView view · data availability varies by exchange."}</span>
      {(status === "failed" || status === "slow") && <a href={externalUrl} target="_blank" rel="noopener noreferrer" className="ml-auto shrink-0 text-accent underline underline-offset-2">Open directly ↗</a>}
    </div>
  </div>;
});

/** Memoized so editing a native trade plan never recreates the external market view. */
export const StockMarketWidgets = memo(function StockMarketWidgets({ symbol }: { symbol: string }) {
  const [tab, setTab] = useState<StockWidgetTab>("chart");
  const [interval, setInterval] = useState<StockChartInterval>("5");
  const [screen, setScreen] = useState<StockScreen>("top_gainers");
  const [news, setNews] = useState<StockNewsMode>("market");
  const [hidden, setHidden] = useState(false);
  const [retry, setRetry] = useState(0);
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const id = useId();
  const valid = validWidgetSymbol(symbol);
  const definition = useMemo(() => valid ? stockWidgetDefinition(tab, symbol, interval, screen, news) : null, [valid, tab, symbol, interval, screen, news]);
  const ticker = symbol.split(":")[1] ?? symbol;
  const onTabKey = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const next = event.key === "ArrowRight" ? (index + 1) % TABS.length : event.key === "ArrowLeft" ? (index + TABS.length - 1) % TABS.length
      : event.key === "Home" ? 0 : event.key === "End" ? TABS.length - 1 : null;
    if (next == null) return;
    event.preventDefault(); setTab(TABS[next].id); tabRefs.current[next]?.focus();
  };

  return <section className="min-w-0 overflow-hidden rounded-2xl border border-line-strong bg-navy" aria-label="Stock market research">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-line px-4 py-4 sm:px-5">
      <div><p className="eyebrow !text-accent">Market workspace</p><h2 className="mt-1 text-[21px] font-semibold tracking-tight">See the trade before you plan it</h2><p className="mt-1 text-[12px] text-ink-2">Chart context, market movement, and headlines in one place.</p></div>
      <span className="rounded-full border border-warn/30 bg-warn/5 px-2.5 py-1 text-[10px] text-warn">Delayed / exchange-limited data</span>
    </header>

    <div className="grid grid-cols-4 border-b border-line bg-ground/50" role="tablist" aria-label="Stock market views">
      {TABS.map((item, index) => <button key={item.id} ref={node => { tabRefs.current[index] = node; }} id={`${id}-${item.id}-tab`} role="tab" type="button"
        aria-selected={tab === item.id} aria-controls={`${id}-${item.id}-panel`} tabIndex={tab === item.id ? 0 : -1}
        onClick={() => setTab(item.id)} onKeyDown={event => onTabKey(event, index)}
        className={`min-w-0 border-b-2 px-2 py-3 text-left sm:px-4 ${tab === item.id ? "border-accent bg-accent/5 text-ink" : "border-transparent text-ink-3 hover:bg-navy-2 hover:text-ink-2"}`}>
        <span className="block text-[13px] font-medium">{item.label}</span><span className="mt-0.5 hidden truncate text-[10px] text-ink-3 sm:block">{item.note}</span>
      </button>)}
    </div>

    <div className="flex min-h-14 flex-wrap items-center gap-x-3 gap-y-2 border-b border-line px-3 py-2.5 sm:px-4">
      {tab === "chart" ? <><span className="num text-[12px] font-medium text-ink">{symbol}</span><div className="flex gap-1" aria-label="Chart timeframe">{INTERVALS.map(item => <button key={item.value} type="button" aria-pressed={interval === item.value} className="control-button !px-2 !py-1 !text-[11px]" onClick={() => setInterval(item.value)}>{item.label}</button>)}</div><span className="text-[10px] text-ink-3">EMA · VWAP · Volume</span></>
        : tab === "screener" ? <><span className="text-[12px] text-ink-2">US stock universe</span><div className="flex gap-1" aria-label="Stock screener preset">{SCREENS.map(item => <button key={item.value} type="button" aria-pressed={screen === item.value} className="control-button !px-2 !py-1 !text-[11px]" onClick={() => setScreen(item.value)}>{item.label}</button>)}</div></>
          : tab === "heatmap" ? <><span className="text-[12px] font-medium">S&amp;P 500</span><span className="text-[11px] text-ink-3">Sector groups · size by market cap · color by daily change</span></>
            : <><span className="text-[12px] text-ink-2">Stock headlines</span><div className="flex gap-1" aria-label="Stock news scope"><button type="button" aria-pressed={news === "market"} className="control-button !px-2 !py-1 !text-[11px]" onClick={() => setNews("market")}>Market news</button><button type="button" aria-pressed={news === "symbol"} className="control-button !px-2 !py-1 !text-[11px]" onClick={() => setNews("symbol")}>{ticker} news</button></div></>}
      <button type="button" className="ml-auto shrink-0 text-[11px] text-ink-2 underline underline-offset-2 hover:text-ink" onClick={() => setHidden(value => !value)}>{hidden ? "Show market view" : "Hide market view"}</button>
    </div>

    <div id={`${id}-${tab}-panel`} role="tabpanel" aria-labelledby={`${id}-${tab}-tab`} className="h-[520px] min-w-0 bg-[#0c1422] sm:h-[620px]">
      {!definition ? <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center"><p className="text-[16px] font-medium">Choose a valid stock</p><p className="max-w-sm text-[12px] text-ink-2">Use the workspace watchlist to choose a symbol with its exchange, such as NASDAQ:NVDA. The chart will always match that selection.</p></div>
        : hidden ? <div className="flex h-full flex-col items-center justify-center gap-3 p-6 text-center"><span className="eyebrow">Quiet reading</span><p className="text-[18px] font-medium">Market view hidden</p><p className="max-w-sm text-[12px] text-ink-2">The external widget is closed while you read or edit your plan. Your workspace stays in place.</p><button type="button" className="control-button" onClick={() => setHidden(false)}>Show {TABS.find(item => item.id === tab)?.label.toLowerCase()}</button></div>
          : <TradingViewFrame key={tab} definition={definition} retry={retry} />}
    </div>

    <footer className="space-y-2 border-t border-line px-4 py-3 text-[11px] sm:px-5">
      <div className="flex flex-wrap items-center justify-between gap-2"><p className="font-medium text-ink-2">Verify current price in Kraken before using a plan.</p><div className="flex gap-3">{definition && <a href={definition.externalUrl} target="_blank" rel="noopener noreferrer" className="text-accent underline underline-offset-2">Open in TradingView ↗</a>}<button type="button" className="text-ink-2 underline underline-offset-2 disabled:opacity-40" disabled={hidden || !definition} onClick={() => setRetry(value => value + 1)}>Retry widget</button></div></div>
      <p className="text-ink-3">{tab === "chart" ? "Selected symbol is controlled by your workspace watchlist. " : tab === "news" ? "Market headlines may include international stocks. " : "Broad US market research; Kraken availability is not verified. "}TradingView controls its own updates; workspace reading controls do not pause this embed. Use “Hide market view” for quiet reading.</p>
      <p className="text-ink-3">These free embeds can be delayed or unavailable for some exchanges. A paid TradingView plan does not upgrade their data. <a href={TRADINGVIEW_DATA_FAQ} target="_blank" rel="noopener noreferrer" className="text-ink-2 underline underline-offset-2">Data details</a></p>
    </footer>
  </section>;
});
