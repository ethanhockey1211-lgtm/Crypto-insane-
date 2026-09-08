"use client";
import { useTape } from "@/lib/display";
import { fmtTime } from "@/lib/format";

export function MarketTape({ onOpen }: { onOpen: (s: string) => void }) {
  const tape = useTape();
  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-2 px-3 h-9 border-b border-line">
        <span className="eyebrow">What&apos;s moving now</span>
        <span className="num text-[11px] text-ink-3">{tape.length}</span>
      </div>
      <ol className="overflow-auto min-h-0 flex-1 text-[12px]">
        {tape.map((e) => (
          <li key={e.id} className={`flex gap-2 px-3 py-1 border-b border-line/50 ${e.symbol ? "row-hover cursor-pointer" : ""}`} onClick={() => e.symbol && onOpen(e.symbol)}>
            <span className="num text-ink-3 shrink-0">{fmtTime(e.at)}</span>
            <span className={`shrink-0 w-1.5 mt-[6px] h-1.5 rounded-full ${e.severity === "Alert" ? "bg-warn" : e.severity === "Notice" ? "bg-accent" : "bg-ink-3/60"}`} aria-hidden />
            <span className={e.severity === "Alert" ? "warn" : e.severity === "Notice" ? "text-ink" : "text-ink-2"}>{e.text}</span>
          </li>
        ))}
        {tape.length === 0 && <li className="px-3 py-6 text-ink-3">Quiet. Breakouts, failures, volume surges, VWAP crosses and regime changes land here as they happen.</li>}
      </ol>
    </section>
  );
}
