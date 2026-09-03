# TradingScanner — Production Architecture

Status: living document. Written before any code (Phase 0), updated as phases land.

## 0. Repository inspection (what existed before this work)

Nothing. The repository had zero commits, zero files, and no remote branches.

Consequences:

- No reusable code. Everything below is greenfield.
- No inherited architectural problems. The only "problem" is the risk of building
  the wrong thing fast, which the phased build order is meant to prevent.

Environment constraints discovered while inspecting (they shape Phase 1 verification):

| Constraint | Effect |
|---|---|
| Build sandbox blocks `api.exchange.coinbase.com`, `api.kraken.com`, `api.binance.com` (proxy 403) | Live exchange connectivity **cannot** be validated from the sandbox. The Coinbase adapter is validated against a local WebSocket server that replays the real Coinbase Exchange feed protocol. Live validation must be done by running the API on a machine with normal internet. |
| .NET 8 SDK available from Ubuntu apt, NuGet reachable | Backend builds and tests run here. |
| No BTC dominance source is an exchange feed | BTC dominance requires an aggregator (CoinGecko/CMC). It is treated as an optional enrichment with its own provider interface, never faked. Until wired, the header shows "n/a". |

## 1. Product in one sentence

A deterministic real-time engine that ingests exchange trade streams for the liquid crypto
universe, maintains multi-timeframe candles and indicators in memory, detects and classifies
market-structure events (breakouts, retests, failures, reclaims), scores every asset for
risk-adjusted short-term opportunity, and pushes the ranked result to a terminal-style UI.

Everything numerical is computed by the engine. The AI layer (Phase 10) only narrates
structured engine output.

## 2. Solution layout

```
TradingScanner.sln
src/
  TradingScanner.Core/            Domain models, provider interfaces, time/bucketing, config, metrics abstractions
  TradingScanner.MarketData/      Exchange adapters (Coinbase first), universe selection, candle engine, market state engine
  TradingScanner.Analytics/       Indicators (EMA/RSI/ATR/VWAP/RelVol/momentum/volatility) + market structure (Phase 2-3)
  TradingScanner.Signals/         Breakout/retest state machines, setup classification, scoring, ranking, BTC filter (Phase 3-4)
  TradingScanner.Infrastructure/  PostgreSQL/Timescale + Redis persistence, outbox, migrations (Phase 5+)
  TradingScanner.Api/             ASP.NET Core host: SignalR hubs, REST, health checks, hosted services, observability
tests/
  TradingScanner.Tests/           xunit. Deterministic fixtures. Protocol replay server for provider tests.
web/                              Next.js + TypeScript + Tailwind + Lightweight Charts (Phase 5)
docs/                             This document, ADRs, schema
docker-compose.yml                timescaledb + redis for local dev
```

Dependency direction (strict, enforced by project references):

```
Api -> Signals -> Analytics -> Core
Api -> MarketData -> Core
Api -> Infrastructure -> Core
Tests -> everything
```

`MarketData` never references `Analytics` or `Signals`. Ingestion knows nothing about signals.
`Signals` never references `MarketData`. Signals consume `SymbolState` snapshots from `Core`
abstractions, which is what lets the backtester drive the identical signal code.

## 3. Core domain models (`TradingScanner.Core`)

Money and prices are `decimal`. Derived analytics (indicator values, scores) are `double`.
All timestamps are `DateTimeOffset` in UTC, plus a local `ReceivedAt` for latency accounting.

```csharp
readonly record struct Symbol(string Value);                       // canonical: "BTC-USD"
enum Timeframe { M1 = 60, M3 = 180, M5 = 300, M15 = 900, M30 = 1800, H1 = 3600, H4 = 14400 }
enum TradeSide { Buy, Sell }                                       // taker side

readonly record struct Trade(
    Symbol Symbol, string Provider, string Exchange,
    long TradeId, decimal Price, decimal Size, TradeSide TakerSide,
    DateTimeOffset ExchangeTime, DateTimeOffset ReceivedAt, long? Sequence);

readonly record struct TickerUpdate(
    Symbol Symbol, string Provider, string Exchange,
    decimal LastPrice, decimal BestBid, decimal BestAsk,
    decimal Volume24h, decimal Open24h, decimal High24h, decimal Low24h,
    DateTimeOffset ExchangeTime, DateTimeOffset ReceivedAt);

sealed class Candle {                                              // mutable while forming, frozen on close
    Symbol Symbol; Timeframe Timeframe; DateTimeOffset OpenTime; DateTimeOffset CloseTime;
    decimal Open, High, Low, Close; decimal Volume; decimal QuoteVolume;
    decimal BuyVolume, SellVolume; int TradeCount; bool IsClosed; bool IsSynthetic; }

sealed class CandleSeries { ... }   // ring buffer of closed candles + separate Forming candle; indexer [^1] = last closed

sealed class PriceQuote(Symbol, decimal Price, decimal Bid, decimal Ask, string Provider, string Exchange,
                        DateTimeOffset ExchangeTime, DateTimeOffset ReceivedAt) { TimeSpan Age(now); bool IsStale(threshold); }

enum FeedStatus { Disconnected, Connecting, Connected, Degraded /* gaps detected */, Reconnecting }
```

Phase 2-4 models (defined now, implemented later):

```
IndicatorSnapshot { Ema9, Ema20, Ema50, Ema200, Rsi, Atr, AtrPct, Vwap, RelVolume, ... } per timeframe
MarketStructure   { SwingHighs[], SwingLows[], Support[], Resistance[], Trend (HH/HL vs LH/LL) }
BreakoutState     { Watching, Approaching, Attempt, Confirmed, Retesting, RetestHeld, Failed, Extended }
SetupType         { Breakout, BreakoutRetest, VwapReclaim, SupportBounce, MomentumContinuation, RangeBreakout, TrendPullback, Reversal, VolumeExpansion, VolatilityExpansion }
Opportunity       { Symbol, Score, Components{}, Penalties{}, SetupType, Confidence, Entry, Trigger, Invalidation, Stop, Targets[3], RR, Explanation[] }
MarketRegime      { StrongRiskOn, RiskOn, Neutral, RiskOff, StrongRiskOff } + breadth + BTC state
```

## 4. Market-data pipeline

```
 Exchange WS  ──►  Provider adapter (parse on socket thread, zero-copy Utf8JsonReader)
                        │  normalized Trade / TickerUpdate / FeedStatus
                        ▼
                Channel<MarketEvent> (bounded, single consumer, back-pressure metrics)
                        │
                        ▼
            MarketStateEngine (BackgroundService, single writer per symbol)
              ├─ SymbolState[symbol]
              │    ├─ CandleBuilder(1m) ← trades      (exchange timestamp bucketing)
              │    ├─ CandleSeries per timeframe      (3m..4h aggregated from closed 1m)
              │    ├─ LastQuote                       (price + provider + exchange + age)
              │    └─ [Phase 2] IndicatorSet, [Phase 3] Structure, [Phase 4] Scores
              ├─ Clock-driven close: a 1s timer closes candles for symbols with no trades
              └─ publishes immutable snapshots (Interlocked swap) for readers
                        │
                        ▼
              SignalR broadcaster (batched every 250ms: quotes; 1m candle closes; feed status)
```

Design rules:

1. **Bucketing uses exchange time**, never local receive time. Local time is recorded for latency metrics only.
2. **Single writer per symbol.** The engine is the only thing that mutates `SymbolState`. Readers get snapshots. No locks in the hot path.
3. **Gap detection.** Coinbase `match` messages carry per-product `sequence`; `heartbeat` carries `last_trade_id`. A gap marks the symbol `Degraded` and triggers a REST backfill of the affected minute(s); it never silently continues.
4. **Empty minutes are filled** with a synthetic flat candle (O=H=L=C=previous close, volume 0, `IsSynthetic=true`) so every series is time-regular. Indicators treat synthetic candles as real closes with zero volume.
5. **Warm-up via REST** on start: 300 × 1m candles (and 300 × 5m / 1h) per symbol to seed EMA200/ATR/RSI. Rate limited at 8 req/s (Coinbase public limit is 10).
6. **Stale detection.** Every `PriceQuote` carries `ExchangeTime`; the API exposes `Age`. UI shows `LIVE DATA INTERRUPTED` when the feed status is not `Connected` or the newest quote is older than a threshold.
7. **Reconnect** with exponential backoff + jitter (1s → 30s cap), full resubscribe, and a post-reconnect backfill for the outage window. Reconnect count is a metric.
8. **Universe**: on start, list products, keep USD-quoted, `online`, non-stablecoin bases, rank by 24h USD volume, take top N (config, default 200). Re-evaluated hourly.

### Provider abstraction

```csharp
interface IMarketDataProvider : IAsyncDisposable {
    string Name { get; }                 // "coinbase"
    string Exchange { get; }             // "Coinbase Exchange"
    FeedStatus Status { get; }
    event? none — status changes are emitted as MarketEvent.StatusChanged on the channel
    Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct);
    Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol, Timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task RunAsync(IReadOnlySet<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct); // runs until cancelled; owns reconnect loop
}

interface IWebSocketClientFactory { IWebSocketClient Create(); }   // seam for tests + protocol replay
```

Adapters: `CoinbaseExchangeProvider` (Phase 1). `KrakenProvider` planned to prove the seam.
Symbol mapping is inside the adapter (`BTC-USD` is canonical; Kraken's `XBT/USD` maps in the adapter).

## 5. Signal pipeline (Phases 2-4, same code path live and backtest)

```
closed 1m candle for symbol S
  → IndicatorSet.Update(candle)           incremental EMA/RSI(Wilder)/ATR/VWAP/vol baseline, O(1) per candle
  → Aggregator closes higher TFs          each closed higher-TF candle updates that TF's IndicatorSet
  → MarketStructureAnalyzer(S)            swings (fractal N), levels (clustered swings), trend labeling — 5m/15m
  → BreakoutStateMachine(S, level)        state transitions require close-through + relvol + wick ratio + follow-through
  → RetestTracker(S, level)               depth, dwell time, retest volume, recovery speed
  → OverextensionAnalyzer(S)              distance from VWAP/EMA20 in ATRs, N-bar % move, parabolic accel, volume climax
  → SetupClassifier(S)                    exactly one SetupType, or None
  → OpportunityScorer(S, BtcContext)      components + penalties from ScoringConfig (weights configurable)
  → TradePlanBuilder(S)                   entry zone / trigger / invalidation / stop / T1-T3 / R:R from structure + ATR
Ranker: sort all Opportunity by score; publish ScannerSnapshot (throttled 500ms) and per-symbol detail on demand.
```

Look-ahead prevention: every analyzer consumes `CandleSeries`, which only exposes **closed** candles
plus an explicitly separate `Forming` candle. Backtesting feeds closed candles in order through the same
`SymbolState.OnCandleClosed`. There is no second implementation.

Scoring config is a record loaded from `appsettings` / DB, with a `Version` so score history is attributable.

## 6. Frontend real-time updates (Phase 5)

- One SignalR connection. Server → client messages:
  - `quotes`: `[{s, p, b, a, t, age}]` batched every 250 ms (only symbols that changed)
  - `scanner`: full snapshot on connect + every 10 s; deltas every 500 ms
  - `candle`: closed 1m/5m candles for symbols the client has subscribed to (chart open)
  - `feed`: provider status transitions, gap notices
  - `tape`: market-tape events
- Client store lives **outside React** (a `Map` per stream). Rows subscribe with `useSyncExternalStore` keyed by symbol; a `requestAnimationFrame` flush coalesces bursts. Only rows whose data changed re-render. Charts are updated through Lightweight Charts' `update()` API, never through React state.
- No indicator math in the browser. The client renders what the engine sent.

## 7. Database schema (Phase 5+; defined now)

PostgreSQL 16 + TimescaleDB. Redis for hot state (latest quote per symbol, ranking, alert cooldowns, pub/sub), never as source of truth.

```sql
-- identity
users(id uuid pk, email citext unique, password_hash text, created_at timestamptz)

-- user state
watchlists(id uuid pk, user_id fk, name text, created_at)
watchlist_items(watchlist_id fk, symbol text, added_at, pk(watchlist_id, symbol))
alerts(id uuid pk, user_id fk, name text, enabled bool, conditions jsonb, hold_seconds int,
       cooldown_seconds int, channels jsonb, created_at, last_fired_at)
alert_events(id bigserial pk, alert_id fk, fired_at timestamptz, snapshot jsonb)

-- engine configuration
scoring_configs(id uuid pk, version int, weights jsonb, penalties jsonb, active bool, created_at)

-- time series (hypertables)
candles(symbol text, timeframe int, open_time timestamptz, open numeric, high numeric, low numeric, close numeric,
        volume numeric, quote_volume numeric, buy_volume numeric, sell_volume numeric, trade_count int,
        synthetic bool, provider text, pk(symbol, timeframe, open_time))      -- hypertable on open_time
signals(id uuid pk, symbol text, generated_at timestamptz, setup_type text, score numeric, components jsonb,
        penalties jsonb, entry numeric, stop numeric, t1 numeric, t2 numeric, t3 numeric, rr numeric,
        regime text, btc_trend text, config_version int, explanation jsonb)   -- hypertable on generated_at
signal_outcomes(signal_id fk pk, ret_5m, ret_15m, ret_30m, ret_1h numeric, mfe numeric, mae numeric,
        stop_hit bool, t1_hit bool, t2_hit bool, t3_hit bool, first_event text, evaluated_at timestamptz)
score_history(symbol text, at timestamptz, score numeric, setup_type text, config_version int)  -- hypertable

-- paper trading
paper_accounts(id uuid pk, user_id fk, starting_balance numeric, balance numeric, fee_bps int, slippage_bps int)
paper_orders(id uuid pk, account_id fk, symbol text, side text, type text, qty numeric, limit_price numeric,
        stop_price numeric, status text, created_at, filled_at, fill_price numeric, signal_id uuid null)
paper_positions(id uuid pk, account_id fk, symbol text, qty numeric, avg_entry numeric, opened_at, closed_at,
        realized_pnl numeric, fees numeric, mfe numeric, mae numeric, score_at_entry numeric, regime_at_entry text,
        setup_at_entry text)
```

## 8. Observability

Structured logging via `Microsoft.Extensions.Logging` with JSON console output. Metrics via
`System.Diagnostics.Metrics` (OpenTelemetry-compatible, exposed at `/metrics` when Prometheus exporter is enabled):

- `md.ws.messages` (counter, tag provider), `md.ws.bytes`
- `md.ws.reconnects`, `md.ws.gaps`, `md.ws.parse_errors`
- `md.channel.depth` (gauge) — if this grows, the engine is behind the market
- `md.trade.latency_ms` (histogram: exchange time → received)
- `engine.candle.closes`, `engine.tick.process_us` (histogram)
- `scanner.cycle_ms`, `scanner.symbols`, `signals.generated`
- `signalr.broadcast_ms`, `signalr.clients`

Health checks: `/health/live` (process up), `/health/ready` (provider Connected, engine consuming, newest quote age < threshold).

## 9. Security (initial implementation)

- No API secrets in the frontend. Public market-data channels need none; any future keys live in env / secret manager.
- ASP.NET Core Identity-style auth arrives with user state (Phase 5/6); until then endpoints are read-only market data.
- Rate limiting middleware on REST; input validation via data annotations + explicit guards; CORS locked to the web origin.
- Real-money order execution is not implemented and there is no code path for it.

## 10. First implementation milestone (Phase 1 definition of done)

1. `dotnet build` and `dotnet test` green.
2. `CoinbaseExchangeProvider` connects to `wss://ws-feed.exchange.coinbase.com`, subscribes `matches` + `ticker` + `heartbeat` for the selected universe, normalizes into `Trade`/`TickerUpdate`, detects sequence gaps, reconnects with backoff, resubscribes.
3. Universe selection from `GET /products` + 24h volume from `GET /products/{id}/stats` (or `ticker` stream), top-N USD pairs.
4. Candle engine: trades → 1m → 3m/5m/15m/30m/1h/4h, exchange-time bucketing, synthetic fill, clock-driven closes, REST warm-up.
5. API exposes `GET /api/market/symbols`, `GET /api/market/{symbol}/candles?tf=`, `GET /api/market/{symbol}/quote`, `GET /health/*`; SignalR `MarketHub` streams batched quotes, candle closes, feed status.
6. Tests: Coinbase message parsing against real protocol fixtures; candle bucketing/aggregation/synthetic fill; reconnect + resubscribe + gap detection against an in-process WebSocket server that speaks the Coinbase protocol and can drop connections on command.

## 11. Build order (from the brief, unchanged)

Phase 1 Market data → 2 Analytics → 3 Structure → 4 Scanner → 5 Dashboard → 6 Alerts → 7 Paper trading →
8 Signal analytics → 9 Backtesting → 10 AI explanation. Each phase ends with a green build and green tests.
