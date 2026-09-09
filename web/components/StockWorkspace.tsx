"use client";
import { useEffect, useState } from "react";
import { defaultStockState, normalizeStockSymbol, parseStockState, STOCK_STORAGE_KEY, type StockExchange, type StockWorkspaceState } from "@/lib/stocks";
import { getStockSession } from "@/lib/stock-session";
import { StockMarketWidgets } from "@/components/StockMarketWidgets";
import { StockTradeDesk } from "@/components/StockTradeDesk";

function SessionClock() {
  const [now, setNow] = useState<Date | null>(null);
  useEffect(() => { setNow(new Date()); const timer = setInterval(() => setNow(new Date()), 30_000); return () => clearInterval(timer); }, []);
  const session = now ? getStockSession(now) : null;
  return <div className="text-sm max-w-xl">
    <div className="flex flex-wrap gap-2 items-center"><span className="tag">{session?.label ?? "Exchange schedule"}</span><span className="num text-ink-2">{session ? `${session.centralTime} · ${session.easternTime}` : "Minnesota · Central time"}</span></div>
    <p className="text-ink-3 text-xs mt-2">{session?.detail ?? "Regular session: 8:30 a.m.–3 p.m. Central, subject to exchange holidays and early closes."}</p>
  </div>;
}

function downloadNotebook(state: StockWorkspaceState) {
  const blob = new Blob([JSON.stringify({ version: 1, exportedAt: new Date().toISOString(), ...state }, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a"); link.href = url; link.download = `stock-notebook-${new Date().toISOString().slice(0, 10)}.json`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export default function StockWorkspace() {
  const [state, setState] = useState<StockWorkspaceState>(defaultStockState);
  const [loaded, setLoaded] = useState(false);
  const [storageError, setStorageError] = useState("");
  const [query, setQuery] = useState("");
  const [ticker, setTicker] = useState("");
  const [exchange, setExchange] = useState<StockExchange>("NASDAQ");
  const [addError, setAddError] = useState("");
  const [confirmedOnly, setConfirmedOnly] = useState(false);
  const [showHidden, setShowHidden] = useState(false);
  const [watchlistOpen, setWatchlistOpen] = useState(false);
  const [notebookRevision, setNotebookRevision] = useState(0);
  const [backup, setBackup] = useState<StockWorkspaceState | null>(null);
  const [backupError, setBackupError] = useState("");
  useEffect(() => {
    try { setState(parseStockState(localStorage.getItem(STOCK_STORAGE_KEY))); }
    catch { setStorageError("Browser storage is unavailable. Export your notebook to keep a copy before leaving."); }
    setLoaded(true);
  }, []);
  useEffect(() => {
    if (!loaded) return;
    try { localStorage.setItem(STOCK_STORAGE_KEY, JSON.stringify(state)); }
    catch { setStorageError("Your latest changes could not be saved in this browser. Export the notebook to keep them."); }
  }, [state, loaded]);
  const selected = state.watchlist.find(s => s.symbol === state.selected);
  const visible = state.watchlist.filter(s => (showHidden || s.availability !== "unavailable") && (!confirmedOnly || s.availability === "confirmed") && `${s.symbol} ${s.name}`.toLowerCase().includes(query.toLowerCase()));
  const hidden = state.watchlist.filter(s => s.availability === "unavailable").length;
  const confirmed = state.watchlist.filter(s => s.availability === "confirmed").length;
  function addStock(e: React.FormEvent) {
    e.preventDefault();
    const symbol = normalizeStockSymbol(ticker, exchange);
    if (!symbol) { setAddError("Enter a US ticker such as AAPL or BRK.B and select its listing exchange."); return; }
    if (state.watchlist.some(s => s.symbol === symbol)) { setState(old => ({ ...old, selected: symbol, watchlist: old.watchlist.map(s => s.symbol === symbol && s.availability === "unavailable" ? { ...s, availability: "unconfirmed" } : s) })); setAddError(""); setTicker(""); return; }
    if (state.watchlist.length >= 100) { setAddError("Your watchlist holds up to 100 stocks. Remove one before adding another."); return; }
    setState(old => ({ ...old, selected: symbol, watchlist: [...old.watchlist, { symbol, name: symbol.split(":")[1], availability: "unconfirmed" }] })); setTicker(""); setAddError("");
  }
  function availability(value: "confirmed" | "unconfirmed" | "unavailable") {
    setState(old => {
      const watchlist = old.watchlist.map(s => s.symbol === old.selected ? { ...s, availability: value } : s);
      const next = value === "unavailable" ? watchlist.find(s => s.availability !== "unavailable")?.symbol ?? "" : old.selected;
      return { ...old, watchlist, selected: next };
    });
  }
  function removeSelected() {
    setState(old => { const watchlist = old.watchlist.filter(s => s.symbol !== old.selected); return { ...old, watchlist, selected: watchlist.find(s => s.availability !== "unavailable")?.symbol ?? "" }; });
  }
  async function readBackup(file?: File) {
    setBackup(null); setBackupError("");
    if (!file) return;
    if (file.size > 2_000_000) { setBackupError("Choose a stock notebook export under 2 MB."); return; }
    try {
      const raw = await file.text();
      const data = JSON.parse(raw);
      if (data?.version !== 1 || !Array.isArray(data.watchlist) || !Array.isArray(data.plans) || !Array.isArray(data.journal)) throw new Error("format");
      const restored = parseStockState(raw);
      if (restored.watchlist.length !== data.watchlist.length || restored.plans.length !== data.plans.length || restored.journal.length !== data.journal.length || !data.watchlist.every((item: { symbol?: unknown }, index: number) => typeof item?.symbol === "string" && normalizeStockSymbol(item.symbol) === restored.watchlist[index]?.symbol)) throw new Error("records");
      setBackup(restored);
    } catch { setBackupError("This file is not a valid stock notebook export. Your current notebook is unchanged."); }
  }
  if (!loaded) return <div className="panel p-6">Opening your stock workspace…</div>;
  return <div className="h-full overflow-y-auto min-w-0 pr-1" data-testid="stock-workspace">
    <header className="panel rounded-lg p-4 sm:p-5 mb-3">
      <div className="flex flex-wrap gap-4 justify-between items-start">
        <div><div className="eyebrow">Kraken · US stocks & ETFs · USD</div><h1 className="text-2xl sm:text-3xl font-semibold mt-1">Stock trading desk</h1><p className="text-ink-2 mt-2">Find movers. Study the chart. Build your plan. Review the trade.</p></div>
        <SessionClock />
      </div>
      <div className="flex flex-wrap items-center gap-3 mt-4 text-xs text-ink-2">
        <span className="tag text-warn">Free stock data · delayed / exchange-limited</span>
        <span>Regular Kraken app · Minnesota</span>
        <a className="text-accent underline" href="https://www.kraken.com/prices/stocks" target="_blank" rel="noopener noreferrer">Browse Kraken stocks ↗</a>
        <button className="control-button sm:ml-auto" onClick={() => downloadNotebook(state)}>Export notebook</button>
        <label className="control-button cursor-pointer">Import notebook<input className="sr-only" aria-label="Import stock notebook" type="file" accept=".json,application/json" onChange={e => { void readBackup(e.target.files?.[0]); e.target.value = ""; }} /></label>
      </div>
      <p className="text-xs text-ink-3 mt-3">Charts and screeners are supplied by TradingView. Confirm availability and the current price in Kraken. App orders outside market hours may queue for the next session. <a className="underline" href="https://support.kraken.com/articles/how-to-buy-and-sell-stocks-on-the-kraken-app" target="_blank" rel="noopener noreferrer">Kraken trading hours</a></p>
    </header>
    {storageError && <p role="alert" className="panel p-3 text-warn mb-3">{storageError}</p>}
    {backupError && <p role="alert" className="panel p-3 text-warn mb-3">{backupError}</p>}
    {backup && <div className="panel rounded-lg p-3 mb-3"><p className="mb-3">Import {backup.watchlist.length} stocks, {backup.plans.length} plans and {backup.journal.length} journal entries? This replaces the notebook on this browser. Export the current notebook first if you want to keep it.</p><div className="flex flex-wrap gap-2"><button className="control-button" onClick={() => { setState(backup); setBackup(null); setNotebookRevision(v => v + 1); }}>Replace notebook with backup</button><button className="control-button" onClick={() => setBackup(null)}>Cancel import</button></div></div>}
    <div className="grid grid-cols-1 lg:grid-cols-[230px_minmax(0,1fr)] gap-3 items-start">
      <aside className="panel rounded-lg p-3 min-w-0" aria-label="Stock watchlist">
        <div className="flex items-center justify-between mb-2"><h2 className="font-semibold text-base">Research watchlist</h2><span className="num text-ink-3">{state.watchlist.length}</span></div>
        <button className="control-button lg:hidden w-full" aria-expanded={watchlistOpen} onClick={() => setWatchlistOpen(v => !v)}>{watchlistOpen ? "Close watchlist" : `Choose stock · ${selected?.symbol.split(":")[1] ?? "none selected"}`}</button>
        <div className={watchlistOpen ? "mt-3 lg:mt-0" : "hidden lg:block"}>
        <p className="text-xs text-ink-3 mb-3">Starter ideas, not a verified Kraken catalog. {confirmed} marked available by you.</p>
        <label className="sr-only" htmlFor="stock-search">Search stock watchlist</label><input id="stock-search" className="field !py-2" placeholder="Search ticker or name" value={query} onChange={e => setQuery(e.target.value)} />
        <label className="flex gap-2 items-center text-xs my-3"><input type="checkbox" checked={confirmedOnly} onChange={e => setConfirmedOnly(e.target.checked)} />Only my confirmed stocks</label>
        <div className="max-h-64 lg:max-h-[540px] overflow-y-auto grid gap-1" aria-label="Research stocks">
          {visible.map(item => <button key={item.symbol} aria-pressed={item.symbol === state.selected} className={`min-w-0 w-full text-left rounded-md p-2 border ${item.symbol === state.selected ? "border-accent bg-navy-3" : "border-transparent hover:bg-navy-2"}`} onClick={() => { setState(old => ({ ...old, selected: item.symbol })); setWatchlistOpen(false); }}>
            <div className="flex items-center justify-between gap-2"><strong className="num shrink-0">{item.symbol.split(":")[1]}</strong><span className={`text-[10px] truncate ${item.availability === "confirmed" ? "text-accent" : "text-ink-3"}`}>{item.availability === "confirmed" ? "In my Kraken" : item.availability === "unavailable" ? "Hidden" : "Check Buy list"}</span></div>
            <div className="text-xs text-ink-3 truncate">{item.name} · {item.symbol.split(":")[0]}</div>
          </button>)}
          {!visible.length && <p className="text-xs text-ink-2 py-5">{confirmedOnly ? "Mark a stock available in your Kraken app, or turn off this filter to research more." : "No matching stocks. Add a ticker below."}</p>}
        </div>
        {hidden > 0 && <button className="control-button w-full mt-3" aria-pressed={showHidden} onClick={() => setShowHidden(v => !v)}>{showHidden ? "Hide unavailable" : `Show hidden (${hidden})`}</button>}
        <form className="border-t border-line-strong mt-4 pt-3 grid gap-2" onSubmit={addStock}>
          <h3 className="font-medium text-sm">Add a stock or ETF</h3>
          <label className="text-xs text-ink-2">Listing exchange<select className="field !py-2 mt-1" value={exchange} onChange={e => setExchange(e.target.value as StockExchange)}><option>NASDAQ</option><option>NYSE</option><option value="AMEX">NYSE Arca / American</option></select></label>
          <label className="text-xs text-ink-2">Ticker<input className="field !py-2 mt-1 uppercase" value={ticker} maxLength={24} onChange={e => setTicker(e.target.value)} placeholder="e.g. AAPL" /></label>
          <button className="control-button" type="submit">Add to watchlist</button>
          {addError && <p className="text-xs text-warn" role="alert">{addError}</p>}
        </form>
        <p className="text-[11px] text-ink-3 mt-3">Saved locally in this browser. Export your notebook for a backup.</p>
        </div>
      </aside>
      <main className="min-w-0 grid gap-3">
        {selected ? <>
          <section className="panel rounded-lg p-3 sm:p-4" aria-label="Selected stock availability">
            <div className="flex flex-wrap justify-between items-center gap-3"><div><div className="eyebrow">Selected stock</div><h2 className="text-xl font-semibold">{selected.symbol.split(":")[1]} <span className="font-normal text-sm text-ink-2">{selected.name}</span></h2><p className="text-xs text-ink-3">{selected.symbol}</p></div>
              <div className="flex flex-wrap gap-2"><button className="control-button" onClick={() => availability("unavailable")}>Hide unavailable stock</button><button className="control-button" onClick={removeSelected}>Remove from watchlist</button></div>
            </div>
            <label className="flex gap-2 items-center text-sm mt-3"><input type="checkbox" checked={selected.availability === "confirmed"} onChange={e => availability(e.target.checked ? "confirmed" : "unconfirmed")} />I can buy {selected.symbol.split(":")[1]} in my Kraken app</label>
            {selected.availability !== "confirmed" && <p className="text-xs text-warn mt-2">Availability is unconfirmed for your account. Check the Stocks Buy list in Kraken before planning an entry.</p>}
          </section>
          <div className="grid grid-cols-1 2xl:grid-cols-[minmax(0,1.35fr)_minmax(380px,1fr)] gap-3 items-start min-w-0">
            <StockMarketWidgets symbol={selected.symbol} />
            <StockTradeDesk key={`${selected.symbol}:${notebookRevision}`} symbol={selected.symbol} confirmed={selected.availability === "confirmed"} plans={state.plans} journal={state.journal}
              onSavePlan={plan => setState(old => ({ ...old, plans: [plan, ...old.plans].slice(0, 100) }))}
              onDeletePlan={id => setState(old => ({ ...old, plans: old.plans.filter(p => p.id !== id) }))}
              onAddTrade={trade => setState(old => ({ ...old, journal: [trade, ...old.journal].slice(0, 500) }))}
              onDeleteTrade={id => setState(old => ({ ...old, journal: old.journal.filter(t => t.id !== id) }))} />
          </div>
        </> : <section className="panel rounded-lg p-8 text-center"><h2 className="text-lg font-semibold mb-2">Choose a stock to open your desk</h2><p className="text-ink-2">Add a ticker or restore one from your hidden list. Your saved plans and journal remain in the exported notebook.</p></section>}
      </main>
    </div>
  </div>;
}
