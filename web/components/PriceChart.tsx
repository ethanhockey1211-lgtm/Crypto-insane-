"use client";
import { useEffect, useRef, useState } from "react";
import { CandlestickSeries, HistogramSeries, LineStyle, createChart, type IChartApi, type IPriceLine, type ISeriesApi, type UTCTimestamp } from "lightweight-charts";
import { api } from "@/lib/api";
import { subscribeCandles, unsubscribeCandles } from "@/lib/connection";
import { store, useRow } from "@/lib/store";
import type { CandleDto, TradePlan } from "@/lib/types";

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
  const lines = useRef<IPriceLine[]>([]);
  const [tf, setTf] = useState("5m");
  const [status, setStatus] = useState<string>("loading");
  const row = useRow(symbol);

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
    return () => { c.remove(); chart.current = null; candles.current = null; volume.current = null; };
  }, []);

  // Load history + subscribe to closed bars for symbol/timeframe.
  useEffect(() => {
    let cancelled = false;
    forming.current = null;
    setStatus("loading");
    (async () => {
      try {
        const res = await api.candles(symbol, tf, 400);
        if (cancelled || !candles.current || !volume.current) return;
        candles.current.setData(res.candles.map(toBar));
        volume.current.setData(res.candles.map(toVol));
        if (res.forming) { forming.current = res.forming; candles.current.update(toBar(res.forming)); volume.current.update(toVol(res.forming)); }
        chart.current?.timeScale().fitContent();
        setStatus(`${res.candles.length} bars`);
      } catch (e) {
        if (!cancelled) setStatus(`history unavailable: ${(e as Error).message}`);
      }
    })();
    void subscribeCandles(symbol, tf);
    const off = store.subscribeCandles(symbol, tf, (closed) => {
      if (!candles.current || !volume.current) return;
      candles.current.update(toBar(closed.candle));
      volume.current.update(toVol(closed.candle));
      forming.current = null;
    });
    return () => { cancelled = true; off(); void unsubscribeCandles(symbol, tf); };
  }, [symbol, tf]);

  // Advance the forming bar from the live price.
  useEffect(() => {
    if (!row || !candles.current) return;
    const size = TF_SECONDS[tf] ?? 300;
    const now = Math.floor(Date.now() / 1000);
    const bucket = now - (now % size);
    const p = row.price;
    const f = forming.current;
    if (!f || f.t < bucket) {
      forming.current = { t: bucket, o: p, h: p, l: p, c: p, v: 0, qv: 0, bv: 0, sv: 0, n: 0, src: "live" };
    } else {
      forming.current = { ...f, h: Math.max(f.h, p), l: Math.min(f.l, p), c: p };
    }
    candles.current.update(toBar(forming.current));
  }, [row, tf]);

  // Plan levels as price lines.
  useEffect(() => {
    const s = candles.current;
    if (!s) return;
    for (const l of lines.current) s.removePriceLine(l);
    lines.current = [];
    const add = (price: number | null | undefined, title: string, color: string, style = LineStyle.Dashed) => {
      if (price == null || !isFinite(price)) return;
      lines.current.push(s.createPriceLine({ price, color, lineWidth: 1, lineStyle: style, axisLabelVisible: true, title }));
    };
    add(levels.keyLevel, "level", "#7fb4e0", LineStyle.Solid);
    add(levels.vwap, "VWAP", "#aab3c5", LineStyle.Dotted);
    if (levels.plan) {
      add(levels.plan.entryMid, "entry", "#e6eaf2");
      add(levels.plan.stop, "stop", "#f0525a", LineStyle.Solid);
      add(levels.plan.target1, "T1", "#2ed27c");
      add(levels.plan.target2, "T2", "#2ed27c");
      add(levels.plan.target3, "T3", "#2ed27c");
    }
  }, [levels]);

  return (
    <div className="flex flex-col h-full">
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
