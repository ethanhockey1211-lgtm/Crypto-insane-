"use client";
import { useSyncExternalStore } from "react";
import type { AlertEvent, CandleClosed, FeedStatus, MarketContext, QuoteDto, ScannerRow, ScannerStream, TapeEvent } from "./types";

type Listener = () => void;

/**
 * Market state lives outside React. Rows are keyed by symbol; components subscribe to one symbol (a row) or
 * to a coarse "list" version. Bursts of ticks are coalesced into one animation frame so hundreds of
 * updates per second never cause hundreds of renders.
 */
class MarketStore {
  private rows = new Map<string, ScannerRow>();
  private order: string[] = [];
  private market: MarketContext | null = null;
  private feed: FeedStatus | null = null;
  private hub: "connecting" | "connected" | "reconnecting" | "disconnected" = "connecting";
  private tape: TapeEvent[] = [];
  private alerts: AlertEvent[] = [];
  private cycle: { at: string | null; ms: number } = { at: null, ms: 0 };

  private symbolListeners = new Map<string, Set<Listener>>();
  private listListeners = new Set<Listener>();
  private headerListeners = new Set<Listener>();
  private tapeListeners = new Set<Listener>();
  private alertListeners = new Set<Listener>();
  private paperListeners = new Set<Listener>();
  private paperVersion = 0;
  private candleListeners = new Map<string, Set<(c: CandleClosed) => void>>();

  private dirtySymbols = new Set<string>();
  private dirtyList = false;
  private dirtyHeader = false;
  private dirtyTape = false;
  private dirtyAlerts = false;
  private frame: number | null = null;

  // ---- snapshots (stable references between changes) ----
  getRow = (symbol: string) => this.rows.get(symbol) ?? null;
  getOrder = () => this.order;
  getMarket = () => this.market;
  getFeed = () => this.feed;
  getHub = () => this.hub;
  getTape = () => this.tape;
  getAlerts = () => this.alerts;
  getPaperVersion = () => this.paperVersion;
  /** A fill happened server-side; views re-fetch paper state. */
  bumpPaper(): void { this.paperVersion++; for (const l of this.paperListeners) l(); }
  getCycle = () => this.cycle;

  // ---- mutations ----
  applyScanner(s: ScannerStream): void {
    const nextOrder: string[] = new Array(s.rows.length);
    let changed = false;
    for (let i = 0; i < s.rows.length; i++) {
      const r = s.rows[i];
      nextOrder[i] = r.symbol;
      const prev = this.rows.get(r.symbol);
      if (!prev || rowChanged(prev, r)) {
        changed = true;
        this.rows.set(r.symbol, r);
        this.dirtySymbols.add(r.symbol);
      }
    }
    const included = new Set(nextOrder);
    for (const symbol of this.rows.keys()) {
      if (!included.has(symbol)) { this.rows.delete(symbol); this.dirtySymbols.add(symbol); changed = true; }
    }
    if (changed || nextOrder.length !== this.order.length || nextOrder.some((s2, i) => s2 !== this.order[i])) {
      this.order = nextOrder;
      this.dirtyList = true;
    }
    this.market = s.market;
    this.cycle = { at: s.at, ms: s.cycleMs }; // new object only when a cycle arrives: snapshots must be stable between changes
    this.dirtyHeader = true;
    this.schedule();
  }

  applyQuotes(quotes: QuoteDto[]): void {
    for (const q of quotes) {
      const row = this.rows.get(q.symbol);
      if (row && (row.price !== q.price || row.stale !== q.stale)) {
        this.rows.set(q.symbol, { ...row, price: q.price, stale: q.stale });
        this.dirtySymbols.add(q.symbol);
        this.order = [...this.order];
        this.dirtyList = true;
      }
    }
    this.schedule();
  }

  setFeed(f: FeedStatus): void { this.feed = f; this.dirtyHeader = true; this.schedule(); }
  setHub(state: MarketStore["hub"]): void { this.hub = state; this.dirtyHeader = true; this.schedule(); }

  pushTape(events: TapeEvent[], replace = false): void {
    const merged = replace ? [...events] : [...events, ...this.tape];
    merged.sort((a, b) => b.id - a.id);
    this.tape = merged.slice(0, 300);
    this.dirtyTape = true;
    this.schedule();
  }

  pushAlerts(events: AlertEvent[], replace = false): void {
    const merged = replace ? [...events] : [...events, ...this.alerts];
    merged.sort((a, b) => (a.at < b.at ? 1 : a.at > b.at ? -1 : 0));
    this.alerts = merged.slice(0, 300);
    this.dirtyAlerts = true;
    this.schedule();
  }

  emitCandle(c: CandleClosed): void {
    const set = this.candleListeners.get(`${c.symbol}:${c.timeframe}`);
    if (set) for (const l of set) l(c);
  }

  // ---- subscriptions ----
  subscribeSymbol(symbol: string, l: Listener): () => void {
    let set = this.symbolListeners.get(symbol);
    if (!set) { set = new Set(); this.symbolListeners.set(symbol, set); }
    set.add(l);
    return () => { set!.delete(l); if (set!.size === 0) this.symbolListeners.delete(symbol); };
  }
  subscribeList = (l: Listener) => { this.listListeners.add(l); return () => { this.listListeners.delete(l); }; };
  subscribeHeader = (l: Listener) => { this.headerListeners.add(l); return () => { this.headerListeners.delete(l); }; };
  subscribeTape = (l: Listener) => { this.tapeListeners.add(l); return () => { this.tapeListeners.delete(l); }; };
  subscribeAlerts = (l: Listener) => { this.alertListeners.add(l); return () => { this.alertListeners.delete(l); }; };
  subscribePaper = (l: Listener) => { this.paperListeners.add(l); return () => { this.paperListeners.delete(l); }; };
  subscribeCandles(symbol: string, tf: string, l: (c: CandleClosed) => void): () => void {
    const key = `${symbol}:${tf}`;
    let set = this.candleListeners.get(key);
    if (!set) { set = new Set(); this.candleListeners.set(key, set); }
    set.add(l);
    return () => { set!.delete(l); if (set!.size === 0) this.candleListeners.delete(key); };
  }

  /** Flush pending notifications now (tests) instead of waiting for the animation frame. */
  flush(): void {
    if (this.frame !== null && typeof cancelAnimationFrame === "function") cancelAnimationFrame(this.frame);
    this.frame = null;
    const symbols = [...this.dirtySymbols];
    this.dirtySymbols.clear();
    for (const s of symbols) { const set = this.symbolListeners.get(s); if (set) for (const l of set) l(); }
    if (this.dirtyList) { this.dirtyList = false; for (const l of this.listListeners) l(); }
    if (this.dirtyHeader) { this.dirtyHeader = false; for (const l of this.headerListeners) l(); }
    if (this.dirtyTape) { this.dirtyTape = false; for (const l of this.tapeListeners) l(); }
    if (this.dirtyAlerts) { this.dirtyAlerts = false; for (const l of this.alertListeners) l(); }
  }

  private schedule(): void {
    if (this.frame !== null) return;
    if (typeof requestAnimationFrame !== "function") { this.flush(); return; }
    this.frame = requestAnimationFrame(() => { this.frame = null; this.flush(); });
  }
}

function rowChanged(a: ScannerRow, b: ScannerRow): boolean {
  return a.score !== b.score || a.rank !== b.rank || a.price !== b.price || a.setup !== b.setup || a.breakout !== b.breakout ||
    a.r1m !== b.r1m || a.r5m !== b.r5m || a.r15m !== b.r15m || a.r1h !== b.r1h || a.r24h !== b.r24h || a.relVol !== b.relVol ||
    a.doNotChase !== b.doNotChase || a.stale !== b.stale || a.entry !== b.entry || a.stop !== b.stop || a.target1 !== b.target1 ||
    a.rr !== b.rr || a.penalty !== b.penalty || a.confidence !== b.confidence || a.keyLevel !== b.keyLevel || a.vwapDev !== b.vwapDev ||
    a.entryState !== b.entryState || a.chaseCeiling !== b.chaseCeiling || a.executionStatus !== b.executionStatus ||
    a.netRewardRatio !== b.netRewardRatio || a.volume24h !== b.volume24h || a.trend !== b.trend ||
    a.executionReason !== b.executionReason || a.assessedPrice !== b.assessedPrice ||
    a.entryLow !== b.entryLow || a.entryHigh !== b.entryHigh || a.trigger !== b.trigger || a.setupBias !== b.setupBias ||
    !sameStrings(a.executionReasons, b.executionReasons) ||
    a.components.length !== b.components.length || a.components.some((v, i) => v !== b.components[i]);
}

function sameStrings(a: readonly string[] | null | undefined, b: readonly string[] | null | undefined): boolean {
  return a === b || (a?.length === b?.length && a?.every((value, i) => value === b?.[i]) === true);
}

export const store = new MarketStore();

const EMPTY: never[] = [];
export const useRow = (symbol: string) => useSyncExternalStore((l) => store.subscribeSymbol(symbol, l), () => store.getRow(symbol), () => null);
export const useOrder = () => useSyncExternalStore(store.subscribeList, store.getOrder, () => EMPTY as string[]);
export const useMarket = () => useSyncExternalStore(store.subscribeHeader, store.getMarket, () => null);
export const useFeed = () => useSyncExternalStore(store.subscribeHeader, store.getFeed, () => null);
export const useHub = () => useSyncExternalStore(store.subscribeHeader, store.getHub, () => "connecting" as const);
export const useTape = () => useSyncExternalStore(store.subscribeTape, store.getTape, () => EMPTY as TapeEvent[]);
export const useAlerts = () => useSyncExternalStore(store.subscribeAlerts, store.getAlerts, () => EMPTY as AlertEvent[]);
export const usePaperVersion = () => useSyncExternalStore(store.subscribePaper, store.getPaperVersion, () => 0);
// Server/prerender snapshots must be stable references or React reports an infinite-loop risk.
const NO_CYCLE = { at: null, ms: 0 } as const;
export const useCycle = () => useSyncExternalStore(store.subscribeHeader, store.getCycle, () => NO_CYCLE);
