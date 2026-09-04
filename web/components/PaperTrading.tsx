"use client";
import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { usePaperVersion } from "@/lib/store";
import { fmtMoney, fmtPct, fmtPrice, fmtTime } from "@/lib/format";
import type { PaperAccountView, PaperOrder, PaperPosition, PaperPositionView, PaperStats } from "@/lib/types";

function Money({ v }: { v: number | null | undefined }) {
  return <span className={`num ${v == null ? "text-ink-3" : v > 0 ? "up" : v < 0 ? "down" : ""}`}>{fmtMoney(v)}</span>;
}

export function PaperTrading({ onOpen }: { onOpen: (s: string) => void }) {
  const version = usePaperVersion();
  const [account, setAccount] = useState<PaperAccountView | null>(null);
  const [positions, setPositions] = useState<PaperPositionView[]>([]);
  const [orders, setOrders] = useState<PaperOrder[]>([]);
  const [trades, setTrades] = useState<PaperPosition[]>([]);
  const [stats, setStats] = useState<PaperStats | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    const [a, p, o, t, s] = await Promise.allSettled([api.paper.account(), api.paper.positions(), api.paper.orders(), api.paper.trades(), api.paper.stats()]);
    if (a.status === "fulfilled") setAccount(a.value); else setError(`Paper trading unavailable: ${(a.reason as Error).message}`);
    if (p.status === "fulfilled") setPositions(p.value);
    if (o.status === "fulfilled") setOrders(o.value);
    if (t.status === "fulfilled") setTrades(t.value);
    if (s.status === "fulfilled") setStats(s.value);
  }, []);
  useEffect(() => { void load(); const id = setInterval(load, 3000); return () => clearInterval(id); }, [load, version]);

  const close = async (symbol: string, qty: number) => {
    try { await api.paper.place({ symbol, side: "Sell", quantity: qty, notional: null, stopPrice: null, takeProfitPrice: null, note: "close from positions" }); await load(); }
    catch (e) { setError((e as Error).message); }
  };
  const cancel = async (id: string) => { try { await api.paper.cancel(id); await load(); } catch (e) { setError((e as Error).message); } };
  const reset = async () => {
    if (!window.confirm("Reset the paper account? Open positions are abandoned and history stays.")) return;
    try { await api.paper.reset(account?.account.startingBalance ?? 10000); await load(); } catch (e) { setError((e as Error).message); }
  };

  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-4 px-3 h-9 border-b border-line text-[11px]">
        <span className="eyebrow">Paper trading</span>
        {account && (
          <>
            <span>equity <span className="num text-ink">{fmtMoney(account.equity)}</span></span>
            <span>cash <span className="num text-ink">{fmtMoney(account.account.cash)}</span></span>
            <span>open <Money v={account.unrealizedPnl} /></span>
            <span>realized <Money v={account.realizedPnl} /></span>
            <span className="text-ink-3">fees {account.account.feeBps} bps · slippage {account.account.slippageBps} bps · simulated fills, no exchange orders</span>
          </>
        )}
        <button className="ml-auto text-ink-3 hover:text-ink" onClick={reset}>reset account</button>
      </div>
      {error && <div className="px-3 py-1.5 warn text-[11.5px] border-b border-line">{error}</div>}
      <div className="overflow-auto min-h-0 flex-1 text-[12px]">
        <div className="px-3 py-2 border-b border-line">
          <h3 className="eyebrow mb-1">Open positions</h3>
          {positions.length === 0 && <p className="text-ink-3">None. Use “Paper buy” in a setup card; the calculator’s size, stop and target become a bracket order.</p>}
          {positions.length > 0 && (
            <table className="w-full">
              <thead><tr className="eyebrow text-left"><th className="font-normal py-1">Symbol</th><th className="font-normal text-right">Qty</th><th className="font-normal text-right">Entry</th><th className="font-normal text-right">Last</th><th className="font-normal text-right">Unrealized</th><th className="font-normal text-right">MFE</th><th className="font-normal text-right">MAE</th><th className="font-normal">Setup · score</th><th className="font-normal">Opened</th><th /></tr></thead>
              <tbody>
                {positions.map(({ position: p, lastPrice, unrealizedPnl }) => (
                  <tr key={p.id} className="row-hover border-t border-line/50 h-7">
                    <td className="font-medium cursor-pointer" onClick={() => onOpen(p.symbol)}>{p.symbol.replace("-USD", "")}</td>
                    <td className="num text-right">{p.quantity.toLocaleString("en-US", { maximumFractionDigits: 6 })}</td>
                    <td className="num text-right">{fmtPrice(p.avgEntry)}</td>
                    <td className="num text-right">{fmtPrice(lastPrice)}</td>
                    <td className="text-right"><Money v={unrealizedPnl} /></td>
                    <td className="num text-right up">{fmtPct(p.mfePct)}</td>
                    <td className="num text-right down">{fmtPct(p.maePct)}</td>
                    <td className="text-ink-2">{p.setupAtEntry ?? "—"} · {p.scoreAtEntry?.toFixed(0) ?? "—"} · {p.regimeAtEntry ?? "—"}</td>
                    <td className="num text-ink-3">{fmtTime(p.openedAt)}</td>
                    <td className="text-right"><button className="text-[11px] px-2 py-0.5 bg-navy-3 rounded-[3px]" onClick={() => close(p.symbol, p.quantity)}>Close</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
        <div className="grid md:grid-cols-2">
          <div className="px-3 py-2 border-b md:border-r border-line">
            <h3 className="eyebrow mb-1">Working orders</h3>
            {orders.filter((o) => o.status === "Open").length === 0 && <p className="text-ink-3">No stops or take-profits working.</p>}
            <ul>
              {orders.filter((o) => o.status === "Open").map((o) => (
                <li key={o.id} className="flex gap-2 items-center py-0.5">
                  <span className="font-medium w-14">{o.symbol.replace("-USD", "")}</span>
                  <span className={o.type === "Stop" ? "down" : "up"}>{o.type === "Stop" ? "stop" : "take profit"}</span>
                  <span className="num">{fmtPrice(o.triggerPrice)}</span>
                  <span className="num text-ink-3">× {o.quantity.toLocaleString("en-US", { maximumFractionDigits: 6 })}</span>
                  <button className="ml-auto text-ink-3 hover:text-down" onClick={() => cancel(o.id)}>cancel</button>
                </li>
              ))}
            </ul>
          </div>
          <div className="px-3 py-2 border-b border-line">
            <h3 className="eyebrow mb-1">Performance</h3>
            {stats && stats.trades > 0 ? (
              <div className="grid grid-cols-2 md:grid-cols-4 gap-x-3 gap-y-1">
                <div><div className="eyebrow">Trades</div><div className="num">{stats.trades}</div></div>
                <div><div className="eyebrow">Win rate</div><div className="num">{(stats.winRate * 100).toFixed(0)}%</div></div>
                <div><div className="eyebrow">Profit factor</div><div className="num">{stats.profitFactor?.toFixed(2) ?? "—"}</div></div>
                <div><div className="eyebrow">Avg R</div><div className="num">{stats.avgR?.toFixed(2) ?? "—"}</div></div>
                <div><div className="eyebrow">Total P/L</div><Money v={stats.totalPnl} /></div>
                <div><div className="eyebrow">Avg P/L</div><Money v={stats.avgPnl} /></div>
                <div className="col-span-2"><div className="eyebrow">By setup</div><div className="text-[11px] text-ink-2">{stats.bySetup.map((b) => `${b.key} ${b.trades}t ${(b.winRate * 100).toFixed(0)}%${b.avgR != null ? ` ${b.avgR.toFixed(1)}R` : ""}`).join(" · ")}</div></div>
              </div>
            ) : <p className="text-ink-3">No closed trades yet.</p>}
          </div>
        </div>
        <div className="px-3 py-2">
          <h3 className="eyebrow mb-1">Closed trades</h3>
          {trades.length === 0 && <p className="text-ink-3">None yet.</p>}
          {trades.length > 0 && (
            <table className="w-full">
              <thead><tr className="eyebrow text-left"><th className="font-normal py-1">Symbol</th><th className="font-normal text-right">Entry</th><th className="font-normal text-right">P/L</th><th className="font-normal text-right">R</th><th className="font-normal text-right">MFE</th><th className="font-normal text-right">MAE</th><th className="font-normal">Exit</th><th className="font-normal">Setup · score · regime</th><th className="font-normal">Closed</th></tr></thead>
              <tbody>
                {trades.map((p) => (
                  <tr key={p.id} className="border-t border-line/50 h-7">
                    <td className="font-medium cursor-pointer" onClick={() => onOpen(p.symbol)}>{p.symbol.replace("-USD", "")}</td>
                    <td className="num text-right">{fmtPrice(p.avgEntry)}</td>
                    <td className="text-right"><Money v={p.realizedPnl} /></td>
                    <td className={`num text-right ${p.rMultiple == null ? "text-ink-3" : p.rMultiple > 0 ? "up" : "down"}`}>{p.rMultiple?.toFixed(2) ?? "—"}</td>
                    <td className="num text-right">{fmtPct(p.mfePct)}</td>
                    <td className="num text-right">{fmtPct(p.maePct)}</td>
                    <td className="text-ink-2">{p.exitReason}</td>
                    <td className="text-ink-2">{p.setupAtEntry ?? "—"} · {p.scoreAtEntry?.toFixed(0) ?? "—"} · {p.regimeAtEntry ?? "—"}</td>
                    <td className="num text-ink-3">{p.closedAt ? fmtTime(p.closedAt) : "—"}</td>
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
