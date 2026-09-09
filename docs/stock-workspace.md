# US stock trading workspace

Open **Stocks & ETFs** in the dashboard, or link directly to `/#stocks`. The stock workspace ships in the same static dashboard and Render deployment as the crypto scanner. No API key or new service is required.

## Daily workflow

1. Use the US stock screener to research movers and volume leaders. Its universe is TradingView's US listings, not an account-specific Kraken catalog.
2. Add a ticker and its listing exchange to the research watchlist. The 20 starter symbols are research examples, all initially unconfirmed. Check Kraken's Stocks Buy list, mark stocks available in your account, and use **Only my confirmed stocks** to focus the list. Hide unavailable instruments; restore them through **Show hidden**.
3. Select a stock to study its intraday chart, heatmap context or news. The selected stock controls the chart; changing planner inputs does not recreate the widget. Widget tabs mount on demand.
4. Build a long trade plan using your own current Kraken quote, entry, stop reference and target. Sizing uses whole shares, an account risk percentage and available settled cash. It reserves the editable round-trip cost buffer in both the risk and cash constraints. A plan is a notebook record, not a detected or executable entry.
5. Save the plan. After a completed trade, enter the actual entry/exit fills and quantity in **Log result**. Actual fills already include spread/slippage, so the journal's additional cost field starts at zero and should contain only additional round-trip fees. Journal P/L and R are calculated from these manual inputs; they are not broker-synced returns.
6. Export the notebook for backup. Import displays the stock/plan/trade counts and requires an explicit replacement action. Watchlists, up to 100 plans and 500 journal records are stored only in this browser.

## Market data and availability

Kraken's regular app supports US stocks for eligible Minnesota accounts. These are real stocks/ETFs; this feature does not use the Spot API's tokenized-asset catalog or xStocks. Kraken's public stock pages do not prove a particular account can buy a security. See [Kraken stock eligibility](https://support.kraken.com/articles/getting-started-with-equities), [buying stocks in the app](https://support.kraken.com/articles/how-to-buy-and-sell-stocks-on-the-kraken-app) and the [public stocks catalog](https://www.kraken.com/prices/stocks).

The free TradingView embeds are independent third-party research tools. Stock data can be delayed or exchange-limited, and some symbols are unavailable in embeds. The workspace does not read or scrape widget quotes/indicators into its own signal engine, claim a live consolidated stock feed, or send stock orders through the crypto paper-trading API. Verify the current price, spread and availability in Kraken before using a plan. External widget refresh is controlled by TradingView; the crypto display's pause control does not apply to embeds. Native lists and plan inputs have no automatic reordering or price-flash animation.

TradingView attribution is retained. Network/script failures show retry and external research links. An iframe loading does not verify that the provider has data for its symbol; TradingView may display its own coverage message. [Widget data limitations](https://www.tradingview.com/widget-docs/faq/data/) explain why a paid TradingView account does not upgrade website embeds. Official [Advanced Chart](https://www.tradingview.com/widget-docs/widgets/charts/advanced-chart/), [stock screener](https://www.tradingview.com/widget-docs/widgets/screeners/screener/demos/stock/) and [widget catalog](https://www.tradingview.com/widget-docs/) document the integrations.

The minute-resolution session clock uses the published [NYSE 2026–2028 holiday and early-close calendar](https://www.nyse.com/trade/hours-calendars), with Eastern and Central time zones. It reports scheduled hours, not live exchange status or trading halts. Unsupported calendar years show unknown. In the regular Kraken app, orders outside market hours may queue for the next session; the clock does not imply that Kraken Pro's extended hours apply.

## Validation

Frontend unit tests cover symbol validation, persisted record recovery, sizing constraints/costs, manual journal P/L and R, exchange holidays, early closes and daylight-saving boundaries. The production static export must build successfully. Browser checks exercise widget loading, stable chart instances during local edits, availability controls, notebook save/reload/export/import and narrow screens.

Automated stock entries would require a separately documented equity data feed with appropriate real-time coverage. This free workspace intentionally identifies manual plans and delayed research data in the UI rather than presenting them as live trading signals.
