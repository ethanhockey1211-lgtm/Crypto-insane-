// Mirrors the API contracts (System.Text.Json camelCase, enums as names).

export interface ScannerRow {
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

export interface FeedStatus {
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
}
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
