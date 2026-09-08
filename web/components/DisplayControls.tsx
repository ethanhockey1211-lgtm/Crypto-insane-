"use client";
import { store, useDisplay } from "@/lib/display";

export function DisplayControls() {
  const display = useDisplay();
  const time = display.capturedAt == null ? "Waiting for data" : new Date(display.capturedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  return <div className="shrink-0 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-line bg-navy px-3 py-2 text-[12px]" aria-label="Display controls">
    <span className={display.paused ? "text-warn font-medium" : "text-ink-2"}>{display.paused ? "Display paused · reference snapshot" : "Display refreshes every 5 seconds"}</span>
    <span className="num text-[11px] text-ink-3">Shown {time}</span>
    <div className="ml-auto flex gap-2">
      <button type="button" className="control-button" aria-pressed={display.paused} onClick={() => store.setPaused(!display.paused)}>{display.paused ? "Resume display" : "Pause display"}</button>
      <button type="button" className="control-button" onClick={store.capture}>Refresh now</button>
    </div>
    {display.paused && <p className="basis-full text-[11px] text-ink-2">Prices, rankings and plans are held for reading. Live data and enabled alerts continue. Refresh before using a setup.</p>}
  </div>;
}
