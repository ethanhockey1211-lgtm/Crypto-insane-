# Connect the stock setup scanner

The **Stocks & ETFs** workspace can scan your watchlist for intraday stock setups using a free Alpaca data account. You continue buying and selling in the regular Kraken app. This integration reads market data; it does not place orders or connect to your Kraken account. The existing charts, manual planner and trading notebook remain available before the data connection is configured.

## 1. Create your free data credentials

Create a free account at the [Alpaca dashboard](https://app.alpaca.markets/). A paper account on the **Basic** data plan is sufficient for this scanner. In the dashboard's right sidebar, open **API Keys**, select **Generate New Keys**, and keep both the key ID and secret in your password manager. Alpaca documents this location in its [market-data setup guide](https://docs.alpaca.markets/us/docs/getting-started-with-alpaca-market-data). Basic is the free default for paper and live accounts; see [Alpaca's data plans](https://docs.alpaca.markets/us/docs/about-market-data-api).

Create a separate random personal access code of **at least 24 characters** in your password manager. This code unlocks your scanner's stock feed. It must be different from the Alpaca key ID, Alpaca secret and your account passwords.

## 2. Add three settings to Render

In the [Render dashboard](https://dashboard.render.com/), open the **existing web service that serves this scanner**, then **Environment → Add Environment Variable**. Enter these names exactly, including the double underscores:

| Environment variable | Value to enter |
|---|---|
| `Stocks__ApiKey` | Your Alpaca API key ID |
| `Stocks__ApiSecret` | Your Alpaca API secret |
| `Stocks__AccessToken` | Your separate random personal access code, at least 24 characters |

Choose **Save and deploy** to restart the existing build with these settings. If the stock-scanner code has not been deployed yet, deploy that build as well. **Save only** does not activate new settings until a later deployment. [Render environment-variable instructions](https://render.com/docs/configure-environment-variables)

These settings require your Render dashboard access; merging the code does not create an Alpaca account or supply the credentials. Keep the provider key and secret in the server's environment. Do not paste them into the scanner, chat, GitHub, frontend environment variables or screenshots.

## 3. Unlock the scanner and choose stocks

Open **Stocks & ETFs → Stocks to review now** and enter **only your personal access code** in its password field. This is the value of `Stocks__AccessToken`, not an Alpaca credential.

Check the stocks you can actually buy in Kraken and mark them confirmed in your watchlist. The scanner prioritizes confirmed stocks and scans up to **40 visible watchlist symbols**; a list of 20–40 keeps the view focused. Hide unavailable stocks. A data-provider listing does not verify availability in your Kraken account.

The scanner refreshes every **30 seconds**. It needs at least **21 closed regular-session one-minute bars** before assessing a setup, so a fresh session needs time to warm up. Missing credentials, failed authentication, incomplete history or stale quotes produce a clear unavailable/waiting state instead of invented entry levels. Some IEX minutes have no qualifying trades, so collecting 21 bars can take longer than 21 minutes.

Stock alerts are opt-in and use a **ten-minute cooldown per symbol**. They are reminders to inspect a setup while the app is open. The scanner's evidence points are heuristic rankings, not a validated win probability or a promise of profit.

## What the data means

Free Alpaca data covers **IEX, one US exchange**. Its quotes are not the consolidated national best bid and offer (NBBO), and its volume is not total US market volume. The scanner checks source trade/quote times and quote validity; a successful refresh alone does not make an old quote current. [Alpaca coverage and feed details](https://docs.alpaca.markets/us/docs/market-data-faq)

The engine's **estimated IEX session VWAP** is calculated from the regular session's completed one-minute bars. It excludes premarket and after-hours bars. This bar-weighted estimate can differ from tick-based VWAP because Alpaca's internal VWAP volume can differ from the bar's reported volume. It is not Kraken's execution price. [Alpaca's bar aggregation rules](https://docs.alpaca.markets/us/docs/market-data-faq#how-are-bars-aggregated)

Review each plan and the final quote in Kraken before placing an order. Kraken documents regular app hours as **9:30 a.m.–4 p.m. Eastern**, normally **8:30 a.m.–3 p.m. Central**, with exchange holidays and early closes. Market orders submitted outside regular hours may queue for the next opening. [Buying and selling stocks in the Kraken app](https://support.kraken.com/articles/how-to-buy-and-sell-stocks-on-the-kraken-app)

## Personal feed and implementation limits

Private stock-data requests to `GET /api/stocks/scan` carry the personal access code in `X-Stock-Access-Token`. The provider key and secret stay on the backend and are not returned to the browser or logged. Public `GET /api/stocks/status` exposes whether the connection is configured, without credentials or market data. Keep the access code private: it protects both your feed allowance and personal-use access. Alpaca's terms limit personal data use and require permission for publication or distribution; this connection is not a public market-data service. [Alpaca terms, pages 1–2](https://files.alpaca.markets/disclosures/library/TermsAndConditions.pdf)

The backend uses explicitly selected `feed=iex` for bulk [snapshots](https://docs.alpaca.markets/us/reference/stocksnapshots-1) and [one-minute history](https://docs.alpaca.markets/us/reference/stockbars). Requests use a shared cache of up to eight symbol sets with a 30-second lifetime, coalesce matching fetches, and guard upstream traffic at 180 requests per minute, below the Basic plan's documented 200-per-minute allowance. History is bounded to three provider pages and 400 retained bars per symbol; unfinished pagination cannot qualify a setup.

If the app remains locked after deployment, check the access code against `Stocks__AccessToken`. If the provider rejects the connection, check both Alpaca credentials in Render and redeploy after correcting them. If you regenerate Alpaca keys, update both Render values. A configured status means settings exist; it does not prove the provider accepted them or that quotes are fresh.

## Connection troubleshooting

The scanner shows a diagnostic code beside connection failures. These codes contain no credentials:

| Message/code | Next step |
|---|---|
| Access code rejected | Enter your personal `Stocks__AccessToken` in the scanner. |
| `invalid-credentials` | Replace an endpoint URL in `Stocks__ApiKey` or `Stocks__ApiSecret` with the actual credential. The scanner already uses Alpaca's data endpoint; no endpoint variable is needed. |
| `provider-unauthorized` (Alpaca 401) | Update **both** Alpaca credentials in Render using the same generated pair, then save and deploy. |
| `provider-forbidden` (Alpaca 403) | Check the matching key/secret pair and your Alpaca account's IEX data access. This status can indicate credentials or permissions. |
| `provider-request` or `provider-response` | Refresh once. If it persists, report the diagnostic code so the scanner request or response handling can be investigated. |
| `provider-rate-limit`, `provider-unavailable`, `provider-timeout`, or `provider-network` | Let the next refresh retry. Persistent failures need a connection/service check. |

Share only the diagnostic message when asking for help. The scanner never displays Alpaca's raw error response or your keys. A generic HTTP error without a diagnostic code may come from an older deployment or hosting proxy and does not establish that your keys are wrong.
