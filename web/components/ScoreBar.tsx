"use client";

const NAMES = ["Momentum", "Volume", "Structure", "Breakout", "Market", "Liquidity", "Risk/reward"];
const MAX = [20, 20, 20, 15, 10, 10, 15];
const SHADES = ["#7fb4e0", "#6aa3d1", "#5891c1", "#4a80b0", "#3f709c", "#376289", "#2f5477"];

/**
 * The score as a ledger: seven component segments in a cool ramp and a hatched cut for penalties.
 * The width of each segment is the points it earned out of 100, so two scores of 72 with different
 * makeups look different at a glance.
 */
export function ScoreBar({ components, penalty, total }: { components: number[]; penalty: number; total: number }) {
  const segs = NAMES.map((n, i) => ({ name: n, pts: components[i] ?? 0, max: MAX[i], shade: SHADES[i] }));
  const raw = segs.reduce((s, x) => s + x.pts, 0);
  const cut = Math.min(penalty, raw);
  const title = segs.map((s) => `${s.name} ${s.pts.toFixed(0)}/${s.max}`).join(" · ") + (penalty > 0 ? ` · penalties −${penalty.toFixed(0)}` : "");
  return (
    <div className="flex items-center gap-2" title={title} aria-label={`Score ${total.toFixed(0)}. ${title}`}>
      <span className={`num w-7 text-right font-medium ${total >= 80 ? "text-ink" : total >= 60 ? "text-ink-2" : "text-ink-3"}`}>{total.toFixed(0)}</span>
      <div className="relative h-[7px] bg-navy-3/60 rounded-[1px] overflow-hidden w-[84px] sm:w-[120px]">
        <div className="absolute inset-y-0 left-0 flex">
          {segs.map((s) => (
            <div key={s.name} style={{ width: `${s.pts}%`, background: s.shade }} />
          ))}
        </div>
        {cut > 0 && (
          <div className="absolute inset-y-0 score-cut" style={{ left: `${raw - cut}%`, width: `${cut}%` }} />
        )}
      </div>
    </div>
  );
}
