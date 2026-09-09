# TradingScanner

Real-time crypto day-trading intelligence platform. Ingests exchange trade streams for the liquid crypto
universe, builds multi-timeframe candles and indicators in memory, detects and classifies market-structure
events, scores every asset for risk-adjusted short-term opportunity, and pushes ranked results to a
terminal-style UI. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and phase plan.

Nothing here predicts prices. No setup is ever presented as certain. Real-money execution does not exist in this codebase.

## Kraken USD scanner

The shipped configuration uses Kraken public spot market data with `Kraken:CountryCode=US` for this
installation's Minnesota account. The universe comes from live, online crypto USD AssetPairs after
Kraken's country filter and configured app exclusions. `MarketData:IncludeAllPairs=true` includes small
markets, stablecoins and pairs without volume statistics; it bypasses the ranked universe's size, volume
and generic excluded-base filters, **never the country or app exclusions**. Being listed does not establish
a qualified entry or regular-app Buy eligibility. BTC and ETH remain available for market context.
REST and WebSocket symbols are normalized (XBT to BTC and XDG to DOGE), and every quote retains Kraken
provenance. Listings refresh when the service restarts.

### Regular Kraken app availability

The deployment profile is US / Minnesota / Kraken app Buy & Sell. Only the two-letter country code is
sent to Kraken; the state and trading venue are display context. A failed regional request is an error,
with no fallback to the global catalog. See the [AssetPairs country filter](https://docs.kraken.com/api-reference/market-data/get-tradable-asset-pairs)
and [Kraken's regional restrictions](https://support.kraken.com/articles/where-is-kraken-licensed-or-regulated).

`Kraken:ExcludedAssets` ships with `NPC`, `RE` and `KAS`, which this installation's user reported absent
from the regular app's Buy list. These are account/app exceptions, not claims of US prohibition or
delisting. They are removed before subscriptions, history warm-up, analysis and alerts. The list can be
updated in configuration when the user's availability changes; restart after changing it.

The public spot catalog does not verify account-specific Instant Buy eligibility. Kraken documents
regional restrictions and delays before newly listed assets appear in the regular app. The header
therefore identifies the public market-data scope and leaves app eligibility unverified. See the
[Instant Buy FAQ](https://support.kraken.com/articles/360060101312-faq-s-about-buying-instantly).

For other unavailable coins, use **Hide unavailable** in the coin drawer. Manage and restore hidden coins
from the scanner. These additional preferences persist in the current browser and filter discovery and
browser alerts; they do not change server webhook rules or erase paper trades and historical records.
Deployment-level exclusions apply across devices, while browser preferences stay on that browser.

The decision board separates in-zone candidates, setups waiting for price, and developing setups still
blocked by execution checks. It shows entry zones, confirmation triggers, stops, first targets and net R
at the assessed price. Developing setups are not qualified entries. All blocker reasons contribute to the
diagnosis, and stale rows are excluded from candidate counts. A stale high-priority breakout at one level
can no longer hide a fresh eligible breakout at another; scoring uses the selected breakout's evidence.
BTC and breadth context now exclude stale, future-dated or unwarmed inputs.

### Discovery, inspection and entry alerts

The dashboard presents a coherent snapshot every five seconds. Price flashes are disabled; the market
table, radar, decision board, heatmap, watchlist, tape and setup panel share the reading cadence instead
of re-ranking on every incoming tick. **Pause display** holds a clearly labeled reference snapshot;
**Refresh now** takes one fresh snapshot even while paused, and **Resume display** jumps to the latest
data. Feed-loss and freshness warnings remain active. The underlying exchange feed, scanner and enabled
entry alerts continue at their original cadence while the display is held.

The setup chart updates price lines only when their values change. Position-calculator edits remain
intact across refreshed plans; **Use latest plan** explicitly loads updated levels when desired.

The market radar surfaces fresh 5m/15m gainers, unusual 5m volume, passing plans nearest their entry zones,
and a separate 24h leaderboard covering the full catalog before analysis warms. Activity rankings do not
establish an entry. The entry desk keeps eligible plans separate from collapsible developing plans and
their blockers. Quick filters jump to in-zone plans, waiting plans, volume spikes or gainers; reset returns
the full searchable catalog.

Open any listed coin to inspect it. Pending history moves to the next available warm-up worker, and the
chart and plan load automatically when ready. BTC/ETH start first; other untouched pairs retain volume
order. Four workers and the existing shared REST rate limit remain in place. Failed granularities retry
once without refetching successful responses, and partial failures never become execution-ready.

**Entry alerts** are opt-in in the dashboard. New in-zone visits must pass the existing execution checks
and remain observed across fresh scanner cycles for two seconds. Each coin has a ten-minute cooldown;
enabling alerts or reconnecting does not replay existing setups. Sound must be armed in the current tab,
desktop notifications require browser permission, and the tab must remain open. Settings are saved in
this browser; recent entry-alert history is held only in the tab. These are inspection reminders, not orders.

The evaluator now considers every matching pattern and active breakout level in the original priority
order, selecting the first candidate that independently passes all checks. A blocked preferred retest
therefore cannot hide a feasible alternative. If none passes, the preferred setup and its blockers stay
visible. Resistance between the current price and the planned entry midpoint also caps the first target.

This installation assumes eligible **Kraken+ app/web trades**: `Signals:Scanner:Execution:FeeBps` is `0`.
That is a configured commission assumption, not a verified account entitlement. Kraken+ does not waive
Kraken Pro fees; it has a monthly allowance, and app quote spreads and processing charges can still apply.
The scanner retains observed market spread and `SlippageBps=5` per side. Public spot bid/ask is not the
Kraken app's executable Instant Buy/Sell quote. Verify the app's final price and remaining allowance;
change the fee/slippage assumptions for your actual costs. The drawer shows the configured assumptions.
See the [Kraken+ FAQ](https://support.kraken.com/articles/kraken-faq-subscription-service-overview).

Provider configuration lives in `MarketData:Provider` (`kraken` or `coinbase`) and the corresponding
`Kraken` / `Coinbase` section. Kraken uses a shared public REST limiter of two requests per second by
default. The full catalog is visible and searchable immediately, with unassessed coins labeled as waiting
for history or analytics. History warms progressively while live trading data streams; loading hundreds
of pairs can take tens of minutes. No API key or trading permission is needed.

The full-universe configuration bounds candle retention to 300 bars for 1m/5m/15m/30m/1h, 240 three-minute
bars and 180 four-hour bars to fit the existing small hosting tier. These limits retain
the fetched warm-up history; live indicators maintain their own incremental state. Chart/API history is
limited to these buffers. Empty or unsupported live markets remain listed without invented prices.
The API explicitly uses workstation garbage collection to reduce memory pressure on the small hosting
tier; the full-universe scan is checked under a 384 MiB managed-heap limit.

Kraken REST OHLC returns at most 720 recent rows, including the still-forming final candle. The adapter
excludes that final candle and does not pretend pagination supplies older data. Multi-day backtests
therefore require an archived history source; the API rejects incomplete requested history instead of
reporting a partial run as a full multi-day test. See [Kraken OHLC limits](https://docs.kraken.com/api-reference/market-data/get-ohlc-data).

## Execution-quality checks

### Decision workspace and validation

The default scanner view separates in-zone candidates from candidates waiting for their zone,
with evidence scores used for ordering within each group and controls to expand each list.
Only backend `Watch` assessments with valid positive net R enter the shortlist. There is no automated
buy state: users must verify the trigger. Blocked entries remain inspectable in the collapsible full-market
table. Missing assessments, stale quotes, disconnected feeds and scanner snapshots older than ten seconds
fail closed. The setup drawer suppresses execution guidance when its data is interrupted.

The store refreshes execution-only changes, filtering and sorting when ranks do not change, stale flags
when price is unchanged, and removes symbols absent from the latest snapshot. Signal evidence is explicitly
distinguished from realized paper-trade performance and from validation of the execution policy.

Replay fixes: actual fill price/time now determine entry, initial risk, T1 reward ratio and outcome horizons;
bars before a delayed fill cannot affect outcomes. Fills already beyond the stop or T1 are rejected and
latency is at least one bar. Both entry and exit include fees, slippage and half-spread at their respective
reference prices. These are still bar-based signal simulations, not order-book executions or a portfolio
backtest; stop gaps, dependent positions and available depth are not fully modeled.

`.github/workflows/validate.yml` runs the .NET test suite, frontend tests, typecheck and production build on
pull requests and default-branch pushes. No production credentials or live orders are used. Database tests
use an isolated, disposable Postgres service with test-only credentials. Passing software tests does not prove
that a strategy will be profitable.

The setup panel and scanner rows now distinguish evidence ranking from execution feasibility. `execution`
on the opportunity response reports `Blocked` or `Watch`, reasons, net reward/risk to T1 at the assessed
current price, and the **required** break-even win rate. This is not a forecast, calibrated win probability,
or automatic trigger confirmation. All checks passing still means watch and verify the plan's trigger.

`Signals:Scanner:Execution` configures per-side `FeeBps` (library default 60; this Kraken+ deployment 0), `SlippageBps` (5),
`MinNetRewardRatio` (1.5), `MaxSpreadBps` (20), `MinVolume24hQuote` (2,000,000), and `MinScore` (60).
Fees are conservative assumptions, not exchange fee quotes; configure your actual tier. Half the observed
spread, fees and slippage are applied on each side at the respective entry/exit price. Stops can gap and
order-book depth is not modeled, so modeled risk is not a maximum possible loss.

Checks veto stale/future quotes, incomplete history, missing/invalid/wide spreads, inadequate liquidity,
missing BTC context, BTC dumping or strong risk-off, overextension, weak evidence, invalid long geometry,
chase entries and insufficient net reward. A late price remains visible as a wait-for-pullback candidate
when it is still below the no-chase ceiling; it is never presented as an immediate buy. Nearby resistance
above entry now caps T1 even when it
destroys the apparent reward/risk; it is no longer skipped to manufacture a better target.

Signal collection still records evidence setups rather than executed trades. Candidate selection can now
choose a different feasible pattern, so newly collected signals are not the same population as earlier
first-match results. Existing reports are not validation of this updated policy. A separate out-of-sample
evaluation with actual fees and paper fills is required before claiming predictive value.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Market data: Kraken Spot and Coinbase Exchange adapters, universe selection, candle engine (1m→4h), REST warm-up, reconnect/gap handling, API + SignalR stream | Implemented, tested |
| 2 | Analytics: EMA 9/20/50/200, Wilder RSI/ATR, session VWAP with deviation bands, relative volume, realized volatility, multi-horizon momentum with acceleration, EMA alignment/cross tracking; rebuild-from-history; `GET /api/market/{symbol}/analytics` | Implemented, tested |
| 3 | Market structure on 5m/15m/1h: confirmed fractal swings, stable clustered levels, HH/HL/LH/LL/EH/EL trend labels, range and session extremes; per-level breakout state machine (Watching → Approaching → Attempt → Confirmed → Retesting → Retest Held / Failed / Extended) in ATR units with retest metrics and narratives; chronological history replay through the live path; `GET /api/market/{symbol}/breakouts` | Implemented, tested |
| 4 | Scanner: BTC/ETH state, breadth and risk regime; anti-FOMO overextension assessment with DO NOT CHASE; setup classification (Breakout, Breakout+Retest, VWAP Reclaim, Support Bounce, Momentum Continuation, Range Breakout, Trend Pullback, Reversal, Volume/Volatility Expansion); configurable 0–100 score with evidence per component and penalty; trade plans with entry zone, trigger, invalidation, stop, three resistance-capped targets and R:R; why/invalidation/risk explanations; score-change reasons; BTC correlation; ranked universe every second over SignalR; market tape | Implemented, tested |
| 5 | Dashboard: Next.js 15 / React 19 / TypeScript / Tailwind 4 / Lightweight Charts 5. Market regime header, ranked scanner with per-row score ledger (component segments + penalty cut), setup drawer with chart and plan lines, plan / why / invalidation / risks, momentum, levels, score breakdown, position calculator, watchlist (browser-persisted), heatmap, market tape, LIVE DATA INTERRUPTED banner. Store lives outside React with per-symbol subscriptions and animation-frame batching | Implemented |
| 6 | Alerts: compound conditions over price, score, momentum, volume, RSI, VWAP relation, breakout state, setup, BTC trend/dump, regime and more; cross-above/below operators; hold time so wicks never fire; edge-triggered with optional repeat and cooldown; per-symbol or universe-wide rules; browser notifications and https webhooks (Discord-compatible body plus the full event); `/api/alerts` CRUD, `/api/alerts/events`, SignalR `alert`; alert manager view | Implemented, tested |
| 7 | Paper trading: market buy/sell against the live quote with slippage and fees, bracket stop and take-profit (one cancels the other) evaluated every cycle and never on stale prices, partial exits, MFE/MAE, R-multiple from the initial stop, score / setup / regime captured at entry, account equity and stats by setup and regime; `/api/paper/*`; SignalR `paper`; paper view and “Paper buy with bracket” from the setup card. In memory unless a database is configured | Implemented, tested |
| 8 | Signal performance: every setup scoring at or above the threshold is recorded once per symbol and setup within a dedupe window, whether traded or not, and followed for an hour: 5/15/30/60-minute returns, MFE/MAE, which plan level was hit first, outcome R; reports by score bucket, setup, regime, confidence, and coin with target-before-stop rate, stop rate, average R, expectancy, and profit factor; `/api/performance`, `/api/performance/signals`; performance view | Implemented, tested |
| 9 | Backtesting: replays 1m history through the same analytics, breakout, evaluation, and outcome code as the live scanner (the per-symbol evaluation is one shared static function); bars fed in chronological close order across symbols, evaluation at every 5m close using only closed bars, entry at the next bar's open, stop tested before targets within a bar, fee/slippage/spread cost model applied to R; gross and net reports; `POST /api/backtest` (≤5 symbols, ≤14 days over the provider's REST history); backtest view. A test proves truncating future bars never changes earlier signals | Implemented, tested |
| 10 | AI explanation: the deterministic engine's structured output (setup, score components and penalties with evidence, plan, overextension, metrics, market context) is sent as JSON to Claude, which is instructed to use only those numbers, never invent values, and never call anything certain; response parsed into summary / why / invalidation / risks / appears-extended; cached per symbol; enabled only when `ANTHROPIC_API_KEY` is set on the API server; `POST /api/scanner/{symbol}/explain`; “Explain with AI” in the setup card | Implemented, tested |

## Run the backend

Requires .NET 8 SDK. Docker Compose provides TimescaleDB + Redis for later phases (not needed for Phase 1).

```bash
dotnet build
dotnet test
dotnet run --project src/TradingScanner.Api
```

The API listens on `http://localhost:5080` by default (`Urls` in `appsettings.json`).

| Endpoint | Purpose |
|---|---|
| `GET /api/market/symbols` | Universe with latest quote, provenance (provider, exchange, exchange time, age, stale flag), 24h stats |
| `POST /api/market/{symbol}/prepare` | Prioritize a pending catalog symbol for history loading; never adds unlisted markets or duplicate requests |
| `GET /api/market/{symbol}/quote` | One symbol |
| `GET /api/market/{symbol}/candles?tf=1m&limit=300` | Closed candles + forming bar. `tf` ∈ 1m,3m,5m,15m,30m,1h,4h |
| `GET /api/market/{symbol}/analytics` | Per-timeframe indicators as of the last closed bar, market structure (swings, levels, trend), plus momentum and VWAP deviation projected at the live price |
| `GET /api/market/{symbol}/breakouts` | Breakout state per structure level on the 5m timeframe with retest metrics and a plain-language narrative |
| `GET /api/scanner` | Ranked universe (compact rows) plus market context from the latest scanner cycle |
| `GET /api/scanner/{symbol}` | Full opportunity: score breakdown, setup evidence, trade plan, overextension, why / invalidation / risks, metrics, data quality |
| `GET /api/scanner/market` | Regime, BTC/ETH state, breadth, notes |
| `GET /api/scanner/tape?limit=100` | Recent what's-moving-now events |
| `GET/POST/PUT/DELETE /api/alerts`, `GET /api/alerts/events`, `GET /api/alerts/fields` | Alert rules and fired events. Rules are in memory unless a database is configured |
| `GET /api/paper/account`, `POST /api/paper/orders`, `GET /api/paper/positions`, `/orders`, `/trades`, `/stats`, `POST /api/paper/account/reset` | Simulated execution. There is no real-money order path anywhere in the codebase |
| `GET /api/performance`, `GET /api/performance/signals?limit=&symbol=` | Signal outcome report and recent signals with outcomes |
| `POST /api/backtest` | Replay a few symbols over recent history with cost assumptions; returns signals plus gross and net reports |
| `POST /api/scanner/{symbol}/explain` | AI narrative of the engine's numbers. 503 with a clear message when no `ANTHROPIC_API_KEY` is configured |
| `GET /api/system/feed` | Provider status per connection, last feed event age, universe size, startup phase and error, stats/history warm-up counts, recent engine-loop exceptions, persistence kind and Kraken `marketAccess` profile (country, display region/venue and configured exclusions; not verified Buy eligibility) |
| `GET /api/system/metrics` | Ingestion counters: messages, reconnects, gaps, latency, channel depth |
| `GET /health/live`, `GET /health/ready` | Liveness / readiness (ready = feed connected and fresh) |
| `/hubs/market` (SignalR) | `quotes` batches every 250 ms, `candle` closes for subscribed groups, `feed` status, `gap` notices, `scanner` ranked snapshot each cycle, `tape` events, `alert` firings |

Startup sequence: list products → fetch 24h stats → select all online USD pairs → open sharded
WebSocket connections → warm 1m/5m/15m/1h history via REST while live trades stream. Kraken warm-up uses
four requests per symbol at two requests per second; 200 symbols take roughly seven minutes plus overhead.

### Configuration (`appsettings.json` → `MarketData`)

| Key | Default | Meaning |
|---|---|---|
| `IncludeAllPairs` | true (shipped configuration) | Include online pairs within the provider's country/app filters; bypass ranked size, volume and generic excluded-base filters |
| `UniverseSize` | 200 | Ranked-mode cap; ignored when `IncludeAllPairs` is true |
| `MinVolume24hQuote` | 1,000,000 | Ranked-mode liquidity floor; ignored when `IncludeAllPairs` is true |
| `SymbolsPerConnection` | 60 | Sharding of the WebSocket subscription |
| `CandleCloseGrace` | 2s | Wait for late trades before the clock closes a bar |
| `StaleQuoteThreshold` | 30s | Quotes older than this are flagged stale |
| `ReceiveTimeout` | 15s | Silence that forces a reconnect (heartbeats arrive every second) |
| `WarmUpHistory` | true | Load REST history at startup |

### Persistence (optional)

Set `ConnectionStrings:Postgres` (or the `ConnectionStrings__Postgres` environment variable) and the API migrates the
schema on startup and stores alert rules and events, the paper account with its orders and positions, every signal
with its outcome, and every closed candle of every timeframe (a TimescaleDB hypertable when the extension is present).
`docker compose up -d` provides TimescaleDB and Redis; the connection string for it is
`Host=localhost;Username=scanner;Password=scanner;Database=tradingscanner`. Without a connection string everything
runs in memory and resets on restart, which the UI states. Redis is provisioned but not yet used by the code.

Integration tests for the repositories run only when `TS_TEST_POSTGRES` points at a disposable database:

```bash
TS_TEST_POSTGRES="Host=localhost;Username=scanner;Password=scanner;Database=tradingscanner_test" dotnet test
```

### AI explanation (optional)

Set `ANTHROPIC_API_KEY` in the API server's environment (never in the browser). The model defaults to `claude-opus-5`
(`Anthropic:Model` in `appsettings.json`) with low effort, since the input is already fully structured, and server-side
refusal fallbacks are enabled so a declined request is rerouted instead of failing. The model receives only the
engine's computed numbers and is told to invent nothing; every narrative carries a disclaimer.

## Deploy (Render or any Docker host)

One image is the whole product. The root `Dockerfile` builds the dashboard as a static export (`NEXT_OUTPUT=export`)
and the API serves it from `wwwroot` on its own origin, so `https://your-service/` is the dashboard and
`https://your-service/api/...` is the API. No second service, no CORS configuration, no API URL to bake in.
`render.yaml` is a Render Blueprint that creates that service plus a Postgres database, passing the database URL as
`ConnectionStrings__Postgres` (postgres:// URLs are converted for Npgsql automatically). On Render: New → Blueprint →
this repository, or set an existing service's runtime to Docker with Dockerfile path `./Dockerfile` and context `.`.
A service left on auto-detect fails within seconds because the repository holds both a .NET solution and a Node app.

`web/Dockerfile` still builds the dashboard as its own Node service (standalone mode, `NEXT_PUBLIC_API_URL` baked
in at build time) for deployments that want the two split; then set the dashboard origin as `Cors__Origins__0` on
the API.

## Run the dashboard

The **Stocks & ETFs** tab adds ranked stock setups from a connected personal Alpaca IEX feed,
with entry zones, stop/target references, opt-in alerts and one-click transfer into the risk planner.
[Connect the free stock data feed](docs/stock-signal-setup.md) using server-side credentials and a separate personal access code.
TradingView charts/screeners, a personal Kraken availability list, saved plans and a manual journal
remain available before connecting. Stock widgets can be delayed; Alpaca IEX covers one exchange.
See [stock workspace usage and data coverage](docs/stock-workspace.md).

Requires Node 22 and pnpm.

```bash
cd web
cp .env.example .env.local        # NEXT_PUBLIC_API_URL, default http://localhost:5080
pnpm install
pnpm dev                          # http://localhost:3000
pnpm test                         # position sizing + store batching tests
```

The UI takes its initial state from REST and then follows the SignalR hub. Whenever the hub or the exchange feed
is not live, a `LIVE DATA INTERRUPTED` banner appears and rows dim; prices never pretend to move.

`web/mock/server.mjs` is a development-only mock of the REST surface with fabricated, static data so the UI can be
reviewed without an exchange connection (`node mock/server.mjs`). It reports its feed as not live on purpose. It is
not used by the product.

## Data integrity rules implemented in Phase 1

- Every price carries provider, exchange, exchange timestamp, receive timestamp, and computed age.
- Candles are bucketed by exchange time; local time is only used for latency metrics.
- Missed trades are detected by per-product `trade_id` continuity and surfaced as gap events and a `Degraded` feed status. Nothing is interpolated.
- Empty minutes are filled with explicitly flagged synthetic bars so series stay time-regular; synthetic bars carry zero volume.
- Feed loss is a first-class state (`Reconnecting`) exposed on `/api/system/feed`, `/health/ready`, and the SignalR `feed` message. The UI (Phase 5) shows `LIVE DATA INTERRUPTED` from this signal.

## Known gaps

- **No authentication or per-user state yet.** Every endpoint is open; run the API on a private network. Alerts, the
  paper account and the watchlist are single-tenant (the watchlist lives in the browser). The `users` / `watchlists`
  tables from the architecture document are not created.
- **Live smoke tests are bounded checks, not operational guarantees.** On September 8, 2026, Kraken catalog,
  trade/BBO sockets, REST warm-up and API/UI streaming were exercised with 47 selected USD pairs:
  47 histories loaded, no warm-up failures or engine exceptions. No execution-qualified entry was observed
  in that short window. This verifies connectivity, not strategy profitability or future signal frequency.
- **Detected trade gaps are surfaced as degraded feeds; automatic REST gap repair is not implemented.**
- **The universe is selected once at startup**; symbols that become liquid later are picked up on restart.
- **BTC dominance** needs an aggregator source and is shown as n/a.
- **Scoring weights are untuned defaults.** The performance and backtest views exist to measure them; treat every
  score as a ranking of evidence, not a probability.
- **Relative volume uses a rolling baseline** rather than a time-of-day profile.
- Persistence stores complex records as `jsonb` documents with indexed lookup columns instead of the fully
  normalized tables sketched in the architecture document.

## Tests

`dotnet test` covers bucketing, candle construction, aggregation, series storage, universe selection,
the Kraken and Coinbase protocol parsers (against documented message shapes), REST clients (stubbed HTTP), the providers'
reconnect/resubscribe/gap logic (scripted sockets), a real in-process WebSocket server drop-and-reconnect scenario,
the engine loop, and the API host with a fake provider (REST, health, SignalR).

Live exchange connectivity is **not** exercised by the test suite. Run the API on a machine with internet access to validate against the real feed.
