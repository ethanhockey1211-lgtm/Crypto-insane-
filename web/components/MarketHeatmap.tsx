"use client";
import { useMemo, useState } from "react";
import { store, useOrder } from "@/lib/display";
import { fmtPct, fmtPrice } from "@/lib/format";

type Metric = "r1m" | "r5m" | "r15m" | "r1h" | "r24h" | "score";
type Size = "volume24h" | "momentum" | "score";

function color(v: number | null, metric: Metric): string {
  if (v == null) return "var(--color-navy-2)";
  if (metric === "score") {
    const a = Math.max(0, Math.min(1, v / 100));
    return `color-mix(in srgb, var(--color-accent) ${Math.round(a * 70)}%, var(--color-navy-2))`;
  }
  const scale = metric === "r1m" ? 0.004 : metric === "r5m" ? 0.01 : metric === "r15m" ? 0.02 : metric === "r1h" ? 0.04 : 0.12;
  const a = Math.max(-1, Math.min(1, v / scale));
  const tone = a >= 0 ? "var(--color-up)" : "var(--color-down)";
  return `color-mix(in srgb, ${tone} ${Math.round(Math.abs(a) * 60)}%, var(--color-navy-2))`;
}

export function MarketHeatmap({ onOpen }: { onOpen: (s: string) => void }) {
  const order = useOrder();
  const [metric, setMetric] = useState<Metric>("r15m");
  const [size, setSize] = useState<Size>("volume24h");
  const tiles = useMemo(() => {
    const rows = order.map((s) => store.getRow(s)).filter((r): r is NonNullable<typeof r> => r !== null);
    const key = (r: (typeof rows)[number]) => size === "volume24h" ? r.volume24h ?? 0 : size === "momentum" ? r.r15m ?? -1 : r.score;
    rows.sort((a, b) => key(b) - key(a));
    const bigCount = Math.max(1, Math.floor(rows.length / 12));
    return rows.map((r, i) => ({ r, big: i < bigCount }));
  }, [order, metric, size]);

  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-1.5 sm:py-0 sm:h-9 border-b border-line text-[11px]">
        <span className="eyebrow">Heatmap</span>
        <span className="text-ink-3">color</span>
        {(["r1m", "r5m", "r15m", "r1h", "r24h", "score"] as Metric[]).map((m) => (
          <button key={m} onClick={() => setMetric(m)} className={`px-2 py-0.5 rounded-[3px] ${metric === m ? "bg-navy-3 text-ink" : "text-ink-3 hover:text-ink-2"}`}>{m === "score" ? "setup score" : m.slice(1)}</button>
        ))}
        <span className="text-ink-3 ml-3">size by</span>
        {(["volume24h", "momentum", "score"] as Size[]).map((s) => (
          <button key={s} onClick={() => setSize(s)} className={`px-2 py-0.5 rounded-[3px] ${size === s ? "bg-navy-3 text-ink" : "text-ink-3 hover:text-ink-2"}`}>{s === "volume24h" ? "24h volume" : s === "momentum" ? "momentum" : "setup score"}</button>
        ))}
      </div>
      <div className="overflow-auto min-h-0 flex-1 p-2 grid gap-1 grid-cols-[repeat(auto-fill,minmax(88px,1fr))] auto-rows-[56px]">
        {tiles.map(({ r, big }) => {
          const v = metric === "score" ? r.score : r[metric];
          return (
            <button key={r.symbol} onClick={() => onOpen(r.symbol)} style={{ background: color(v, metric) }}
              className={`text-left p-1.5 rounded-[2px] border border-line/40 hover:border-line-strong ${big ? "col-span-2 row-span-2" : ""}`}>
              <div className="flex justify-between items-baseline">
                <span className={`font-medium ${big ? "text-[14px]" : "text-[12px]"}`}>{r.symbol.replace("-USD", "")}</span>
                {r.doNotChase && <span className="text-[9px] warn">DNC</span>}
              </div>
              <div className="num text-[11px] text-ink-2">{fmtPrice(r.price)}</div>
              <div className="num text-[12px]">{metric === "score" ? r.score.toFixed(0) : fmtPct(v as number | null)}</div>
              {big && <div className="text-[10px] text-ink-2 mt-1 truncate">{r.setup !== "None" ? r.setup : r.breakout ?? ""}</div>}
            </button>
          );
        })}
      </div>
    </section>
  );
}
