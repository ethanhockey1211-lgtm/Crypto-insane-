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
  const blocked = rows.filter(r => decision(r, live).state === "avoid").length;
  return <section className="p-4 sm:p-5 border-b border-line" aria-label="Trading decision shortlist">
    <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
      <div><h2 className="text-[20px] font-semibold">Your next decision</h2>
        <p className="text-[14px] text-ink-2 mt-1">{live ? `${rows.length} ranked coins · ${blocked} entries ruled out · up to 3 to inspect` : "Entries paused until market data and scanner updates are fresh."}</p></div>
      <span className={`text-[14px] rounded px-3 py-1 border ${live ? "border-line-strong text-ink-2" : "border-warn/50 warn"}`}>{live ? "Research mode" : "Data interrupted"}</span>
    </div>
    {!candidates.length ? <div className="rounded border border-line-strong bg-ground p-5">
      <h3 className="text-[18px]">{live ? "No qualifying entries right now" : "Wait for fresh data"}</h3>
      <p className="text-[14px] text-ink-2 mt-2">{live ? "Don’t force a trade. Browse the full market below to see why setups were excluded." : "Previous prices may still be visible, but they are not current trade guidance."}</p>
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
