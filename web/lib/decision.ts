import type { ScannerRow } from "./types";

export function decision(row: ScannerRow, live: boolean) {
  if (!live || row.stale) return { state: "unavailable", label: "Data unavailable", reason: "Wait for a fresh scanner cycle and a connected feed." } as const;
  if (row.executionStatus === "Blocked" || row.doNotChase || row.entryState === "Chase" || row.entryState === "Late")
    return { state: "avoid", label: "Avoid this entry", reason: row.executionReason || "Price is extended or the trade fails execution checks." } as const;
  if (row.executionStatus !== "Watch" || row.netRewardRatio == null || !Number.isFinite(row.netRewardRatio) || row.netRewardRatio <= 0)
    return { state: "unavailable", label: "Assessment unavailable", reason: "A score alone is not an entry signal." } as const;
  if (row.entryState !== "InZone") return { state: "wait", label: "Wait for the zone", reason: "Price has not reached the planned entry zone. Open the plan for the trigger." } as const;
  return { state: "watch", label: "Check confirmation", reason: "Execution checks pass. Verify the plan’s stated trigger; this is not a buy signal." } as const;
}

export function scannerIsFresh(at: string | null, now: number) {
  if (!at) return false;
  const age = now - Date.parse(at);
  return Number.isFinite(age) && age >= -2000 && age <= 10000;
}

export function shortlist(rows: ScannerRow[], live: boolean) {
  return rows.filter(r => {
    const state = decision(r, live).state;
    return state === "watch" || state === "wait";
  }).sort((a, b) => Number(b.entryState === "InZone") - Number(a.entryState === "InZone") || b.score - a.score || a.symbol.localeCompare(b.symbol)).slice(0, 3);
}
