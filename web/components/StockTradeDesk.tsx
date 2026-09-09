"use client";
import { useEffect, useRef, useState } from "react";
import { sizeStockPlan, tradePnl, tradeR, type StockPlan, type StockTrade } from "@/lib/stocks";
import type { ScannerPlan } from "@/components/StockEntryScanner";

const usd = (n: number) => n.toLocaleString("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 2 });
const setups: StockPlan["setup"][] = ["Opening range breakout", "VWAP reclaim", "Breakout", "Pullback"];
const setupNotes: Record<StockPlan["setup"], string> = {
  "Opening range breakout": "Record the opening range high, the level that invalidates the idea, and a target before considering an entry.",
  "VWAP reclaim": "Use the chart to study a reclaim of session VWAP. Check the current price and whether the reclaim holds in Kraken.",
  Breakout: "Mark resistance and an invalidation level. Check whether the move has already run beyond your planned entry.",
  Pullback: "Mark the support you want price to hold and the point where the pullback would invalidate your idea.",
};

interface Props {
  symbol: string;
  confirmed: boolean;
  plans: StockPlan[];
  journal: StockTrade[];
  onSavePlan: (plan: StockPlan) => void;
  onDeletePlan: (id: string) => void;
  onAddTrade: (trade: StockTrade) => void;
  onDeleteTrade: (id: string) => void;
  scannerPlan?: ScannerPlan;
}

function NumberField({ label, value, onChange, step = "any" }: { label: string; value: string; onChange: (value: string) => void; step?: string }) {
  return <label className="text-[12px] text-ink-2 grid gap-1 min-w-0">{label}<input className="field !py-2 num" type="number" min="0" step={step} value={value} onChange={e => onChange(e.target.value)} /></label>;
}

export function StockTradeDesk(props: Props) {
  const [tab, setTab] = useState<"plan" | "saved" | "journal">("plan");
  const [entry, setEntry] = useState("");
  const [stop, setStop] = useState("");
  const [target, setTarget] = useState("");
  const [account, setAccount] = useState("");
  const [cash, setCash] = useState("");
  const [riskPct, setRiskPct] = useState("0.5");
  const [cost, setCost] = useState("0.02");
  const [setup, setSetup] = useState<StockPlan["setup"]>(setups[0]);
  const [notes, setNotes] = useState("");
  const [checks, setChecks] = useState([false, false, false]);
  const [message, setMessage] = useState("");
  const [logging, setLogging] = useState<StockPlan | null>(null);
  const [budgetLoaded, setBudgetLoaded] = useState(false);
  useEffect(() => {
    try {
      const saved = JSON.parse(localStorage.getItem("kraken.stock-risk-settings.v1") ?? "null");
      if (saved && typeof saved === "object") {
        for (const [key, setter] of [["account", setAccount], ["cash", setCash], ["riskPct", setRiskPct], ["cost", setCost]] as const) {
          const value = saved[key];
          if (typeof value === "string" && value.length <= 30 && Number.isFinite(Number(value)) && Number(value) >= 0) setter(value);
        }
      }
    } catch { /* optional browser preferences */ }
    setBudgetLoaded(true);
  }, []);
  useEffect(() => {
    if (!budgetLoaded) return;
    try { localStorage.setItem("kraken.stock-risk-settings.v1", JSON.stringify({ account, cash, riskPct, cost })); } catch { /* keep editing in memory */ }
  }, [account, cash, riskPct, cost, budgetLoaded]);
  const appliedScannerPlan = useRef("");
  useEffect(() => {
    const plan = props.scannerPlan;
    if (!plan || plan.symbol !== props.symbol || appliedScannerPlan.current === plan.id) return;
    appliedScannerPlan.current = plan.id;
    setEntry(String(plan.entry)); setStop(String(plan.stop)); setTarget(String(plan.target)); setSetup(plan.setup);
    setChecks([false, false, false]); setTab("plan");
    setMessage(`Scanner levels loaded from ${new Date(plan.asOf).toLocaleTimeString()} (Alpaca IEX), using the top of the entry zone for sizing. Check the current Kraken quote before placing an order.`);
  }, [props.scannerPlan, props.symbol]);
  const input = { entry: Number(entry), stop: Number(stop), target: Number(target), account: Number(account), riskPct: Number(riskPct), cash: Number(cash), costPerShare: Number(cost) };
  const result = sizeStockPlan(input);
  const symbolPlans = props.plans.filter(p => p.symbol === props.symbol);
  const totalPnl = props.journal.reduce((sum, t) => sum + tradePnl(t), 0);
  const wins = props.journal.filter(t => tradePnl(t) > 0).length;
  const ready = props.confirmed && checks.every(Boolean) && result.valid;
  function changeLevel(setter: (value: string) => void, value: string) { setter(value); setChecks([false, false, false]); setMessage(""); }
  function save() {
    if (!result.valid) return;
    props.onSavePlan({ ...input, id: crypto.randomUUID(), symbol: props.symbol, setup, notes, createdAt: new Date().toISOString() });
    setMessage("Plan saved on this browser. No order was sent.");
    setTab("saved");
  }
  function load(plan: StockPlan) {
    setEntry(String(plan.entry)); setStop(String(plan.stop)); setTarget(String(plan.target)); setAccount(String(plan.account));
    setCash(String(plan.cash)); setRiskPct(String(plan.riskPct)); setCost(String(plan.costPerShare)); setSetup(plan.setup); setNotes(plan.notes);
    setChecks([false, false, false]); setMessage("Saved levels loaded. Recheck them against the current Kraken quote."); setTab("plan");
  }
  return <section className="panel rounded-lg p-3 sm:p-4 min-w-0" aria-label="Stock trade planner">
    <div className="flex flex-wrap gap-2 items-center justify-between mb-3">
      <div><div className="eyebrow">Your trading notebook</div><h2 className="text-lg font-semibold">{props.symbol.split(":")[1]} · plan & review</h2></div>
      <span className="tag">Long only · manual order</span>
    </div>
    <div className="flex flex-wrap gap-2 mb-4" aria-label="Stock notebook views">
      <button className="control-button" aria-pressed={tab === "plan"} onClick={() => setTab("plan")}>Build plan</button>
      <button className="control-button" aria-pressed={tab === "saved"} onClick={() => setTab("saved")}>Saved plans ({symbolPlans.length})</button>
      <button className="control-button" aria-pressed={tab === "journal"} onClick={() => setTab("journal")}>Journal ({props.journal.length})</button>
    </div>
    {message && <p role="status" className="text-accent text-sm mb-3">{message}</p>}
    {tab === "plan" && <>
      <p className="text-ink-2 text-sm mb-4">Use a qualified scanner plan or enter your own levels. Adjust them to the current Kraken quote before sizing. Orders and fill checks remain manual.</p>
      <label className="text-sm text-ink-2 grid gap-1">Setup to track<select className="field !py-2" value={setup} onChange={e => { setSetup(e.target.value as StockPlan["setup"]); setChecks([false, false, false]); }}>
        {setups.map(s => <option key={s}>{s}</option>)}
      </select></label>
      <p className="text-ink-3 text-[12px] my-2">{setupNotes[setup]}</p>
      <div className="grid grid-cols-2 sm:grid-cols-3 gap-3 my-4">
        <NumberField label="Entry price ($)" value={entry} onChange={v => changeLevel(setEntry, v)} />
        <NumberField label="Stop reference ($)" value={stop} onChange={v => changeLevel(setStop, v)} />
        <NumberField label="Target price ($)" value={target} onChange={v => changeLevel(setTarget, v)} />
        <NumberField label="Account value ($)" value={account} onChange={v => changeLevel(setAccount, v)} />
        <NumberField label="Available settled cash ($)" value={cash} onChange={v => changeLevel(setCash, v)} />
        <NumberField label="Risk budget (%)" value={riskPct} onChange={v => changeLevel(setRiskPct, v)} />
        <NumberField label="Round-trip cost / share ($)" value={cost} onChange={v => changeLevel(setCost, v)} />
      </div>
      <p className="text-[12px] text-ink-3 mb-3">Cost is an editable spread, slippage and fee estimate, not a quoted Kraken fee. Sizing uses whole shares and caps both planned risk and cash. Stops and fills can differ from your references.</p>
      <div className="bg-ground border border-line-strong rounded-md p-3 mb-3" aria-live="polite">
        {result.valid ? <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm">
          <div><div className="eyebrow">Shares</div><strong className="num text-lg">{result.shares}</strong></div>
          <div><div className="eyebrow">Cash reserved</div><strong className="num">{usd(result.capital)}</strong></div>
          <div><div className="eyebrow">Planned risk</div><strong className="num text-warn">{usd(result.risk)}</strong></div>
          <div><div className="eyebrow">Reward / risk</div><strong className="num">{result.rewardRisk.toFixed(2)}R</strong></div>
          <p className="col-span-2 sm:col-span-4 text-ink-2">Estimated target profit {usd(result.reward)} after the cost buffer · risk budget {usd(result.riskBudget)}.</p>
        </div> : <p className="text-ink-2 text-sm">{result.reason ?? "Enter levels and a cash budget to calculate a position."}</p>}
      </div>
      <div className="grid gap-2 text-sm my-4">
        {["I checked the current Kraken price and spread.", "I observed the entry trigger and checked the news.", "I checked settled cash and my remaining risk budget."].map((label, index) => <label className="flex gap-2 items-start" key={label}><input className="mt-0.5" type="checkbox" checked={checks[index]} onChange={e => setChecks(old => old.map((v, i) => i === index ? e.target.checked : v))} />{label}</label>)}
        <p className={ready ? "text-accent" : "text-ink-3"}>{ready ? "Your manual checklist is complete. Recheck in Kraken when placing any order." : !props.confirmed ? "Confirm this stock appears in your Kraken Buy list above." : "Keep the plan as a draft until your manual checks are complete."}</p>
      </div>
      <label className="text-sm text-ink-2 grid gap-1">Catalyst, trigger & invalidation notes<textarea className="field min-h-20 !p-2" value={notes} maxLength={2000} onChange={e => setNotes(e.target.value)} /></label>
      <button className="control-button mt-3 disabled:opacity-40" disabled={!result.valid || props.plans.length >= 100} onClick={save}>Save plan</button>
      {props.plans.length >= 100 && <p className="text-warn text-xs mt-2">100-plan limit reached. Export your notebook, then remove old plans.</p>}
    </>}
    {tab === "saved" && <div className="grid gap-3">
      {!symbolPlans.length && <p className="text-ink-2 py-6">No saved plans for this stock. Build a plan using your entry, stop and target.</p>}
      {symbolPlans.map(plan => <article key={plan.id} className="border border-line-strong rounded-md p-3">
        <div className="flex flex-wrap gap-2 justify-between"><strong>{plan.setup}</strong><time className="text-ink-3 text-xs">{new Date(plan.createdAt).toLocaleString()}</time></div>
        <p className="num text-sm my-2">Entry {usd(plan.entry)} · Stop {usd(plan.stop)} · Target {usd(plan.target)}</p>
        <p className="text-ink-2 text-sm whitespace-pre-wrap break-words">{plan.notes}</p>
        <div className="flex flex-wrap gap-2 mt-3"><button className="control-button" onClick={() => load(plan)}>Load levels</button><button className="control-button" onClick={() => setLogging(plan)}>Log result</button><button className="control-button" onClick={() => props.onDeletePlan(plan.id)}>Remove plan</button></div>
      </article>)}
    </div>}
    {tab === "journal" && <>
      <div className="grid grid-cols-3 gap-2 bg-ground rounded-md p-3 mb-3"><div><div className="eyebrow">All stocks P/L</div><strong className={`num ${totalPnl < 0 ? "text-down" : "text-ink"}`}>{usd(totalPnl)}</strong></div><div><div className="eyebrow">Win rate</div><strong className="num">{props.journal.length ? `${Math.round(wins / props.journal.length * 100)}%` : "—"}</strong></div><div><div className="eyebrow">Logged trades</div><strong className="num">{props.journal.length}</strong></div></div>
      <p className="text-xs text-ink-3 mb-3">Your manually entered fills, stored on this browser. No broker sync or mark-to-market pricing. Log a result from a saved plan.</p>
      <div className="grid gap-3">{props.journal.map(trade => <article className="border border-line-strong rounded-md p-3" key={trade.id}>
        <div className="flex flex-wrap gap-2 justify-between"><strong>{trade.symbol.split(":")[1]} · {trade.setup}</strong><span className={`num ${tradePnl(trade) < 0 ? "text-down" : "text-up"}`}>{usd(tradePnl(trade))} · {tradeR(trade).toFixed(2)}R</span></div>
        <p className="text-ink-2 text-xs my-2">{trade.shares} shares · entry {usd(trade.entry)} → exit {usd(trade.exit)} · {new Date(trade.closedAt).toLocaleString()}</p>
        <p className="text-sm whitespace-pre-wrap break-words">{trade.notes}</p><button className="control-button mt-2" onClick={() => props.onDeleteTrade(trade.id)}>Remove journal entry</button>
      </article>)}</div>
    </>}
    {logging && <LogStockResult key={logging.id} plan={logging} full={props.journal.length >= 500} onClose={() => setLogging(null)} onSave={trade => { props.onAddTrade(trade); setLogging(null); setTab("journal"); setMessage("Result added to your manual journal."); }} />}
  </section>;
}

function LogStockResult({ plan, full, onClose, onSave }: { plan: StockPlan; full: boolean; onClose: () => void; onSave: (trade: StockTrade) => void }) {
  const [entry, setEntry] = useState("");
  const [exit, setExit] = useState("");
  const [shares, setShares] = useState("");
  const [cost, setCost] = useState("0");
  const [notes, setNotes] = useState("");
  const [closedAt, setClosedAt] = useState(() => {
    const now = new Date();
    const local = new Date(now.getTime() - now.getTimezoneOffset() * 60_000);
    return local.toISOString().slice(0, 16);
  });
  const closeTime = new Date(closedAt).getTime();
  const validTime = Number.isFinite(closeTime) && closeTime <= Date.now() + 60_000;
  const candidate: StockTrade = { id: "preview", planId: plan.id, symbol: plan.symbol, setup: plan.setup, entry: Number(entry), stop: plan.stop, exit: Number(exit), shares: Number(shares), costPerShare: Number(cost), notes, closedAt: validTime ? new Date(closeTime).toISOString() : "" };
  const valid = validTime && [candidate.entry, candidate.exit, candidate.shares].every(n => Number.isFinite(n) && n > 0) && candidate.entry > candidate.stop && Number.isFinite(candidate.costPerShare) && candidate.costPerShare >= 0 && Number.isFinite(tradePnl(candidate)) && Number.isFinite(tradeR(candidate));
  return <div className="border border-accent rounded-md p-3 mt-4 bg-navy-2" role="region" aria-label="Log stock trade result">
    <h3 className="font-semibold">Log completed trade · {plan.symbol.split(":")[1]}</h3>
    <p className="text-xs text-ink-2 my-2">Enter actual fills, not the plan&apos;s expected prices. The saved stop reference is {usd(plan.stop)}; entry must be above it for this long trade&apos;s R calculation.</p>
    <div className="grid grid-cols-2 gap-3"><NumberField label="Actual entry fill ($)" value={entry} onChange={setEntry} /><NumberField label="Actual exit fill ($)" value={exit} onChange={setExit} /><NumberField label="Shares closed" value={shares} onChange={setShares} /><NumberField label="Actual fees / share ($)" value={cost} onChange={setCost} /></div>
    <p className="text-xs text-ink-3 mt-2">Actual fills already include spread and slippage. Use only additional round-trip fees here.</p>
    <label className="text-sm text-ink-2 grid gap-1 mt-3">Trade closed at (your local time)<input className="field !py-2" type="datetime-local" value={closedAt} onChange={e => setClosedAt(e.target.value)} /></label>
    {!validTime && <p className="text-warn text-xs mt-2">Enter the actual close date and time, at or before now.</p>}
    <label className="text-sm text-ink-2 grid gap-1 mt-3">Review notes<textarea className="field !p-2" value={notes} maxLength={2000} onChange={e => setNotes(e.target.value)} /></label>
    {valid && <p className="num my-3">Net result {usd(tradePnl(candidate))} · {tradeR(candidate).toFixed(2)}R</p>}
    {full && <p className="text-warn">500-entry limit reached. Export and remove older entries first.</p>}
    <div className="flex gap-2 mt-3"><button className="control-button disabled:opacity-40" disabled={!valid || full} onClick={() => onSave({ ...candidate, id: crypto.randomUUID() })}>Save result</button><button className="control-button" onClick={onClose}>Cancel</button></div>
  </div>;
}
