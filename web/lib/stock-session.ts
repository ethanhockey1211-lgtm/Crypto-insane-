export type StockSessionPhase = "regular" | "before-open" | "after-close" | "closed" | "unknown";

export interface StockSession {
  label: string;
  detail: string;
  phase: StockSessionPhase;
  easternTime: string;
  centralTime: string;
}

// Published NYSE cash-equity calendar, checked September 9, 2026.
// https://www.nyse.com/trade/hours-calendars
// https://ir.theice.com/press/news-details/2025/NYSE-Group-Announces-2026-2027-and-2028-Holiday-and-Early-Closings-Calendar/default.aspx
// Use the published dates instead of generic bank-holiday rules. In particular,
// July 2, 2026 is a full session and December 31, 2027 is not a New Year's holiday.
const HOLIDAYS: Readonly<Record<string, string>> = {
  "2026-01-01": "New Year's Day",
  "2026-01-19": "Martin Luther King Jr. Day",
  "2026-02-16": "Washington's Birthday",
  "2026-04-03": "Good Friday",
  "2026-05-25": "Memorial Day",
  "2026-06-19": "Juneteenth",
  "2026-07-03": "Independence Day observed",
  "2026-09-07": "Labor Day",
  "2026-11-26": "Thanksgiving Day",
  "2026-12-25": "Christmas Day",
  "2027-01-01": "New Year's Day",
  "2027-01-18": "Martin Luther King Jr. Day",
  "2027-02-15": "Washington's Birthday",
  "2027-03-26": "Good Friday",
  "2027-05-31": "Memorial Day",
  "2027-06-18": "Juneteenth observed",
  "2027-07-05": "Independence Day observed",
  "2027-09-06": "Labor Day",
  "2027-11-25": "Thanksgiving Day",
  "2027-12-24": "Christmas Day observed",
  "2028-01-17": "Martin Luther King Jr. Day",
  "2028-02-21": "Washington's Birthday",
  "2028-04-14": "Good Friday",
  "2028-05-29": "Memorial Day",
  "2028-06-19": "Juneteenth",
  "2028-07-04": "Independence Day",
  "2028-09-04": "Labor Day",
  "2028-11-23": "Thanksgiving Day",
  "2028-12-25": "Christmas Day",
};

const EARLY_CLOSES = new Set([
  "2026-11-27", "2026-12-24", "2027-11-26", "2028-07-03", "2028-11-24",
]);

const easternParts = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit",
  weekday: "short", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
});
const easternClock = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/New_York", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
});
const centralClock = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/Chicago", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
});
const SCHEDULE_ONLY = "Scheduled hours only; live exchange status and stock halts are not verified.";

/** Regular cash-equity schedule, not a live market-status or broker-eligibility check. */
export function getStockSession(now: Date): StockSession {
  if (!Number.isFinite(now.getTime())) return {
    phase: "unknown", label: "Session schedule unknown", detail: "A valid clock time is unavailable.",
    easternTime: "—", centralTime: "—",
  };

  const parts = Object.fromEntries(easternParts.formatToParts(now).map(part => [part.type, part.value]));
  const clocks = { easternTime: `${easternClock.format(now)} ET`, centralTime: `${centralClock.format(now)} CT` };
  const year = Number(parts.year);
  if (year < 2026 || year > 2028) return {
    ...clocks, phase: "unknown", label: "Session schedule unknown",
    detail: "This calendar covers 2026–2028. Check the exchange schedule for this date.",
  };

  const date = `${parts.year}-${parts.month}-${parts.day}`;
  const holiday = HOLIDAYS[date];
  if (holiday || parts.weekday === "Sat" || parts.weekday === "Sun") return {
    ...clocks, phase: "closed", label: "Scheduled market closure",
    detail: `${holiday ?? "Weekend"}: no regular session. ${SCHEDULE_ONLY}`,
  };

  const earlyClose = EARLY_CLOSES.has(date);
  const closeMinutes = (earlyClose ? 13 : 16) * 60;
  const minute = Number(parts.hour) * 60 + Number(parts.minute);
  const hours = earlyClose ? "09:30–13:00 ET / 08:30–12:00 CT (early close)" : "09:30–16:00 ET / 08:30–15:00 CT";
  const phase = minute < 9 * 60 + 30 ? "before-open" : minute >= closeMinutes ? "after-close" : "regular";
  const label = phase === "before-open" ? "Before scheduled open"
    : phase === "after-close" ? "After scheduled close" : "Scheduled regular session";
  return { ...clocks, phase, label, detail: `Today's regular session: ${hours}. ${SCHEDULE_ONLY}` };
}
