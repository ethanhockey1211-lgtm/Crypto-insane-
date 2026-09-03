"use client";
import { useEffect, useMemo, useState } from "react";
import { sizePosition } from "@/lib/position";
import { fmtMoney, fmtPct, fmtPrice } from "@/lib/format";
import type { TradePlan } from "@/lib/types";

const KEY = "ts.risk.v1";
interface RiskDefaults { accountBalance: number; maxPositionUsd: number; maxRiskPct: number; feePct: number }
const DEFAULTS: RiskDefaults = { accountBalance: 3000, maxPositionUsd: 3000, maxRiskPct: 0.01, feePct: 0.001 };

function loadDefaults(): RiskDefaults {
  try { const raw = localStorage.getItem(KEY); return raw ? { ...DEFAULTS, ...(JSON.parse(raw) as Partial<RiskDefaults>) } : DEFAULTS; } catch { return DEFAULTS; }
}

function Num({ label, value, onChange, step = "any", suffix }: { label: string; value: number; onChange: (v: number) => void; step?: string; suffix?: string }) {
  return (
    <label className="flex flex-col gap-0.5">
      <span className="eyebrow">{label}{suffix ? ` (${suffix})` : ""}</span>
      <input className="field num" type="number" step={step} value={Number.isFinite(value) ? value : ""} onChange={(e) => onChange(parseFloat(e.target.value))} />
    </label>
  );
}

export function PositionCalculator({ plan, price }: { plan: TradePlan | null; price: number }) {
  const [d, setD] = useState<RiskDefaults>(DEFAULTS);
  const [entry, setEntry] = useState(price);
  const [stop, setStop] = useState(plan?.stop ?? price * 0.98);
  const [t1, setT1] = useState(plan?.target1 ?? price * 1.02);
  const [t2, setT2] = useState(plan?.target2 ?? price * 1.03);
  const [t3, setT3] = useState(plan?.target3 ?? price * 1.05);
  useEffect(() => { setD(loadDefaults()); }, []);
  useEffect(() => {
    if (plan) { setEntry(plan.entryMid); setStop(plan.stop); setT1(plan.target1); setT2(plan.target2); setT3(plan.target3); }
  }, [plan]);
  const update = (patch: Partial<RiskDefaults>) => setD((prev) => { const n = { ...prev, ...patch }; try { localStorage.setItem(KEY, JSON.stringify(n)); } catch { /* ignore */ } return n; });

  const r = useMemo(() => sizePosition({ ...d, entry, stop, targets: [t1, t2, t3] }), [d, entry, stop, t1, t2, t3]);

  return (
    <div className="grid grid-cols-2 gap-3">
      <div className="grid grid-cols-2 gap-2">
        <Num label="Account" value={d.accountBalance} onChange={(v) => update({ accountBalance: v })} suffix="$" />
        <Num label="Max position" value={d.maxPositionUsd} onChange={(v) => update({ maxPositionUsd: v })} suffix="$" />
        <Num label="Max risk / trade" value={Math.round(d.maxRiskPct * 10000) / 100} onChange={(v) => update({ maxRiskPct: v / 100 })} suffix="%" step="0.1" />
        <Num label="Fees round trip" value={Math.round(d.feePct * 10000) / 100} onChange={(v) => update({ feePct: v / 100 })} suffix="%" step="0.01" />
        <Num label="Entry" value={entry} onChange={setEntry} />
        <Num label="Stop" value={stop} onChange={setStop} />
        <Num label="Target 1" value={t1} onChange={setT1} />
        <Num label="Target 2" value={t2} onChange={setT2} />
        <Num label="Target 3" value={t3} onChange={setT3} />
      </div>
      <div className="text-[12px]">
        {!r.valid ? (
          <div className="warn">{r.reason}</div>
        ) : (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
            <dt className="text-ink-3">Quantity</dt><dd className="num">{r.quantity.toLocaleString("en-US", { maximumFractionDigits: 6 })}</dd>
            <dt className="text-ink-3">Capital deployed</dt><dd className="num">{fmtMoney(r.capitalDeployed)} <span className="text-ink-3">({fmtPct(r.positionPctOfAccount, 1)} of account)</span></dd>
            <dt className="text-ink-3">Money at risk</dt><dd className="num down">{fmtMoney(r.dollarRisk)} <span className="text-ink-3">({fmtPct(r.riskPctOfAccount, 2)} of account, bound by {r.boundBy})</span></dd>
            <dt className="text-ink-3">Risk per unit</dt><dd className="num">{fmtPrice(r.riskPerUnit)}</dd>
            {r.targets.map((t, i) => (
              <>
                <dt key={`k${i}`} className="text-ink-3">Profit at T{i + 1}</dt>
                <dd key={`v${i}`} className="num up">{fmtMoney(t.profitUsd)} <span className="text-ink-3">{t.rewardRatio.toFixed(1)}R · {fmtPct(t.pct)}</span></dd>
              </>
            ))}
            <dt className="text-ink-3">Fees</dt><dd className="num text-ink-2">{fmtMoney(r.fees)}</dd>
          </dl>
        )}
        <p className="text-[10.5px] text-ink-3 mt-2">Sizes from your own inputs. Nothing here is a recommendation and no order is placed.</p>
      </div>
    </div>
  );
}
