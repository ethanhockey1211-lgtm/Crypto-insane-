import { describe, expect, it } from "vitest";
import { getStockSession } from "./stock-session";

const session = (at: string) => getStockSession(new Date(at));

describe("stock session schedule", () => {
  it("uses inclusive regular open and exclusive regular close boundaries", () => {
    expect(session("2026-09-09T13:29:59.999Z").phase).toBe("before-open");
    expect(session("2026-09-09T13:30:00Z").phase).toBe("regular");
    expect(session("2026-09-09T19:59:59.999Z").phase).toBe("regular");
    expect(session("2026-09-09T20:00:00Z").phase).toBe("after-close");
  });

  it.each([
    ["2026-03-06T14:30:00Z", "09:30 ET", "08:30 CT"], // before spring DST
    ["2026-03-09T13:30:00Z", "09:30 ET", "08:30 CT"], // after spring DST
    ["2026-10-30T13:30:00Z", "09:30 ET", "08:30 CT"], // before autumn DST
    ["2026-11-02T14:30:00Z", "09:30 ET", "08:30 CT"], // after autumn DST
  ])("handles Eastern and Minnesota clocks across DST: %s", (at, easternTime, centralTime) => {
    expect(session(at)).toMatchObject({ phase: "regular", easternTime, centralTime });
  });

  it.each([
    "2026-01-01", "2026-01-19", "2026-02-16", "2026-04-03", "2026-05-25",
    "2026-06-19", "2026-07-03", "2026-09-07", "2026-11-26", "2026-12-25",
    "2027-01-01", "2027-01-18", "2027-02-15", "2027-03-26", "2027-05-31",
    "2027-06-18", "2027-07-05", "2027-09-06", "2027-11-25", "2027-12-24",
    "2028-01-17", "2028-02-21", "2028-04-14", "2028-05-29", "2028-06-19",
    "2028-07-04", "2028-09-04", "2028-11-23", "2028-12-25",
  ])("marks the published NYSE holiday %s closed", date => {
    expect(session(`${date}T16:00:00Z`).phase).toBe("closed");
  });

  it("identifies holidays by name and weekends independently of host timezone", () => {
    expect(session("2026-09-07T16:00:00Z").detail).toContain("Labor Day");
    expect(session("2026-09-12T16:00:00Z").detail).toContain("Weekend");
    // It is Sunday in UTC but still Saturday evening in New York.
    expect(session("2026-09-13T00:30:00Z")).toMatchObject({ phase: "closed", easternTime: "20:30 ET", centralTime: "19:30 CT" });
  });

  it.each([
    "2026-11-27T18:00:00Z", "2026-12-24T18:00:00Z", "2027-11-26T18:00:00Z",
    "2028-07-03T17:00:00Z", "2028-11-24T18:00:00Z",
  ])("ends early sessions at 13:00 Eastern: %s", close => {
    const boundary = new Date(close);
    expect(getStockSession(new Date(boundary.getTime() - 1)).phase).toBe("regular");
    expect(getStockSession(boundary)).toMatchObject({ phase: "after-close", easternTime: "13:00 ET", centralTime: "12:00 CT" });
    expect(getStockSession(boundary).detail).toContain("early close");
  });

  it("does not import bank-holiday or generic holiday-eve rules", () => {
    // These dates retain their full regular session in the published calendar.
    expect(session("2026-07-02T19:00:00Z").phase).toBe("regular");
    expect(session("2027-12-31T20:00:00Z").phase).toBe("regular");
    expect(session("2026-10-12T19:00:00Z").phase).toBe("regular"); // Columbus Day
    expect(session("2026-11-11T20:00:00Z").phase).toBe("regular"); // Veterans Day
  });

  it("uses the Eastern calendar date at midnight and at year boundaries", () => {
    expect(session("2026-09-08T00:30:00Z").detail).toContain("Labor Day");
    expect(session("2027-01-02T00:30:00Z").detail).toContain("New Year's Day");
    expect(session("2026-01-01T00:30:00Z").phase).toBe("unknown"); // still 2025 in New York
    expect(session("2029-01-01T00:30:00Z").phase).toBe("closed"); // still Sunday in supported 2028
  });

  it.each(["2025-09-09T16:00:00Z", "2029-09-10T16:00:00Z"])("does not invent sessions outside the calendar: %s", at => {
    expect(session(at).phase).toBe("unknown");
    expect(session(at).detail).toContain("2026–2028");
  });

  it("handles an invalid clock without throwing or claiming the market is open", () => {
    expect(getStockSession(new Date(NaN))).toMatchObject({ phase: "unknown", easternTime: "—", centralTime: "—" });
  });

  it("describes scheduled sessions, not verified live status, and does not show seconds", () => {
    const result = session("2026-09-09T16:23:59Z");
    expect(result.label).toContain("Scheduled");
    expect(result.detail).toContain("live exchange status and stock halts are not verified");
    expect(result.easternTime).toBe("12:23 ET");
    expect(result.centralTime).toBe("11:23 CT");
  });
});
