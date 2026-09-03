"use client";
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { API_BASE, api } from "./api";
import { store } from "./store";
import type { AlertEvent, CandleClosed, FeedStatus, QuoteDto, ScannerStream, TapeEvent } from "./types";

let connection: HubConnection | null = null;
let started = false;

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
  const [scanner, feed, tape, alerts] = await Promise.allSettled([api.scanner(), api.feed(), api.tape(100), api.alerts.events(100)]);
  if (scanner.status === "fulfilled") store.applyScanner(scanner.value);
  if (feed.status === "fulfilled") store.setFeed(feed.value);
  if (tape.status === "fulfilled") store.pushTape(tape.value, true);
  if (alerts.status === "fulfilled") store.pushAlerts(alerts.value, true);
}

/** Browser notification for a fired alert, when the user has granted permission. */
function notify(a: AlertEvent): void {
  try {
    if (typeof Notification === "undefined" || Notification.permission !== "granted") return;
    new Notification(`${a.ruleName} · ${a.symbol}`, { body: a.message, tag: a.id });
  } catch { /* notifications unavailable in this context */ }
}

export async function subscribeCandles(symbol: string, tf: string): Promise<void> {
  if (connection?.state === HubConnectionState.Connected) await connection.invoke("SubscribeCandles", symbol, tf).catch(() => undefined);
}

export async function unsubscribeCandles(symbol: string, tf: string): Promise<void> {
  if (connection?.state === HubConnectionState.Connected) await connection.invoke("UnsubscribeCandles", symbol, tf).catch(() => undefined);
}

/** Feed status is also polled slowly so a silent hub still surfaces a dead feed. */
export function startFeedPolling(): () => void {
  const id = setInterval(async () => {
    try { store.setFeed(await api.feed()); } catch { /* the hub state already reflects loss of the API */ }
  }, 10_000);
  return () => clearInterval(id);
}
