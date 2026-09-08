"use client";
import { useSyncExternalStore } from "react";
import { MarketStore, store as liveStore } from "./store";
import { HiddenMarkets, hiddenMarkets } from "./hidden-markets";
import type { FeedStatus, MarketContext, ScannerRow, SymbolSummaryDto, TapeEvent } from "./types";

export const DISPLAY_INTERVAL_MS = 5_000;
export interface DisplaySnapshot {
  rows: ReadonlyMap<string, ScannerRow>;
  symbols: ReadonlyMap<string, SymbolSummaryDto>;
  order: string[];
  allOrder: string[];
  market: MarketContext | null;
  feed: FeedStatus | null;
  cycle: { at: string | null; ms: number };
  tape: TapeEvent[];
  capturedAt: number | null;
  revision: number;
  paused: boolean;
}
const EMPTY: DisplaySnapshot = {
  rows: new Map(), symbols: new Map(), order: [], allOrder: [], market: null, feed: null,
  cycle: { at: null, ms: 0 }, tape: [], capturedAt: null, revision: 0, paused: false,
};

/** Readable UI snapshots. The original store keeps every live update for alerts and feed health. */
export class DisplayStore {
  private snapshot: DisplaySnapshot = EMPTY;
  private capturedOrder: string[] = [];
  private capturedAllOrder: string[] = [];
  private capturedTape: TapeEvent[] = [];
  private listeners = new Set<() => void>();

  constructor(private source: MarketStore, private visibility: HiddenMarkets, private now = Date.now) { }
  getSnapshot = () => this.snapshot;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  getRow = (symbol: string) => this.snapshot.rows.get(symbol) ?? null;
  getSymbol = (symbol: string) => this.snapshot.symbols.get(symbol) ?? null;

  capture = (): void => {
    const source = this.source.getDisplaySource();
    this.capturedOrder = source.order; this.capturedAllOrder = source.allOrder; this.capturedTape = source.tape;
    this.snapshot = { ...this.snapshot, ...source,
      capturedAt: this.now(), revision: this.snapshot.revision + 1 };
    this.applyVisibility();
  };
  setPaused = (paused: boolean): void => {
    if (paused === this.snapshot.paused) return;
    this.snapshot = { ...this.snapshot, paused };
    if (paused) this.emit();
    else this.capture(); // Resume shows the newest data once, never a replay of queued ticks.
  };
  tick = (): void => { if (!this.snapshot.paused) this.capture(); };

  start(): () => void {
    this.tick();
    // Do not make first-load users wait for a timer before their catalog appears.
    const offList = this.source.subscribeList(() => {
      if (!this.snapshot.paused && !this.capturedAllOrder.length && this.source.getAllOrder().length) this.capture();
    });
    const offVisibility = this.visibility.subscribe(() => {
      if (this.snapshot.paused) this.applyVisibility();
      else this.capture();
    });
    const timer = setInterval(this.tick, DISPLAY_INTERVAL_MS);
    return () => { clearInterval(timer); offList(); offVisibility(); };
  }
  private applyVisibility(): void {
    this.snapshot = { ...this.snapshot,
      order: this.capturedOrder.filter(symbol => !this.visibility.isHidden(symbol)),
      allOrder: this.capturedAllOrder.filter(symbol => !this.visibility.isHidden(symbol)),
      tape: this.capturedTape.filter(event => !this.visibility.isHidden(event.symbol)),
    };
    this.emit();
  }
  private emit(): void { for (const listener of this.listeners) listener(); }
}

export const store = new DisplayStore(liveStore, hiddenMarkets);
export const useDisplay = () => useSyncExternalStore(store.subscribe, store.getSnapshot, () => EMPTY);
export const useRow = (symbol: string) => useDisplay().rows.get(symbol) ?? null;
export const useSymbol = (symbol: string) => useDisplay().symbols.get(symbol) ?? null;
export const useOrder = () => useDisplay().order;
export const useAllOrder = () => useDisplay().allOrder;
export const useMarket = () => useDisplay().market;
export const useCycle = () => useDisplay().cycle;
export const useTape = () => useDisplay().tape;
