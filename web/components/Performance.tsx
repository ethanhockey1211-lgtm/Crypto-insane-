"use client";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fmtPct, fmtPrice, fmtTime, setupLabel } from "@/lib/format";
import type { PerformanceBucket, PerformanceReport, SignalWithOutcome } from "@/lib/types";

function pct(v: number | null | undefined, d = 0) { return v == null ? "—" : `${(v * 100).toFixed(d)}%`; }
function num(v: number | null | undefined, d = 2) { return v == null ? "—" : v.toFixed(d); }

export function BucketTable({ title, rows, labelFn }: { title: string; rows: PerformanceBucket[]; labelFn?: (k: string) => string }) {
  return (
    <div className="px-3 py-2 border-b border-line">
      <h3 className="eyebrow mb-1">{title}</h3>
      {rows.length === 0 ? <p className="text-ink-3 text-[12px]">No signals yet.</p> : (
        <table className="w-full text-[12px]">
          <thead><tr className="eyebrow text-left"><th className="font-normal py-1">Bucket</th><th className="font-normal text-right">Signals</th><th className="font-normal text-right">Done</th><th className="font-normal text-right">T1 before stop</th><th className="font-normal text-right">Stopped</th><th className="font-normal text-right">Avg R</th><th className="font-normal text-right">PF</th><th className="font-normal text-right">5m</th><th className="font-normal text-right">15m</th><th className="font-normal text-right">1h</th><th className="font-normal text-right">MFE</th><th className="font-normal text-right">MAE</th></tr></thead>
          <tbody>
            {rows.map((b) => (
              <tr key={b.key} className="border-t border-line/50 h-7">
                <td>{labelFn ? labelFn(b.key) : b.key}</td>
                <td className="num text-right">{b.signals}</td>
                <td className="num text-right text-ink-2">{b.completed}</td>
                <td className={`num text-right ${b.targetBeforeStopRate != null && b.targetBeforeStopRate >= 0.5 ? "up" : ""}`}>{pct(b.targetBeforeStopRate)}</td>
                <td className="num text-right">{pct(b.stopRate)}</td>
                <td className={`num text-right ${b.avgR == null ? "" : b.avgR > 0 ? "up" : "down"}`}>{num(b.avgR)}</td>
                <td className="num text-right">{num(b.profitFactor)}</td>
                <td className={`num text-right ${b.avgRet5m == null ? "" : b.avgRet5m > 0 ? "up" : "down"}`}>{fmtPct(b.avgRet5m)}</td>
                <td className={`num text-right ${b.avgRet15m == null ? "" : b.avgRet15m > 0 ? "up" : "down"}`}>{fmtPct(b.avgRet15m)}</td>
                <td className={`num text-right ${b.avgRet1h == null ? "" : b.avgRet1h > 0 ? "up" : "down"}`}>{fmtPct(b.avgRet1h)}</td>
                <td className="num text-right up">{fmtPct(b.avgMfe)}</td>
                <td className="num text-right down">{fmtPct(b.avgMae)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

export function Performance({ onOpen }: { onOpen: (s: string) => void }) {
  const [report, setReport] = useState<PerformanceReport | null>(null);
  const [signals, setSignals] = useState<SignalWithOutcome[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const load = async () => {
      const [r, s] = await Promise.allSettled([api.performance.report(), api.performance.signals(150)]);
      if (r.status === "fulfilled") { setReport(r.value); setError(null); } else setError(`Performance unavailable: ${(r.reason as Error).message}`);
      if (s.status === "fulfilled") setSignals(s.value);
    };
    void load();
    const id = setInterval(load, 10_000);
    return () => clearInterval(id);
  }, []);
  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-3 px-3 h-9 border-b border-line text-[11px]">
        <span className="eyebrow">Signal performance</span>
        {report && <span className="num text-ink-3">{report.overall.signals} signals · {report.overall.completed} completed · config v{report.configVersion}</span>}
        {report && <span className="ml-auto text-ink-2">{report.note}</span>}
      </div>
      {error && <div className="px-3 py-1.5 warn text-[11.5px] border-b border-line">{error}</div>}
      <div className="overflow-auto min-h-0 flex-1">
        {report && (
          <>
            <BucketTable title="By score bucket" rows={report.byScoreBucket} />
            <BucketTable title="By setup" rows={report.bySetup} labelFn={setupLabel} />
            <BucketTable title="By market regime" rows={report.byRegime} />
            <BucketTable title="By confidence" rows={report.byConfidence} />
            <BucketTable title="By coin" rows={report.bySymbol} />
          </>
        )}
        <div className="px-3 py-2">
          <h3 className="eyebrow mb-1">Recent signals</h3>
          {signals.length === 0 ? <p className="text-ink-3 text-[12px]">Signals are recorded when a setup scores at or above the threshold, whether or not anyone trades them.</p> : (
            <table className="w-full text-[12px]">
              <thead><tr className="eyebrow text-left"><th className="font-normal py-1">Time</th><th className="font-normal">Symbol</th><th className="font-normal">Setup</th><th className="font-normal text-right">Score</th><th className="font-normal text-right">Price</th><th className="font-normal text-right">Stop</th><th className="font-normal text-right">T1</th><th className="font-normal">Outcome</th><th className="font-normal text-right">R</th><th className="font-normal text-right">15m</th><th className="font-normal text-right">1h</th><th className="font-normal text-right">MFE</th><th className="font-normal text-right">MAE</th><th className="font-normal">Regime</th></tr></thead>
              <tbody>
                {signals.map(({ signal: s, outcome: o }) => (
                  <tr key={s.id} className="border-t border-line/50 h-7 row-hover cursor-pointer" onClick={() => onOpen(s.symbol)}>
                    <td className="num text-ink-3">{fmtTime(s.at)}</td>
                    <td className="font-medium">{s.symbol.replace("-USD", "")}</td>
                    <td>{setupLabel(s.setup)}{s.doNotChase ? <span className="tag tag-warn ml-1">dnc</span> : null}</td>
                    <td className="num text-right">{s.score.toFixed(0)}</td>
                    <td className="num text-right">{fmtPrice(s.price)}</td>
                    <td className="num text-right text-down/80">{fmtPrice(s.stop)}</td>
                    <td className="num text-right text-up/80">{fmtPrice(s.target1)}</td>
                    <td className={o.firstEvent === "stop" ? "down" : o.firstEvent.startsWith("t") ? "up" : "text-ink-3"}>{o.complete ? o.firstEvent : `${o.firstEvent} · tracking`}</td>
                    <td className={`num text-right ${o.r == null ? "text-ink-3" : o.r > 0 ? "up" : "down"}`}>{num(o.r)}</td>
                    <td className="num text-right">{fmtPct(o.ret15m)}</td>
                    <td className="num text-right">{fmtPct(o.ret1h)}</td>
                    <td className="num text-right up">{fmtPct(o.mfe)}</td>
                    <td className="num text-right down">{fmtPct(o.mae)}</td>
                    <td className="text-ink-2">{s.regime}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </section>
  );
}
