// Mirrors the API contracts (System.Text.Json camelCase, enums as names).

export interface ScannerRow {
  executionReason?: string | null;
  /** All execution blockers (or the single advisory for a passing setup). */
  executionReasons?: string[] | null;
  assessedPrice?: number | null;
  entryLow?: number | null;
  entryHigh?: number | null;
  trigger?: string | null;
  setupBias?: "Neutral" | "Bullish" | "Bearish" | null;
  executionStatus?: "Blocked" | "Watch" | null;
  netRewardRatio?: number | null;
  rank: number;
  symbol: string;
  score: number;
  setup: string;
  confidence: "Low" | "Medium" | "High";
  price: number;
  entry: number | null;
  stop: number | null;
  target1: number | null;
  rr: number | null;
  r1m: number | null;
  r5m: number | null;
  r15m: number | null;
  r1h: number | null;
  r24h: number | null;
  relVol: number | null;
  breakout: string | null;
  doNotChase: boolean;
  stale: boolean;
  vwapDev: number | null;
  volume24h: number | null;
  keyLevel: number | null;
  trend: string | null;
  /** Component points: momentum, volume, structure, breakout, market, liquidity, riskReward. */
  components: number[];
  penalty: number;
  entryState: EntryState | null;
  chaseCeiling: number | null;
}

export interface AssetState {
  symbol: string;
  price: number;
  r5m: number | null;
  r15m: number | null;
  r1h: number | null;
  r24h: number | null;
  trend: "Neutral" | "Bullish" | "Bearish";
  aboveVwap: boolean | null;
  vwapSigma: number | null;
  atrPct: number | null;
  relVol5m: number | null;
  dumping: boolean;
  summary: string;
}

export interface MarketContext {
  at: string;
  regime: "StrongRiskOff" | "RiskOff" | "Neutral" | "RiskOn" | "StrongRiskOn";
  altsFavorable: boolean;
  btc: AssetState | null;
  eth: AssetState | null;
  breadthAboveVwap: number;
  breadthPositive1h: number;
  breadthBullishAlignment: number;
  medianRelVolume: number;
  symbolsEvaluated: number;
  notes: string[];
}

export interface ScannerStream {
  at: string;
  market: MarketContext;
  rows: ScannerRow[];
  universe: number;
  cycleMs: number;
}

export interface QuoteDto {
  symbol: string;
  price: number;
  bid: number;
  ask: number;
  exchangeTimeMs: number;
  receivedAtMs: number;
  ageMs: number;
  stale: boolean;
  provider: string;
  exchange: string;
}

/** Every selected exchange pair, including pairs whose analysis has not warmed up. */
export interface SymbolSummaryDto {
  symbol: string;
  quote: QuoteDto | null;
  open24h: number | null;
  high24h: number | null;
  low24h: number | null;
  volume24hBase: number | null;
  /** Percentage points (unlike scanner returns, which are fractions). */
  change24hPct: number | null;
  tradesSeen: number;
  historyLoaded: boolean;
}

export interface FeedStatus {
  /** Public market-data scope; it does not verify this account's Buy & Sell eligibility. */
  marketAccess?: { countryCode: string; region: string; tradingVenue: string; excludedAssets: string[] };
  provider: string;
  exchange: string;
  status: string;
  live: boolean;
  connections: Record<string, string>;
  lastEventAgeMs: number | null;
  lastTradeAgeMs: number | null;
  universeSize: number;
  universeSelectedAt: string | null;
  engineErrors: number;
  startupPhase: string;
  startupError: string | null;
  startupAttempts: number;
  /** Candidate products excluded from ranking because their 24h stats could not be fetched. */
  statsUnavailable: number;
  history: { total: number; loaded: number; failed: number; lastError: string | null; complete: boolean };
  recentErrors: { at: string; kind: string; symbol: string | null; error: string; site: string | null }[];
  /** "postgres" or "memory". In memory, alerts, paper trades and signal history reset on every restart. */
  persistence: string;
}

export interface TapeEvent {
  id: number;
  at: string;
  symbol: string | null;
  kind: string;
  severity: "Info" | "Notice" | "Alert";
  text: string;
}

export interface CandleDto { t: number; o: number; h: number; l: number; c: number; v: number; qv: number; bv: number; sv: number; n: number; src: string }
export interface CandlesResponse { symbol: string; timeframe: string; candles: CandleDto[]; forming: CandleDto | null }
export interface CandleClosed { symbol: string; timeframe: string; candle: CandleDto }

export interface ScoreComponent { name: string; points: number; max: number; evidence: string }
export interface ScoreBreakdown { components: ScoreComponent[]; penalties: ScoreComponent[]; raw: number; total: number; configVersion: number }
export interface TradePlan {
  entryLow: number; entryHigh: number; trigger: string; invalidation: number; stop: number;
  target1: number; target2: number; target3: number; riskPerUnit: number;
  rewardRatio1: number; rewardRatio2: number; rewardRatio3: number; basis: string[]; entryMid: number;
  /** Price above which fewer than the configured R remain to target 1. */
  chaseCeiling: number;
  entryState: EntryState;
}
export type EntryState = "Watch" | "InZone" | "Late" | "Chase";
export interface Overextension {
  score: number; doNotChase: boolean; flags: string[]; vwapSigma: number | null; ema20DistanceAtr: number | null;
  move5mAtr: number | null; move15mAtr: number | null; move1hPct: number | null; move24hPct: number | null; supportDistanceAtr: number | null;
}
export interface BreakoutStatus {
  level: { id: string; price: number; touches: number; strength: number };
  direction: "Up" | "Down"; state: string; barsInState: number; barsSinceBreakout: number | null;
  breakoutRelVol: number | null; weakVolume: boolean; distanceAtr: number; narrative: string;
}
export interface SetupClassification { type: string; confidence: "Low" | "Medium" | "High"; bias: string; evidence: string[]; breakout: BreakoutStatus | null; keyLevel: number | null }
export interface OpportunityMetrics {
  r1m: number | null; r5m: number | null; r15m: number | null; r1h: number | null; r24h: number | null; accel5m: number | null;
  relVol5m: number | null; buyShare5m: number | null; rsi5m: number | null; rsi15m: number | null; atr5m: number | null; atrPct5m: number | null;
  vwap: number | null; vwapDeviationPct: number | null; vwapSigma: number | null; nearestResistance: number | null; nearestSupport: number | null;
  spreadBps: number | null; volume24hQuote: number | null; btcCorrelation: number | null;
  trend5m: string | null; trend15m: string | null; alignment5m: string | null; alignment15m: string | null;
}
export interface Opportunity {
  execution?: { status: "Blocked" | "Watch"; netRewardRatio: number | null; breakEvenWinRate: number | null; entryCostPerUnit: number | null; netRewardPerUnit: number | null; netRiskPerUnit: number | null; maxEntryPriceAfterCosts: number | null; reasons: string[]; feeBps?: number | null; slippageBps?: number | null };
  symbol: { value: string };
  at: string; price: number; rank: number; score: number;
  breakdown: ScoreBreakdown; setup: SetupClassification; plan: TradePlan | null; overextension: Overextension;
  why: string[]; invalidation: string; risks: string[]; metrics: OpportunityMetrics;
  change: { previous: number; current: number; reasons: string[] } | null;
  quality: { stale: boolean; ageMs: number; historyLoaded: boolean; provider: string; exchange: string };
}

export interface AlertCondition { field: string; operator: string; value: string }
export interface AlertRule {
  id: string; name: string; enabled: boolean; symbol: string | null; conditions: AlertCondition[];
  holdSeconds: number; cooldownSeconds: number; channels: string[]; webhookUrl: string | null;
  createdAt: string; lastFiredAt: string | null; repeatWhileTrue: boolean;
}
export interface AlertRuleRequest {
  name: string; enabled: boolean; symbol: string | null; conditions: AlertCondition[];
  holdSeconds: number; cooldownSeconds: number; channels: string[]; webhookUrl: string | null; repeatWhileTrue: boolean;
}
export interface AlertEvent { id: string; ruleId: string; ruleName: string; at: string; symbol: string; message: string; values: Record<string, string> }
export interface AlertFieldInfo { name: string; kind: "number" | "boolean" | "text" }

export interface PaperAccount { id: string; name: string; startingBalance: number; cash: number; feeBps: number; slippageBps: number; createdAt: string }
export interface PaperAccountView { account: PaperAccount; equity: number; openValue: number; unrealizedPnl: number; realizedPnl: number; openPositions: number }
export interface PaperOrder {
  id: string; accountId: string; symbol: string; side: "Buy" | "Sell"; type: "Market" | "Stop" | "TakeProfit"; quantity: number; triggerPrice: number | null;
  status: "Open" | "Filled" | "Cancelled" | "Rejected"; createdAt: string; filledAt: string | null; fillPrice: number | null; fees: number; slippage: number; positionId: string | null; note: string | null;
}
export interface PaperPosition {
  id: string; symbol: string; status: "Open" | "Closed"; quantity: number; avgEntry: number; openedAt: string; closedAt: string | null;
  realizedPnl: number; fees: number; maxFavorablePrice: number; maxAdversePrice: number; initialStop: number | null; initialRiskUsd: number | null;
  scoreAtEntry: number | null; setupAtEntry: string | null; regimeAtEntry: string | null; stopOrderId: string | null; takeProfitOrderId: string | null; exitReason: string | null;
  mfePct: number; maePct: number; rMultiple: number | null;
}
export interface PaperPositionView { position: PaperPosition; lastPrice: number | null; unrealizedPnl: number | null; marketValue: number | null }
export interface PaperBucket { key: string; trades: number; winRate: number; totalPnl: number; avgR: number | null }
export interface PaperStats { trades: number; wins: number; winRate: number; totalPnl: number; grossProfit: number; grossLoss: number; profitFactor: number | null; avgPnl: number; avgR: number | null; expectancy: number | null; bySetup: PaperBucket[]; byRegime: PaperBucket[] }
export interface PlaceOrderRequest { symbol: string; side: "Buy" | "Sell"; quantity: number | null; notional: number | null; stopPrice: number | null; takeProfitPrice: number | null; note: string | null }

export interface PerformanceBucket {
  key: string; signals: number; completed: number; withPlan: number; targetBeforeStopRate: number | null; stopRate: number | null;
  avgR: number | null; expectancy: number | null; profitFactor: number | null; avgRet5m: number | null; avgRet15m: number | null; avgRet30m: number | null; avgRet1h: number | null; avgRet2h: number | null;
  positiveRate15m: number | null; positiveRate30m: number | null; positiveRate1h: number | null; positiveRate2h: number | null; avgMfe: number | null; avgMae: number | null;
}
export interface PerformanceReport { at: string; overall: PerformanceBucket; byScoreBucket: PerformanceBucket[]; bySetup: PerformanceBucket[]; byRegime: PerformanceBucket[]; bySymbol: PerformanceBucket[]; byConfidence: PerformanceBucket[]; configVersion: number; note: string }
export interface SignalRecord { id: string; symbol: string; at: string; setup: string; confidence: string; score: number; price: number; entry: number | null; stop: number | null; target1: number | null; rewardRatio1: number | null; regime: string; btcTrend: string | null; doNotChase: boolean; configVersion: number }
export interface SignalOutcome { ret5m: number | null; ret15m: number | null; ret30m: number | null; ret1h: number | null; ret2h: number | null; mfe: number; mae: number; stopHit: boolean | null; target1Hit: boolean | null; firstEvent: string; r: number | null; lastPrice: string; complete: boolean }
export interface SignalWithOutcome { signal: SignalRecord; outcome: SignalOutcome }

export interface BacktestApiRequest { symbols: string[]; days: number; feeBps: number; slippageBps: number; spreadBps: number; recordThreshold: number; includeBtc: boolean }
export interface BacktestResult { signals: SignalWithOutcome[]; gross: PerformanceReport; net: PerformanceReport; barsProcessed: number; evaluations: number; duration: string; notes: string[] }

export interface Explanation { summary: string; why: string[]; invalidation: string[]; risks: string[]; appearsExtended: boolean | null; model: string; at: string; disclaimer: string }
