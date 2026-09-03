# TradingScanner

Real-time crypto day-trading intelligence platform. Ingests exchange trade streams for the liquid crypto
universe, builds multi-timeframe candles and indicators in memory, detects and classifies market-structure
events, scores every asset for risk-adjusted short-term opportunity, and pushes ranked results to a
terminal-style UI. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and phase plan.

Nothing here predicts prices. No setup is ever presented as certain. Real-money execution does not exist in this codebase.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Market data: Coinbase Exchange WebSocket adapter, universe selection, candle engine (1m→4h), REST warm-up, reconnect/gap handling, API + SignalR stream | Implemented, tested |
| 2 | Analytics: EMA 9/20/50/200, Wilder RSI/ATR, session VWAP with deviation bands, relative volume, realized volatility, multi-horizon momentum with acceleration, EMA alignment/cross tracking; rebuild-from-history; `GET /api/market/{symbol}/analytics` | Implemented, tested |
| 3 | Market structure on 5m/15m/1h: confirmed fractal swings, stable clustered levels, HH/HL/LH/LL/EH/EL trend labels, range and session extremes; per-level breakout state machine (Watching → Approaching → Attempt → Confirmed → Retesting → Retest Held / Failed / Extended) in ATR units with retest metrics and narratives; chronological history replay through the live path; `GET /api/market/{symbol}/breakouts` | Implemented, tested |
| 4 | Scanner: BTC/ETH state, breadth and risk regime; anti-FOMO overextension assessment with DO NOT CHASE; setup classification (Breakout, Breakout+Retest, VWAP Reclaim, Support Bounce, Momentum Continuation, Range Breakout, Trend Pullback, Reversal, Volume/Volatility Expansion); configurable 0–100 score with evidence per component and penalty; trade plans with entry zone, trigger, invalidation, stop, three resistance-capped targets and R:R; why/invalidation/risk explanations; score-change reasons; BTC correlation; ranked universe every second over SignalR; market tape | Implemented, tested |
| 5 | Dashboard: Next.js 15 / React 19 / TypeScript / Tailwind 4 / Lightweight Charts 5. Market regime header, ranked scanner with per-row score ledger (component segments + penalty cut), setup drawer with chart and plan lines, plan / why / invalidation / risks, momentum, levels, score breakdown, position calculator, watchlist (browser-persisted), heatmap, market tape, LIVE DATA INTERRUPTED banner. Store lives outside React with per-symbol subscriptions and animation-frame batching | Implemented |
| 6 | Alerts: compound conditions over price, score, momentum, volume, RSI, VWAP relation, breakout state, setup, BTC trend/dump, regime and more; cross-above/below operators; hold time so wicks never fire; edge-triggered with optional repeat and cooldown; per-symbol or universe-wide rules; browser notifications and https webhooks (Discord-compatible body plus the full event); `/api/alerts` CRUD, `/api/alerts/events`, SignalR `alert`; alert manager view | Implemented, tested |
| 7 | Paper trading: market buy/sell against the live quote with slippage and fees, bracket stop and take-profit (one cancels the other) evaluated every cycle and never on stale prices, partial exits, MFE/MAE, R-multiple from the initial stop, score / setup / regime captured at entry, account equity and stats by setup and regime; `/api/paper/*`; SignalR `paper`; paper view and “Paper buy with bracket” from the setup card. In memory unless a database is configured | Implemented, tested |
| 8–10 | Signal analytics, backtesting, AI explanation | Planned |

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
| `GET /api/system/feed` | Provider status per connection, last event age, universe size |
| `GET /api/system/metrics` | Ingestion counters: messages, reconnects, gaps, latency, channel depth |
| `GET /health/live`, `GET /health/ready` | Liveness / readiness (ready = feed connected and fresh) |
| `/hubs/market` (SignalR) | `quotes` batches every 250 ms, `candle` closes for subscribed groups, `feed` status, `gap` notices, `scanner` ranked snapshot each cycle, `tape` events, `alert` firings |

Startup sequence: list products → fetch 24h stats → select top-N USD pairs by quote volume → open sharded
WebSocket connections (`matches`, `ticker`, `heartbeat`) → warm 1m/5m/15m/1h history via REST while live
trades stream. Expect roughly a minute for warm-up of 200 symbols at the default 8 requests/second.

### Configuration (`appsettings.json` → `MarketData`)

| Key | Default | Meaning |
|---|---|---|
| `UniverseSize` | 200 | Symbols after ranking by 24h quote volume |
| `MinVolume24hQuote` | 1,000,000 | Liquidity floor in quote currency |
| `SymbolsPerConnection` | 60 | Sharding of the WebSocket subscription |
| `CandleCloseGrace` | 2s | Wait for late trades before the clock closes a bar |
| `StaleQuoteThreshold` | 30s | Quotes older than this are flagged stale |
| `ReceiveTimeout` | 15s | Silence that forces a reconnect (heartbeats arrive every second) |
| `WarmUpHistory` | true | Load REST history at startup |

## Run the dashboard

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

## Tests

`dotnet test` runs unit tests for bucketing, candle construction, aggregation, series storage, universe selection,
the Coinbase protocol parser (against documented message shapes), the REST client (stubbed HTTP), the provider's
reconnect/resubscribe/gap logic (scripted sockets), a real in-process WebSocket server drop-and-reconnect scenario,
the engine loop, and the API host with a fake provider (REST, health, SignalR).

Live exchange connectivity is **not** exercised by the test suite. Run the API on a machine with internet access to validate against the real feed.
