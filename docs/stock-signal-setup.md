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

## Read and compare a trade brief

The opportunity board compares the same confirmed-first watchlist, with sorts for **setup quality**, **net reward/risk**, and **profit if the target is reached**. Its detailed brief explains the observed trigger, supporting evidence, entry zone, invalidation level and reasons to wait. Technical evidence does not establish a news catalyst; review the stock's news separately.

The upper end of the entry zone is used for conservative sizing. Each plan includes a **1R checkpoint** and the existing **2R target**, measured before costs from that upper entry to the stop reference. These are fixed planning scenarios, not predicted prices or resistance levels. A current setup must also offer at least **1.5R after the cost buffer** to qualify as an opportunity; increasing the cost estimate can remove it from that group.

Enter **account value, available cash, risk percentage and round-trip cost per share** to compare whole-share plans within your limits. These four budget fields are saved locally and shared with the manual planner. The cost buffer estimates spread, slippage and fees; it is not a quoted Kraken charge. Without an account budget, comparisons remain per share. Each stock is an alternative using the same cash and risk budget: adding the displayed profits together does not describe an affordable portfolio.

**Profit if target reached** assumes the full position exits at that target, after the entered cost buffer. **Planned loss at stop** assumes an exit at the stop reference. **Break-even win rate** is the rate required by those two outcomes: planned loss divided by planned loss plus target profit. It is an arithmetic threshold, not a measured win rate or a probability that this trade will succeed. A larger potential payoff alone does not make a setup more likely to work.

Your selected stock stays selected between refreshes so you can read its brief. Rankings update with new scan snapshots; the one-second age check only expires eligibility. Pausing or a data failure leaves references visible but disables using them as a current entry plan. **Use entry plan** copies levels into the manual planner; later scans do not overwrite levels you edit there.

Orders remain manual in Kraken. A displayed stop is a planning reference; the scanner does not attach a stop order or monitor an actual position. Execution can differ from the displayed price, and a stop price does not guarantee the loss amount. [SEC explanation of order execution and stops](https://www.investor.gov/introduction-investing/general-resources/news-alerts/alerts-bulletins/investor-bulletins-14)

## What the data means

Free Alpaca data covers **IEX, one US exchange**. Its quotes are not the consolidated national best bid and offer (NBBO), and its volume is not total US market volume. The scanner checks source trade/quote times and quote validity; a successful refresh alone does not make an old quote current. [Alpaca coverage and feed details](https://docs.alpaca.markets/us/docs/market-data-faq)

The engine's **estimated IEX session VWAP** is calculated from the regular session's completed one-minute bars. It excludes premarket and after-hours bars. This bar-weighted estimate can differ from tick-based VWAP because Alpaca's internal VWAP volume can differ from the bar's reported volume. The volume ratio compares the last completed IEX minute with the prior 20 observed bars, not historical daily volume. These are IEX references, not Kraken execution prices. [Alpaca's bar aggregation rules](https://docs.alpaca.markets/us/docs/market-data-faq#how-are-bars-aggregated)

Review each plan and the final quote in Kraken before placing an order. Kraken documents regular app hours as **9:30 a.m.–4 p.m. Eastern**, normally **8:30 a.m.–3 p.m. Central**, with exchange holidays and early closes. Market orders submitted outside regular hours may queue for the next opening. [Buying and selling stocks in the Kraken app](https://support.kraken.com/articles/how-to-buy-and-sell-stocks-on-the-kraken-app)

## Personal feed and implementation limits

Private stock-data requests to `GET /api/stocks/scan` carry the personal access code in `X-Stock-Access-Token`. The provider key and secret stay on the backend and are not returned to the browser or logged. Public `GET /api/stocks/status` exposes whether the connection is configured, without credentials or market data. Keep the access code private: it protects both your feed allowance and personal-use access. Alpaca's terms limit personal data use and require permission for publication or distribution; this connection is not a public market-data service. [Alpaca terms, pages 1–2](https://files.alpaca.markets/disclosures/library/TermsAndConditions.pdf)

The backend uses explicitly selected `feed=iex` for bulk [snapshots](https://docs.alpaca.markets/us/reference/stocksnapshots-1) and [one-minute history](https://docs.alpaca.markets/us/reference/stockbars). Requests use a shared cache of up to eight symbol sets with a 20-second lifetime, coalesce matching fetches, and guard upstream traffic at 180 requests per minute, below the Basic plan's documented 200-per-minute allowance. History is bounded to twelve provider pages and 400 retained bars per symbol; unfinished pagination cannot qualify a setup.

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

## Trade discovery and entry lifetime

All visible watchlist stocks are assessed for research, including starter stocks whose Kraken availability has not been checked. Stocks explicitly marked unavailable stay excluded. **Use entry plan** requires your availability confirmation; this does not place an order.

The scanner evaluates completed opening-range breakouts, VWAP reclaims, pullbacks and 20-bar breakouts. A valid trigger can remain under review for three elapsed minutes after its candle closes. It keeps the original trigger's ATR, volume test, entry ceiling, stop and targets. Every subsequent completed minute must be present, hold the trigger on its close and remain above the original stop. A new scan with a missing latest completed minute cannot retain an entry; quotes and trades must still be at most 45 seconds old. The current bar freshness ceiling is 60 seconds after its close.

The lower entry reference remains the trigger level plus 0.02 ATR. The upper limit is the greater of that lower reference or the confirming close, plus 0.25 ATR. A confirming close more than 1 ATR beyond its trigger is rejected. This accommodates a completed crossing candle while keeping a fixed limit on follow-through; later price movement cannot expand an existing plan. Targets remain hypothetical 2R planning levels, with the 1.5R after-cost filter unchanged.

A budget that cannot fund one whole share now leaves the qualifying setup and per-share comparison visible, with a sizing warning. It disables loading the plan until the budget supports it. A blank cost field shows an explicit error and a restore-default action. If no current entry qualifies, the board lists the missing data, price, pattern or cost checks.

Snapshots are fetched after history pagination and validated against their receipt time so request latency cannot turn genuine current observations into future timestamps. The public configured status only confirms that server settings are present; provider authentication is checked when you connect and scan.

## Alpaca service-error recovery

A failed history or snapshot GET with HTTP 408, 500, 502, 503 or 504 gets one additional attempt within the same scan. Backoff is 750 milliseconds, or the provider's longer `Retry-After` value when that delay is at most two seconds. Longer requested delays suppress the inline retry; the existing 30-second polling cycle remains unchanged. Authentication, access, invalid-request and rate-limit errors are not retried inline. Both attempts count toward the provider request limit and share the existing 20-second scan timeout. Cancellation stops the retry. Failed scans never return old prices as new observations.

If the request still fails, the diagnostic identifies **minute-bar history** or **live snapshot**, the actual upstream HTTP status and number of attempts. A valid hexadecimal `X-Request-ID` is included when supplied by Alpaca; this can help their support team locate a specific failed request. Provider response bodies, credential headers, arbitrary request IDs and redirects are not forwarded. [Alpaca request-ID documentation](https://docs.alpaca.markets/us/docs/getting-started-with-alpaca-market-data)

`provider-unavailable` now represents an upstream server error. Other unexpected HTTP statuses, including redirects, use `provider-http-error` so a request rejection is not mislabeled as a service outage. A public operational status page does not prove that a particular authenticated request succeeded.
