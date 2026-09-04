"use client";
import { useCycle, useFeed, useHub, useMarket, useRow } from "@/lib/store";
import { fmtAge, fmtPct, fmtPrice, fmtX, signClass } from "@/lib/format";
import type { AssetState } from "@/lib/types";

const REGIME_LABEL: Record<string, string> = {
  StrongRiskOn: "STRONG RISK-ON", RiskOn: "RISK-ON", Neutral: "NEUTRAL", RiskOff: "RISK-OFF", StrongRiskOff: "STRONG RISK-OFF",
};

function Cell({ label, children, className }: { label: string; children: React.ReactNode; className?: string }) {
  return (
    <div className={`flex flex-col justify-center px-2.5 sm:px-3 py-1 lg:py-0 border-r border-b lg:border-b-0 border-line whitespace-nowrap ${className ?? ""}`}>
      <span className="eyebrow">{label}</span>
      <span className="num text-[13px] leading-tight">{children}</span>
    </div>
  );
}

function Asset({ a, symbol }: { a: AssetState | null; symbol: string }) {
  const row = useRow(symbol);
  const price = row?.price ?? a?.price;
  const name = symbol.replace("-USD", "");
  return (
    <>
      <Cell label={name}>{price ? fmtPrice(price) : "—"}</Cell>
      <Cell label={`${name} 1h`} className="max-md:hidden"><span className={signClass(a?.r1h)}>{fmtPct(a?.r1h)}</span></Cell>
      <Cell label={`${name} 24h`}><span className={signClass(a?.r24h)}>{fmtPct(a?.r24h)}</span></Cell>
    </>
  );
}

export function MarketHeader() {
  const market = useMarket();
  const feed = useFeed();
  const hub = useHub();
  const cycle = useCycle();
  const regime = market?.regime ?? "Neutral";
  const regimeClass = regime.endsWith("On") ? "up" : regime.endsWith("Off") ? "down" : "text-ink-2";
  const live = hub === "connected" && (feed?.live ?? false);
  return (
    <header className="glass flex flex-wrap lg:flex-nowrap items-stretch lg:h-[52px] overflow-x-auto shrink-0">
      <div className="flex flex-col justify-center px-2.5 sm:px-3 py-1 lg:py-0 border-r border-b lg:border-b-0 border-line whitespace-nowrap">
        <span className="eyebrow">Regime</span>
        <span className={`font-semibold tracking-[0.08em] text-[13px] ${regimeClass}`}>{REGIME_LABEL[regime] ?? regime}</span>
      </div>
      <Asset a={market?.btc ?? null} symbol="BTC-USD" />
      <Cell label="BTC trend"><span className={market?.btc?.trend === "Bullish" ? "up" : market?.btc?.trend === "Bearish" ? "down" : "text-ink-2"}>{market?.btc?.trend ?? "—"}{market?.btc?.dumping ? " · DUMP" : ""}</span></Cell>
      <Cell label="BTC vs VWAP" className="max-md:hidden">{market?.btc ? (market.btc.aboveVwap ? "above" : "below") : "—"}{market?.btc?.vwapSigma != null ? ` ${market.btc.vwapSigma.toFixed(1)}σ` : ""}</Cell>
      <Asset a={market?.eth ?? null} symbol="ETH-USD" />
      <Cell label="Breadth > VWAP"><span className={market ? (market.breadthAboveVwap >= 0.5 ? "up" : "down") : ""}>{market ? `${Math.round(market.breadthAboveVwap * 100)}%` : "—"}</span></Cell>
      <Cell label="Up on hour" className="max-md:hidden">{market ? `${Math.round(market.breadthPositive1h * 100)}%` : "—"}</Cell>
      <Cell label="Volume trend" className="max-md:hidden">{market ? `${fmtX(market.medianRelVolume, 2)} median` : "—"}</Cell>
      <Cell label="BTC dom." className="text-ink-3 max-md:hidden">n/a</Cell>
      <div className="flex flex-col justify-center px-2.5 sm:px-3 py-1 lg:py-0 border-r border-b lg:border-b-0 border-line whitespace-nowrap max-sm:hidden">
        <span className="eyebrow">Altcoins</span>
        <span className={`text-[12px] ${market?.altsFavorable ? "up" : "warn"}`}>{market ? (market.altsFavorable ? "Favor aggressive setups" : "Demand confirmation") : "—"}</span>
      </div>
      <div className="flex flex-col justify-center px-2.5 sm:px-3 py-1 lg:py-0 ml-auto whitespace-nowrap text-right">
        <span className="eyebrow"><span className={live ? "up" : "warn"}>●</span> {feed?.exchange ?? "Feed"} · {live ? "live" : "not live"}</span>
        <span className="num text-[11px] text-ink-3">
          {market?.symbolsEvaluated ?? 0} symbols · cycle {cycle.ms.toFixed(0)}ms · feed {fmtAge(feed?.lastEventAgeMs)}
          {feed && feed.history.total > 0 && !feed.history.complete ? <span className="warn"> · history {feed.history.loaded}/{feed.history.total}</span> : null}
          {feed && feed.history.failed > 0 ? <span className="warn" title={feed.history.lastError ?? undefined}> · {feed.history.failed} history failed</span> : null}
          {feed && feed.engineErrors > 0 ? <span className="down" title={feed.recentErrors.at(-1)?.error}> · {feed.engineErrors} engine errors</span> : null}
        </span>
      </div>
    </header>
  );
}
