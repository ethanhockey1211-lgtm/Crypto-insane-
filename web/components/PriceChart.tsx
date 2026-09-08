"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { CandlestickSeries, HistogramSeries, LineStyle, createChart, type IChartApi, type IPriceLine, type ISeriesApi, type UTCTimestamp } from "lightweight-charts";
import { api } from "@/lib/api";
import { subscribeCandles, unsubscribeCandles } from "@/lib/connection";
import { store, useHub } from "@/lib/store";
import { useDisplay } from "@/lib/display";
import { scannerIsFresh } from "@/lib/decision";
import type { CandleDto, CandlesResponse, TradePlan } from "@/lib/types";

const TF_SECONDS: Record<string, number> = { "1m": 60, "5m": 300, "15m": 900, "1h": 3600 };

export interface ChartLevels { plan: TradePlan | null; keyLevel: number | null; vwap: number | null }

/**
 * Candles + volume. History from REST, closed bars from the hub, and the forming bar advanced from live quotes
 * (a display convenience only; the server's closed bar replaces it). No indicator math happens here.
 */
export function PriceChart({ symbol, levels }: { symbol: string; levels: ChartLevels }) {
  const el = useRef<HTMLDivElement>(null);
  const chart = useRef<IChartApi | null>(null);
  const candles = useRef<ISeriesApi<"Candlestick"> | null>(null);
  const volume = useRef<ISeriesApi<"Histogram"> | null>(null);
  const forming = useRef<CandleDto | null>(null);
  const lines = useRef(new Map<string, IPriceLine>());
  const loadedSeries = useRef<string | null>(null);
  const bars = useRef(new Map<number, CandleDto>());
  const queuedClosed = useRef(new Map<number, CandleDto>());
  const queuedHistory = useRef<{ key: string; response: CandlesResponse } | null>(null);
  const lastPublished = useRef<string | null>(null);
  const [tf, setTf] = useState("5m");
  const [status, setStatus] = useState<string>("loading");
  const display = useDisplay();
  const paused = useRef(display.paused);
  paused.current = display.paused;
  const row = display.rows.get(symbol) ?? null;
  const historyLoaded = display.symbols.get(symbol)?.historyLoaded ?? false;
  const hub = useHub();

  const applyHistory = useCallback((key: string, response: CandlesResponse) => {
    if (!candles.current || !volume.current) return;
    const fit = loadedSeries.current !== key || bars.current.size === 0;
    const history = [...response.candles, ...(response.forming ? [response.forming] : [])];
    bars.current = new Map(history.map(candle => [candle.t, candle]));
    candles.current.setData(history.map(toBar));
    volume.current.setData(history.map(toVol));
    forming.current = response.forming;
    loadedSeries.current = key;
    if (fit) chart.current?.timeScale().fitContent();
    setStatus(`${response.candles.length} bars`);
  }, []);

  const applyBar = useCallback((bar: CandleDto) => {
    if (!candles.current || !volume.current) return;
    const existing = bars.current.has(bar.t);
    const latest = Math.max(-Infinity, ...bars.current.keys());
    bars.current.set(bar.t, bar);
    if ((!existing && bar.t < latest) || bars.current.size > 600) {
      const ordered = [...bars.current.values()].sort((a, b) => a.t - b.t).slice(-600);
      bars.current = new Map(ordered.map(candle => [candle.t, candle]));
      candles.current.setData(ordered.map(toBar)); volume.current.setData(ordered.map(toVol));
    } else {
      candles.current.update(toBar(bar), bar.t < latest);
      volume.current.update(toVol(bar), bar.t < latest);
    }
  }, []);

  useEffect(() => {
    if (!el.current) return;
    const c = createChart(el.current, {
      layout: { background: { color: "transparent" }, textColor: "#8b95a7", fontFamily: "var(--font-plex-mono), monospace", fontSize: 11 },
      grid: { vertLines: { color: "rgba(148,163,184,0.06)" }, horzLines: { color: "rgba(148,163,184,0.06)" } },
      rightPriceScale: { borderColor: "rgba(148,163,184,0.15)" },
      timeScale: { borderColor: "rgba(148,163,184,0.15)", timeVisible: true, secondsVisible: false },
      crosshair: { horzLine: { color: "rgba(127,180,224,0.5)" }, vertLine: { color: "rgba(127,180,224,0.5)" } },
      autoSize: true,
    });
    const cs = c.addSeries(CandlestickSeries, { upColor: "#2ed27c", downColor: "#f0525a", wickUpColor: "#2ed27c", wickDownColor: "#f0525a", borderVisible: false });
    const vs = c.addSeries(HistogramSeries, { priceFormat: { type: "volume" }, priceScaleId: "vol", color: "rgba(127,180,224,0.35)" });
    c.priceScale("vol").applyOptions({ scaleMargins: { top: 0.82, bottom: 0 } });
    c.priceScale("right").applyOptions({ scaleMargins: { top: 0.06, bottom: 0.22 } });
    chart.current = c; candles.current = cs; volume.current = vs;
    return () => { c.remove(); chart.current = null; candles.current = null; volume.current = null; lines.current.clear(); loadedSeries.current = null; bars.current.clear(); queuedClosed.current.clear(); queuedHistory.current = null; };
  }, []);

  // Load history + subscribe to closed bars for symbol/timeframe.
  useEffect(() => {
    let cancelled = false;
    const key = `${symbol}:${tf}`;
    const newSeries = loadedSeries.current !== key;
    if (newSeries) {
      forming.current = null;
      bars.current.clear(); queuedClosed.current.clear(); queuedHistory.current = null;
      candles.current?.setData([]);
      volume.current?.setData([]);
      setStatus("loading");
    }
    (async () => {
      try {
        const res = await api.candles(symbol, tf, 400);
        if (cancelled || !candles.current || !volume.current) return;
        if (paused.current && loadedSeries.current === key) queuedHistory.current = { key, response: res };
        else applyHistory(key, res);
      } catch (e) {
        if (!cancelled) setStatus(`history unavailable: ${(e as Error).message}`);
      }
    })();
    void subscribeCandles(symbol, tf);
    const off = store.subscribeCandles(symbol, tf, (closed) => {
      queuedClosed.current.set(closed.candle.t, closed.candle);
      if (queuedClosed.current.size > 600) queuedClosed.current.delete(queuedClosed.current.keys().next().value!);
    });
    return () => { cancelled = true; off(); void unsubscribeCandles(symbol, tf); };
  }, [symbol, tf, historyLoaded, hub, applyHistory]);

  // Advance the forming bar at the display cadence, including an explicit paused refresh.
  useEffect(() => {
    const key = `${symbol}:${tf}`;
    const publication = `${key}:${display.revision}`;
    if (display.paused && lastPublished.current === publication) return;
    lastPublished.current = publication;
    if (queuedHistory.current?.key === key) { applyHistory(key, queuedHistory.current.response); queuedHistory.current = null; }
    for (const closed of [...queuedClosed.current.values()].sort((a, b) => a.t - b.t)) {
      applyBar(closed);
      if (forming.current && forming.current.t <= closed.t) forming.current = null;
    }
    queuedClosed.current.clear();
    if (!row || row.stale || hub !== "connected" || !candles.current ||
      display.capturedAt == null || !scannerIsFresh(display.cycle.at, Date.now())) return;
    const size = TF_SECONDS[tf] ?? 300;
    const now = Math.floor(display.capturedAt / 1000);
    const bucket = now - (now % size);
    const p = row.price;
    const f = forming.current;
    if (f && f.t > bucket) return;
    if (!f || f.t < bucket) {
      forming.current = { t: bucket, o: p, h: p, l: p, c: p, v: 0, qv: 0, bv: 0, sv: 0, n: 0, src: "live" };
    } else {
      forming.current = { ...f, h: Math.max(f.h, p), l: Math.min(f.l, p), c: p };
    }
    applyBar(forming.current);
  }, [row, symbol, tf, hub, display.revision, display.capturedAt, display.cycle.at, display.paused, applyBar, applyHistory]);

  // Plan levels as price lines.
  useEffect(() => {
    const s = candles.current;
    if (!s) return;
    const desired = new Set<string>();
    const add = (price: number | null | undefined, title: string, color: string, style = LineStyle.Dashed) => {
      if (price == null || !isFinite(price)) return;
      desired.add(title);
      const previous = lines.current.get(title);
      if (!previous) lines.current.set(title, s.createPriceLine({ price, color, lineWidth: 1, lineStyle: style, axisLabelVisible: true, title }));
      else if (previous.options().price !== price) previous.applyOptions({ price });
    };
    add(levels.keyLevel, "level", "#7fb4e0", LineStyle.Solid);
    add(levels.vwap, "VWAP", "#aab3c5", LineStyle.Dotted);
    if (levels.plan) {
      add(levels.plan.entryMid, "entry", "#e6eaf2");
      add(levels.plan.stop, "stop", "#f0525a", LineStyle.Solid);
      add(levels.plan.chaseCeiling, "no chase", "#e8b04c");
      add(levels.plan.target1, "T1", "#2ed27c");
      add(levels.plan.target2, "T2", "#2ed27c");
      add(levels.plan.target3, "T3", "#2ed27c");
    }
    for (const [title, line] of lines.current) if (!desired.has(title)) { s.removePriceLine(line); lines.current.delete(title); }
  // Depend on line values, not the fresh wrapper object created by a parent render.
  }, [levels.keyLevel, levels.vwap, levels.plan?.entryMid, levels.plan?.stop, levels.plan?.chaseCeiling,
    levels.plan?.target1, levels.plan?.target2, levels.plan?.target3]);

  return (
    <div className="flex flex-col h-full" role="region" aria-label={`${symbol} price chart`}>
      <div className="flex items-center gap-1 px-2 h-7 text-[11px]">
        {Object.keys(TF_SECONDS).map((k) => (
          <button key={k} onClick={() => setTf(k)} className={`px-2 py-0.5 rounded-[3px] ${tf === k ? "bg-navy-3 text-ink" : "text-ink-3 hover:text-ink-2"}`}>{k}</button>
        ))}
        <span className="ml-auto text-ink-3 num">{status}</span>
      </div>
      <div ref={el} className="flex-1 min-h-[220px]" />
    </div>
  );
}

const toBar = (c: CandleDto) => ({ time: c.t as UTCTimestamp, open: c.o, high: c.h, low: c.l, close: c.c });
const toVol = (c: CandleDto) => ({ time: c.t as UTCTimestamp, value: c.v, color: c.c >= c.o ? "rgba(46,210,124,0.35)" : "rgba(240,82,90,0.35)" });
