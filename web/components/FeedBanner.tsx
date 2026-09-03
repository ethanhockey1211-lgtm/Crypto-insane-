"use client";
import { useFeed, useHub } from "@/lib/store";
import { fmtAge } from "@/lib/format";

/** Loud, unambiguous, and only present when data is not live. */
export function FeedBanner() {
  const feed = useFeed();
  const hub = useHub();
  const hubDown = hub === "disconnected" || hub === "reconnecting";
  const feedDown = feed !== null && !feed.live;
  if (!hubDown && !feedDown) return null;
  const detail = hubDown
    ? hub === "reconnecting" ? "Reconnecting to the scanner…" : "Scanner API unreachable. Prices shown are the last received and are not moving."
    : `${feed?.exchange ?? "Exchange"} feed ${feed?.status ?? "unknown"}. Last event ${fmtAge(feed?.lastEventAgeMs)} ago. Reconnection is automatic.`;
  return (
    <div role="alert" className="flex items-center gap-3 px-3 py-1.5 border-b border-warn/50 bg-warn/10 text-warn">
      <span className="font-semibold tracking-[0.14em] text-[11px]">LIVE DATA INTERRUPTED</span>
      <span className="text-[12px] text-ink-2">{detail}</span>
    </div>
  );
}
