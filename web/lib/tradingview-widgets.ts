import { normalizeStockSymbol } from "./stocks";

export type StockWidgetTab = "chart" | "screener" | "heatmap" | "news";
export type StockChartInterval = "1" | "5" | "15" | "60" | "D";
export type StockScreen = "top_gainers" | "top_losers" | "volume_leaders";
export type StockNewsMode = "market" | "symbol";

export interface StockWidgetDefinition {
  script: string;
  config: Record<string, unknown>;
  title: string;
  externalUrl: string;
  attributionUrl: string;
  attribution: string;
}

const EMBED = "https://s3.tradingview.com/external-embedding/";
export const TRADINGVIEW_DATA_FAQ = "https://www.tradingview.com/widget-docs/faq/data/";

export function validWidgetSymbol(symbol: string): boolean {
  return normalizeStockSymbol(symbol) === symbol;
}

/** Public widget configurations, verified against TradingView's official widget generator. */
export function stockWidgetDefinition(tab: StockWidgetTab, symbol: string, interval: StockChartInterval = "5", screen: StockScreen = "top_gainers", news: StockNewsMode = "market"): StockWidgetDefinition {
  if (!validWidgetSymbol(symbol)) throw new Error("Choose a stock with its exchange prefix, such as NASDAQ:NVDA.");
  const symbolUrl = `https://www.tradingview.com/symbols/${encodeURIComponent(symbol.replace(":", "-"))}/`;
  const stock = symbol.split(":")[1];
  const shared = { width: "100%", height: "100%", locale: "en" };
  if (tab === "chart") return {
    script: `${EMBED}embed-widget-advanced-chart.js`, title: `${symbol} TradingView price chart`,
    externalUrl: `https://www.tradingview.com/chart/?symbol=${encodeURIComponent(symbol)}`,
    attributionUrl: symbolUrl, attribution: `${stock} stock chart`,
    config: { ...shared, autosize: true, symbol, interval, timezone: "exchange", theme: "dark", style: "1",
      allow_symbol_change: false, hide_top_toolbar: true, hide_side_toolbar: false, hide_legend: false,
      hide_volume: false, withdateranges: true, save_image: false, details: false, hotlist: false,
      studies: ["STD;EMA", "STD;VWAP"], backgroundColor: "#0c1422", gridColor: "rgba(148, 163, 184, 0.08)",
      support_host: "https://www.tradingview.com" },
  };
  if (tab === "screener") return {
    script: `${EMBED}embed-widget-screener.js`, title: "TradingView US stock screener",
    externalUrl: "https://www.tradingview.com/screener/", attributionUrl: "https://www.tradingview.com/screener/", attribution: "Stock Screener",
    config: { ...shared, market: "america", defaultColumn: "overview", defaultScreen: screen,
      showToolbar: false, colorTheme: "dark", isTransparent: false },
  };
  if (tab === "heatmap") return {
    script: `${EMBED}embed-widget-stock-heatmap.js`, title: "TradingView S&P 500 stock heatmap",
    externalUrl: "https://www.tradingview.com/heatmap/stock/", attributionUrl: "https://www.tradingview.com/heatmap/stock/", attribution: "Stock Heatmap",
    config: { ...shared, exchanges: [], dataSource: "SPX500", grouping: "sector", blockSize: "market_cap_basic",
      blockColor: "change", colorTheme: "dark", hasTopBar: false, isDataSetEnabled: false,
      isZoomEnabled: true, hasSymbolTooltip: true, isMonoSize: false },
  };
  return {
    script: `${EMBED}embed-widget-timeline.js`, title: news === "symbol" ? `${symbol} TradingView news` : "TradingView stock-market headlines",
    externalUrl: news === "symbol" ? `${symbolUrl}news/` : "https://www.tradingview.com/news/",
    attributionUrl: "https://www.tradingview.com/news/", attribution: "Top stories",
    config: { ...shared, ...(news === "symbol" ? { feedMode: "symbol", symbol } : { feedMode: "market", market: "stock" }),
      colorTheme: "dark", isTransparent: false, displayMode: "regular" },
  };
}
