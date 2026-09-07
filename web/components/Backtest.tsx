"use client";
import { useState } from "react";
import { api } from "@/lib/api";
import { fmtPct, fmtPrice, fmtTime, setupLabel } from "@/lib/format";
import type { BacktestResult } from "@/lib/types";
import { BucketTable } from "./Performance";

export function Backtest({ onOpen }: { onOpen: (s: string) => void }) {
  const [symbols, setSymbols] = useState("XRP-USD, SOL-USD");
  const [days, setDays] = useState(3);
  const [fee, setFee] = useState(10);
  const [slip, setSlip] = useState(5);
  const [spread, setSpread] = useState(4);
  const [threshold, setThreshold] = useState(60);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<BacktestResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setRunning(true); setError(null);
    try {
      const list = symbols.split(/[,\s]+/).map((s) => s.trim().toUpperCase()).filter(Boolean).map((s) => (s.includes("-") ? s : `${s}-USD`));
      setResult(await api.backtest({ symbols: list, days, feeBps: fee, slippageBps: slip, spreadBps: spread, recordThreshold: threshold, includeBtc: true }));
    } catch (e) { setError((e as Error).message); }
    finally { setRunning(false); }
  };

  const Num = ({ label, value, set, step = 1 }: { label: string; value: number; set: (v: number) => void; step?: number }) => (
    <label className="flex flex-col gap-0.5"><span className="eyebrow">{label}</span><input className="field num" type="number" step={step} value={value} onChange={(e) => set(parseFloat(e.target.value))} /></label>
  );

  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-3 px-3 h-9 border-b border-line">
        <span className="eyebrow">Backtest</span>
        <span className="text-[11px] text-ink-3 hidden lg:inline">Replays 1m history through the same analytics, breakout, scoring, and outcome code as the live scanner. History comes from the exchange REST API.</span>
      </div>
      <div className="px-3 py-2 border-b border-line grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-[2fr_repeat(5,minmax(0,1fr))_auto] gap-2 items-end">
        <label className="flex flex-col gap-0.5"><span className="eyebrow">Symbols (max 5, BTC added for context)</span><input className="field" value={symbols} onChange={(e) => setSymbols(e.target.value)} /></label>
        <Num label="Days (1–14)" value={days} set={setDays} />
        <Num label="Fee bps" value={fee} set={setFee} />
        <Num label="Slippage bps" value={slip} set={setSlip} />
        <Num label="Spread bps" value={spread} set={setSpread} />
        <Num label="Record ≥ score" value={threshold} set={setThreshold} />
        <button className="px-3 py-1 bg-navy-3 rounded-[3px] text-[12px] disabled:opacity-50" disabled={running} onClick={run}>{running ? "Running…" : "Run"}</button>
      </div>
      {error && <div className="px-3 py-1.5 warn text-[11.5px] border-b border-line">{error}</div>}
      <div className="overflow-auto min-h-0 flex-1">
        <p className="px-4 py-3 text-[14px] text-ink-2 border-b border-line">Historical signal replay, not a validated trading strategy. R values use the simulated fill and costs on both sides. Target-hit rates and price returns remain gross. This replay does not filter by the live execution checks, model order-book depth, or prove out-of-sample profitability.</p>
        {result && (
          <>
            <div className="px-3 py-2 border-b border-line text-[11.5px] text-ink-2">
              <span className="num text-ink">{result.barsProcessed.toLocaleString()}</span> bars · <span className="num text-ink">{result.evaluations.toLocaleString()}</span> evaluations · <span className="num text-ink">{result.signals.length}</span> signals · {result.duration}
              <ul className="mt-1 text-ink-3">{result.notes.map((n, i) => <li key={i}>· {n}</li>)}</ul>
              <p className="mt-1">{result.net.note}</p>
            </div>
            <BucketTable title="Net of costs · overall" rows={[result.net.overall]} />
            <BucketTable title="Gross · overall" rows={[result.gross.overall]} />
            <BucketTable title="Net · by score bucket" rows={result.net.byScoreBucket} />
            <BucketTable title="Net · by setup" rows={result.net.bySetup} labelFn={setupLabel} />
            <BucketTable title="Net · by regime" rows={result.net.byRegime} />
            <div className="px-3 py-2">
              <h3 className="eyebrow mb-1">Signals</h3>
              <table className="w-full text-[12px]">
                <thead><tr className="eyebrow text-left"><th className="font-normal py-1">Time</th><th className="font-normal">Symbol</th><th className="font-normal">Setup</th><th className="font-normal text-right">Score</th><th className="font-normal text-right">Entry</th><th className="font-normal text-right">Stop</th><th className="font-normal text-right">T1</th><th className="font-normal">First</th><th className="font-normal text-right">R</th><th className="font-normal text-right">1h</th><th className="font-normal text-right">MFE</th><th className="font-normal text-right">MAE</th></tr></thead>
                <tbody>
                  {result.signals.map(({ signal: s, outcome: o }) => (
                    <tr key={s.id} className="border-t border-line/50 h-7 row-hover cursor-pointer" onClick={() => onOpen(s.symbol)}>
                      <td className="num text-ink-3">{fmtTime(s.at)}</td><td className="font-medium">{s.symbol.replace("-USD", "")}</td><td>{setupLabel(s.setup)}</td>
                      <td className="num text-right">{s.score.toFixed(0)}</td><td className="num text-right">{fmtPrice(s.price)}</td><td className="num text-right text-down/80">{fmtPrice(s.stop)}</td><td className="num text-right text-up/80">{fmtPrice(s.target1)}</td>
                      <td className={o.firstEvent === "stop" ? "down" : o.firstEvent.startsWith("t") ? "up" : "text-ink-3"}>{o.firstEvent}{o.complete ? "" : " (incomplete)"}</td>
                      <td className={`num text-right ${o.r == null ? "text-ink-3" : o.r > 0 ? "up" : "down"}`}>{o.r?.toFixed(2) ?? "—"}</td>
                      <td className="num text-right">{fmtPct(o.ret1h)}</td><td className="num text-right up">{fmtPct(o.mfe)}</td><td className="num text-right down">{fmtPct(o.mae)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
        {!result && !error && <p className="px-3 py-6 text-ink-3 text-[12px]">Pick a few symbols and a window, then run. Results are descriptive: they measure this scoring configuration on that window with the stated cost assumptions.</p>}
      </div>
    </section>
  );
}
