"use client";
import { useEffect, useState } from "react";
import { store, useCycle, useFeed, useHub, useOrder } from "@/lib/store";
import { decision, scannerIsFresh, shortlist } from "@/lib/decision";
import { fmtPrice } from "@/lib/format";

export function DecisionBoard({ onOpen }: { onOpen: (symbol: string) => void }) {
  const order = useOrder();
  const cycle = useCycle();
  const feed = useFeed();
  const hub = useHub();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const id = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(id); }, []);
  const live = hub === "connected" && feed?.live === true && scannerIsFresh(cycle.at, now);
  const rows = order.flatMap(s => { const r = store.getRow(s); return r ? [r] : []; });
  const candidates = shortlist(rows, live);
  const qualified = rows.filter(r => r.executionStatus === "Watch");
  const entryReady = qualified.filter(r => r.entryState === "InZone").length;
  const waiting = qualified.length - entryReady;
  const rejected = rows.filter(r => r.executionStatus === "Blocked");
  const blockerCounts = new Map<string, number>();
  for (const row of rejected) {
    const reason = row.executionReasons?.[0] ?? row.executionReason ?? "No execution assessment available";
    blockerCounts.set(reason, (blockerCounts.get(reason) ?? 0) + 1);
  }
  const topBlockers = [...blockerCounts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).slice(0, 3);
  const closestRejected = rejected
    .filter(r => r.setup !== "None" && r.entry != null && r.stop != null && r.target1 != null)
    .sort((a, b) => b.score - a.score || (b.netRewardRatio ?? -Infinity) - (a.netRewardRatio ?? -Infinity))[0];
  return <section className="p-4 sm:p-5 border-b border-line" aria-label="Trading decision shortlist">
    <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
      <div><h2 className="text-[20px] font-semibold">Your next decision</h2>
        <p className="text-[14px] text-ink-2 mt-1">{live ? `${rows.length} ranked coins · ${entryReady} entry-ready · ${waiting} waiting for price · ${rejected.length} rejected by safeguards` : "Entries paused until market data and scanner updates are fresh."}</p></div>
      <span className={`text-[14px] rounded px-3 py-1 border ${live ? "border-line-strong text-ink-2" : "border-warn/50 warn"}`}>{live ? "Research mode" : "Data interrupted"}</span>
    </div>
    {!candidates.length ? <div className="rounded border border-line-strong bg-ground p-5">
      <h3 className="text-[18px]">{live ? "No qualifying entries right now" : "Wait for fresh data"}</h3>
      <p className="text-[14px] text-ink-2 mt-2">{live ? "Nothing has passed every safety and reward check yet. This is a diagnosis, not a reason to force a trade." : "Previous prices may still be visible, but they are not current trade guidance."}</p>
      {live && <>
        <div className="grid grid-cols-3 gap-2 mt-4 text-center">
          <div className="rounded border border-line p-2"><p className="text-[11px] text-ink-3">Entry-ready</p><p className="num text-[20px] mt-1">{entryReady}</p></div>
          <div className="rounded border border-line p-2"><p className="text-[11px] text-ink-3">Waiting</p><p className="num text-[20px] mt-1">{waiting}</p></div>
          <div className="rounded border border-line p-2"><p className="text-[11px] text-ink-3">Rejected</p><p className="num text-[20px] mt-1">{rejected.length}</p></div>
        </div>
        {topBlockers.length > 0 && <div className="mt-4">
          <p className="text-[12px] text-ink-3 uppercase tracking-wide">What is blocking the rest</p>
          <ul className="mt-2 space-y-1 text-[14px] text-ink-2">
            {topBlockers.map(([reason, count]) => <li key={reason} className="flex gap-2"><span className="num text-ink">{count}</span><span>{reason}</span></li>)}
          </ul>
        </div>}
        {closestRejected && <button onClick={() => onOpen(closestRejected.symbol)} className="mt-4 text-left text-[14px] text-ink-2 hover:text-ink focus-visible:outline focus-visible:outline-accent">
          <span className="text-ink font-medium">Closest rejected setup: {closestRejected.symbol.replace("-USD", "")}</span>
          <span> · score {closestRejected.score.toFixed(0)} · {closestRejected.executionReasons?.[0] ?? closestRejected.executionReason ?? "execution checks did not pass"}</span>
        </button>}
      </>}
    </div> : <div className="grid grid-cols-1 xl:grid-cols-3 gap-3">
      {candidates.map(r => { const d = decision(r, live); return <article key={r.symbol} className="rounded border border-line-strong border-t-2 border-t-accent bg-ground p-4 min-w-0">
        <div className="flex justify-between gap-2 items-baseline"><h3 className="text-[20px] font-semibold">{r.symbol.replace("-USD", "")}</h3><span className="text-[12px] text-ink-2">Evidence {r.score.toFixed(0)}/100</span></div>
        <p className="text-[14px] text-accent mt-2 font-medium">{d.label}</p>
        <p className="text-[14px] text-ink-2 mt-2">{d.reason}</p>
        <dl className="grid grid-cols-2 gap-3 text-[14px] my-4">
          <div><dt className="text-ink-2">Assessed price</dt><dd className="num">{fmtPrice(r.assessedPrice)}</dd></div>
          <div><dt className="text-ink-2">Net reward / risk</dt><dd className="num">{r.netRewardRatio?.toFixed(2)}R</dd></div>
          <div><dt className="text-ink-2">Stop reference</dt><dd className="num text-down">{fmtPrice(r.stop)}</dd></div>
          <div><dt className="text-ink-2">First target</dt><dd className="num">{fmtPrice(r.target1)}</dd></div>
        </dl>
        <button className="w-full py-2.5 px-3 text-[14px] rounded bg-navy-3 hover:bg-navy-2 focus-visible:outline focus-visible:outline-accent" onClick={() => onOpen(r.symbol)}>Inspect entry and trigger</button>
      </article>; })}
    </div>}
    <p className="text-[12px] text-ink-2 mt-3">Scores rank evidence, not win probability. No strategy edge has been established by these checks. Paper-test first.</p>
  </section>;
}
