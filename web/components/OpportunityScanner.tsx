"use client";
import { useMemo, useState } from "react";
import { store, useOrder } from "@/lib/store";
import { SETUP_ORDER } from "@/lib/format";
import { OpportunityRow } from "./OpportunityRow";
import { DecisionBoard } from "./DecisionBoard";

type SortKey = "score" | "momentum" | "volume" | "rr" | "setup" | "volume24h" | "r24h";
const SORTS: { key: SortKey; label: string }[] = [
  { key: "score", label: "Score" }, { key: "momentum", label: "Momentum" }, { key: "volume", label: "Rel. volume" }, { key: "rr", label: "R:R" },
  { key: "setup", label: "Setup" }, { key: "volume24h", label: "24h volume" }, { key: "r24h", label: "24h change" },
];

export function OpportunityScanner({ active, onOpen }: { active: string | null; onOpen: (s: string) => void }) {
  const order = useOrder();
  const [sort, setSort] = useState<SortKey>("score");
  const [onlySetups, setOnlySetups] = useState(false);
  const [query, setQuery] = useState("");

  // Sorting reads the store directly: the list re-renders on list version changes, rows re-render on their own.
  const symbols = useMemo(() => {
    const rows = order.map((s) => store.getRow(s)).filter((r): r is NonNullable<typeof r> => r !== null);
    const filtered = rows.filter((r) => (!onlySetups || r.setup !== "None") && (!query || r.symbol.includes(query.toUpperCase())));
    const val = (r: (typeof rows)[number]): number => {
      switch (sort) {
        case "momentum": return r.r15m ?? -Infinity;
        case "volume": return r.relVol ?? -Infinity;
        case "rr": return r.rr ?? -Infinity;
        case "volume24h": return r.volume24h ?? -Infinity;
        case "r24h": return r.r24h ?? -Infinity;
        case "setup": return -SETUP_ORDER.indexOf(r.setup);
        default: return r.score;
      }
    };
    if (sort !== "score") filtered.sort((a, b) => val(b) - val(a) || b.score - a.score);
    return filtered.map((r) => r.symbol);
  }, [order, sort, onlySetups, query]);

  return (
    <section className="panel min-h-0 h-full overflow-auto">
      <DecisionBoard onOpen={onOpen} />
      <details open className="p-1">
      <summary className="cursor-pointer px-3 py-3 text-[14px] text-ink-2">Full market · filters and detailed indicators</summary>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-1.5 sm:py-0 sm:h-9 border-b border-line">
        <span className="eyebrow">Opportunities</span>
        <span className="num text-[11px] text-ink-3 whitespace-nowrap">{symbols.length} of {order.length}</span>
        <label className="flex items-center gap-1.5 text-[11px] text-ink-2 ml-2 whitespace-nowrap">
          <input type="checkbox" checked={onlySetups} onChange={(e) => setOnlySetups(e.target.checked)} /> setups only
        </label>
        <input className="field !w-20 sm:!w-28 text-[11px]" placeholder="filter" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Filter by symbol" />
        <div className="ml-auto hidden md:flex items-center gap-1 text-[11px]">
          <span className="text-ink-3 mr-1">sort</span>
          {SORTS.map((s) => (
            <button key={s.key} onClick={() => setSort(s.key)} className={`px-2 py-0.5 rounded-[3px] ${sort === s.key ? "bg-navy-3 text-ink" : "text-ink-3 hover:text-ink-2"}`}>{s.label}</button>
          ))}
        </div>
        <select className="field !w-auto ml-auto md:hidden text-[11px]" value={sort} onChange={(e) => setSort(e.target.value as SortKey)} aria-label="Sort">
          {SORTS.map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}
        </select>
      </div>
      <div className="overflow-auto min-h-0 flex-1">
        <table className="w-full border-collapse text-[12px]">
          <thead className="sticky top-0 bg-navy z-10">
            <tr className="eyebrow h-7 border-b border-line">
              <th className="text-right pl-3 pr-1 font-normal hidden sm:table-cell">#</th>
              <th className="text-left px-2 font-normal">Symbol</th>
              <th className="text-left px-2 font-normal">Score<span className="hidden md:inline"> · components</span></th>
              <th className="text-left px-2 font-normal hidden sm:table-cell">Setup</th>
              <th className="text-left px-2 font-normal hidden md:table-cell">Breakout</th>
              <th className="text-right px-2 font-normal">Price</th>
              <th className="text-right px-2 font-normal hidden xl:table-cell">Entry</th>
              <th className="text-right px-2 font-normal hidden xl:table-cell">Stop</th>
              <th className="text-right px-2 font-normal hidden xl:table-cell">Target</th>
              <th className="text-right px-2 font-normal hidden sm:table-cell" title="Before execution costs, measured from planned midpoint">Gross R:R</th>
              <th className="text-right px-2 font-normal hidden lg:table-cell">5m</th>
              <th className="text-right px-2 font-normal">15m</th>
              <th className="text-right px-2 font-normal hidden lg:table-cell">1h</th>
              <th className="text-right px-2 pr-3 md:pr-2 font-normal">24h</th>
              <th className="text-right px-2 pr-3 font-normal hidden md:table-cell">RelVol</th>
            </tr>
          </thead>
          <tbody>
            {symbols.map((s) => <OpportunityRow key={s} symbol={s} active={active === s} onOpen={onOpen} />)}
            {symbols.length === 0 && (
              <tr><td colSpan={15} className="px-3 py-8 text-center text-ink-3">No opportunities yet. The scanner ranks the universe once analytics have warmed up.</td></tr>
            )}
          </tbody>
        </table>
      </div>
      </details>
    </section>
  );
}
