"use client";
import { useEffect, useState } from "react";
import { useRow } from "@/lib/display";
import { loadWatchlist, saveWatchlist } from "@/lib/watchlist";
import { fmtPct, fmtPrice, fmtX } from "@/lib/format";
import { hiddenMarkets, useHiddenMarkets } from "@/lib/hidden-markets";

function WatchRow({ symbol, onOpen, onRemove }: { symbol: string; onOpen: (s: string) => void; onRemove: (s: string) => void }) {
  const row = useRow(symbol);
  const dist = row?.keyLevel != null && row.price > 0 ? (row.keyLevel - row.price) / row.price : null;
  return (
    <tr className="row-hover border-b border-line/60 h-11 sm:h-[30px] cursor-pointer" onClick={() => onOpen(symbol)}>
      <td className="px-3 font-medium">{symbol.replace("-USD", "")}</td>
      <td className="num px-2 text-right">{row ? fmtPrice(row.price) : "—"}</td>
      <td className="num px-2 text-right">{row ? row.score.toFixed(0) : "—"}</td>
      <td className="px-2 text-[11px]">{row?.breakout ?? "—"}{row?.setup && row.setup !== "None" ? ` · ${row.setup}` : ""}</td>
      <td className="num px-2 text-right text-ink-2">{row?.keyLevel != null ? fmtPrice(row.keyLevel) : "—"}</td>
      <td className={`num px-2 text-right ${dist == null ? "text-ink-3" : Math.abs(dist) < 0.005 ? "warn" : "text-ink-2"}`}>{dist == null ? "—" : `${fmtPct(dist)} away`}</td>
      <td className="num px-2 text-right">{fmtX(row?.relVol)}</td>
      <td className="px-2 text-[11px]">{row?.trend ?? "—"}</td>
      <td className="px-2 text-[11px] text-ink-3">no alerts</td>
      <td className="px-2 text-right"><button className="text-ink-3 hover:text-down" onClick={(e) => { e.stopPropagation(); onRemove(symbol); }} aria-label={`Remove ${symbol}`}>×</button></td>
    </tr>
  );
}

export function useWatchlist(): [string[], (s: string) => void, (s: string) => void] {
  const [list, setList] = useState<string[]>([]);
  useEffect(() => { setList(loadWatchlist()); }, []);
  const add = (s: string) => setList((l) => { const n = l.includes(s) ? l : [...l, s]; saveWatchlist(n); return n; });
  const remove = (s: string) => setList((l) => { const n = l.filter((x) => x !== s); saveWatchlist(n); return n; });
  return [list, add, remove];
}

export function Watchlist({ onOpen, list, add, remove }: { onOpen: (s: string) => void; list: string[]; add: (s: string) => void; remove: (s: string) => void }) {
  const [input, setInput] = useState("");
  const visibility = useHiddenMarkets();
  const visibleList = list.filter(symbol => !hiddenMarkets.isHidden(symbol));
  const hiddenCount = list.length - visibleList.length;
  const submit = () => {
    const s = input.trim().toUpperCase();
    if (!s) return;
    add(s.includes("-") ? s : `${s}-USD`);
    setInput("");
  };
  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-3 px-3 h-9 border-b border-line">
        <span className="eyebrow">Watchlist</span>
        <span className="num text-[11px] text-ink-3">{visibleList.length}</span>
        <form className="ml-auto flex gap-1" onSubmit={(e) => { e.preventDefault(); submit(); }}>
          <input className="field !w-28 text-[11px]" placeholder="add symbol" value={input} onChange={(e) => setInput(e.target.value)} aria-label="Add symbol to watchlist" />
          <button type="submit" className="px-2 text-[11px] bg-navy-3 rounded-[3px]">Add</button>
        </form>
      </div>
      {hiddenCount > 0 && <p className="px-3 py-2 text-[11px] text-ink-2">{hiddenCount} watched {hiddenCount === 1 ? "coin is" : "coins are"} hidden. Restore under Hidden coins in Find setups; your saved watchlist is retained.</p>}
      <div className="overflow-auto min-h-0 flex-1">
        <table className="w-full border-collapse text-[12px]">
          <thead className="sticky top-0 bg-navy">
            <tr className="eyebrow h-7 border-b border-line">
              <th className="text-left px-3 font-normal">Symbol</th><th className="text-right px-2 font-normal">Price</th><th className="text-right px-2 font-normal">Score</th>
              <th className="text-left px-2 font-normal">Status</th><th className="text-right px-2 font-normal">Trigger</th><th className="text-right px-2 font-normal">Distance</th>
              <th className="text-right px-2 font-normal">RelVol</th><th className="text-left px-2 font-normal">Trend</th><th className="text-left px-2 font-normal">Alerts</th><th />
            </tr>
          </thead>
          <tbody>
            {visibleList.map((s) => <WatchRow key={s} symbol={s} onOpen={onOpen} onRemove={remove} />)}
            {visibleList.length === 0 && <tr><td colSpan={10} className="px-3 py-6 text-ink-3">{visibility.symbols.length && list.length ? "Your watched coins are hidden. Restore them from Hidden coins in Find setups." : "Empty. Add a symbol above or use “Watch” in a setup card. Saved in this browser."}</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}
