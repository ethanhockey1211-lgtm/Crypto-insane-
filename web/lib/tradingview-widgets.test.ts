import { describe, expect, it } from "vitest";
import { stockWidgetDefinition, validWidgetSymbol } from "./tradingview-widgets";

describe("stock widget boundaries", () => {
  it("rejects crypto and ambiguous symbols instead of silently choosing a different market", () => {
    for (const symbol of ["KRAKEN:BTCUSD", "BINANCE:BTCUSDT", "LSE:VOD", "NVDA", "nasdaq:nvda", "NASDAQ:NVDA?symbol=NYSE:IBM", "NASDAQ:NVDA\"</script>"]) {
      expect(validWidgetSymbol(symbol), symbol).toBe(false);
      expect(() => stockWidgetDefinition("chart", symbol)).toThrow();
    }
    expect(validWidgetSymbol("NYSE:BRK.B")).toBe(true);
    expect(validWidgetSymbol("AMEX:SPY")).toBe(true);
  });

  it("locks the chart to the native plan symbol while allowing explicit timeframe changes", () => {
    const initial = stockWidgetDefinition("chart", "NASDAQ:NVDA");
    expect(initial.config).toMatchObject({ symbol: "NASDAQ:NVDA", interval: "5", allow_symbol_change: false, hide_top_toolbar: true, hide_volume: false });
    expect(initial.config.studies).toEqual(["STD;EMA", "STD;VWAP"]);
    const changed = stockWidgetDefinition("chart", "NYSE:BRK.B", "15");
    expect(changed.config).toMatchObject({ symbol: "NYSE:BRK.B", interval: "15", allow_symbol_change: false });
    expect(new URL(changed.externalUrl).searchParams.get("symbol")).toBe("NYSE:BRK.B");
  });

  it("keeps every screener preset in the official US universe", () => {
    for (const preset of ["top_gainers", "top_losers", "volume_leaders"] as const) {
      expect(stockWidgetDefinition("screener", "NASDAQ:NVDA", "5", preset).config)
        .toMatchObject({ market: "america", defaultScreen: preset, showToolbar: false });
    }
    expect(stockWidgetDefinition("heatmap", "NASDAQ:NVDA").config)
      .toMatchObject({ dataSource: "SPX500", isDataSetEnabled: false });
  });

  it("distinguishes broad stock headlines from the selected-symbol news feed", () => {
    const broad = stockWidgetDefinition("news", "NASDAQ:NVDA");
    expect(broad.config).toMatchObject({ feedMode: "market", market: "stock" });
    expect(broad.config).not.toHaveProperty("symbol");
    const selected = stockWidgetDefinition("news", "NYSE:IBM", "5", "top_gainers", "symbol");
    expect(selected.config).toMatchObject({ feedMode: "symbol", symbol: "NYSE:IBM" });
    expect(selected.config).not.toHaveProperty("market");
  });
});
