"use client";
import { useMemo, useState } from "react";
import { store, useAllOrder, useCycle, useDisplay } from "@/lib/display";
import { useFeed, useHub } from "@/lib/store";
import { SETUP_ORDER } from "@/lib/format";
import { decision, scannerIsFresh } from "@/lib/decision";
import { OpportunityRow } from "./OpportunityRow";
import { DecisionBoard } from "./DecisionBoard";
import { MarketRadar } from "./MarketRadar";
import { HiddenMarketsControl } from "./HiddenMarketsControl";
import { useHiddenMarkets } from "@/lib/hidden-markets";

type SortKey = "score" | "momentum" | "volume" | "rr" | "setup" | "volume24h" | "r24h";
const SORTS: { key: SortKey; label: string }[] = [
  { key: "score", label: "Score" }, { key: "momentum", label: "Momentum" }, { key: "volume", label: "Rel. volume" }, { key: "rr", label: "R:R" },
  { key: "setup", label: "Setup" }, { key: "volume24h", label: "24h volume" }, { key: "r24h", label: "24h change" },
];

export function OpportunityScanner({ active, onOpen }: { active: string | null; onOpen: (s: string) => void }) {
  const order = useAllOrder();
  const visibility = useHiddenMarkets();
  const [sort, setSort] = useState<SortKey>("score");
  const [onlySetups, setOnlySetups] = useState(false);
  const [query, setQuery] = useState("");
  const [focus, setFocus] = useState("all");
  const cycle = useCycle(); const feed = useFeed(); const hub = useHub();
  const display = useDisplay();
  const live = hub === "connected" && feed?.live === true && scannerIsFresh(cycle.at, display.paused ? display.capturedAt ?? Date.now() : Date.now());

  // Every table value and its ordering come from the same readable display snapshot.
  const symbols = useMemo(() => {
    const normalizedQuery = query.trim().toUpperCase().replace("/", "-");
    const filtered = order.filter(symbol => {
      const row = store.getRow(symbol);
      if (focus !== "all") {
        if (!row || !live || row.stale) return false;
        const state = decision(row, live).state;
        if (focus === "zone" && state !== "watch") return false;
        if (focus === "waiting" && state !== "wait") return false;
        if (focus === "volume" && !(row.relVol != null && row.relVol >= 1.5)) return false;
        if (focus === "movers" && !(row.r5m != null && row.r5m > 0)) return false;
      }
      return (!onlySetups || (row && row.setup !== "None")) && (!normalizedQuery || symbol.includes(normalizedQuery));
    });
    const val = (symbol: string): number => {
      const r = store.getRow(symbol);
      const summary = store.getSymbol(symbol);
      if (!r) {
        if (sort === "r24h") return summary?.quote && summary.quote.price > 0 && summary.open24h && summary.open24h > 0 ? summary.quote.price / summary.open24h - 1
          : summary?.change24hPct != null ? summary.change24hPct / 100 : -Infinity;
        if (sort === "volume24h" && summary?.volume24hBase != null && summary.quote) return summary.volume24hBase * summary.quote.price;
        return -Infinity;
      }
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
    if (sort !== "score") filtered.sort((a, b) => val(b) - val(a) || (store.getRow(b)?.score ?? -Infinity) - (store.getRow(a)?.score ?? -Infinity));
    return filtered;
  }, [order, sort, onlySetups, query, focus, live]);
  const pending = order.filter(symbol => !store.getRow(symbol)).length;

  return (
    <section className="panel rounded-xl min-h-0 h-full overflow-auto">
      <HiddenMarketsControl />
      <MarketRadar onOpen={onOpen} onFilter={onOpen} />
      <DecisionBoard onOpen={onOpen} />
      <details open className="p-1">
      <summary className="cursor-pointer px-3 py-3 text-[14px] text-ink-2">All USD pairs · filters and detailed indicators</summary>
      <div className="flex flex-wrap gap-2 px-3 pb-3" aria-label="Market quick filters">
        {[["all", "All coins"], ["zone", "In the zone"], ["waiting", "Waiting for price"], ["volume", "Volume spikes"], ["movers", "5m gainers"]].map(([key, label]) => <button key={key} className="control-button" aria-pressed={focus === key} onClick={() => { setFocus(key); if (key === "volume") setSort("volume"); else if (key === "movers") setSort("momentum"); }}>{label}</button>)}
      </div>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-1.5 sm:py-0 sm:h-9 border-b border-line">
        <span className="eyebrow">All coins</span>
        <span className="num text-[11px] text-ink-3 whitespace-nowrap">{symbols.length} of {order.length}</span>
        <label className="flex items-center gap-1.5 text-[11px] text-ink-2 ml-2 whitespace-nowrap">
          <input type="checkbox" checked={onlySetups} onChange={(e) => setOnlySetups(e.target.checked)} /> setups only
        </label>
        <input className="field !w-32 sm:!w-44 text-[12px]" placeholder="Search any Kraken coin" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Filter by symbol" />
        {(query || onlySetups || focus !== "all") && <button className="text-[11px] text-accent underline underline-offset-2" onClick={() => { setQuery(""); setOnlySetups(false); setFocus("all"); }}>Reset filters</button>}
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
      {pending > 0 && <p className="px-3 py-2 text-[12px] text-ink-2" role="status">{pending} of {order.length} pairs awaiting analysis. All pairs are listed while history loads; scores and plans appear as data becomes available.</p>}
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
              <tr><td colSpan={15} className="px-3 py-8 text-center text-ink-3">{order.length ? "No visible pairs match these filters. Reset filters or manage Hidden coins above." : visibility.symbols.length ? "No visible markets. Restore coins using Hidden coins above, or wait for the market catalog to load." : "Loading the exchange's USD pairs. Coins appear here before their analysis is ready."}</td></tr>
            )}
          </tbody>
        </table>
      </div>
      </details>
    </section>
  );
}
