"use client";
import { useSyncExternalStore } from "react";

export const HIDDEN_MARKETS_KEY = "kraken.hidden-markets.v1";
type StoragePort = Pick<Storage, "getItem" | "setItem">;
type HiddenSnapshot = { symbols: readonly string[]; persistence: "pending" | "browser" | "session" };
const EMPTY: HiddenSnapshot = { symbols: [], persistence: "pending" };

export function normalizeHiddenSymbol(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const symbol = value.trim().toUpperCase().replace("/", "-");
  return symbol.length <= 80 && /^[A-Z0-9][A-Z0-9._]*-USD$/.test(symbol) ? symbol : null;
}

export function parseHiddenSymbols(raw: string | null): string[] {
  try {
    const parsed: unknown = raw == null ? [] : JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return [...new Set(parsed.flatMap(value => { const symbol = normalizeHiddenSymbol(value); return symbol ? [symbol] : []; }))].sort();
  } catch { return []; }
}

/** Browser display preferences only. Exchange data and trading records stay intact. */
export class HiddenMarkets {
  private snapshot: HiddenSnapshot = EMPTY;
  private hidden = new Set<string>();
  private listeners = new Set<() => void>();
  private storage: StoragePort | null = null;

  getSnapshot = () => this.snapshot;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  isHidden = (symbol: string | null | undefined) => symbol != null && this.hidden.has(normalizeHiddenSymbol(symbol) ?? "");

  initialize(storage: StoragePort | null): void {
    this.storage = storage;
    if (!storage) { this.update([...this.hidden], "session"); return; }
    try { this.update(parseHiddenSymbols(storage.getItem(HIDDEN_MARKETS_KEY)), "browser"); }
    catch { this.update([...this.hidden], "session"); }
  }

  /** Storage events update this tab without writing back or creating an event loop. */
  receiveStorage(raw: string | null): void { this.update(parseHiddenSymbols(raw), "browser"); }

  hide(symbol: string): void {
    const normalized = normalizeHiddenSymbol(symbol);
    if (!normalized || this.hidden.has(normalized)) return;
    this.save([...this.hidden, normalized].sort());
  }
  restore(symbol: string): void {
    const normalized = normalizeHiddenSymbol(symbol);
    if (!normalized || !this.hidden.has(normalized)) return;
    this.save([...this.hidden].filter(value => value !== normalized));
  }
  restoreAll(): void { if (this.hidden.size) this.save([]); }

  private save(symbols: string[]): void {
    let persistence: HiddenSnapshot["persistence"] = "session";
    try { if (this.storage) { this.storage.setItem(HIDDEN_MARKETS_KEY, JSON.stringify(symbols)); persistence = "browser"; } } catch { /* keep the preference for this tab */ }
    this.update(symbols, persistence);
  }
  private update(symbols: string[], persistence: HiddenSnapshot["persistence"]): void {
    const sameSymbols = symbols.length === this.snapshot.symbols.length && symbols.every((symbol, index) => symbol === this.snapshot.symbols[index]);
    if (sameSymbols && persistence === this.snapshot.persistence) return;
    this.hidden = new Set(symbols);
    this.snapshot = { symbols: sameSymbols ? this.snapshot.symbols : symbols, persistence };
    for (const listener of this.listeners) listener();
  }
}

export const hiddenMarkets = new HiddenMarkets();
export const useHiddenMarkets = () => useSyncExternalStore(hiddenMarkets.subscribe, hiddenMarkets.getSnapshot, () => EMPTY);

/** Hydrate after mount, before market connections start; safe with SSR and blocked storage. */
export function startHiddenMarkets(): () => void {
  if (typeof window === "undefined") return () => undefined;
  let storage: Storage | null = null;
  try { storage = window.localStorage; } catch { /* unavailable */ }
  hiddenMarkets.initialize(storage);
  const onStorage = (event: StorageEvent) => {
    if (event.storageArea !== storage || (event.key !== HIDDEN_MARKETS_KEY && event.key !== null)) return;
    hiddenMarkets.receiveStorage(event.key === null ? null : event.newValue);
  };
  window.addEventListener("storage", onStorage);
  return () => window.removeEventListener("storage", onStorage);
}
