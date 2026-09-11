"use client";
import { useEffect, useMemo, useState } from "react";
import { stockSetupIsCurrent, type StockScanResponse, type StockSetup } from "@/lib/stock-scanner";
import { rankStockOpportunities, type StockOpportunity } from "@/lib/stock-opportunities";
import type { StockItem } from "@/lib/stocks";
import type { StockRiskDraft } from "@/components/useStockRiskSettings";

const money = (value: number | null | undefined, digits = 4) => value == null || !Number.isFinite(value) ? "—" : value.toLocaleString("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: digits });
const decimal = (value: number | null | undefined, suffix = "", digits = 2) => value == null || !Number.isFinite(value) ? "—" : `${value.toFixed(digits)}${suffix}`;
type SortMode = "quality" | "target-profit" | "net-rr";

function briefNotes(item: StockOpportunity, asOf: string): string {
  const s = item.setup;
  return [
    `${s.ticker} · ${s.setup ?? "Setup pending"} · Alpaca IEX snapshot ${asOf}`,
    s.details?.thesis,
    `Conditional entry zone ${money(s.entry)}–${money(s.entryMax)}. Stop reference ${money(s.stop)}. 1R checkpoint ${money(item.target1)}. 2R planned target ${money(item.target2)}.`,
    s.details?.confirmation,
    s.details?.invalidation,
    `After cost buffer: ${decimal(item.netRewardRisk, "R")} reward/risk. Per share: ${money(item.rewardPerShare)} if target reached; ${money(item.riskPerShare)} planned loss at stop.`,
    item.position?.valid ? `${item.position.shares} whole shares; ${money(item.position.capital, 2)} cash reserved; ${money(item.position.reward, 2)} if all shares exit at target; ${money(item.position.risk, 2)} planned loss at stop.` : "Position size requires an account and settled-cash budget.",
    "Targets are payoff scenarios, not forecasts. Stops and fills can differ from references. Check Kraken price/availability and news; no stock order has been sent.",
  ].filter(Boolean).join("\n");
}

function Metric({ label, value, detail, color = "text-ink" }: { label: string; value: string; detail?: string; color?: string }) {
  return <div className="min-w-0"><div className="eyebrow">{label}</div><div className={`num text-lg sm:text-xl font-semibold mt-1 break-words ${color}`}>{value}</div>{detail && <p className="text-xs text-ink-3 mt-1">{detail}</p>}</div>;
}

export function StockOpportunityBoard({ opportunities, response, enabled, now, risk, onRiskChange, watchlist, onOpen, onUsePlan, onConfirm }: {
  opportunities: StockOpportunity[]; response: StockScanResponse | null; enabled: boolean; now: number;
  risk: StockRiskDraft; onRiskChange: React.Dispatch<React.SetStateAction<StockRiskDraft>>; watchlist: StockItem[];
  onOpen: (symbol: string) => void; onUsePlan: (setup: StockSetup, notes: string) => void; onConfirm: (symbol: string) => void;
}) {
  const [mode, setMode] = useState<SortMode>("quality");
  const [selected, setSelected] = useState<string | null>(() => rankStockOpportunities(opportunities, "quality")[0]?.setup.symbol ?? null);
  const [showAll, setShowAll] = useState(false);
  const [copied, setCopied] = useState("");
  // Ranking changes with market snapshots or deliberate settings edits, never the age ticker.
  const ranked = useMemo(() => rankStockOpportunities(opportunities, mode), [opportunities, mode]);
  useEffect(() => {
    if (!ranked.some(item => item.setup.symbol === selected)) setSelected(ranked[0]?.setup.symbol ?? null);
  }, [ranked, selected]);
  const focused = ranked.find(item => item.setup.symbol === selected) ?? ranked[0];
  const isCurrent = (item: StockOpportunity) => enabled && item.eligible && stockSetupIsCurrent(item.setup, response, now);
  const active = ranked.filter(isCurrent);
  const leader = active[0];
  const budgetReady = [risk.account, risk.cash, risk.riskPct].every(value => Number.isFinite(Number(value)) && Number(value) > 0);
  const visible = showAll ? ranked : ranked.slice(0, 6);
  const choose = (symbol: string) => { setSelected(symbol); setCopied(""); };
  const focusActive = focused && isCurrent(focused);
  const marketCurrent = focused && enabled && stockSetupIsCurrent(focused.setup, response, now);
  const brokerConfirmed = watchlist.find(item => item.symbol === focused?.setup.symbol)?.availability === "confirmed";
  const canUsePlan = focusActive && brokerConfirmed && focused.position?.valid !== false;
  const blockers = [...new Set(ranked.filter(item => !isCurrent(item)).flatMap(item =>
    item.setup.state === "entry-zone" ? [stockSetupIsCurrent(item.setup, response, now) ? item.reason : "A displayed entry has expired or its prices are no longer current. Waiting for fresh qualifying data."] : item.setup.reasons.filter(reason => !reason.includes("availability"))))].slice(0, 3);
  const data = focused?.setup;
  const details = data?.details;
  const position = focused?.position;
  function label(item: StockOpportunity): string {
    if (isCurrent(item)) return "Entry zone · verify Kraken";
    if (!enabled || (item.setup.state === "entry-zone" && !stockSetupIsCurrent(item.setup, response, now))) return "Reference only · recheck data";
    if (item.setup.state === "entry-zone") return "Wait · budget or costs";
    return item.setup.state === "unavailable" ? "Confirm Kraken availability" : item.setup.state === "extended" ? "Extended · wait for reset" : item.setup.state === "watch" ? "Watch · trigger pending" : "Wait · checks incomplete";
  }
  async function copyBrief() {
    if (!focused || !enabled || !focused.eligible || !stockSetupIsCurrent(focused.setup, response, Date.now()) || !response) return;
    try { await navigator.clipboard.writeText(briefNotes(focused, response.asOf)); setCopied("Trade brief copied. Recheck its timestamp before using it."); }
    catch { setCopied("Copy is unavailable in this browser. The full brief is shown here."); }
  }
  return <div className="mt-4 min-w-0">
    <div className="rounded-lg border border-line-strong bg-ground/60 p-3 sm:p-4 mb-4">
      <div className="flex flex-wrap gap-2 justify-between"><h3 className="font-semibold">Compare trades with your budget</h3><span className="text-xs text-ink-3">Saved on this browser · shared with your planner</span></div>
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3 mt-3">
        {([
          ["account", "Comparison account value ($)"], ["cash", "Comparison settled cash ($)"], ["riskPct", "Comparison risk budget (%)"], ["cost", "Comparison round-trip cost / share ($)"],
        ] as const).map(([key, title]) => <label className="text-xs text-ink-2 grid gap-1 min-w-0" key={key}>{title}<input type="number" min="0" max={key === "riskPct" ? 100 : undefined} step="any" className="field !py-2 num w-full min-w-0" value={risk[key]} onChange={e => onRiskChange(old => ({ ...old, [key]: e.target.value }))} placeholder={key === "account" || key === "cash" ? "Enter your amount" : undefined} /></label>)}
      </div>
      {(risk.cost.trim() === "" || !Number.isFinite(Number(risk.cost)) || Number(risk.cost) < 0) && <p role="alert" className="text-sm text-warn mt-2">Enter a round-trip cost per share to compare trades. <button className="underline" onClick={() => onRiskChange(old => ({ ...old, cost: "0.02" }))}>Restore $0.02 assumption</button></p>}
      <p className="text-xs text-ink-3 mt-3">{budgetReady ? "Whole-share scenarios use the top of each entry zone and cap both cash and planned risk. Each stock is a separate use of the same budget; these are alternatives, not combined positions." : "Enter account value and settled cash to compare position sizes and dollar outcomes. Until then, compare potential reward and risk per share."} Cost is your editable round-trip spread, slippage and fee buffer; the starting $0.02/share is an assumption.</p>
    </div>

    <div className="flex flex-wrap gap-3 items-end justify-between mb-3">
      <div><div className="eyebrow !text-accent">Opportunity shortlist</div><h3 className="text-xl font-semibold mt-1">{active.length ? `${active.length} setup${active.length === 1 ? " clears" : "s clear"} the current checks` : "No trade clears the current checks"}</h3><p className="text-xs text-ink-2 mt-1">{leader ? `${leader.setup.ticker} leads this comparison. ${leader.rankReason}` : "Inspect the developing ideas below. A strong score alone does not create a valid entry."}</p></div>
      <label className="text-xs text-ink-2 grid gap-1">Rank opportunities by<select className="field !py-2" value={mode} onChange={e => setMode(e.target.value as SortMode)}><option value="quality">Setup strength</option><option value="net-rr">Reward / risk after costs</option><option value="target-profit" disabled={!budgetReady}>Profit if target reached</option></select></label>
    </div>
    <p className="text-xs text-ink-3 mb-3">All visible watchlist stocks can qualify for research. Confirm availability in your Kraken account before using a plan. Completed triggers stay under review for up to three minutes while their original price limits hold.</p>
    {!active.length && enabled && blockers.length > 0 && <div role="status" className="border border-warn/30 rounded-lg p-3 mb-3"><p className="text-sm font-semibold">What is holding back entries</p><ul className="list-disc pl-4 text-xs text-ink-2 mt-2 space-y-1">{blockers.map(reason => <li key={reason}>{reason}</li>)}</ul></div>}
    {mode === "target-profit" && <p className="text-xs text-warn mb-3">This sort compares the result if every planned target is reached. It does not estimate which target is most likely to be reached or which stock will make the most money.</p>}
    <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3 mb-3">
      {visible.map((item, index) => <button key={item.setup.symbol} onClick={() => choose(item.setup.symbol)} aria-pressed={focused?.setup.symbol === item.setup.symbol} aria-label={`Read ${item.setup.ticker} trade brief`} className={`text-left rounded-lg border p-3 min-w-0 ${focused?.setup.symbol === item.setup.symbol ? "border-accent bg-accent/5" : "border-line-strong bg-ground/40"}`}>
        <div className="flex gap-2 justify-between"><span className="font-semibold text-lg"><span className="text-ink-3 num text-xs mr-2">{String(index + 1).padStart(2, "0")}</span>{item.setup.ticker}</span><span className="text-xs text-ink-2 num">{item.setup.score}/100</span></div>
        <p className={`text-xs mt-1 ${isCurrent(item) ? "text-accent" : "text-warn"}`}>{label(item)}</p><p className="text-xs text-ink-2 mt-1">{item.setup.setup ?? "Building price structure"}</p>
        {isCurrent(item) && item.position?.valid === false && <p className="text-xs text-warn mt-2">Setup qualifies · budget too small for one share</p>}
        <div className="grid grid-cols-2 gap-2 mt-3"><div className="text-xs text-ink-3">Net reward/risk<strong className="num block text-sm text-ink-2">{isCurrent(item) ? decimal(item.netRewardRisk, "R") : "—"}</strong></div><div className="text-xs text-ink-3">{budgetReady ? "If target reached" : "Target reward / share"}<strong className="num block text-sm text-ink-2">{isCurrent(item) ? money(budgetReady ? item.position?.valid ? item.position.reward : null : item.rewardPerShare, budgetReady ? 2 : 4) : "—"}</strong></div></div>
      </button>)}
    </div>
    {ranked.length > 6 && <button className="control-button mb-4" onClick={() => setShowAll(value => !value)}>{showAll ? "Show top 6 stocks" : `Show all ${ranked.length} scanned stocks`}</button>}

    {focused && data && <article className={`rounded-lg border overflow-hidden ${focusActive ? "border-accent/60" : "border-line-strong"}`} aria-label={`${data.ticker} detailed trade brief`}>
      <div className="p-4 sm:p-5 bg-navy-2">
        <div className="flex flex-wrap items-start justify-between gap-3"><div><div className="eyebrow !text-accent">Selected trade brief</div><h3 className="text-2xl sm:text-3xl font-semibold mt-1">{data.ticker} <span className="text-sm text-ink-2 font-normal">{data.name}</span></h3><p className="text-sm text-ink-2 mt-1">{data.setup ?? "Developing setup"} · {details?.trendLabel ?? "Trend still being assessed"}</p></div><div className="text-right"><div className="num text-2xl">{money(data.price)}</div><p className="text-xs text-ink-3">IEX trade · {decimal(data.changePct, "%")} from prior close</p></div></div>
        <p className={`text-sm mt-3 ${focusActive ? "text-accent" : "text-warn"}`}>{label(focused)}{!focusActive && focused.reason ? ` · ${focused.reason}` : ""}</p>
        <p className="text-base text-ink mt-3 leading-relaxed">{details?.thesis ?? data.reasons[0] ?? "Waiting for enough completed price evidence."}</p>
        <div className="flex flex-wrap gap-2 mt-4"><button className="control-button" onClick={() => onOpen(data.symbol)}>Review chart & news</button>{focusActive && <>{canUsePlan && <button className="control-button !border-accent text-accent" onClick={() => onUsePlan(data, briefNotes(focused, response!.asOf))}>Use entry plan</button>}<button className="control-button" onClick={() => void copyBrief()}>Copy trade brief</button></>}{watchlist.find(s => s.symbol === data.symbol)?.availability === "unconfirmed" && <button className="control-button" onClick={() => onConfirm(data.symbol)}>I can buy this in Kraken</button>}{leader && leader.setup.symbol !== data.symbol && <button className="control-button" onClick={() => choose(leader.setup.symbol)}>Read leading setup · {leader.setup.ticker}</button>}</div>
        {focusActive && !brokerConfirmed && <p className="text-xs text-warn mt-3">This market setup qualifies. Confirm you can buy {data.ticker} in Kraken to enable its entry plan.</p>}
        {focusActive && position?.valid === false && <p className="text-xs text-warn mt-3">{focused.reason}</p>}
        {copied && <p className="text-xs text-accent mt-2" role="status">{copied}</p>}
      </div>
      <div className="grid lg:grid-cols-2 divide-y lg:divide-y-0 lg:divide-x divide-line-strong">
        <div className="p-4 sm:p-5 min-w-0">
          <h4 className="font-semibold mb-3">Entry and exit map</h4>
          {data.entry != null ? <><p className="text-xs text-ink-3 mb-3">{focusActive ? "Conditional plan from the current scan" : "Captured reference plan · not currently actionable"}. Size at the upper entry price. Targets are planned checkpoints, not forecasts.</p><div className="grid grid-cols-2 gap-x-4 gap-y-5"><Metric label="Entry zone starts" value={money(data.entry)} color="text-accent" /><Metric label="Entry ceiling" value={money(data.entryMax)} detail="Reassess above this price" color="text-accent" /><Metric label="Stop reference" value={money(data.stop)} detail="Planned invalidation, not a guaranteed fill" color="text-down" /><Metric label="1R checkpoint" value={money(details?.target1)} detail="Gross reward equals planned price risk" /><Metric label="2R planned target" value={money(data.target)} detail="Twice planned price risk before costs" color="text-up" /><Metric label="Move to target" value={decimal(focused.targetMovePct, "%")} detail="From the entry ceiling" /></div></> : <p className="text-sm text-ink-2">Entry, stop and targets appear only after a completed trigger, current quotes, and market-data checks pass.{details?.triggerPrice != null ? ` The level being watched is ${money(details.triggerPrice)}; this is a reference, not an entry instruction.` : ""}</p>}
          <div className="rounded-md bg-ground/60 border border-line p-3 mt-5"><h5 className="text-sm font-semibold">What must happen</h5><p className="text-sm text-ink-2 mt-2">{details?.confirmation ?? data.reasons[0]}</p></div>
          <div className="rounded-md bg-down/5 border border-down/20 p-3 mt-3"><h5 className="text-sm font-semibold text-down">When the idea fails</h5><p className="text-sm text-ink-2 mt-2">{details?.invalidation ?? "Wait for a fresh setup; the current checks are incomplete."}</p></div>
          {!!details?.levels.length && <details className="mt-4"><summary className="cursor-pointer text-sm text-ink-2">Observed chart levels</summary><dl className="grid grid-cols-2 gap-2 mt-3 text-xs">{details.levels.map((level, i) => <div key={`${level.label}:${i}`} className="flex flex-wrap justify-between gap-1 rounded border border-line p-2"><dt className="text-ink-3">{level.label}</dt><dd className="num">{money(level.price)}</dd></div>)}</dl><p className="text-xs text-ink-3 mt-2">Completed IEX bar references. A past high or low need not hold as resistance or support.</p></details>}
        </div>
        <div className="p-4 sm:p-5 min-w-0">
          <h4 className="font-semibold mb-3">Reward, risk and trade-offs</h4>
          {marketCurrent && focused.netRewardRisk !== null ? <><div className="grid grid-cols-2 gap-4"><Metric label="Net reward / risk" value={decimal(focused.netRewardRisk, "R")} detail="After your round-trip cost buffer" color="text-accent" /><Metric label="Required break-even win rate" value={decimal(focused.breakEvenWinRate, "%", 1)} detail="Binary target/stop math, not a predicted win rate" /><Metric label="Target reward / share" value={money(focused.rewardPerShare)} detail="If the full target is reached" color="text-up" /><Metric label="Planned loss / share" value={money(focused.riskPerShare)} detail="At the stop, including cost buffer" color="text-down" /></div>
          {position?.valid ? <div className="rounded-lg border border-accent/30 bg-accent/5 p-3 mt-5"><div className="eyebrow !text-accent">Your budget scenario</div><div className="grid grid-cols-2 gap-4 mt-3"><Metric label="Whole shares" value={String(position.shares)} /><Metric label="Cash reserved" value={money(position.capital, 2)} /><Metric label="If target reached" value={money(position.reward, 2)} color="text-up" /><Metric label="Planned loss at stop" value={money(position.risk, 2)} color="text-down" /></div><p className="text-xs text-ink-2 mt-3">Assumes the entire position exits at the 2R target or stop reference, with your cost buffer. Partial exits change the result. Actual fills and losses can differ.</p></div> : <p className="text-sm text-ink-2 mt-4">{position?.reason ?? "Add your account value and settled cash above to calculate shares and dollar scenarios."}</p>}</> : <p className="text-sm text-ink-2">{focused.reason || "Payoff comparisons need an eligible setup and fresh data."} New entry actions are unavailable until all checks pass.</p>}
          <div className="grid grid-cols-3 gap-2 border-y border-line py-3 mt-5"><div className="text-xs text-ink-3">IEX minute volume<strong className="num block text-ink-2 mt-1">{decimal(data.relativeVolume, "×")}</strong></div><div className="text-xs text-ink-3">IEX spread<strong className="num block text-ink-2 mt-1">{decimal(data.spreadPct, "%", 3)}</strong></div><div className="text-xs text-ink-3">Est. session VWAP<strong className="num block text-ink-2 mt-1">{money(data.vwap)}</strong></div></div>
          <p className="text-xs text-ink-3 mt-2">Volume compares the last closed IEX minute with the prior 20 observed bars. It is not whole-market daily relative volume.</p>
          <h5 className="font-semibold text-sm mt-5">Reasons to be cautious</h5><ul className="list-disc pl-4 text-sm text-ink-2 mt-2 space-y-2">{[...new Set([...(details?.cautions ?? []), ...(!focusActive ? data.reasons : []), "News and earnings catalysts are not verified by this price scanner. Review the news before deciding."])].slice(0, 7).map((reason, i) => <li key={i}>{reason}</li>)}</ul>
        </div>
      </div>
      <details className="border-t border-line-strong p-4 sm:p-5"><summary className="cursor-pointer font-semibold">Why this score · {data.score}/100 evidence points</summary><p className="text-xs text-ink-3 mt-2">Points describe observed conditions. They are not a success probability. Opportunity comparisons also require at least 1.5R after your cost buffer.</p><div className="grid sm:grid-cols-2 gap-x-6 gap-y-4 mt-4">{details?.scoreFactors.map(factor => <div key={factor.label}><div className="flex justify-between text-sm gap-2"><strong>{factor.label}</strong><span className="num text-ink-2">{factor.earned}/{factor.possible}</span></div><div className="h-1 bg-ground rounded mt-2 overflow-hidden"><div className="h-full bg-accent" style={{ width: `${factor.possible ? factor.earned / factor.possible * 100 : 0}%` }} /></div><p className="text-xs text-ink-3 mt-2">{factor.detail}</p></div>)}</div><ul className="list-disc pl-4 text-xs text-ink-3 space-y-1 mt-4">{data.evidence.map((text, i) => <li key={i}>{text}</li>)}</ul></details>
    </article>}
    <p className="text-xs text-ink-3 mt-3">The selected brief stays selected through refreshes. Use Pause to read a captured plan; age checks still disable old entries. All orders remain manual in Kraken.</p>
  </div>;
}
