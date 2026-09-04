// DEVELOPMENT MOCK. Serves fabricated, static scanner output so the UI can be built and reviewed without an
// exchange connection. It is not the product, it never runs in production, and its feed status reports
// live: false so the interface shows LIVE DATA INTERRUPTED. Start: node mock/server.mjs (port 5080).
import http from "node:http";

const symbols = ["BTC-USD","ETH-USD","XRP-USD","ADA-USD","SUI-USD","SOL-USD","APT-USD","ARB-USD","LINK-USD","AVAX-USD","DOGE-USD","NEAR-USD","OP-USD","INJ-USD","TIA-USD","SEI-USD","RNDR-USD","FET-USD","JUP-USD","WIF-USD","PEPE-USD","LTC-USD","BCH-USD","ATOM-USD","DOT-USD","UNI-USD","AAVE-USD","MKR-USD","ICP-USD","FIL-USD"];
let seed = 7;
const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
const setups = ["Breakout + Retest","Breakout","VWAP Reclaim","Support Bounce","Momentum Continuation","Range Breakout","Trend Pullback","None","None","None","Volume Expansion","None"];
const states = ["RetestHeld","Confirmed","Approaching","Watching","Retesting","Attempt","Failed","Extended","Watching","Watching"];

const rows = symbols.map((s, i) => {
  const price = s === "BTC-USD" ? 64123.5 : s === "ETH-USD" ? 3412.2 : +(rnd() * 20).toFixed(4) + 0.05;
  const comps = [rnd()*20, rnd()*20, rnd()*20, rnd()*15, rnd()*10, 5+rnd()*5, rnd()*15].map(x => Math.round(x*10)/10);
  const penalty = Math.round(rnd() * 25 * (rnd() > 0.6 ? 1 : 0.2));
  const score = Math.max(0, Math.min(100, comps.reduce((a,b)=>a+b,0) - penalty));
  const setup = setups[i % setups.length];
  const r5 = (rnd() - 0.45) * 0.02, r24 = (rnd() - 0.4) * 0.2;
  const dnc = r24 > 0.12 || r5 > 0.012;
  const stop = price * 0.992, t1 = price * 1.018;
  return { rank: 0, symbol: s, score: Math.round(score), setup, confidence: score > 75 ? "High" : score > 55 ? "Medium" : "Low", price,
    entry: setup === "None" ? null : price * 0.999, stop: setup === "None" ? null : stop, target1: setup === "None" ? null : t1, rr: setup === "None" ? null : (t1 - price*0.999)/(price*0.999 - stop),
    r1m: (rnd()-0.5)*0.004, r5m: r5, r15m: r5*1.8, r1h: r5*3, r24h: r24, relVol: +(0.4 + rnd()*3).toFixed(2), breakout: states[i % states.length],
    doNotChase: dnc, entryState: dnc ? "Chase" : i % 3 === 0 ? "InZone" : i % 3 === 1 ? "Late" : "Watch", chaseCeiling: price * 1.012, stale: i === 27, vwapDev: (rnd()-0.4)*0.03, volume24h: 2e6 + rnd()*8e8, keyLevel: price * 1.004, trend: rnd() > 0.5 ? "Bullish" : "Mixed", components: comps, penalty };
}).sort((a,b) => b.score - a.score).map((r,i) => ({ ...r, rank: i+1 }));

const market = { at: new Date().toISOString(), regime: "RiskOn", altsFavorable: true,
  btc: { symbol: "BTC-USD", price: 64123.5, r5m: 0.0012, r15m: 0.003, r1h: 0.011, r24h: 0.024, trend: "Bullish", aboveVwap: true, vwapSigma: 0.8, atrPct: 0.004, relVol5m: 1.2, dumping: false, summary: "BTC-USD bullish, +0.12% 5m / +1.10% 1h, above VWAP" },
  eth: { symbol: "ETH-USD", price: 3412.2, r5m: 0.001, r15m: 0.002, r1h: 0.008, r24h: 0.031, trend: "Bullish", aboveVwap: true, vwapSigma: 0.5, atrPct: 0.005, relVol5m: 1.0, dumping: false, summary: "" },
  breadthAboveVwap: 0.63, breadthPositive1h: 0.58, breadthBullishAlignment: 0.44, medianRelVolume: 1.08, symbolsEvaluated: rows.length, notes: [] };

const tape = [
  { id: 5, at: new Date().toISOString(), symbol: "ARB-USD", kind: "VolumeSurge", severity: "Notice", text: "ARB-USD volume 3.1× its 5m baseline" },
  { id: 4, at: new Date(Date.now()-40000).toISOString(), symbol: "XRP-USD", kind: "RetestHeld", severity: "Notice", text: "XRP-USD retest of 1.4000 held" },
  { id: 3, at: new Date(Date.now()-90000).toISOString(), symbol: "SUI-USD", kind: "BreakoutFailed", severity: "Notice", text: "SUI-USD failed breakout of 0.78000" },
  { id: 2, at: new Date(Date.now()-120000).toISOString(), symbol: "ADA-USD", kind: "BreakoutConfirmed", severity: "Notice", text: "ADA-USD broke 0.20900: Closed above 0.20900 by 0.62 ATR on 2.4× volume, close in top 88% of range" },
  { id: 1, at: new Date(Date.now()-200000).toISOString(), symbol: null, kind: "RegimeChange", severity: "Alert", text: "Market regime Neutral → RiskOn" },
];

function opportunity(symbol) {
  const r = rows.find(x => x.symbol === symbol);
  if (!r) return null;
  const plan = r.entry == null ? null : { entryLow: r.entry*0.999, entryHigh: r.entry*1.002, entryMid: r.entry, trigger: `5m close holding above ${r.keyLevel.toFixed(4)}`, invalidation: r.stop*1.001, stop: r.stop, target1: r.target1, target2: r.target1*1.01, target3: r.target1*1.025, riskPerUnit: r.entry - r.stop, rewardRatio1: r.rr, rewardRatio2: r.rr*1.6, rewardRatio3: r.rr*2.4, basis: ["stop sits 0.3 ATR under the breakout level"], chaseCeiling: r.chaseCeiling, entryState: r.entryState };
  const names = ["Momentum","Volume","Structure","Breakout","Market","Liquidity","RiskReward"], maxes = [20,20,20,15,10,10,15];
  return { symbol: { value: symbol }, at: new Date().toISOString(), price: r.price, rank: r.rank, score: r.score,
    breakdown: { components: names.map((n,i)=>({ name: n, points: r.components[i], max: maxes[i], evidence: "mock evidence" })), penalties: r.penalty ? [{ name: "Overextension", points: r.penalty, max: 20, evidence: "mock: 2.3σ above VWAP" }] : [], raw: r.score + r.penalty, total: r.score, configVersion: 1 },
    setup: { type: ({ "Breakout + Retest": "BreakoutRetest", "VWAP Reclaim": "VwapReclaim", "Support Bounce": "SupportBounce", "Momentum Continuation": "MomentumContinuation", "Range Breakout": "RangeBreakout", "Trend Pullback": "TrendPullback", "Volume Expansion": "VolumeExpansion" })[r.setup] ?? r.setup, confidence: r.confidence, bias: "Bullish", evidence: ["mock: closed above level on 2.1× volume"], breakout: { level: { id: "x", price: r.keyLevel, touches: 3, strength: 3 }, direction: "Up", state: r.breakout, barsInState: 1, barsSinceBreakout: 2, breakoutRelVol: 2.1, weakVolume: false, distanceAtr: 0.8, narrative: "mock narrative" }, keyLevel: r.keyLevel },
    plan, overextension: { score: r.doNotChase ? 0.8 : 0.1, doNotChase: r.doNotChase, flags: r.doNotChase ? ["+18.0% in 24h"] : [], vwapSigma: 1.1, ema20DistanceAtr: 0.9, move5mAtr: 0.4, move15mAtr: 1.1, move1hPct: r.r1h, move24hPct: r.r24h, supportDistanceAtr: 1.2 },
    why: ["mock: broke intraday resistance with 2.1× relative volume", "mock: BTC above VWAP and 5m momentum accelerating"], invalidation: "mock: a sustained close below the level invalidates the thesis.", risks: r.doNotChase ? ["DO NOT CHASE: +18.0% in 24h"] : [],
    metrics: { r1m: r.r1m, r5m: r.r5m, r15m: r.r15m, r1h: r.r1h, r24h: r.r24h, accel5m: 0.001, relVol5m: r.relVol, buyShare5m: 0.58, rsi5m: 64, rsi15m: 61, atr5m: r.price*0.006, atrPct5m: 0.006, vwap: r.price*0.995, vwapDeviationPct: 0.005, vwapSigma: 1.1, nearestResistance: r.price*1.02, nearestSupport: r.price*0.985, spreadBps: 3.2, volume24hQuote: r.volume24h, btcCorrelation: 0.71, trend5m: "Uptrend", trend15m: "Range", alignment5m: "Bullish", alignment15m: "Mixed" },
    change: null, quality: { stale: r.stale, ageMs: r.stale ? 95000 : 320, historyLoaded: true, provider: "mock", exchange: "Mock Exchange" } };
}

function candles(symbol, tf) {
  const r = rows.find(x => x.symbol === symbol); if (!r) return null;
  const size = { "1m": 60, "5m": 300, "15m": 900, "1h": 3600 }[tf] ?? 300;
  const now = Math.floor(Date.now()/1000); const end = now - now % size; let p = r.price * 0.97; const out = [];
  for (let i = 300; i > 0; i--) { const t = end - i*size; const o = p; const c = p * (1 + (rnd()-0.48)*0.006); const h = Math.max(o,c)*(1+rnd()*0.002), l = Math.min(o,c)*(1-rnd()*0.002); out.push({ t, o, h, l, c, v: 100+rnd()*400, qv: 0, bv: 0, sv: 0, n: 10, src: "mock" }); p = c; }
  return { symbol, timeframe: tf, candles: out, forming: null };
}

http.createServer((req, res) => {
  const u = new URL(req.url, "http://x"); let body = null;
  if (u.pathname === "/api/scanner") body = { at: new Date().toISOString(), market, rows, universe: rows.length, cycleMs: 4.2 };
  else if (u.pathname === "/api/scanner/tape") body = tape;
  else if (u.pathname === "/api/scanner/market") body = market;
  else if (u.pathname.startsWith("/api/scanner/")) body = opportunity(decodeURIComponent(u.pathname.split("/")[3]));
  else if (u.pathname === "/api/system/feed") body = { provider: "mock", exchange: "Mock Exchange", status: "Disconnected", live: false, connections: {}, lastEventAgeMs: 12000, lastTradeAgeMs: null, universeSize: rows.length, universeSelectedAt: null, engineErrors: 0, startupPhase: "Streaming", startupError: null, startupAttempts: 0, statsUnavailable: 0, history: { total: rows.length, loaded: rows.length, failed: 0, lastError: null, complete: true }, recentErrors: [], persistence: "memory" };
  else if (/^\/api\/market\/[^/]+\/candles$/.test(u.pathname)) body = candles(decodeURIComponent(u.pathname.split("/")[3]), u.searchParams.get("tf") ?? "5m");
  res.setHeader("Access-Control-Allow-Origin", "*"); res.setHeader("Content-Type", "application/json");
  if (body === null) { res.statusCode = 404; res.end("{}"); return; }
  res.end(JSON.stringify(body));
}).listen(5080, () => console.log("mock API on :5080 (fabricated data, feed reports not live)"));
