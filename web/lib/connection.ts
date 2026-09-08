"use client";
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { API_BASE, api } from "./api";
import { store } from "./store";
import { hiddenMarkets } from "./hidden-markets";
import type { AlertEvent, CandleClosed, FeedStatus, QuoteDto, ScannerStream, TapeEvent } from "./types";

let connection: HubConnection | null = null;
let started = false;
const shownNotifications = new Map<string, { symbol: string; notification: Notification }>();
hiddenMarkets.subscribe(() => {
  for (const [id, entry] of shownNotifications) if (hiddenMarkets.isHidden(entry.symbol)) {
    entry.notification.close(); shownNotifications.delete(id);
  }
});

/** One SignalR connection per page. Initial state is loaded over REST, then the hub keeps it live. */
export async function startConnection(): Promise<void> {
  if (started) return;
  started = true;

  connection = new HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/market`)
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on("quotes", (q: QuoteDto[]) => store.applyQuotes(q));
  connection.on("scanner", (s: ScannerStream) => store.applyScanner(s));
  connection.on("tape", (e: TapeEvent) => store.pushTape([e]));
  connection.on("feed", (f: FeedStatus) => store.setFeed(f));
  connection.on("candle", (c: CandleClosed) => store.emitCandle(c));
  connection.on("alert", (a: AlertEvent) => { store.pushAlerts([a]); notify(a); });
  connection.on("paper", () => store.bumpPaper());
  connection.onreconnecting(() => store.setHub("reconnecting"));
  connection.onreconnected(() => { store.setHub("connected"); void refresh(); });
  connection.onclose(() => { store.setHub("disconnected"); retryStart(); });

  await refresh();
  try {
    await connection.start();
    store.setHub("connected");
  } catch {
    store.setHub("disconnected");
    retryStart();
  }
}

function retryStart(): void {
  setTimeout(async () => {
    if (!connection || connection.state !== HubConnectionState.Disconnected) return;
    try { await connection.start(); store.setHub("connected"); await refresh(); } catch { retryStart(); }
  }, 3000);
}

async function refresh(): Promise<void> {
  const [scanner, symbols, feed, tape, alerts] = await Promise.allSettled([api.scanner(), api.symbols(), api.feed(), api.tape(100), api.alerts.events(100)]);
  if (scanner.status === "fulfilled") store.applyScanner(scanner.value);
  if (symbols.status === "fulfilled") store.applySymbols(symbols.value);
  if (feed.status === "fulfilled") store.setFeed(feed.value);
  if (tape.status === "fulfilled") store.pushTape(tape.value, true);
  if (alerts.status === "fulfilled") store.pushAlerts(alerts.value, true);
}

/** Browser notification for a fired alert, when the user has granted permission. */
function notify(a: AlertEvent): void {
  try {
    if (hiddenMarkets.isHidden(a.symbol)) return;
    if (typeof Notification === "undefined" || Notification.permission !== "granted") return;
    const notification = new Notification(`${a.ruleName} · ${a.symbol}`, { body: a.message, tag: a.id });
    shownNotifications.set(a.id, { symbol: a.symbol, notification });
    notification.onclose = () => { shownNotifications.delete(a.id); };
    if (shownNotifications.size > 100) {
      const oldest = shownNotifications.entries().next().value;
      if (oldest) { oldest[1].notification.close(); shownNotifications.delete(oldest[0]); }
    }
  } catch { /* notifications unavailable in this context */ }
}

export async function subscribeCandles(symbol: string, tf: string): Promise<void> {
  if (connection?.state === HubConnectionState.Connected) await connection.invoke("SubscribeCandles", symbol, tf).catch(() => undefined);
}

export async function unsubscribeCandles(symbol: string, tf: string): Promise<void> {
  if (connection?.state === HubConnectionState.Connected) await connection.invoke("UnsubscribeCandles", symbol, tf).catch(() => undefined);
}

/** Poll feed status and catalog so warm-up progress and newly listed pairs remain visible. */
export function startFeedPolling(): () => void {
  let active = true;
  let polling = false;
  const expiry = setInterval(() => store.expireCatalogQuotes(), 1000);
  const id = setInterval(async () => {
    if (polling) return;
    polling = true;
    const [feed, symbols] = await Promise.allSettled([api.feed(), api.symbols()]);
    if (active) {
      if (feed.status === "fulfilled") store.setFeed(feed.value);
      if (symbols.status === "fulfilled") store.applySymbols(symbols.value);
    }
    polling = false;
  }, 10_000);
  return () => { active = false; clearInterval(id); clearInterval(expiry); };
}
