"use client";
import { hiddenMarkets, useHiddenMarkets } from "@/lib/hidden-markets";

export function HiddenMarketsControl() {
  const { symbols, persistence } = useHiddenMarkets();
  return <details className="border-b border-line px-4 py-3 text-[12px]" aria-label="Manage hidden coins">
    <summary className="cursor-pointer text-ink-2 w-fit">Hidden coins <span className="num ml-1 text-accent">{symbols.length}</span><span className="ml-3 text-[11px] text-ink-3">Manage / restore</span></summary>
    <div className="mt-3 max-w-3xl">
      <p className="text-ink-2">Use “Hide unavailable” in a coin’s setup panel when your Kraken account cannot buy it. Hidden coins leave discovery lists and browser alerts.</p>
      <p className={`mt-1 text-[11px] ${persistence === "session" ? "text-warn" : "text-ink-3"}`} role="status">{persistence === "pending" ? "Loading browser preferences…" : persistence === "session" ? "Browser storage is unavailable. Hiding works for this tab until it closes." : "Saved in this browser and shared with your other tabs."} Paper positions and historical records are kept.</p>
      {symbols.length > 0 ? <>
        <ul className="mt-3 grid max-h-48 grid-cols-1 gap-2 overflow-auto sm:grid-cols-2 lg:grid-cols-3">{symbols.map(symbol => <li key={symbol} className="flex items-center justify-between gap-3 rounded border border-line bg-ground px-3 py-2"><span className="font-medium">{symbol.replace("-", "/")}</span><button type="button" className="text-accent underline underline-offset-2" onClick={() => hiddenMarkets.restore(symbol)} aria-label={`Restore ${symbol.replace("-", "/")}`}>Restore</button></li>)}</ul>
        <button type="button" className="control-button mt-3" onClick={() => hiddenMarkets.restoreAll()}>Restore all hidden coins</button>
        <p className="mt-2 text-[11px] text-ink-3">Restored coins reappear if included by the market filters. This does not verify that your account can buy them.</p>
      </> : <p className="mt-3 text-ink-3">No coins hidden in this browser.</p>}
    </div>
  </details>;
}
