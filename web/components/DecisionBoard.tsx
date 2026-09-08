"use client";
import { useEffect, useState } from "react";
import { store, useCycle, useFeed, useHub, useOrder } from "@/lib/store";
import { decision, decisionSummary, executionBlockers, scannerIsFresh } from "@/lib/decision";
import { fmtPrice, fmtR, setupLabel } from "@/lib/format";
import type { ScannerRow } from "@/lib/types";

type Lane = "ready" | "waiting" | "developing";

function SetupLane({ rows, lane, onOpen }: { rows: ScannerRow[]; lane: Lane; onOpen: (symbol: string) => void }) {
  const [expanded, setExpanded] = useState(false);
  if (!rows.length) return null;
  const blocked = lane === "developing";
  const title = lane === "ready" ? "In the entry zone · confirm the trigger" : lane === "waiting" ? "Waiting for price" : "Developing setups · not eligible";
  const visible = expanded ? rows : rows.slice(0, 3);
  return <div className="mt-5">
    <div className="flex flex-wrap items-baseline justify-between gap-2 mb-3">
      <h3 className="text-[16px] font-medium">{title} <span className="text-ink-3 num">{rows.length}</span></h3>
      {rows.length > 3 && <button onClick={() => setExpanded(value => !value)} aria-expanded={expanded} className="text-[13px] text-ink-2 hover:text-ink underline underline-offset-4">{expanded ? "Show top 3" : `Show all ${rows.length}`}</button>}
    </div>
    {blocked && <p className="text-[13px] text-ink-2 mb-3">Bullish plans ranked by evidence. Each still fails the checks listed below; levels are planning references until those checks pass.</p>}
    <div className="grid grid-cols-1 xl:grid-cols-3 gap-3">
      {visible.map(row => {
        const d = decision(row, true);
        const hasZone = row.entryLow != null && row.entryHigh != null && Number.isFinite(row.entryLow) && Number.isFinite(row.entryHigh);
        return <article key={row.symbol} className={`rounded border border-line-strong border-t-2 ${blocked ? "border-t-warn/60" : lane === "ready" ? "border-t-accent" : "border-t-line-strong"} bg-ground p-4 min-w-0`}>
          <div className="flex justify-between gap-2 items-baseline"><h4 className="text-[20px] font-semibold">{row.symbol.replace("-", "/")}</h4><span className="text-[12px] text-ink-2 whitespace-nowrap">Score {row.score.toFixed(0)}</span></div>
          <p className="text-[12px] text-ink-2 mt-1">{setupLabel(row.setup)} · {row.confidence} confidence</p>
          <p className={`text-[14px] mt-3 font-medium ${blocked ? "warn" : lane === "ready" ? "text-accent" : "text-ink-2"}`}>{blocked ? "Blocked — checks must pass first" : d.label}</p>
          {!blocked && <p className="text-[13px] text-ink-2 mt-2">{d.reason}</p>}
          <dl className="grid grid-cols-2 gap-3 text-[13px] my-4">
            <div className="col-span-2"><dt className="text-ink-2">{hasZone ? "Planned entry zone" : "Planned entry reference"}</dt><dd className="num text-[16px]">{hasZone ? `${fmtPrice(row.entryLow)} – ${fmtPrice(row.entryHigh)}` : fmtPrice(row.entry)}</dd></div>
            <div><dt className="text-ink-2">Assessed price</dt><dd className="num">{fmtPrice(row.assessedPrice)}</dd></div>
            <div><dt className="text-ink-2">Net reward / risk</dt><dd className="num">{fmtR(row.netRewardRatio)}</dd></div>
            <div><dt className="text-ink-2">Stop reference</dt><dd className="num text-down">{fmtPrice(row.stop)}</dd></div>
            <div><dt className="text-ink-2">First target</dt><dd className="num">{fmtPrice(row.target1)}</dd></div>
          </dl>
          {blocked ? <div className="mb-4">
            <p className="text-[12px] text-ink-3 mb-2">What needs to change</p>
            <ul className="space-y-2 text-[13px] text-ink-2 list-disc pl-4">{executionBlockers(row).map(reason => <li key={reason}>{reason}</li>)}</ul>
          </div> : row.trigger && <div className="text-[13px] text-ink-2 border-t border-line pt-3 mb-4"><span className="text-ink font-medium">Required trigger: </span>{row.trigger}</div>}
          <button className="w-full py-2.5 px-3 text-[14px] rounded bg-navy-3 hover:bg-navy-2 focus-visible:outline focus-visible:outline-accent" onClick={() => onOpen(row.symbol)}>{blocked ? "Inspect plan and blockers" : "Inspect entry and trigger"}</button>
        </article>;
      })}
    </div>
  </div>;
}

export function DecisionBoard({ onOpen }: { onOpen: (symbol: string) => void }) {
  const order = useOrder();
  const cycle = useCycle();
  const feed = useFeed();
  const hub = useHub();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const id = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(id); }, []);
  const live = hub === "connected" && feed?.live === true && scannerIsFresh(cycle.at, now);
  const rows = order.flatMap(symbol => { const row = store.getRow(symbol); return row ? [row] : []; });
  const summary = decisionSummary(rows, live);
  const coverage = feed?.universeSize ? `${rows.length} / ${feed.universeSize} pairs assessed` : `${rows.length} pairs assessed`;
  return <section className="p-4 sm:p-5 border-b border-line" aria-label="Trading decision shortlist">
    <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
      <div><h2 className="text-[20px] font-semibold">Your next decision</h2>
        <p className="text-[14px] text-ink-2 mt-1">{feed?.exchange ?? "Market"} · USD pairs · {live ? coverage : "Entries paused until market data and scanner updates are fresh."}</p></div>
      <span className={`text-[14px] rounded px-3 py-1 border ${live ? "border-line-strong text-ink-2" : "border-warn/50 warn"}`}>{live ? "Live scan" : "Data interrupted"}</span>
    </div>
    {!live ? <div className="rounded border border-line-strong bg-ground p-5" role="status">
      <h3 className="text-[18px]">Wait for fresh data</h3>
      <p className="text-[14px] text-ink-2 mt-2">Previous prices may still be visible in the full market table. Entry candidates resume after the feed reconnects and a fresh scanner cycle arrives.</p>
      {feed?.history && !feed.history.complete && <p className="text-[13px] text-ink-2 mt-2">History loaded for {feed.history.loaded} / {feed.history.total} pairs{feed.history.failed ? ` · ${feed.history.failed} failed` : ""}.</p>}
    </div> : <>
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 text-center">
        {[["In zone · confirm", summary.ready.length], ["Waiting for price", summary.waiting.length], ["Blocked", summary.blocked.length], ["Unavailable", summary.unavailable.length]].map(([label, count]) => <div key={label} className="rounded border border-line p-2"><p className="text-[12px] text-ink-2">{label}</p><p className="num text-[22px] mt-1">{count}</p></div>)}
      </div>
      {!summary.ready.length && <div className="rounded border border-line-strong bg-ground p-4 mt-4">
        <h3 className="text-[17px]">{rows.length ? "No eligible entry in the zone right now" : "The scanner is warming up"}</h3>
        <p className="text-[14px] text-ink-2 mt-2">{summary.waiting.length ? `${summary.waiting.length} plans pass execution checks and are waiting for price. Their zones and triggers are below.` : summary.developing.length ? "The developing plans below show the levels being tracked and every check still blocking entry." : rows.length ? "No current bullish plan passes the entry checks. Review the blockers below or inspect the full market table." : "Plans appear here as price history and analytics become available."}</p>
      </div>}
      <SetupLane rows={summary.ready} lane="ready" onOpen={onOpen} />
      <SetupLane rows={summary.waiting} lane="waiting" onOpen={onOpen} />
      <SetupLane rows={summary.developing} lane="developing" onOpen={onOpen} />
      {summary.blockers.length > 0 && <details className="mt-4 rounded border border-line p-3">
        <summary className="cursor-pointer text-[14px] text-ink-2">Why {summary.blocked.length} pairs are blocked · all checks</summary>
        <p className="text-[12px] text-ink-3 mt-2">A pair can fail multiple checks. Each count is the number of affected pairs.</p>
        <ul className="mt-3 space-y-2 text-[13px] text-ink-2">{summary.blockers.map(([reason, count]) => <li key={reason} className="flex gap-3"><span className="num text-ink min-w-6">{count}</span><span>{reason}</span></li>)}</ul>
      </details>}
      {summary.unavailable.length > 0 && <p className="text-[13px] text-ink-2 mt-3">{summary.unavailable.length} pairs lack fresh data or a complete execution assessment and are excluded from entry candidates.</p>}
    </>}
    <p className="text-[12px] text-ink-2 mt-3">Net reward / risk includes configured execution costs at the assessed price. Scores rank evidence, not win probability. No strategy edge has been established by these checks. Paper-test first.</p>
    {feed?.provider === "kraken" && <p className="text-[12px] text-ink-2 mt-2">Kraken+ fee waiver assumed within your allowance. App quote spreads can differ from this market feed; verify the final quote. <a className="underline underline-offset-2 hover:text-ink" href="https://support.kraken.com/articles/kraken-faq-subscription-service-overview" target="_blank" rel="noreferrer">Kraken+ terms</a></p>}
  </section>;
}
