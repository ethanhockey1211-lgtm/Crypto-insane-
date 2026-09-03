"use client";
import { fmtPct, signClass } from "@/lib/format";
import type { OpportunityMetrics } from "@/lib/types";

export function MomentumPanel({ m }: { m: OpportunityMetrics }) {
  const cells: [string, number | null][] = [["1m", m.r1m], ["5m", m.r5m], ["15m", m.r15m], ["1h", m.r1h], ["24h", m.r24h]];
  return (
    <div>
      <div className="grid grid-cols-5 gap-px bg-line rounded-[3px] overflow-hidden">
        {cells.map(([k, v]) => (
          <div key={k} className="bg-navy px-2 py-1.5">
            <div className="eyebrow">{k}</div>
            <div className={`num text-[13px] ${signClass(v)}`}>{fmtPct(v)}</div>
          </div>
        ))}
      </div>
      <div className="flex gap-4 mt-1.5 text-[11px] text-ink-2">
        <span>5m acceleration <span className={`num ${signClass(m.accel5m)}`}>{m.accel5m == null ? "—" : (m.accel5m > 0 ? "strengthening" : "fading")} {fmtPct(m.accel5m, 3)}</span></span>
        <span>RSI 5m <span className="num text-ink">{m.rsi5m?.toFixed(0) ?? "—"}</span> · 15m <span className="num text-ink">{m.rsi15m?.toFixed(0) ?? "—"}</span></span>
        <span>taker buys <span className="num text-ink">{m.buyShare5m != null ? `${Math.round(m.buyShare5m * 100)}%` : "—"}</span></span>
      </div>
    </div>
  );
}
