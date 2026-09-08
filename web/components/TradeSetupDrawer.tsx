"use client";
import { useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { useFeed, useHub } from "@/lib/store";
import { useDisplay } from "@/lib/display";
import { scannerIsFresh } from "@/lib/decision";
import { fmtAge, fmtPct, fmtPrice, fmtVolume, fmtX, setupLabel } from "@/lib/format";
import type { Explanation, Opportunity } from "@/lib/types";
import { PriceChart } from "./PriceChart";
import { MomentumPanel } from "./MomentumPanel";
import { PositionCalculator } from "./PositionCalculator";
import { hiddenMarkets, useHiddenMarkets } from "@/lib/hidden-markets";

const ENTRY_STATE: Record<string, string> = { Watch: "Watch: below the zone, wait for the trigger", InZone: "In the entry zone", Late: "Late: above the zone, reward shrinking", Chase: "Do not chase: reward to T1 is gone" };

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="px-3 sm:px-4 py-3 border-b border-line">
      <h3 className="eyebrow mb-2">{title}</h3>
      {children}
    </section>
  );
}

function Stat({ k, v, cls }: { k: string; v: React.ReactNode; cls?: string }) {
  return (<div><div className="eyebrow">{k}</div><div className={`num text-[12.5px] ${cls ?? ""}`}>{v}</div></div>);
}

export function TradeSetupDrawer({ symbol, onClose, watched, onWatch }: { symbol: string; onClose: () => void; watched: boolean; onWatch: (s: string) => void }) {
  const display = useDisplay();
  const displayState = useRef({ paused: display.paused, revision: display.revision });
  displayState.current = { paused: display.paused, revision: display.revision };
  const row = display.rows.get(symbol) ?? null;
  const visibility = useHiddenMarkets();
  const hidden = visibility.symbols.includes(symbol);
  const summary = display.symbols.get(symbol) ?? null;
  const cycle = display.cycle;
  const feed = useFeed();
  const hub = useHub();
  const [, refreshClock] = useState(0);
  useEffect(() => { const id = setInterval(() => refreshClock(value => value + 1), 1000); return () => clearInterval(id); }, []);
  const now = Date.now();
  const [detail, setDetail] = useState<{ symbol: string; value: Opportunity; receivedAt: number } | null>(null);
  const opp = detail?.symbol === symbol ? detail.value : null;
  const [failure, setFailure] = useState<{ symbol: string; message: string } | null>(null);
  const error = failure?.symbol === symbol ? failure.message : null;
  const pendingDetail = useRef<{ symbol: string; request: Promise<Opportunity> } | null>(null);
  const [priority, setPriority] = useState(false);
  const [ai, setAi] = useState<{ loading: boolean; result: Explanation | null; error: string | null }>({ loading: false, result: null, error: null });
  useEffect(() => { setAi({ loading: false, result: null, error: null }); }, [symbol]);
  const explain = async () => {
    setAi({ loading: true, result: null, error: null });
    try { setAi({ loading: false, result: await api.explain(symbol), error: null }); }
    catch (e) { setAi({ loading: false, result: null, error: (e as Error).message }); }
  };

  useEffect(() => {
    let alive = true;
    setPriority(false);
    void api.prepare(symbol).then(result => { if (alive) setPriority(result.prioritized); }).catch(() => { /* normal history queue remains active */ });
    return () => { alive = false; };
  }, [symbol]);

  // One detail fetch on open and each display capture. Pausing the display also holds this plan.
  useEffect(() => {
    let alive = true;
    const requestedRevision = display.revision;
    const requestedWhilePaused = displayState.current.paused;
    const canPublish = () => alive && displayState.current.revision === requestedRevision &&
      (!displayState.current.paused || requestedWhilePaused);
    const pending = pendingDetail.current?.symbol === symbol ? pendingDetail.current
      : { symbol, request: api.opportunity(symbol) };
    pendingDetail.current = pending;
    void pending.request.then(value => {
      if (canPublish()) { setDetail({ symbol, value, receivedAt: Date.now() }); setFailure(null); }
    }).catch((e: unknown) => {
      if (canPublish()) setFailure({ symbol, message: e instanceof Error ? e.message : String(e) });
    }).finally(() => { if (pendingDetail.current === pending) pendingDetail.current = null; });
    return () => { alive = false; };
  }, [symbol, display.revision]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const price = row?.price ?? summary?.quote?.price ?? opp?.price;
  const plan = opp?.plan ?? null;
  const m = opp?.metrics;
  const current = !display.paused && !error && opp != null && detail != null && now - detail.receivedAt <= 10000 &&
    hub === "connected" && feed?.live === true && scannerIsFresh(cycle.at, now) && scannerIsFresh(opp.at, now) && !opp.quality.stale;
  const scoreClass = (opp?.score ?? 0) >= 80 ? "text-ink" : (opp?.score ?? 0) >= 60 ? "text-ink-2" : "text-ink-3";

  return (
    <aside className="panel flex flex-col h-full min-h-0" aria-label={`${symbol} setup`}>
      <div className="relative z-10 shrink-0 min-h-12 flex flex-wrap items-center gap-x-3 gap-y-1 px-3 sm:px-4 py-2 border-b border-line bg-navy">
        <span className="text-[16px] font-semibold">{symbol.replace("-USD", "")}<span className="text-ink-3 font-normal">/USD</span></span>
        <span className="num text-[16px]">{fmtPrice(price)}</span>
        {opp && <span className={`num text-[20px] font-medium ${scoreClass}`}>{opp.score.toFixed(0)}</span>}
        {opp && <span className="text-[12px]">{opp.setup.type === "None" ? <span className="text-ink-3">no setup</span> : setupLabel(opp.setup.type)} <span className="text-ink-3">· {opp.setup.confidence} confidence</span></span>}
        {opp?.overextension.doNotChase && <span className="tag tag-warn">do not chase</span>}
        {opp?.quality.stale && <span className="tag text-warn border-warn/40">stale {fmtAge(opp.quality.ageMs)}</span>}
        <button onClick={() => onWatch(symbol)} className="ml-auto text-[11px] px-2 py-0.5 rounded-[3px] bg-navy-3 hover:bg-navy-2">{watched ? "Watching" : "Watch"}</button>
        <button type="button" className="text-[11px] px-2 py-1 rounded border border-line-strong text-ink-2 hover:text-ink hover:bg-navy-2" aria-label={hidden ? `Restore ${symbol.replace("-", "/")} to scanner` : `Hide ${symbol.replace("-", "/")} as unavailable`} title="Hide a coin you cannot buy in your Kraken account. Restore it from Hidden coins in the scanner." onClick={() => {
          if (hidden) hiddenMarkets.restore(symbol);
          else { hiddenMarkets.hide(symbol); onClose(); }
        }}>{hidden ? "Restore to scanner" : "Hide unavailable"}</button>
        <button onClick={onClose} className="text-ink-3 hover:text-ink text-[18px] px-2 py-0.5 -mr-1" aria-label="Close setup">×</button>
      </div>
      <div className="overflow-auto min-h-0 flex-1">
        {display.paused && <p role="status" className="px-4 py-3 text-[12px] text-warn border-b border-line bg-warn/5">Reading paused — prices and plans are held for reference. Refresh now to inspect a new snapshot, or resume display updates. Entry alerts continue using live data.</p>}
        {hidden && <p className="px-4 py-2 text-[12px] text-ink-2 border-b border-line">Hidden from market discovery and browser alerts. Existing paper positions and historical records remain available.</p>}
        <div className="h-[240px] sm:h-[300px] border-b border-line">
          <PriceChart symbol={symbol} levels={{ plan, keyLevel: opp?.setup.keyLevel ?? null, vwap: m?.vwap ?? null }} />
        </div>
        {!opp && summary && <div className="m-3 rounded-lg border border-accent/25 bg-accent/5 p-4" role="status"><p className="text-[15px] font-medium">Preparing {symbol.replace("-", "/")} analysis</p><p className="mt-2 text-ink-2">{priority ? "This market has moved to the front of the history queue." : "History and indicators are loading. The plan will appear here when ready."} Quotes appear as Kraken provides them.</p><p className="mt-2 text-[12px] text-ink-3">You can keep browsing; loading continues in the background.</p></div>}
        {error && (opp || !summary || !error.endsWith("404")) && <div className="px-4 py-3 warn text-[12px]">Setup detail unavailable: {error}</div>}
        {opp && (
          <>
            <Section title="Execution quality · not a price prediction">
              {!current && <p role="status" className="text-[14px] warn">{display.paused ? "READING SNAPSHOT — this plan is for reference only while display updates are paused." : "DATA INTERRUPTED — the plan below is for reference only. Wait for fresh updates before considering an entry."}</p>}
              {(current || display.paused) && opp.execution ? <>
                <p className={`text-[13px] ${opp.execution.status === "Blocked" ? "warn" : "text-ink-2"}`}>
                  {display.paused ? "At the shown assessment: " : ""}{opp.execution.status === "Blocked" ? "NO TRADE — execution checks failed" : "WATCH — confirm the trigger"}
                </p>
                <div className="grid grid-cols-2 gap-3 my-2">
                  <Stat k="Net R:R to T1 at assessed price" v={opp.execution.netRewardRatio == null ? "—" : `${opp.execution.netRewardRatio.toFixed(2)}R`} />
                  <Stat k="Required break-even win rate" v={fmtPct(opp.execution.breakEvenWinRate)} />
                  <Stat k="Maximum entry after costs" v={fmtPrice(opp.execution.maxEntryPriceAfterCosts)} cls="warn" />
                  <Stat k="Fee assumption · each side" v={opp.execution.feeBps != null ? `${opp.execution.feeBps} bps` : "Unavailable"} />
                  <Stat k="Slippage assumption · each side" v={opp.execution.slippageBps != null ? `${opp.execution.slippageBps} bps` : "Unavailable"} />
                </div>
                <ul className="text-[12px] space-y-1">{opp.execution.reasons.map((reason, i) => <li key={i}>{reason}</li>)}</ul>
                <p className="text-[11px] text-ink-3 mt-2">Assessed at {fmtPrice(opp.price)}. Includes configured fees, slippage and spread on entry and exit. Break-even is a required success rate, not a predicted probability. Actual fills and stop losses can be worse.</p>
                {opp.quality.provider === "kraken" && <p className="text-[12px] text-ink-2 mt-2">Kraken+ fee waiver assumed within your allowance. App quote spreads can differ from this market feed; verify the final quote. <a href="https://support.kraken.com/articles/kraken-faq-subscription-service-overview" target="_blank" rel="noreferrer" className="underline underline-offset-2">Kraken+ terms</a></p>}
              </> : <p className="text-[12px] warn">Execution assessment unavailable. Score is not a buy signal.</p>}
            </Section>
            <Section title="Plan">
              {plan ? (
                <>
                  <div className="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-2">
                    <Stat k="Entry zone" v={`${fmtPrice(plan.entryLow)} – ${fmtPrice(plan.entryHigh)}`} />
                    <Stat k="Invalidation" v={fmtPrice(plan.invalidation)} cls="down" />
                    <Stat k="Stop reference" v={fmtPrice(plan.stop)} cls="down" />
                    <Stat k="Risk / unit" v={fmtPrice(plan.riskPerUnit)} />
                    <Stat k="Target 1" v={<>{fmtPrice(plan.target1)} <span className="text-ink-3">{plan.rewardRatio1.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Target 2" v={<>{fmtPrice(plan.target2)} <span className="text-ink-3">{plan.rewardRatio2.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Target 3" v={<>{fmtPrice(plan.target3)} <span className="text-ink-3">{plan.rewardRatio3.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Confirmation" v={<span className="font-sans text-ink-2 text-[11.5px]">{plan.trigger}</span>} />
                    <Stat k="Do not chase above" v={fmtPrice(plan.chaseCeiling)} cls="warn" />
                    <Stat k="Entry state" v={<span className={`font-sans ${plan.entryState === "Chase" ? "warn" : plan.entryState === "InZone" ? "up" : plan.entryState === "Late" ? "warn" : "text-ink-2"}`}>{ENTRY_STATE[plan.entryState] ?? plan.entryState}</span>} />
                  </div>
                  {plan.basis.length > 0 && <p className="text-[11px] text-ink-3 mt-2">{plan.basis.join(" · ")}</p>}
                </>
              ) : <p className="text-ink-3 text-[12px]">No plan: nothing actionable is set up on this symbol right now.</p>}
            </Section>
            <Section title="Why this is ranked here">
              <ul className="space-y-1 text-[12px]">
                {opp.why.map((w, i) => <li key={i} className="flex gap-2"><span className="text-accent">›</span><span>{w}</span></li>)}
                {opp.why.length === 0 && <li className="text-ink-3">No supporting evidence: the score is mostly baseline liquidity and market context.</li>}
              </ul>
              {opp.change && (
                <p className="text-[11px] text-ink-2 mt-2">Score {opp.change.previous.toFixed(0)} → {opp.change.current.toFixed(0)}: {opp.change.reasons.join("; ") || "small drift across components"}</p>
              )}
            </Section>
            <Section title="What invalidates this">
              <p className="text-[12px]">{opp.invalidation}</p>
            </Section>
            <Section title="Analyst narrative">
              {!ai.result && (
                <div className="flex items-center gap-3">
                  <button className="text-[11px] px-2 py-1 bg-navy-3 rounded-[3px] disabled:opacity-50" disabled={ai.loading} onClick={explain}>{ai.loading ? "Writing…" : "Explain with AI"}</button>
                  <span className="text-[11px] text-ink-3">The model only narrates the numbers above; it computes nothing.</span>
                </div>
              )}
              {ai.error && <p className="warn text-[11.5px] mt-1">{ai.error}</p>}
              {ai.result && (
                <div className="text-[12px] space-y-2">
                  <p>{ai.result.summary}</p>
                  {ai.result.why.length > 0 && <ul className="space-y-0.5">{ai.result.why.map((w, i) => <li key={i} className="flex gap-2"><span className="text-accent">›</span><span>{w}</span></li>)}</ul>}
                  {ai.result.invalidation.length > 0 && <p className="text-ink-2"><span className="eyebrow mr-2">Invalidation</span>{ai.result.invalidation.join(" · ")}</p>}
                  {ai.result.risks.length > 0 && <p className="text-ink-2"><span className="eyebrow mr-2">Risks</span>{ai.result.risks.join(" · ")}</p>}
                  {ai.result.appearsExtended != null && <p className={ai.result.appearsExtended ? "warn" : "text-ink-2"}>{ai.result.appearsExtended ? "The model reads this move as already extended." : "The model does not read this move as extended."}</p>}
                  <p className="text-[10.5px] text-ink-3">{ai.result.model} · {ai.result.disclaimer} <button className="underline" onClick={explain}>refresh</button></p>
                </div>
              )}
            </Section>
            {opp.risks.length > 0 && (
              <Section title="Risks">
                <ul className="space-y-1 text-[12px]">
                  {opp.risks.map((r, i) => <li key={i} className={r.startsWith("DO NOT CHASE") ? "warn" : "text-ink-2"}>{r}</li>)}
                </ul>
              </Section>
            )}
            <Section title="Momentum">
              <MomentumPanel m={opp.metrics} />
            </Section>
            <Section title="Levels and context">
              <div className="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-2">
                <Stat k="ATR 5m" v={<>{fmtPrice(m?.atr5m)} <span className="text-ink-3">{fmtPct(m?.atrPct5m)}</span></>} />
                <Stat k="VWAP" v={<>{fmtPrice(m?.vwap)} <span className={m?.vwapDeviationPct != null && m.vwapDeviationPct > 0 ? "up" : "down"}>{fmtPct(m?.vwapDeviationPct)}</span>{m?.vwapSigma != null ? <span className="text-ink-3"> {m.vwapSigma.toFixed(1)}σ</span> : null}</>} />
                <Stat k="Relative volume" v={fmtX(m?.relVol5m)} />
                <Stat k="Spread" v={m?.spreadBps != null ? `${m.spreadBps.toFixed(1)} bps` : "—"} />
                <Stat k="Nearest resistance" v={fmtPrice(m?.nearestResistance)} />
                <Stat k="Nearest support" v={fmtPrice(m?.nearestSupport)} />
                <Stat k="BTC correlation" v={m?.btcCorrelation?.toFixed(2) ?? "—"} />
                <Stat k="24h volume" v={fmtVolume(m?.volume24hQuote)} />
                <Stat k="5m structure" v={<span className="font-sans">{m?.trend5m ?? "—"} · {m?.alignment5m ?? "—"}</span>} />
                <Stat k="15m structure" v={<span className="font-sans">{m?.trend15m ?? "—"} · {m?.alignment15m ?? "—"}</span>} />
                <Stat k="Extension" v={<span className={opp.overextension.doNotChase ? "warn" : ""}>{(opp.overextension.score * 100).toFixed(0)}/100</span>} />
                <Stat k="Data" v={<span className="font-sans text-[11px]">{opp.quality.exchange} via {opp.quality.provider} · {fmtAge(opp.quality.ageMs)}{opp.quality.historyLoaded ? "" : " · history loading"}</span>} />
              </div>
              {opp.setup.breakout && <p className="text-[11.5px] text-ink-2 mt-2">Breakout tracker: <span className="text-ink">{opp.setup.breakout.state}</span> at {fmtPrice(opp.setup.breakout.level.price)} — {opp.setup.breakout.narrative}</p>}
            </Section>
            <Section title="Score breakdown">
              <div className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1 text-[11.5px]">
                {opp.breakdown.components.map((c) => (
                  <div key={c.name} className="flex gap-2 items-start">
                    <span className="num w-14 shrink-0 text-right">{c.points.toFixed(0)}<span className="text-ink-3">/{c.max.toFixed(0)}</span></span>
                    <span><span className="text-ink">{c.name}</span> <span className="text-ink-3">{c.evidence}</span></span>
                  </div>
                ))}
                {opp.breakdown.penalties.map((c) => (
                  <div key={c.name} className="flex gap-2 items-start">
                    <span className="num w-14 shrink-0 text-right down">−{c.points.toFixed(0)}</span>
                    <span><span className="down">{c.name}</span> <span className="text-ink-3">{c.evidence}</span></span>
                  </div>
                ))}
              </div>
              <p className="text-[10.5px] text-ink-3 mt-2">Scoring config v{opp.breakdown.configVersion}. Scores rank evidence; they are not probabilities and nothing here is guaranteed.</p>
            </Section>
            <Section title="Position">
              <PositionCalculator key={symbol} plan={plan} price={price ?? opp.price} symbol={symbol} />
            </Section>
          </>
        )}
        {!opp && !error && <div className="px-4 py-6 text-ink-3 text-[12px]">Loading setup…</div>}
      </div>
    </aside>
  );
}
