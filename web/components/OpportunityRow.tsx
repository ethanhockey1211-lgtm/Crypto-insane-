"use client";
import { memo, useEffect, useRef } from "react";
import { useRow } from "@/lib/store";
import { fmtPct, fmtPrice, fmtR, fmtX, signClass } from "@/lib/format";
import { ScoreBar } from "./ScoreBar";

const STATE_SHORT: Record<string, string> = {
  Watching: "watch", Approaching: "approach", Attempt: "attempt", Confirmed: "CONFIRMED", Retesting: "retest", RetestHeld: "RETEST HELD", Failed: "failed", Extended: "extended",
};

export const OpportunityRow = memo(function OpportunityRow({ symbol, active, onOpen }: { symbol: string; active: boolean; onOpen: (s: string) => void }) {
  const row = useRow(symbol);
  const priceRef = useRef<HTMLSpanElement>(null);
  const lastPrice = useRef<number | null>(null);
  useEffect(() => {
    if (!row) return;
    if (lastPrice.current !== null && lastPrice.current !== row.price && priceRef.current) {
      priceRef.current.classList.remove("flash");
      void priceRef.current.offsetWidth;
      priceRef.current.classList.add("flash");
    }
    lastPrice.current = row.price;
  }, [row]);
  if (!row) return null;
  const state = row.breakout ? STATE_SHORT[row.breakout] ?? row.breakout : "";
  const stateClass = row.breakout === "Confirmed" || row.breakout === "RetestHeld" ? "up" : row.breakout === "Failed" ? "down" : row.breakout === "Extended" ? "warn" : "text-ink-3";
  return (
    <tr
      className={`row-hover cursor-pointer border-b border-line/60 h-11 sm:h-[30px] ${active ? "row-active" : ""} ${row.stale ? "opacity-60" : ""}`}
      onClick={() => onOpen(symbol)}
      onKeyDown={(e) => { if (e.key === "Enter") onOpen(symbol); }}
      tabIndex={0}
      aria-label={`${symbol} score ${row.score.toFixed(0)} ${row.setup}`}
    >
      <td className="num text-ink-3 pl-3 pr-1 text-right w-8 hidden sm:table-cell">{row.rank}</td>
      <td className="pl-3 sm:pl-2 pr-2 font-medium whitespace-nowrap">
        <span className="num text-ink-3 sm:hidden mr-1.5 text-[10.5px]">{row.rank}</span>
        {symbol.replace("-USD", "")}
        {row.stale && <span className="tag ml-1 text-warn border-warn/40">stale</span>}
        {/* Phones hide the setup column; the setup rides under the symbol instead. */}
        <div className="sm:hidden text-[10.5px] font-normal leading-tight mt-0.5">
          <span className={row.setup === "None" ? "text-ink-3" : "text-ink-2"}>{row.setup === "None" ? "no setup" : row.setup}</span>
          {row.doNotChase && <span className="warn ml-1.5">· do not chase</span>}
        </div>
      </td>
      <td className="px-2"><ScoreBar components={row.components} penalty={row.penalty} total={row.score} /></td>
      <td className="px-2 whitespace-nowrap hidden sm:table-cell">
        <span className={row.setup === "None" ? "text-ink-3" : "text-ink"}>{row.setup === "None" ? "—" : row.setup}</span>
        {row.doNotChase && <span className="tag tag-warn ml-1.5">do not chase</span>}
      </td>
      <td className={`px-2 text-[11px] whitespace-nowrap hidden md:table-cell ${stateClass}`}>{state}</td>
      <td className="num px-2 text-right"><span ref={priceRef} className="inline-block px-1 -mx-1 rounded-[2px]">{fmtPrice(row.price)}</span></td>
      <td className="num px-2 text-right text-ink-2 hidden xl:table-cell">{fmtPrice(row.entry)}</td>
      <td className="num px-2 text-right text-down/80 hidden xl:table-cell">{fmtPrice(row.stop)}</td>
      <td className="num px-2 text-right text-up/80 hidden xl:table-cell">{fmtPrice(row.target1)}</td>
      <td className="num px-2 text-right hidden sm:table-cell">{fmtR(row.rr)}</td>
      <td className={`num px-2 text-right hidden lg:table-cell ${signClass(row.r5m)}`}>{fmtPct(row.r5m)}</td>
      <td className={`num px-2 text-right ${signClass(row.r15m)}`}>{fmtPct(row.r15m)}</td>
      <td className={`num px-2 text-right hidden lg:table-cell ${signClass(row.r1h)}`}>{fmtPct(row.r1h)}</td>
      <td className={`num px-2 pr-3 md:pr-2 text-right ${signClass(row.r24h)}`}>{fmtPct(row.r24h)}</td>
      <td className={`num px-2 pr-3 text-right hidden md:table-cell ${row.relVol != null && row.relVol >= 1.5 ? "text-ink" : "text-ink-3"}`}>{fmtX(row.relVol)}</td>
    </tr>
  );
});
