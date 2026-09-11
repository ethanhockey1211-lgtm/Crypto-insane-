import { describe, expect, it } from "vitest";
import { scanStockSetups, stockSetupIsCurrent, type StockBar, type StockMarketData, type StockScanResponse, type StockSetup } from "./stock-scanner";
import type { StockItem } from "./stocks";

const MINUTE = 60_000;
const START = Date.parse("2026-09-09T13:30:00Z");
const NOW = START + 22 * MINUTE + 5_000;
const iso = (time: number) => new Date(time).toISOString();
const ITEM: StockItem = { symbol: "NASDAQ:NVDA", name: "NVIDIA", availability: "confirmed" };
function fixture(): StockMarketData {
  const bars: StockBar[] = Array.from({ length: 22 }, (_, index) => {
    const close = index < 15 ? 100.02 + (index % 3) * 0.02 : 100.1;
    return { at: iso(START + index * MINUTE), open: close - 0.01, high: index < 15 ? 100.2 : 100.17,
      low: index < 15 ? 99.9 : 99.98, close, volume: 1_000, vwap: close };
  });
  bars[21] = { at: iso(START + 21 * MINUTE), open: 100.12, high: 100.24, low: 100.1, close: 100.22, volume: 2_000, vwap: 100.19 };
  return { ticker: "NVDA", bars, latestTrade: { price: 100.22, at: iso(NOW - 1_000) },
    latestQuote: { bid: 100.21, ask: 100.23, bidSize: 5, askSize: 8, at: iso(NOW - 1_000) }, previousClose: 99,
    dayVolume: 23_000, historyComplete: true };
}
function response(data = fixture(), asOf = NOW): StockScanResponse {
  return { status: "ready", provider: "Alpaca", feed: "iex", asOf: iso(asOf), message: null, rows: [data] };
}
function scan(data = fixture(), now = NOW, item: StockItem = ITEM): StockSetup {
  return scanStockSetups(response(data, now), [item], now)[0];
}
function noPlan(row: StockSetup) {
  expect([row.entry, row.entryMax, row.stop, row.target, row.rewardRisk]).toEqual([null, null, null, null, null]);
  expect([row.details?.target1, row.details?.target2]).toEqual([null, null]);
}

describe("native IEX stock setup engine", () => {
  it("produces a coherent confirmed opening-range entry from a completed candle, not a forming move", () => {
    const row = scan();
    expect(row.state, row.reasons.join(" ")).toBe("entry-zone");
    expect(row.setup).toBe("Opening range breakout");
    expect(row.price).toBe(100.22);
    expect(row.stop!).toBeLessThan(row.entry!);
    expect(row.price!).toBeGreaterThanOrEqual(row.entry!);
    expect(row.price!).toBeLessThanOrEqual(row.entryMax!);
    expect(row.target!).toBeGreaterThan(row.entryMax!);
    expect(row.rewardRisk!).toBeGreaterThanOrEqual(2);
    expect(row.relativeVolume).toBe(2);
    expect(row.score).toBe(100);
    expect(row.evidence.join(" ")).toContain("Heuristic ranking, not a win probability");
    expect(row.evidence.join(" ")).toContain("bar-based IEX estimate");
  });

  it("shows research for unconfirmed starter stocks while still excluding stocks marked unavailable", () => {
    const research = scan(fixture(), NOW, { ...ITEM, availability: "unconfirmed" });
    expect(research.state).toBe("entry-zone");
    expect(research.entry).not.toBeNull();
    expect(research.details!.cautions.join(" ")).toContain("availability is unconfirmed");
    const hidden = scan(fixture(), NOW, { ...ITEM, availability: "unavailable" });
    expect(hidden.state).toBe("unavailable"); noPlan(hidden);
  });

  it("recognizes a completed VWAP reclaim independently of a resistance breakout", () => {
    const data = fixture();
    data.bars = data.bars.map(bar => ({ ...bar, open: 100, high: 100.4, low: 99.6, close: 100, vwap: 100 }));
    data.bars[20] = { ...data.bars[20], open: 100, high: 100.1, low: 99.9, close: 99.98, vwap: 99.99 };
    data.bars[21] = { ...data.bars[21], open: 99.98, high: 100.05, low: 99.95, close: 100.04, vwap: 100.02, volume: 1_500 };
    data.latestTrade!.price = 100.04; data.latestQuote!.bid = 100.035; data.latestQuote!.ask = 100.045;
    const row = scan(data); expect(row.state, row.reasons.join(" ")).toBe("entry-zone"); expect(row.setup).toBe("VWAP reclaim");
  });

  it("does not let an under-volume opening-range pattern hide a valid pullback entry", () => {
    const data = fixture(); data.bars[21].volume = 1_050;
    const row = scan(data);
    expect(row.relativeVolume).toBe(1.05);
    expect(row.state, row.reasons.join(" ")).toBe("entry-zone");
    expect(row.setup).toBe("Pullback");
    expect(row.evidence.join(" ")).toContain("prior pullback candle high");
  });

  it("can confirm a 20-bar resistance breakout without a verified opening range or EMA touch", () => {
    const data = fixture();
    data.bars[20] = { ...data.bars[20], open: 100.15, high: 100.18, low: 100.145, close: 100.16, vwap: 100.16 };
    data.bars.splice(7, 1);
    const row = scan(data);
    expect(row.state, row.reasons.join(" ")).toBe("entry-zone"); expect(row.setup).toBe("Breakout");
  });

  it.each([46_000, -5_001])("rejects stale or materially future trades (%i ms age)", age => {
    const data = fixture(); data.latestTrade!.at = iso(NOW - age);
    const row = scan(data); expect(row.state).toBe("blocked"); expect(row.price).toBeNull(); noPlan(row);
  });

  it("allows at most five seconds of upstream clock skew but never uses a forming candle", () => {
    const data = fixture(); data.latestTrade!.at = iso(NOW + 5_000); data.latestQuote!.at = iso(NOW + 5_000);
    data.bars.push({ ...data.bars.at(-1)!, at: iso(START + 22 * MINUTE), high: 150, close: 149, volume: 999_999 });
    data.bars.push({ ...data.bars.at(-1)!, at: iso(START + 23 * MINUTE) });
    const row = scan(data); expect(row.state).toBe("entry-zone"); expect(row.barAt).toBe(iso(START + 21 * MINUTE)); expect(row.relativeVolume).toBe(2);
  });

  it.each([61_000, -5_001])("blocks stale or future whole-scan responses (%i ms age)", age => {
    const row = scanStockSetups(response(fixture(), NOW - age), [ITEM], NOW)[0];
    expect(row.state).toBe("blocked"); noPlan(row); expect(row.reasons.join(" ")).toContain("response");
  });

  it("requires a usable two-sided fresh quote and caps the IEX spread", () => {
    const mutations: ((data: StockMarketData) => void)[] = [
      data => { data.latestQuote = null; }, data => { data.latestQuote!.at = iso(NOW - 45_001); },
      data => { data.latestQuote!.at = iso(NOW + 5_001); }, data => { data.latestQuote!.bid = 0; },
      data => { data.latestQuote!.askSize = 0; }, data => { data.latestQuote!.bidSize = NaN; },
      data => { data.latestQuote!.ask = 100; }, data => { data.latestQuote!.ask = 101; },
    ];
    for (const mutate of mutations) {
      const data = fixture(); mutate(data); const row = scan(data);
      expect(row.state, JSON.stringify(data.latestQuote)).toBe("blocked"); noPlan(row);
    }
  });

  it("does not turn nonfinite or malformed prices into a current price or plan", () => {
    for (const price of [0, -1, NaN, Infinity]) {
      const data = fixture(); data.latestTrade!.price = price;
      const row = scan(data); expect(row.price).toBeNull(); expect(row.state).toBe("blocked"); noPlan(row);
    }
    const data = fixture(); data.previousClose = NaN; expect(scan(data).changePct).toBeNull();
    data.bars[15].high = NaN;
    const row = scan(data); expect(row.state).toBe("blocked"); expect(row.reasons.join(" ")).toContain("Invalid"); noPlan(row);
  });

  it("blocks pre-open, holidays, early close, and unknown calendars without emitting plans", () => {
    for (const when of ["2026-09-09T13:29:00Z", "2026-09-07T14:00:00Z", "2026-11-27T18:01:00Z", "2029-09-10T14:00:00Z"]) {
      const now = Date.parse(when); const row = scan(fixture(), now);
      expect(row.state).toBe("blocked"); noPlan(row); expect(row.reasons.join(" ")).toContain("scheduled regular stock session");
    }
  });

  it("requires a recent completed bar and excludes previous-session data", () => {
    const data = fixture(); data.bars = data.bars.map(bar => ({ ...bar, at: iso(Date.parse(bar.at) - 24 * 60 * MINUTE) }));
    let row = scan(data); expect(row.state).toBe("blocked"); expect(row.barAt).toBeNull(); noPlan(row);
    row = scan(fixture(), NOW + 121_000); expect(row.state).toBe("blocked");
    expect(row.reasons.join(" ")).toContain("60 seconds"); noPlan(row);
  });

  it("needs every opening minute for an opening-range label, without inventing missing IEX bars", () => {
    const data = fixture(); data.bars.splice(7, 1);
    const row = scan(data);
    expect(row.state, row.reasons.join(" ")).toBe("entry-zone");
    expect(row.setup).not.toBe("Opening range breakout");
    expect(row.evidence.join(" ")).toContain("Opening range is unconfirmed");
    expect(row.relativeVolume).toBe(2);
  });

  it("requires adjacent trigger bars and bounds the latest 21 observations to 30 minutes", () => {
    const gap = fixture(); gap.bars.splice(-2, 1);
    let row = scan(gap); expect(row.state).toBe("blocked"); expect(row.reasons.join(" ")).toContain("not adjacent"); noPlan(row);
    const sparse = fixture(); sparse.bars = sparse.bars.map((bar, index) => ({ ...bar, at: iso(START + (index < 20 ? index * 2 : 40 + index - 20) * MINUTE) }));
    const now = START + 42 * MINUTE + 5_000; sparse.latestTrade!.at = iso(now); sparse.latestQuote!.at = iso(now);
    row = scan(sparse, now); expect(row.state).toBe("blocked"); expect(row.reasons.join(" ")).toContain("more than 30 minutes"); noPlan(row);
  });

  it("requires complete session history and labels OHLC fallback VWAP as an estimate", () => {
    const data = fixture(); data.historyComplete = false;
    let row = scan(data); expect(row.state).toBe("blocked"); expect(row.vwap).toBeNull(); noPlan(row);
    data.historyComplete = true; data.bars[3].vwap = null;
    row = scan(data); expect(row.state).toBe("entry-zone"); expect(row.evidence.join(" ")).toContain("typical (high + low + close) / 3");
    data.bars.shift(); row = scan(data);
    expect(row.state, row.reasons.join(" ")).toBe("entry-zone");
    expect(row.setup).not.toBe("Opening range breakout"); expect(row.vwap).not.toBeNull();
    expect(row.evidence.join(" ")).toContain("Opening range is unconfirmed");
  });

  it("deduplicates identical observations but rejects ambiguous corrections", () => {
    const data = fixture(); data.bars.push({ ...data.bars[0] }); data.bars.reverse();
    expect(scan(data)).toEqual(scan());
    data.bars.push({ ...data.bars.find(bar => bar.at === iso(START))!, volume: 1_001 });
    const row = scan(data); expect(row.state).toBe("blocked"); noPlan(row);
  });

  it("returns extended and watch states without fabricating an executable plan", () => {
    const extended = fixture(); extended.latestTrade!.price = 100.6; extended.latestQuote!.bid = 100.59; extended.latestQuote!.ask = 100.61;
    let row = scan(extended); expect(row.state).toBe("extended"); expect(row.reasons.join(" ")).toContain("rather than chase"); noPlan(row);
    const watch = fixture(); watch.bars[21] = { ...watch.bars[20], at: iso(START + 21 * MINUTE), close: 100.12, open: 100.11 };
    row = scan(watch); expect(row.state).toBe("watch"); expect(row.reasons.join(" ")).toContain("wait"); noPlan(row);
    const quiet = fixture(); quiet.bars[21].volume = 500;
    row = scan(quiet); expect(row.state).toBe("watch"); expect(row.reasons.join(" ")).toContain("volume"); noPlan(row);
  });

  it("uses the IEX ask as well as the last trade to prevent a misleading entry-zone label", () => {
    const data = fixture(); data.latestQuote!.ask = 100.4;
    let row = scan(data); expect(row.state).toBe("extended"); noPlan(row);
    data.latestQuote!.bid = 100.1; data.latestQuote!.ask = 100.12;
    row = scan(data); expect(row.state).toBe("watch"); noPlan(row);
  });

  it("rejects mismatched or duplicate response tickers and non-US watchlist symbols", () => {
    const data = fixture(); data.ticker = "AMD";
    let row = scan(data); expect(row.state).toBe("blocked"); expect(row.price).toBeNull(); noPlan(row);
    const duplicate = response(); duplicate.rows.push(fixture());
    row = scanStockSetups(duplicate, [ITEM], NOW)[0]; expect(row.state).toBe("blocked"); noPlan(row);
    row = scan(fixture(), NOW, { ...ITEM, symbol: "KRAKEN:NVDA" }); expect(row.state).toBe("blocked"); noPlan(row);
  });

  it("sorts actionable states before research and blocked states, with deterministic symbol ties", () => {
    const a = fixture(); a.ticker = "AAPL";
    const b = fixture(); b.ticker = "MSFT";
    const c = fixture(); c.ticker = "AMD"; c.latestTrade!.at = iso(NOW - MINUTE);
    const payload = response(); payload.rows = [b, a, c];
    const list = ["MSFT", "AMD", "AAPL"].map(ticker => ({ ...ITEM, symbol: `NASDAQ:${ticker}` }));
    const original = JSON.stringify({ payload, list });
    const rows = scanStockSetups(payload, list, NOW);
    expect(rows.map(row => row.ticker)).toEqual(["AAPL", "MSFT", "AMD"]);
    expect(JSON.stringify({ payload, list })).toBe(original);
  });
});

describe("explainable stock setup details", () => {
  it("accounts for every point in the unchanged 100-point entry checklist", () => {
    const row = scan(), details = row.details!;
    expect(details.scoreFactors.map(factor => factor.possible)).toEqual([20, 15, 10, 15, 15, 10, 15]);
    expect(details.scoreFactors.map(factor => factor.earned)).toEqual([20, 15, 10, 15, 15, 10, 15]);
    expect(details.scoreFactors.reduce((sum, factor) => sum + factor.earned, 0)).toBe(row.score);
    expect(details.triggerPrice).toBe(100.2);
    expect(details.triggerConfirmed).toBe(true);
    expect(details.thesis).toContain("last completed close was 100.22");
    expect(details.thesis).toContain("2.00×");
    expect(details.confirmation).toContain("at least 1.10×");
    expect(details.confirmation).toContain("09:30–09:44 ET");
    expect(details.trendLabel).toContain("Upward");
    expect(details.invalidation).toContain(row.stop!.toFixed(2));
    expect(details.invalidation).toContain("three-bar low of 99.98");
  });

  it("shows unmet trend, VWAP, pattern and zone checks rather than hiding them", () => {
    const data = fixture();
    data.bars = data.bars.map((bar, index) => {
      const close = 100 - index * 0.1;
      return { ...bar, open: close + 0.02, close, high: close + 0.05, low: close - 0.05, vwap: close, volume: 1_000 };
    });
    data.latestTrade!.price = 97.9; data.latestQuote!.bid = 97.895; data.latestQuote!.ask = 97.905;
    const row = scan(data), details = row.details!;
    expect(row.state).toBe("watch"); noPlan(row);
    expect(row.score).toBe(30);
    expect(details.scoreFactors.map(factor => factor.earned)).toEqual([0, 0, 5, 0, 15, 10, 0]);
    expect(details.scoreFactors.reduce((sum, factor) => sum + factor.earned, 0)).toBe(row.score);
    expect(details.scoreFactors.filter(factor => factor.earned === 0).every(factor => factor.detail.length > 30)).toBe(true);
    expect(details.triggerPrice).toBeNull();
    expect(details.triggerConfirmed).toBe(false);
    expect(details.confirmation).toContain("No conditional trigger");
  });

  it("explains partial volume and spread points, and zero points below the thresholds", () => {
    const partialVolume = fixture(); partialVolume.bars[21].volume = 1_050;
    const partialSpread = fixture(); partialSpread.latestQuote!.bid = 100;
    const lowVolume = fixture(); lowVolume.bars[21].volume = 500;
    const wideSpread = fixture(); wideSpread.latestQuote!.ask = 101;
    for (const [data, factor, expected] of [
      [partialVolume, "IEX relative volume", 5], [partialSpread, "IEX quoted spread", 5],
      [lowVolume, "IEX relative volume", 0], [wideSpread, "IEX quoted spread", 0],
    ] as const) {
      const row = scan(data), factors = row.details!.scoreFactors;
      expect(factors.find(value => value.label === factor)!.earned).toBe(expected);
      expect(factors.reduce((sum, value) => sum + value.earned, 0)).toBe(row.score);
      expect(factors.reduce((sum, value) => sum + value.possible, 0)).toBe(100);
    }
  });

  it("supplies precisely labeled observed levels, with no forecast disguised as a level", () => {
    const row = scan(), levels = row.details!.levels;
    expect(levels).toContainEqual({ label: "Recent three completed IEX bars' low", price: 99.98, kind: "support" });
    expect(levels).toContainEqual({ label: "Preceding 20 observed IEX bars' high", price: 100.2, kind: "resistance" });
    expect(levels).toContainEqual({ label: "Verified 09:30–09:44 ET opening-range high", price: 100.2, kind: "resistance" });
    expect(levels).toContainEqual({ label: "Estimated IEX session VWAP", price: row.vwap, kind: "reference" });
    expect(levels).toContainEqual({ label: "EMA9 of completed observed IEX bars", price: row.ema9, kind: "reference" });
    expect(levels).toContainEqual({ label: "EMA20 of completed observed IEX bars", price: row.ema20, kind: "reference" });
    expect(levels.every(level => Number.isFinite(level.price) && level.price > 0)).toBe(true);
    expect(levels.some(level => level.price === row.target)).toBe(false);
  });

  it.each([1, 0.001, 1_000])("places 1R and 2R detail targets beyond the upper entry using the structural stop (scale %s)", scale => {
    const data = fixture();
    data.bars = data.bars.map(bar => ({ ...bar, open: bar.open * scale, high: bar.high * scale, low: bar.low * scale,
      close: bar.close * scale, vwap: bar.vwap === null ? null : bar.vwap * scale }));
    data.latestTrade!.price *= scale; data.latestQuote!.bid *= scale; data.latestQuote!.ask *= scale;
    const row = scan(data), details = row.details!;
    expect(row.state).toBe("entry-zone");
    expect(details.target2).toBe(row.target);
    expect(details.target1!).toBeGreaterThan(row.entryMax!);
    expect(details.target1!).toBeLessThan(details.target2!);
    const upperRisk = row.entryMax! - row.stop!;
    expect((details.target1! - row.entryMax!) / upperRisk).toBeCloseTo(1, 8);
    expect((details.target2! - row.entryMax!) / upperRisk).toBeCloseTo(2, 8);
    // Lower-zone sizing would overstate the reward: both milestones use the same conservative upper entry.
    expect((details.target1! - row.entry!) / (row.entry! - row.stop!)).toBeGreaterThan(1);
  });

  it("keeps a watch trigger conditional and never publishes target details until entry eligibility passes", () => {
    const data = fixture(); data.bars[21].volume = 500;
    const watch = scan(data);
    expect(watch.state).toBe("watch"); noPlan(watch);
    expect(watch.details!.triggerConfirmed).toBe(true);
    expect(watch.details!.triggerPrice).toBe(100.2);
    expect(watch.details!.confirmation).toContain("pattern confirmation alone does not qualify an entry");
    expect(watch.details!.cautions.join(" ")).toContain("Await IEX closed-bar volume");
    const unavailable = scan(fixture(), NOW, { ...ITEM, availability: "unavailable" });
    expect(unavailable.details!.triggerConfirmed).toBe(true); noPlan(unavailable);
    expect(unavailable.details!.cautions.join(" ")).toContain("Marked unavailable in your Kraken account");
    const near = fixture(); near.bars[21] = { ...near.bars[20], at: iso(START + 21 * MINUTE), close: 100.12, open: 100.11 };
    const conditional = scan(near);
    expect(conditional.state).toBe("watch"); noPlan(conditional);
    expect(conditional.details!.triggerConfirmed).toBe(false);
    expect(conditional.details!.triggerPrice).not.toBeNull();
    expect(conditional.details!.confirmation).toContain("Still required");
  });

  it("carries stale and sparse observations into cautions and does not fabricate a missing opening range", () => {
    const stale = fixture(); stale.latestTrade!.at = iso(NOW - 46_000);
    const blocked = scan(stale); expect(blocked.state).toBe("blocked"); noPlan(blocked);
    expect(blocked.details!.cautions.join(" ")).toContain("stale");
    expect(blocked.details!.scoreFactors.find(factor => factor.label === "Fresh trade and quote")!.earned).toBe(0);
    expect(blocked.details!.scoreFactors.reduce((sum, factor) => sum + factor.earned, 0)).toBe(blocked.score);
    const sparse = fixture(); sparse.bars.splice(7, 1);
    const row = scan(sparse);
    expect(row.details!.cautions.join(" ")).toContain("21 completed bars cover 22 one-minute slots");
    expect(row.details!.cautions.join(" ")).toContain("opening range is unconfirmed");
    expect(row.details!.levels.some(level => level.label.includes("opening-range"))).toBe(false);
  });

  it("identifies an overhead observed high as a reference instead of a promised barrier", () => {
    const data = fixture();
    data.bars = data.bars.map(bar => ({ ...bar, open: 100, high: 100.4, low: 99.6, close: 100, vwap: 100 }));
    data.bars[20] = { ...data.bars[20], open: 100, high: 100.1, low: 99.9, close: 99.98, vwap: 99.99 };
    data.bars[21] = { ...data.bars[21], open: 99.98, high: 100.05, low: 99.95, close: 100.04, vwap: 100.02, volume: 1_500 };
    data.latestTrade!.price = 100.04; data.latestQuote!.bid = 100.035; data.latestQuote!.ask = 100.045;
    const row = scan(data); expect(row.state).toBe("entry-zone");
    expect(row.details!.cautions.join(" ")).toContain("high at 100.40");
    expect(row.details!.cautions.join(" ")).toContain("not a guaranteed price barrier");
  });

  it("provides a zero-point unassessed rubric and no levels when usable history or matching data is absent", () => {
    const data = fixture(); data.bars = [];
    const noMatch = response(); noMatch.rows = [];
    for (const row of [scan(data), scanStockSetups(noMatch, [ITEM], NOW)[0]]) {
      expect(row.state).toBe("blocked"); noPlan(row);
      expect(row.score).toBe(0);
      expect(row.details!.scoreFactors.reduce((sum, factor) => sum + factor.possible, 0)).toBe(100);
      expect(row.details!.scoreFactors.every(factor => factor.earned === 0 && factor.detail.includes("Not assessed"))).toBe(true);
      expect(row.details!.levels).toEqual([]);
      expect(row.details!.triggerPrice).toBeNull();
      expect(row.details!.invalidation).toContain("No active entry plan");
    }
  });
});

describe("displayed stock entry freshness", () => {
  it("expires a held plan when the trade or quote reaches 45 seconds without requiring a new scan", () => {
    const payload = response(), setup = scanStockSetups(payload, [ITEM], NOW)[0];
    expect(stockSetupIsCurrent(setup, payload, NOW + 44_000)).toBe(true);
    expect(stockSetupIsCurrent(setup, payload, NOW + 44_001)).toBe(false);
    payload.rows[0].latestQuote!.at = iso(NOW + 44_000);
    expect(stockSetupIsCurrent(setup, payload, NOW + 44_001)).toBe(false); // trade alone has expired
    payload.rows[0].latestTrade!.at = iso(NOW + 44_000); payload.rows[0].latestQuote!.at = iso(NOW - 1_000);
    expect(stockSetupIsCurrent(setup, payload, NOW + 44_001)).toBe(false); // quote alone has expired
  });

  it("independently enforces response and completed-bar expiry and rejects a forming bar", () => {
    const payload = response(), setup = scanStockSetups(payload, [ITEM], NOW)[0];
    payload.asOf = iso(NOW - 60_000); expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(true);
    payload.asOf = iso(NOW - 60_001); expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(false);
    const lastFresh = NOW + 55_000;
    payload.asOf = iso(lastFresh); payload.rows[0].latestTrade!.at = iso(lastFresh); payload.rows[0].latestQuote!.at = iso(lastFresh);
    expect(stockSetupIsCurrent(setup, payload, lastFresh)).toBe(true);
    expect(stockSetupIsCurrent(setup, payload, lastFresh + 1)).toBe(false);
    expect(stockSetupIsCurrent({ ...setup, barAt: iso(Math.floor(lastFresh / MINUTE) * MINUTE) }, payload, lastFresh)).toBe(false);
  });

  it("withdraws a plan when the feed, symbol, quote, price zone, or session no longer qualifies", () => {
    const setup = scan();
    expect(stockSetupIsCurrent(setup, null, NOW)).toBe(false);
    expect(stockSetupIsCurrent({ ...setup, state: "watch" }, response(), NOW)).toBe(false);
    expect(stockSetupIsCurrent({ ...setup, ticker: "AMD" }, response(), NOW)).toBe(false);
    expect(stockSetupIsCurrent(setup, { ...response(), status: "error" }, NOW)).toBe(false);
    expect(stockSetupIsCurrent(setup, response(), Date.parse("2026-09-09T20:00:00Z"))).toBe(false);
    const payload = response(); payload.rows[0].latestQuote!.ask = setup.entryMax! + 0.01;
    expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(false);
    payload.rows[0].latestQuote!.ask = setup.entry! - 0.01;
    expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(false);
    payload.rows[0].latestQuote = null;
    expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(false);
  });

  it("rejects materially future timestamps while tolerating five seconds of clock skew", () => {
    const setup = scan(), payload = response();
    payload.asOf = iso(NOW + 5_000); payload.rows[0].latestTrade!.at = iso(NOW + 5_000); payload.rows[0].latestQuote!.at = iso(NOW + 5_000);
    expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(true);
    payload.rows[0].latestTrade!.at = iso(NOW + 5_001);
    expect(stockSetupIsCurrent(setup, payload, NOW)).toBe(false);
  });
});

function followThrough(data: StockMarketData, count: number): number {
  for (let i = 0; i < count; i++) data.bars.push({ at: iso(START + (22 + i) * MINUTE),
    open: 100.23, close: 100.23, high: 100.24, low: 100.21, volume: 400, vwap: 100.23 });
  const now = NOW + count * MINUTE;
  data.latestTrade = { price: 100.23, at: iso(now) };
  data.latestQuote = { bid: 100.22, ask: 100.24, bidSize: 5, askSize: 8, at: iso(now) };
  return now;
}

describe("stock trigger discovery across scans", () => {
  it("accepts a normal completed breakout beyond the old quarter-ATR ceiling", () => {
    const data = fixture();
    data.bars[21] = { ...data.bars[21], close: 100.35, high: 100.37 };
    data.latestTrade!.price = 100.35; data.latestQuote!.bid = 100.34; data.latestQuote!.ask = 100.36;
    const result = scan(data);
    expect(result.state, result.reasons.join(" ")).toBe("entry-zone");
    expect(result.price!).toBeGreaterThan(result.details!.triggerPrice! + 0.27 * result.atr!);
    expect(result.entryMax!).toBeCloseTo(100.35 + 0.25 * result.atr!);
  });

  it("retains original geometry and trigger volume through quiet follow-through candles", () => {
    const original = scan(), data = fixture(), now = followThrough(data, 2);
    const retained = scan(data, now);
    expect(retained.state, retained.reasons.join(" ")).toBe("entry-zone");
    expect(retained.triggerAt).toBe(original.triggerAt);
    expect(retained.barAt).toBe(data.bars.at(-1)!.at);
    expect([retained.entry, retained.entryMax, retained.stop, retained.target]).toEqual([original.entry, original.entryMax, original.stop, original.target]);
    expect(retained.relativeVolume!).toBeLessThan(1);
    expect(retained.details!.confirmation).toContain("Trigger bar: 2.00×");
    expect(stockSetupIsCurrent(retained, response(data, now), now)).toBe(true);
  });

  it("expires the original trigger by elapsed time, including between scans", () => {
    const data = fixture(); followThrough(data, 3);
    const boundary = START + 25 * MINUTE;
    data.latestTrade!.at = iso(boundary); data.latestQuote!.at = iso(boundary);
    const payload = response(data, boundary), current = scanStockSetups(payload, [ITEM], boundary)[0];
    expect(current.state).toBe("entry-zone");
    expect(stockSetupIsCurrent(current, payload, boundary)).toBe(true);
    expect(stockSetupIsCurrent(current, payload, boundary + 1)).toBe(false);
    expect(scan(data, boundary + 1).state).toBe("watch");
    expect(stockSetupIsCurrent({ ...current, triggerAt: "invalid" }, payload, boundary)).toBe(false);
  });

  it.each(["stop", "below-trigger", "missing-minute"] as const)("does not revive an invalidated trigger after a recovery: %s", cause => {
    const data = fixture(), now = followThrough(data, 2);
    if (cause === "stop") data.bars[22].low = scan().stop!;
    if (cause === "below-trigger") Object.assign(data.bars[22], { close: 100.15, low: 100.14, vwap: 100.15 });
    if (cause === "missing-minute") data.bars.splice(22, 1);
    const result = scan(data, now);
    expect(result.state).not.toBe("entry-zone"); noPlan(result);
  });

  it("withdraws a retained plan when a trailing completed minute is missing", () => {
    const data = fixture(), firstNow = followThrough(data, 1);
    const current = scan(data, firstNow);
    const missingMinuteNow = firstNow + MINUTE;
    data.latestTrade!.at = iso(missingMinuteNow); data.latestQuote!.at = iso(missingMinuteNow);
    const payload = response(data, missingMinuteNow);
    expect(stockSetupIsCurrent(current, payload, missingMinuteNow)).toBe(false);
    const result = scanStockSetups(payload, [ITEM], missingMinuteNow)[0];
    expect(result.state).toBe("blocked"); noPlan(result);
    expect(result.reasons.join(" ")).toContain("latest completed IEX minute is missing");
  });

  it("cannot rescue an under-volume trigger with a later high-volume candle", () => {
    const data = fixture(); data.bars[21].volume = 500;
    const now = followThrough(data, 1); data.bars[22].volume = 10_000;
    const result = scan(data, now);
    expect(result.state).toBe("watch"); noPlan(result);
    expect(result.reasons.join(" ")).toContain("This trigger measured 0.50×");
  });

  it("rejects oversized confirming candles and prices above the fixed retained ceiling", () => {
    const data = fixture(); data.bars[21] = { ...data.bars[21], high: 100.82, close: 100.8 };
    data.latestTrade!.price = 100.8; data.latestQuote!.bid = 100.79; data.latestQuote!.ask = 100.81;
    const oversized = scan(data);
    expect(oversized.state).toBe("extended"); noPlan(oversized);
    expect(oversized.reasons.join(" ")).toContain("more than 1 ATR");
    const retained = fixture(), now = followThrough(retained, 1);
    retained.latestQuote!.ask = scan().entryMax! + 0.01;
    const result = scan(retained, now);
    expect(result.state).toBe("extended"); noPlan(result);
  });
});
