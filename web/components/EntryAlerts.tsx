"use client";
import { useEffect, useRef, useState } from "react";
import { store, useCycle, useFeed, useHub } from "@/lib/store";
import { EntryAlertTracker, type EntryNotice } from "@/lib/entry-alerts";
import { fmtPrice } from "@/lib/format";

const KEY = "kraken-entry-alerts-v1";
type Preferences = { enabled: boolean; sound: boolean; desktop: boolean };
const OFF: Preferences = { enabled: false, sound: false, desktop: false };

export function EntryAlerts({ onOpen }: { onOpen: (symbol: string) => void }) {
  const cycle = useCycle(); const feed = useFeed(); const hub = useHub();
  const [prefs, setPrefs] = useState<Preferences>(OFF);
  const [loaded, setLoaded] = useState(false);
  const [notices, setNotices] = useState<EntryNotice[]>([]);
  const [message, setMessage] = useState("");
  const tracker = useRef(new EntryAlertTracker());
  const audio = useRef<AudioContext | null>(null);
  useEffect(() => {
    try {
      const saved = JSON.parse(localStorage.getItem(KEY) ?? "null") as Partial<Preferences> | null;
      if (saved) setPrefs({ enabled: saved.enabled === true, sound: saved.sound === true, desktop: saved.desktop === true });
    } catch { /* browser storage is optional */ }
    setLoaded(true);
    return () => { void audio.current?.close(); audio.current = null; };
  }, []);
  useEffect(() => { if (loaded) { try { localStorage.setItem(KEY, JSON.stringify(prefs)); } catch { /* optional */ } } }, [prefs, loaded]);
  useEffect(() => {
    const t = tracker.current;
    if (!prefs.enabled) { t.pause(); return; }
    const fresh = t.update(store.getOrder().flatMap(s => { const r = store.getRow(s); return r ? [r] : []; }), hub === "connected" && feed?.live === true, cycle.at, Date.now());
    if (!fresh.length) return;
    setNotices(previous => [...fresh, ...previous].slice(0, 5));
    if (prefs.sound && audio.current?.state === "running") {
      const context = audio.current;
      const oscillator = context.createOscillator(); const gain = context.createGain();
      oscillator.connect(gain); gain.connect(context.destination);
      oscillator.frequency.setValueAtTime(660, context.currentTime);
      oscillator.frequency.setValueAtTime(880, context.currentTime + 0.12);
      gain.gain.setValueAtTime(0.06, context.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + 0.3);
      oscillator.start(); oscillator.stop(context.currentTime + 0.32);
    }
    if (prefs.desktop && typeof Notification !== "undefined" && Notification.permission === "granted") {
      try {
        const first = fresh[0].row;
        const notification = new Notification(fresh.length > 1 ? `${fresh.length} setups entered their zone` : `${first.symbol.replace("-", "/")} entered its zone`, {
          body: `${first.setup} · ${fmtPrice(first.entryLow)}–${fmtPrice(first.entryHigh)}. Check the latest plan and confirm its trigger.`, tag: "kraken-entry-zone",
        });
        notification.onclick = () => { window.focus(); onOpen(first.symbol); notification.close(); };
      } catch { setMessage("Desktop notifications are unavailable here; in-page alerts remain active."); }
    }
  }, [cycle, feed, hub, prefs, onOpen]);

  const enableSound = async () => {
    if (prefs.sound && audio.current?.state === "running") { setPrefs(p => ({ ...p, sound: false })); return; }
    try { audio.current ??= new AudioContext(); await audio.current.resume(); setPrefs(p => ({ ...p, sound: true })); setMessage("Sound armed for this tab."); }
    catch { setMessage("Your browser could not enable sound."); }
  };
  const desktop = async () => {
    if (prefs.desktop) { setPrefs(p => ({ ...p, desktop: false })); return; }
    if (typeof Notification === "undefined") { setMessage("This browser does not support desktop notifications."); return; }
    try {
      const permission = await Notification.requestPermission();
      setPrefs(p => ({ ...p, desktop: permission === "granted" }));
      setMessage(permission === "granted" ? "Desktop alerts enabled while this tab is open." : "Notification permission was not granted. In-page alerts still work.");
    } catch { setMessage("Desktop notifications are unavailable in this browser."); }
  };
  return <details className="entry-alerts shrink-0 border border-line rounded-lg bg-navy px-3 py-2 text-[12px]">
    <summary className="cursor-pointer flex items-center gap-2 list-none">
      <span className={`h-1.5 w-1.5 rounded-full ${prefs.enabled ? "bg-up" : "bg-ink-3"}`} />
      <span className="font-medium shrink-0">Entry alerts {prefs.enabled ? "on" : "off"}</span>
      <span className="text-ink-3 truncate">{notices.length ? `${notices[0].row.symbol.replace("-", "/")} · latest zone event` : "Sound + desktop notifications"}</span>
      <span className="ml-auto text-accent shrink-0">Configure + history</span>
    </summary>
    <div className="flex flex-wrap gap-2 mt-3">
      <button className="control-button" aria-pressed={prefs.enabled} onClick={() => { tracker.current.pause(); setPrefs(p => ({ ...p, enabled: !p.enabled })); }}>{prefs.enabled ? "Pause entry alerts" : "Enable entry alerts"}</button>
      <button className="control-button" aria-pressed={prefs.sound} onClick={() => void enableSound()}>{prefs.sound ? audio.current?.state === "running" ? "Sound on" : "Arm sound" : "Sound off"}</button>
      <button className="control-button" aria-pressed={prefs.desktop} onClick={() => void desktop()}>{prefs.desktop ? "Desktop on" : "Desktop off"}</button>
    </div>
    <p className="text-ink-2 mt-2">Alerts follow new entry-zone visits that pass execution checks for 2 seconds. One per coin per 10 minutes. Keep this tab open; confirm the latest trigger before acting.</p>
    {message && <p role="status" className="text-accent mt-2">{message}</p>}
    {!!notices.length && <ul className="mt-2 divide-y divide-line">{notices.map(n => <li key={n.id}><button onClick={() => onOpen(n.row.symbol)} className="flex w-full gap-3 py-2 text-left"><span className="font-medium">{n.row.symbol.replace("-", "/")}</span><span className="text-ink-2">Zone entered · inspect current plan</span><time className="ml-auto num text-ink-3">{new Date(n.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</time></button></li>)}</ul>}
  </details>;
}
