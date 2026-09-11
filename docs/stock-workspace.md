# US stock trading workspace

Open **Stocks & ETFs** in the dashboard, or link directly to `/#stocks`. The stock workspace ships in the same static dashboard and Render deployment as the crypto scanner. No API key or new service is required.

The optional **Stocks to review now** scanner adds automatic ranking, entry zones, stop/target references and opt-in alerts from a personal Alpaca IEX feed. [Connect the free data account](stock-signal-setup.md) to enable it. Its API credentials stay on the server; the browser uses a separate personal access code. **Use entry plan** loads the upper end of a qualified entry zone into the risk planner for conservative sizing. Account, cash and risk preferences carry across stock selections on this browser; price levels only load after your explicit action.

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

Automatic setup detection uses the separately configured Alpaca IEX feed, not TradingView widget data. The stock scanner requires fresh source trades and quotes, completed regular-session bars, and acceptable IEX spreads before presenting research entry levels. Broker availability confirmation is required before loading an entry plan. Completed triggers stay under review for up to three minutes while their original levels and fresh-data checks hold. Delayed embedded research, waiting candidates and expired entry snapshots remain clearly identified. No trading orders are sent by this application.
