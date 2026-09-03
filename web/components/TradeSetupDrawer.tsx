"use client";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { useRow } from "@/lib/store";
import { fmtAge, fmtPct, fmtPrice, fmtVolume, fmtX, setupLabel } from "@/lib/format";
import type { Opportunity } from "@/lib/types";
import { PriceChart } from "./PriceChart";
import { MomentumPanel } from "./MomentumPanel";
import { PositionCalculator } from "./PositionCalculator";

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="px-4 py-3 border-b border-line">
      <h3 className="eyebrow mb-2">{title}</h3>
      {children}
    </section>
  );
}

function Stat({ k, v, cls }: { k: string; v: React.ReactNode; cls?: string }) {
  return (<div><div className="eyebrow">{k}</div><div className={`num text-[12.5px] ${cls ?? ""}`}>{v}</div></div>);
}

export function TradeSetupDrawer({ symbol, onClose, watched, onWatch }: { symbol: string; onClose: () => void; watched: boolean; onWatch: (s: string) => void }) {
  const row = useRow(symbol);
  const [opp, setOpp] = useState<Opportunity | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setOpp(null); setError(null);
    const load = async () => {
      try { const o = await api.opportunity(symbol); if (alive) { setOpp(o); setError(null); } }
      catch (e) { if (alive) setError((e as Error).message); }
    };
    void load();
    const id = setInterval(load, 2000);
    return () => { alive = false; clearInterval(id); };
  }, [symbol]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const price = row?.price ?? opp?.price ?? 0;
  const plan = opp?.plan ?? null;
  const m = opp?.metrics;
  const scoreClass = (opp?.score ?? 0) >= 80 ? "text-ink" : (opp?.score ?? 0) >= 60 ? "text-ink-2" : "text-ink-3";

  return (
    <aside className="panel flex flex-col h-full min-h-0" aria-label={`${symbol} setup`}>
      <div className="flex items-center gap-3 px-4 h-12 border-b border-line">
        <span className="text-[16px] font-semibold">{symbol.replace("-USD", "")}<span className="text-ink-3 font-normal">/USD</span></span>
        <span className="num text-[16px]">{fmtPrice(price)}</span>
        {opp && <span className={`num text-[20px] font-medium ${scoreClass}`}>{opp.score.toFixed(0)}</span>}
        {opp && <span className="text-[12px]">{opp.setup.type === "None" ? <span className="text-ink-3">no setup</span> : setupLabel(opp.setup.type)} <span className="text-ink-3">· {opp.setup.confidence} confidence</span></span>}
        {opp?.overextension.doNotChase && <span className="tag tag-warn">do not chase</span>}
        {opp?.quality.stale && <span className="tag text-warn border-warn/40">stale {fmtAge(opp.quality.ageMs)}</span>}
        <button onClick={() => onWatch(symbol)} className="ml-auto text-[11px] px-2 py-0.5 rounded-[3px] bg-navy-3 hover:bg-navy-2">{watched ? "Watching" : "Watch"}</button>
        <button onClick={onClose} className="text-ink-3 hover:text-ink text-[16px] px-1" aria-label="Close setup">×</button>
      </div>
      <div className="overflow-auto min-h-0 flex-1">
        <div className="h-[300px] border-b border-line">
          <PriceChart symbol={symbol} levels={{ plan, keyLevel: opp?.setup.keyLevel ?? null, vwap: m?.vwap ?? null }} />
        </div>
        {error && <div className="px-4 py-3 warn text-[12px]">Setup detail unavailable: {error}</div>}
        {opp && (
          <>
            <Section title="Plan">
              {plan ? (
                <>
                  <div className="grid grid-cols-4 gap-x-4 gap-y-2">
                    <Stat k="Entry zone" v={`${fmtPrice(plan.entryLow)} – ${fmtPrice(plan.entryHigh)}`} />
                    <Stat k="Invalidation" v={fmtPrice(plan.invalidation)} cls="down" />
                    <Stat k="Stop reference" v={fmtPrice(plan.stop)} cls="down" />
                    <Stat k="Risk / unit" v={fmtPrice(plan.riskPerUnit)} />
                    <Stat k="Target 1" v={<>{fmtPrice(plan.target1)} <span className="text-ink-3">{plan.rewardRatio1.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Target 2" v={<>{fmtPrice(plan.target2)} <span className="text-ink-3">{plan.rewardRatio2.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Target 3" v={<>{fmtPrice(plan.target3)} <span className="text-ink-3">{plan.rewardRatio3.toFixed(1)}R</span></>} cls="up" />
                    <Stat k="Confirmation" v={<span className="font-sans text-ink-2 text-[11.5px]">{plan.trigger}</span>} />
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
              <div className="grid grid-cols-4 gap-x-4 gap-y-2">
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
              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-[11.5px]">
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
              <PositionCalculator plan={plan} price={price} symbol={symbol} />
            </Section>
          </>
        )}
        {!opp && !error && <div className="px-4 py-6 text-ink-3 text-[12px]">Loading setup…</div>}
      </div>
    </aside>
  );
}
