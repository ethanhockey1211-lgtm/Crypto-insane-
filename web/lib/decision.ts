import type { ScannerRow } from "./types";

export function decision(row: ScannerRow, live: boolean) {
  if (!live || row.stale) return { state: "unavailable", label: "Data unavailable", reason: "Wait for a fresh scanner cycle and a connected feed." } as const;
  if (row.executionStatus === "Blocked" || row.doNotChase || row.entryState === "Chase")
    return { state: "avoid", label: "Avoid this entry", reason: executionBlockers(row)[0] } as const;
  if (row.executionStatus !== "Watch" || !hasPlan(row) || row.entryState == null || row.netRewardRatio == null || !Number.isFinite(row.netRewardRatio) || row.netRewardRatio <= 0)
    return { state: "unavailable", label: "Assessment unavailable", reason: "A score alone is not an entry signal." } as const;
  if (row.entryState === "Late")
    return { state: "wait", label: "Wait for a pullback", reason: "Price is above the planned entry zone but below the no-chase ceiling. Do not market in; wait for a retest or a fresh plan." } as const;
  if (row.entryState !== "InZone") return { state: "wait", label: "Wait for the zone", reason: "Price has not reached the planned entry zone. Open the plan for the trigger." } as const;
  return { state: "watch", label: "Check confirmation", reason: "Execution checks pass. Verify the plan’s stated trigger; this is not a buy signal." } as const;
}

/** All distinct reasons matter: a low score can otherwise hide a spread or data problem. */
export function executionBlockers(row: ScannerRow): string[] {
  const reasons = row.executionStatus === "Blocked"
    ? (row.executionReasons?.length ? row.executionReasons : [row.executionReason ?? ""])
    : [];
  const unique = new Set(reasons.map(reason => reason.trim()).filter(Boolean));
  if (row.entryState === "Chase") unique.add("Price is above the no-chase ceiling.");
  else if (row.doNotChase) unique.add("Price is overextended; wait for a fresh plan.");
  return unique.size ? [...unique] : ["Execution checks did not pass."];
}

function hasPlan(row: ScannerRow): boolean {
  return row.entry != null && Number.isFinite(row.entry) && row.entry > 0 &&
    row.stop != null && Number.isFinite(row.stop) && row.stop > 0 && row.stop < row.entry &&
    row.target1 != null && Number.isFinite(row.target1) && row.target1 > row.entry;
}

const evidenceOrder = (a: ScannerRow, b: ScannerRow) => b.score - a.score ||
  (b.netRewardRatio ?? -Infinity) - (a.netRewardRatio ?? -Infinity) || a.symbol.localeCompare(b.symbol);

/** Built from the entire scanner snapshot, independent of the detailed table's filters. */
export function decisionSummary(rows: ScannerRow[], live: boolean) {
  const ready: ScannerRow[] = [], waiting: ScannerRow[] = [], blocked: ScannerRow[] = [], unavailable: ScannerRow[] = [];
  for (const row of rows) {
    switch (decision(row, live).state) {
      case "watch": ready.push(row); break;
      case "wait": waiting.push(row); break;
      case "avoid": blocked.push(row); break;
      default: unavailable.push(row);
    }
  }
  ready.sort(evidenceOrder);
  waiting.sort(evidenceOrder);
  const developing = blocked.filter(row => row.setupBias === "Bullish" && row.setup !== "None" && hasPlan(row)).sort(evidenceOrder);
  const counts = new Map<string, number>();
  for (const row of blocked) for (const reason of executionBlockers(row)) counts.set(reason, (counts.get(reason) ?? 0) + 1);
  const blockers = [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  return { ready, waiting, blocked, unavailable, developing, blockers };
}

export function scannerIsFresh(at: string | null, now: number) {
  if (!at) return false;
  const age = now - Date.parse(at);
  return Number.isFinite(age) && age >= -2000 && age <= 10000;
}

export function shortlist(rows: ScannerRow[], live: boolean) {
  const { ready, waiting } = decisionSummary(rows, live);
  return [...ready, ...waiting].slice(0, 3);
}
