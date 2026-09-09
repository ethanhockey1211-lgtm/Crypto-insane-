"use client";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { API_BASE } from "@/lib/api";
import { scanStockSetups, stockSetupIsCurrent, type StockScanResponse, type StockScannerStatus, type StockSetup } from "@/lib/stock-scanner";
import type { StockItem, StockPlan } from "@/lib/stocks";
import { StockEntryAlertTracker } from "@/lib/stock-entry-alerts";
import { stockFeedError } from "@/lib/stock-feed-error";
import { buildStockOpportunities } from "@/lib/stock-opportunities";
import { StockOpportunityBoard } from "@/components/StockOpportunityBoard";
import type { StockRiskDraft } from "@/components/useStockRiskSettings";

export interface ScannerPlan {
  id: string; symbol: string; setup: StockPlan["setup"]; entry: number; stop: number; target: number; asOf: string; notes?: string;
}
const ACCESS_KEY = "kraken.stock-scanner.access.v1";
const ALERT_KEY = "kraken.stock-scanner.alerts.v1";
const COOLDOWN_KEY = "kraken.stock-scanner.cooldowns.v1";
const sessionTracker = new StockEntryAlertTracker();
const money = (n: number | null) => n == null ? "—" : n.toLocaleString("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 3 });
const current = stockSetupIsCurrent;

function useStockFeed(symbols: string, access: string, paused: boolean, refresh: number) {
  const [status, setStatus] = useState<StockScannerStatus | null>(null);
  const [data, setData] = useState<StockScanResponse | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const priorRefresh = useRef(refresh);
  useEffect(() => {
    let alive = true, busy = false;
    const controller = new AbortController();
    const force = priorRefresh.current !== refresh;
    priorRefresh.current = refresh;
    if (!access) setData(null);
    async function poll(fetchData: boolean) {
      if (busy) return;
      busy = true;
      const request = new AbortController();
      const abort = () => request.abort();
      controller.signal.addEventListener("abort", abort);
      // Allow the server's 20-second provider timeout to return its diagnostic.
      const timeout = setTimeout(abort, 30_000);
      if (alive) setLoading(true);
      try {
        const state = await fetch(`${API_BASE}/api/stocks/status`, { cache: "no-store", signal: request.signal });
        if (!state.ok) throw new Error("The stock scanner service is unavailable. Try again after deployment finishes.");
        const nextStatus = await state.json() as StockScannerStatus;
        if (typeof nextStatus.configured !== "boolean") throw new Error("The stock scanner returned an unexpected status.");
        if (!alive) return;
        setStatus(nextStatus);
        if (!nextStatus.configured || !access || !symbols || !fetchData) { setError(""); return; }
        const res = await fetch(`${API_BASE}/api/stocks/scan?symbols=${encodeURIComponent(symbols)}`, { cache: "no-store", headers: { "X-Stock-Access-Token": access }, signal: request.signal });
        if (!res.ok) throw new Error(stockFeedError(res.status, await res.json().catch(() => null)));
        const next = await res.json() as StockScanResponse;
        if (next.status !== "ready" || next.provider !== "Alpaca" || next.feed !== "iex" || !Array.isArray(next.rows) || !Number.isFinite(Date.parse(next.asOf))) throw new Error("A complete IEX scan is unavailable. Entry prompts are disabled until a fresh scan succeeds.");
        if (alive) { setData(next); setError(""); }
      } catch (failure) {
        if (alive && !controller.signal.aborted) setError(failure instanceof Error && failure.name !== "AbortError" ? failure.message : "Stock data timed out. Entry prompts are disabled until the next successful scan.");
      } finally {
        clearTimeout(timeout); controller.signal.removeEventListener("abort", abort); busy = false;
        if (alive) setLoading(false);
      }
    }
    void poll(!paused || force);
    const timer = paused ? null : setInterval(() => void poll(true), 30_000);
    return () => { alive = false; controller.abort(); if (timer) clearInterval(timer); };
  }, [symbols, access, paused, refresh]);
  return { status, data, error, loading };
}

export function StockEntryScanner({ watchlist, onOpen, onUsePlan, onConfirm, risk, onRiskChange }: {
  watchlist: StockItem[]; onOpen: (symbol: string) => void; onUsePlan: (plan: ScannerPlan) => void; onConfirm: (symbol: string) => void;
  risk: StockRiskDraft; onRiskChange: React.Dispatch<React.SetStateAction<StockRiskDraft>>;
}) {
  const [access, setAccess] = useState("");
  const [draftAccess, setDraftAccess] = useState("");
  const [paused, setPaused] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [now, setNow] = useState(0);
  const [alerts, setAlerts] = useState(false);
  const [sound, setSound] = useState(false);
  const [notice, setNotice] = useState("");
  const [events, setEvents] = useState<{ id: string; symbol: string; at: number; entry: number }[]>([]);
  const tracker = useRef(sessionTracker);
  const observedEntries = useRef(new Set<string>());
  const audio = useRef<AudioContext | null>(null);
  useEffect(() => {
    tracker.current.resetBaseline();
    try {
      setAccess(sessionStorage.getItem(ACCESS_KEY) ?? ""); setAlerts(localStorage.getItem(ALERT_KEY) === "true");
      const saved = sessionStorage.getItem(COOLDOWN_KEY);
      if (saved) tracker.current.restoreCooldowns(saved, Date.now());
    } catch { /* storage is optional; feed can use memory */ }
    setNow(Date.now()); const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => { clearInterval(timer); tracker.current.resetBaseline(); void audio.current?.close(); };
  }, []);
  const candidates = useMemo(() => watchlist.filter(s => s.availability !== "unavailable").sort((a, b) => Number(b.availability === "confirmed") - Number(a.availability === "confirmed")).slice(0, 40), [watchlist]);
  const symbols = useMemo(() => [...new Set(candidates.map(s => s.symbol.split(":")[1]))].sort().join(","), [candidates]);
  const feed = useStockFeed(symbols, access, paused, refresh);
  const setups = useMemo(() => feed.data ? scanStockSetups(feed.data, candidates, Date.now()) : [], [feed.data, candidates]);
  const enabled = !paused && !feed.error && feed.status?.configured === true && !!access;
  const actionable = useMemo(() => setups.filter(s => enabled && current(s, feed.data, now)), [setups, enabled, feed.data, now]);
  const opportunitySettings = useMemo(() => ({ account: Number(risk.account), cash: Number(risk.cash), riskPct: Number(risk.riskPct), costPerShare: risk.cost.trim() === "" ? NaN : Number(risk.cost) }), [risk]);
  const opportunities = useMemo(() => buildStockOpportunities(setups, feed.data, Date.now(), opportunitySettings), [setups, feed.data, opportunitySettings]);
  const availabilitySignature = candidates.map(s => `${s.symbol}:${s.availability}`).join(",");
  useEffect(() => { tracker.current.resetBaseline(); observedEntries.current.clear(); }, [availabilitySignature]);
  useEffect(() => {
    const at = Date.now();
    if (!alerts || !enabled || !feed.data || at - Date.parse(feed.data.asOf) > 60_000) { tracker.current.update([], false, at); observedEntries.current.clear(); return; }
    // Only a new valid market observation can establish an exit/re-entry. A stale or missing
    // quote is unknown, so it must not manufacture a second visit when the feed recovers.
    for (const setup of setups) {
      if (current(setup, feed.data, at)) observedEntries.current.add(setup.symbol);
      else if (setup.state === "watch" || setup.state === "extended") observedEntries.current.delete(setup.symbol);
    }
    const newlyEntered = new Set(tracker.current.update([...observedEntries.current], true, at));
    const fresh = setups.filter(s => newlyEntered.has(s.symbol) && current(s, feed.data, at));
    if (!fresh.length) return;
    try { sessionStorage.setItem(COOLDOWN_KEY, tracker.current.serializeCooldowns()); } catch { /* in-memory cooldown survives workspace switches */ }
    setEvents(old => [...fresh.map(s => ({ id: `${s.symbol}:${at}`, symbol: s.symbol, at, entry: s.entry! })), ...old].slice(0, 5));
    if (sound && audio.current?.state === "running") {
      const context = audio.current, oscillator = context.createOscillator(), gain = context.createGain();
      oscillator.connect(gain); gain.connect(context.destination); oscillator.frequency.value = 740;
      gain.gain.setValueAtTime(0.05, context.currentTime); gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + 0.25);
      oscillator.start(); oscillator.stop(context.currentTime + 0.27);
    }
  }, [setups, feed.data, alerts, enabled, sound]);
  const connect = (e: React.FormEvent) => {
    e.preventDefault(); const code = draftAccess.trim(); if (code.length < 24) { setNotice("Use the separate personal access code, at least 24 characters, from Render. Do not enter Alpaca API keys here."); return; }
    setAccess(code); setDraftAccess(""); setNotice("");
    try { sessionStorage.setItem(ACCESS_KEY, code); } catch { setNotice("Access is held in memory for this visit because browser storage is unavailable."); }
  };
  const disconnect = () => { setAccess(""); setEvents([]); tracker.current.resetBaseline(); observedEntries.current.clear(); try { sessionStorage.removeItem(ACCESS_KEY); } catch { /* optional */ } };
  const toggleAlerts = () => setAlerts(old => { const next = !old; try { localStorage.setItem(ALERT_KEY, String(next)); } catch { /* optional */ } return next; });
  const toggleSound = async () => {
    if (sound) { setSound(false); return; }
    try { audio.current ??= new AudioContext(); await audio.current.resume(); setSound(true); setNotice("Stock alert sound is armed for this visit."); }
    catch { setNotice("Sound is unavailable. In-page stock alerts still work."); }
  };
  const usePlan = useCallback((setup: StockSetup, notes: string) => {
    if (!enabled || !current(setup, feed.data, Date.now()) || !setup.setup || setup.entry == null || setup.stop == null || setup.target == null) return;
    if (!buildStockOpportunities([setup], feed.data, Date.now(), opportunitySettings)[0]?.eligible) return;
    onUsePlan({ id: crypto.randomUUID(), symbol: setup.symbol, setup: setup.setup, entry: setup.entryMax ?? setup.entry, stop: setup.stop, target: setup.target, asOf: feed.data!.asOf, notes });
  }, [enabled, feed.data, onUsePlan, opportunitySettings]);
  return <section className="panel rounded-lg p-3 sm:p-4 mb-3" aria-label="Automatic stock setups">
    <div className="flex flex-wrap justify-between items-start gap-3">
      <div><div className="eyebrow !text-accent">Automatic stock scanner · Alpaca IEX</div><h2 className="text-xl sm:text-2xl font-semibold mt-1">Stocks to review now</h2><p className="text-sm text-ink-2 mt-1">Find a setup, understand the evidence, and compare its entry plan with your budget.</p></div>
      <span className={`tag ${actionable.length ? "text-accent" : "text-ink-2"}`}>{paused ? "Paused · no stock alerts" : !access ? "Connect personal data" : feed.error ? "Data unavailable" : `${actionable.length} in entry zone`}</span>
    </div>
    <p className="text-xs text-ink-3 mt-3">Live IEX is one US exchange. Its quotes, volume and estimated VWAP differ from Kraken and the consolidated market. Rankings are rule scores, not win probabilities. Confirm the current Kraken quote before an order.</p>
    {feed.status?.configured === false && <div className="border border-accent/30 bg-accent/5 rounded-md p-3 my-3">
      <h3 className="font-semibold">One-time data setup needed</h3><p className="text-sm text-ink-2 my-2">The automatic scanner needs your free Alpaca data credentials. In your Render service&apos;s Environment settings, add these three values and redeploy:</p>
      <ul className="list-disc pl-5 text-sm space-y-1"><li><code>Stocks__ApiKey</code> — Alpaca key ID</li><li><code>Stocks__ApiSecret</code> — Alpaca secret key</li><li><code>Stocks__AccessToken</code> — a separate random personal code of at least 24 characters</li></ul>
      <p className="text-xs text-ink-2 mt-3">Keep the Alpaca keys in Render. Enter only your separate access code below. <a className="text-accent underline" href="https://alpaca.markets/data" target="_blank" rel="noopener noreferrer">Get free Alpaca data ↗</a></p>
    </div>}
    {!access ? <form onSubmit={connect} className="flex flex-wrap gap-2 items-end my-3"><label className="grid gap-1 text-xs text-ink-2 grow max-w-md">Personal scanner access code<input type="password" className="field !py-2" autoComplete="off" value={draftAccess} onChange={e => setDraftAccess(e.target.value)} placeholder="Your Stocks__AccessToken, not an Alpaca API key" /></label><button type="submit" className="control-button">Connect scanner</button></form>
      : <div className="flex flex-wrap gap-2 mt-3"><button className="control-button" onClick={() => setPaused(v => !v)}>{paused ? "Resume stock scanner" : "Pause stock scanner"}</button><button className="control-button disabled:opacity-40" disabled={feed.loading} onClick={() => setRefresh(v => v + 1)}>Refresh stock scan</button><button className="control-button" aria-pressed={alerts} onClick={toggleAlerts}>{alerts ? "Stock alerts on" : "Enable stock alerts"}</button><button className="control-button" aria-pressed={sound} onClick={() => void toggleSound()}>{sound ? "Stock sound on" : "Stock sound off"}</button><button className="control-button ml-auto" onClick={disconnect}>Disconnect data</button></div>}
    {notice && <p role="status" className="text-sm text-accent mt-2">{notice}</p>}
    {feed.error && <p role="alert" className="text-warn text-sm mt-3">{feed.error} Existing cards are reference only.</p>}
    <div className="flex flex-wrap justify-between gap-2 text-xs text-ink-3 mt-3 mb-3"><span>{candidates.length} watchlist stocks · confirmed stocks scanned first · 40 maximum</span><span>{feed.loading ? "Checking stock data…" : feed.data ? `Data captured ${new Date(feed.data.asOf).toLocaleTimeString()} · scans every 30 seconds` : "Waiting for a connected feed"}</span></div>
    {!!events.length && alerts && <details className="border border-line-strong rounded-md p-3 mb-3"><summary className="cursor-pointer text-sm">Stock entry alerts · {events[0].symbol.split(":")[1]} at {new Date(events[0].at).toLocaleTimeString()} · inspect current status</summary><p className="text-xs text-ink-3 my-2">Historical entry-zone events, not current instructions. Maximum one per stock every 10 minutes while this tab is open.</p>{events.map(event => <button key={event.id} className="block text-sm text-accent py-1" onClick={() => onOpen(event.symbol)}>{event.symbol} · entry reference {money(event.entry)} · {new Date(event.at).toLocaleTimeString()}</button>)}</details>}
    {!setups.length ? <div className="border border-line rounded-md p-5 text-sm text-ink-2">{!candidates.length ? "Add or restore stocks in your research watchlist to scan them." : feed.status?.configured && access ? "The first scan will appear here when stock data is available. Setups need at least 21 completed regular-session minute bars." : "Connect the private data feed to receive ranked stock setups. Your free charts and notebook remain available below."}</div>
      : <StockOpportunityBoard opportunities={opportunities} response={feed.data} enabled={enabled} now={now} risk={risk} onRiskChange={onRiskChange} watchlist={watchlist} onOpen={onOpen} onUsePlan={usePlan} onConfirm={onConfirm} />}
    <p className="text-[11px] text-ink-3 mt-3">Cards keep their order between scans. Pausing stops stock polling and alerts. Price age checks continue, so an old entry cannot stay actionable.</p>
  </section>;
}
