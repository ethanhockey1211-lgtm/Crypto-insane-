"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { API_BASE } from "@/lib/api";
import { americanDecimal, centralDate, entryAnalysis, historyEstimate, noVig, numeric, percent, PICK_STORAGE_KEY, rankBoard, readEntries, riskLabel, SPORTS, stalePick, STATS, type Estimate, type Pick, type PicksBoard, type SavedEntry, type Side } from "@/lib/prizepicks";

function EstimateView({ pick, now }: { pick: Pick; now: number }) {
  const outdated = stalePick(pick, now);
  return <div className="mt-3">
    <div className="flex items-baseline flex-wrap gap-3"><strong className="num text-3xl text-accent">{outdated ? "—" : percent(pick.estimate.probability)}</strong><span>{outdated ? "Evidence expired" : riskLabel(pick.estimate.probability)}</span></div>
    <p className="text-ink-2 mt-1">{pick.method === "history" ? "History-based estimate" : "Market-implied estimate"}{pick.estimate.conditional ? " · conditional on no tie" : " · strict hit chance"}</p>
    <div className="h-1.5 bg-navy-3 rounded-full my-3 overflow-hidden" aria-hidden="true"><div className="h-full bg-accent" style={{ width: `${outdated ? 0 : pick.estimate.probability * 100}%` }} /></div>
    <p className="text-ink-2">{pick.estimate.evidence}</p>
    <p className="text-ink-2 mt-2">{pick.method === "history" ? "Historical interval" : "Book range"}: {percent(pick.estimate.low)}–{percent(pick.estimate.high)}</p>
  </div>;
}

function CustomPick({ onAdd }: { onAdd: (pick: Pick) => void }) {
  const [player, setPlayer] = useState(""); const [stat, setStat] = useState("player_points");
  const [line, setLine] = useState(""); const [side, setSide] = useState<Side>("More");
  const [sport, setSport] = useState("basketball_nba"); const [game, setGame] = useState(""); const [startsAt, setStartsAt] = useState("");
  const [method, setMethod] = useState<"history" | "manual-odds">("history");
  const [scores, setScores] = useState(""); const [over, setOver] = useState(""); const [under, setUnder] = useState("");
  const [preview, setPreview] = useState<Pick | null>(null); const [error, setError] = useState("");
  function invalidate() { setPreview(null); setError(""); }
  function analyze(e: React.FormEvent) {
    e.preventDefault(); setPreview(null); setError("");
    const value = numeric(line);
    if (!player.trim() || value === null || value < 0 || value > 10000) { setError("Enter a player and a valid line from 0 to 10,000."); return; }
    if (!startsAt || !Number.isFinite(Date.parse(startsAt)) || Date.parse(startsAt) <= Date.now()) { setError("Choose the upcoming game's start time in your local timezone."); return; }
    let estimate: Estimate;
    if (method === "history") {
      const result = historyEstimate(scores, value, side);
      if (typeof result === "string") { setError(result); return; }
      estimate = result;
    } else {
      const p = noVig(americanDecimal(over), americanDecimal(under));
      if (p === null) { setError("Enter both American odds from the same sportsbook, player, stat and exact line (for example -120 and +100)."); return; }
      const probability = side === "More" ? p : 1 - p;
      estimate = { probability, low: probability, high: probability, conditional: value % 1 !== 0.5,
        evidence: "One user-entered sportsbook pair, with its margin removed. Limited evidence; current odds and the exact line are your inputs." };
    }
    setPreview({ id: crypto.randomUUID(), player: player.trim(), stat, line: value, side, sport,
      eventId: game.trim() ? `${sport}:${game.trim().toLowerCase()}:${startsAt.slice(0, 10)}` : "", matchup: game.trim(), startsAt: new Date(startsAt).toISOString(),
      updatedAt: new Date().toISOString(), method, estimate });
  }
  return <section className="panel rounded-lg p-4 sm:p-5" id="custom-pick">
    <h2 className="text-xl font-semibold">Check your own pick</h2>
    <p className="text-ink-2 mt-2 mb-4">Enter the PrizePicks line, then add recent results or matching sportsbook odds.</p>
    <form onSubmit={analyze} onChange={invalidate} className="grid gap-4">
      <div className="grid sm:grid-cols-2 gap-3">
        <label>Player<input className="field mt-1" value={player} onChange={e => setPlayer(e.target.value)} maxLength={160} required placeholder="Player name" /></label>
        <label>League<select className="field mt-1" value={sport} onChange={e => setSport(e.target.value)}>{Object.entries(SPORTS).map(([key, name]) => <option value={key} key={key}>{name}</option>)}</select></label>
        <label>Stat<select className="field mt-1" value={stat} onChange={e => setStat(e.target.value)}>{Object.entries(STATS).map(([key, name]) => <option value={key} key={key}>{name}</option>)}</select></label>
        <label>PrizePicks line<input className="field mt-1" inputMode="decimal" value={line} onChange={e => setLine(e.target.value)} placeholder="e.g. 24.5" required /></label>
        <label>Your pick<select className="field mt-1" value={side} onChange={e => setSide(e.target.value as Side)}><option>More</option><option>Less</option></select></label>
        <label>Game start (your local time)<input className="field mt-1" type="datetime-local" value={startsAt} onChange={e => setStartsAt(e.target.value)} required /></label>
      </div>
      <label>Game label<input className="field mt-1" value={game} onChange={e => setGame(e.target.value)} maxLength={160} placeholder="e.g. Minnesota at Chicago" /><span className="block text-ink-2 mt-1">Use the same label for picks in the same game so related outcomes can be flagged.</span></label>
      <label>Evidence<select className="field mt-1" value={method} onChange={e => setMethod(e.target.value as typeof method)}><option value="history">Recent game results</option><option value="manual-odds">Two-sided sportsbook odds</option></select></label>
      {method === "history" ? <label>Actual results for this stat<textarea className="field mt-1 min-h-24" value={scores} onChange={e => setScores(e.target.value)} maxLength={5000} placeholder="e.g. 26, 19, 31, 24, 28, 21, 33, 18, 27, 25" required /><span className="block text-ink-2 mt-1">5–200 completed games, separated by commas. Exclude DNPs; keep real zeroes. History does not adjust for injuries, opponent or role changes.</span></label>
        : <div className="grid sm:grid-cols-2 gap-3"><label>Over American odds<input className="field mt-1" value={over} onChange={e => setOver(e.target.value)} placeholder="-120" required /></label><label>Under American odds<input className="field mt-1" value={under} onChange={e => setUnder(e.target.value)} placeholder="+100" required /></label><p className="sm:col-span-2 text-ink-2">Use current prices from the same sportsbook for this exact line. Entering a different line invalidates the estimate.</p></div>}
      {error && <p role="alert" className="text-warn">{error}</p>}
      <button className="control-button !border-accent text-accent justify-self-start" type="submit">Analyze pick</button>
    </form>
    {preview && <div className="bg-ground border border-line-strong rounded-lg p-4 mt-4" aria-live="polite"><h3 className="font-semibold">{preview.player} · {preview.side} {preview.line} {STATS[preview.stat]}</h3><EstimateView pick={preview} now={Date.now()} /><button className="control-button mt-4" onClick={() => { onAdd(preview); setPreview(null); }}>Add analyzed pick to entry</button></div>}
  </section>;
}

export default function PrizePicksWorkspace() {
  const [now, setNow] = useState(Date.now()); const [sport, setSport] = useState("americanfootball_nfl");
  const [configured, setConfigured] = useState<boolean | null>(null); const [token, setToken] = useState("");
  const [board, setBoard] = useState<PicksBoard | null>(null); const [loading, setLoading] = useState(false); const [feedMessage, setFeedMessage] = useState("");
  const [query, setQuery] = useState(""); const [picks, setPicks] = useState<Pick[]>([]);
  const [entries, setEntries] = useState<SavedEntry[]>([]); const [loaded, setLoaded] = useState(false); const [storageError, setStorageError] = useState(""); const [notice, setNotice] = useState("");
  const request = useRef<AbortController | null>(null);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 15000);
    const abort = new AbortController();
    fetch(`${API_BASE}/api/prizepicks/status`, { signal: abort.signal, cache: "no-store" }).then(async r => { if (!r.ok) throw new Error(); const data = await r.json(); setConfigured(data.configured === true); }).catch(() => { if (!abort.signal.aborted) setFeedMessage("The sports feed status could not be loaded. Custom analysis is available."); });
    try { setEntries(readEntries(localStorage.getItem(PICK_STORAGE_KEY))); } catch { setStorageError("Browser storage is unavailable. Entries will last only for this session."); }
    setLoaded(true);
    return () => { clearInterval(timer); abort.abort(); request.current?.abort(); };
  }, []);
  useEffect(() => {
    if (!loaded) return;
    try { localStorage.setItem(PICK_STORAGE_KEY, JSON.stringify(entries)); } catch { setStorageError("Your latest entry could not be saved in this browser."); }
  }, [entries, loaded]);
  const refresh = useCallback(async () => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    setLoading(true); setFeedMessage(""); setBoard(null);
    try {
      const r = await fetch(`${API_BASE}/api/prizepicks/board?sport=${encodeURIComponent(sport)}`, { headers: { "X-PrizePicks-Access-Token": token }, signal: controller.signal, cache: "no-store" });
      const data = await r.json() as PicksBoard;
      if (controller.signal.aborted) return;
      if (!r.ok) throw new Error(data.message || "The board could not be loaded.");
      if (!Array.isArray(data.lines) || typeof data.date !== "string") throw new Error("The feed returned an unreadable board.");
      setBoard(data); setNow(Date.now());
    } catch (e) { if (!controller.signal.aborted) setFeedMessage(e instanceof Error ? e.message : "The sports feed is unavailable."); }
    finally { if (request.current === controller) setLoading(false); }
  }, [sport, token]);
  const ranked = rankBoard(board, now);
  const visible = ranked.filter(p => `${p.player} ${STATS[p.stat]} ${p.matchup}`.toLowerCase().includes(query.toLowerCase()));
  const analysis = entryAnalysis(picks, now);
  function add(pick: Pick) {
    if (picks.length >= 6) { setNotice("This entry already has six picks. Remove one to add another."); return; }
    if (picks.some(p => p.player.trim().toLowerCase() === pick.player.trim().toLowerCase())) { setNotice("That player is already in your entry. Remove their pick before replacing it."); return; }
    setPicks(old => [...old, { ...pick, id: crypto.randomUUID() }]); setNotice(`${pick.player} added to your entry.`);
  }
  function save() {
    if (picks.length < 2) return;
    setEntries(old => [{ id: crypto.randomUUID(), savedAt: new Date().toISOString(), picks, result: "pending" as const }, ...old].slice(0, 100));
    setPicks([]); setNotice("Entry saved in this browser. Update the result after the games.");
  }
  return <main className="prizepicks-workspace h-full overflow-y-auto min-w-0 pr-1" data-testid="prizepicks-workspace">
    <header className="panel rounded-lg p-4 sm:p-5 mb-3 flex flex-wrap gap-4 justify-between items-start">
      <div><p className="text-accent uppercase tracking-widest text-sm">Sports · Daily props</p><h1 className="text-3xl font-semibold mt-1">PrizePicks tracker</h1><p className="text-ink-2 mt-2">Compare today’s lines. Check your picks. Understand the entry’s risk.</p></div>
      <div className="text-ink-2"><p className="num">{centralDate(now)} · Central time</p><p className="mt-2">Standard projections · NBA / WNBA / NFL / MLB</p></div>
    </header>
    <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_380px] items-start">
      <div className="min-w-0 grid gap-3">
        <section className="panel rounded-lg p-4 sm:p-5" aria-labelledby="daily-picks-heading">
          <div className="flex flex-wrap gap-3 items-center justify-between"><div><h2 className="text-xl font-semibold" id="daily-picks-heading">Today’s strongest comparisons</h2><p className="text-ink-2 mt-1">Ranked by estimated hit chance among supported, upcoming lines.</p></div><a className="control-button" href="#custom-pick" onClick={e => { e.preventDefault(); document.getElementById("custom-pick")?.scrollIntoView({ block: "start", behavior: "smooth" }); }}>Check my own pick ↓</a></div>
          {configured === false && <div className="rounded-lg bg-ground border border-line-strong p-4 my-4"><h3 className="font-semibold">Connect the daily sports feed</h3><p className="text-ink-2 mt-2">Your server needs a The Odds API key with player props and PrizePicks coverage, plus a private feed access code. Your custom pick analyzer below works now.</p><a className="text-accent underline inline-block mt-2" href="https://github.com/ethanhockey1211-lgtm/Crypto-insane-/blob/claude/crypto-trading-scanner-ymspes/docs/prizepicks-tracker.md" target="_blank" rel="noopener noreferrer">Feed setup instructions ↗</a></div>}
          <form className="flex flex-wrap items-end gap-3 mt-4" onSubmit={e => { e.preventDefault(); void refresh(); }}>
            <label>League<select className="field mt-1" value={sport} onChange={e => { request.current?.abort(); setSport(e.target.value); setBoard(null); setFeedMessage(""); setLoading(false); }}>{Object.entries(SPORTS).map(([key, name]) => <option key={key} value={key}>{name}</option>)}</select></label>
            <label className="grow min-w-0">Feed access code<input className="field mt-1" type="password" autoComplete="off" maxLength={512} value={token} onChange={e => setToken(e.target.value)} placeholder="Private server access code" /></label>
            <button type="submit" className="control-button" disabled={loading || !token.trim()}>{loading ? "Scanning games…" : "Refresh daily picks"}</button>
          </form>
          <p className="text-ink-2 mt-3">Access code stays in memory. Scans share a two-minute server cache and use your provider quota. Quotes expire after five minutes.</p>
          {feedMessage && <p role="alert" className="text-warn mt-3">{feedMessage}</p>}
          {board && <p role="status" className="text-ink-2 mt-3">{board.message} Last scan {new Date(board.asOf).toLocaleTimeString()} · {board.eventsScanned}/{board.eventsAvailable} events.</p>}
          {ranked.length > 0 && <label className="block mt-4">Find a player or stat<input className="field mt-1" value={query} onChange={e => setQuery(e.target.value)} placeholder="Search comparisons" /></label>}
          <div className="grid md:grid-cols-2 gap-3 mt-4" aria-live="polite" aria-busy={loading}>
            {visible.map((pick, i) => <article key={pick.id} className="rounded-lg bg-ground border border-line-strong p-4">
              <div className="flex gap-3 justify-between"><h3 className="text-lg font-semibold">{pick.player}</h3><span className="text-ink-2 num">#{i + 1}</span></div>
              <p className="mt-2 font-medium">{pick.side} {pick.line} {STATS[pick.stat]}</p><p className="text-ink-2 mt-1">{pick.matchup} · {new Date(pick.startsAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</p>
              <EstimateView pick={pick} now={now} /><p className="text-ink-2 mt-2">{pick.books.join(" · ")} · oldest quote {Math.max(0, Math.floor((now - Date.parse(pick.updatedAt)) / 60000))}m ago</p>
              <button className="control-button mt-4 w-full" onClick={() => add(pick)}>Add to my entry</button>
            </article>)}
          </div>
          {!loading && !visible.length && <div className="border border-dashed border-line-strong p-5 rounded-lg mt-3"><p className="font-semibold">{query && ranked.length ? "No matching players" : board ? "No supported picks to rank right now" : "Daily rankings appear after you connect and refresh"}</p><p className="text-ink-2 mt-2">{board ? "A comparison needs a fresh PrizePicks half-point line and the exact same line at two sportsbooks. Started games, integer lines, Demons and Goblins are excluded." : "No sample picks or invented hit rates are shown. You can analyze your own selections below."}</p></div>}
        </section>
        <CustomPick onAdd={add} />
      </div>
      <aside className="panel rounded-lg p-4 sm:p-5 min-w-0 xl:sticky xl:top-0" aria-label="My entry">
        <div className="flex items-center justify-between gap-2"><h2 className="text-xl font-semibold">My entry</h2><span className="num text-ink-2">{picks.length} / 6</span></div>
        <p className="text-ink-2 mt-2">Add daily comparisons or your own analyzed picks.</p>
        {notice && <p role="status" className="text-accent mt-3">{notice}</p>}
        <div className="grid gap-3 mt-4">{picks.map(p => <div key={p.id} className="border border-line-strong rounded-lg p-3 bg-ground"><div className="flex justify-between gap-2"><strong>{p.player}</strong><button className="text-ink-2 underline" aria-label={`Remove ${p.player}`} onClick={() => setPicks(old => old.filter(q => q.id !== p.id))}>Remove</button></div><p className="mt-1">{p.side} {p.line} {STATS[p.stat]}</p><p className="mt-2 text-accent">{stalePick(p, now) ? "Evidence expired" : `${percent(p.estimate.probability)} estimated · ${riskLabel(p.estimate.probability)}`}</p><p className="text-ink-2 mt-1">{p.method === "history" ? "Recent game history" : p.method === "market" ? "Sportsbook comparison" : "Your sportsbook odds"}{p.estimate.conditional ? " · no-tie condition" : ""}</p></div>)}</div>
        {picks.length >= 2 && <div className="mt-5 border-t border-line-strong pt-4" aria-live="polite"><h3 className="font-semibold">Chance every pick hits</h3><p className="num text-4xl text-accent my-2">{analysis.probability === null ? "—" : percent(analysis.probability)}</p>{analysis.probability !== null && <p className="text-ink-2">{riskLabel(analysis.probability)} · {percent(1 - analysis.probability)} chance at least one pick does not hit.</p>}<p className="text-ink-2 mt-3">{analysis.dependence}</p>{analysis.low !== null && analysis.high !== null && <p className="text-ink-2 mt-3">Possible range without an independence assumption: {percent(analysis.low)}–{percent(analysis.high)}. Mathematical bounds from these estimates, not a confidence interval.</p>}</div>}
        {analysis.warnings.map(w => <p key={w} className="text-warn mt-3">{w}</p>)}
        <p className="text-ink-2 mt-4">This measures all picks hitting, not whether a Flex entry pays. Ties, DNPs, Reboots and actual payouts follow the contest’s rules.</p>
        <button className="control-button !border-accent text-accent w-full mt-4" disabled={picks.length < 2} onClick={save}>Save entry to tracker</button>
      </aside>
    </div>
    <section className="panel rounded-lg p-4 sm:p-5 mt-3"><h2 className="text-xl font-semibold">Saved entries</h2><p className="text-ink-2 mt-2">Saved in this browser only, up to 100 entries. Estimates are snapshots from when you saved; results are entered by you.</p>{storageError && <p role="alert" className="text-warn mt-2">{storageError}</p>}
      {!entries.length ? <p className="text-ink-2 py-5">Your saved entries will appear here.</p> : <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-3 mt-4">{entries.map(entry => <article key={entry.id} className="border border-line-strong bg-ground rounded-lg p-4"><p className="text-ink-2 mb-3">{new Date(entry.savedAt).toLocaleString()}</p><ul className="grid gap-2">{entry.picks.map(p => <li key={p.id}>{p.player} · {p.side} {p.line} {STATS[p.stat]} <span className="text-ink-2">({percent(p.estimate.probability)} at save{p.estimate.conditional ? ", conditional" : ""})</span></li>)}</ul><label className="block mt-4">Entry result<select className="field mt-1" value={entry.result} onChange={e => setEntries(old => old.map(item => item.id === entry.id ? { ...item, result: e.target.value as SavedEntry["result"] } : item))}><option value="pending">Pending</option><option value="hit">Every pick hit</option><option value="miss">Not every pick hit</option><option value="void">Void / canceled</option></select></label><button className="control-button mt-3" onClick={() => setEntries(old => old.filter(item => item.id !== entry.id))}>Delete saved entry</button></article>)}</div>}
    </section>
    <details className="panel rounded-lg p-4 sm:p-5 my-3"><summary className="cursor-pointer font-semibold">How estimates and risk work</summary><div className="grid gap-3 text-ink-2 mt-4"><p>Daily estimates remove each sportsbook’s margin from paired Over/Under odds and average the resulting probabilities. They are market opinions, not validated forecasts or proof a PrizePicks entry offers value. A higher hit chance can still have an unfavorable payout.</p><p>History estimates use (hits + 1) / (games + 2) to avoid claiming certainty from a small sample. Ties count as non-hits. Opponents, injuries, playing time and lineup changes are not modeled.</p><p>Risk bands use estimated strict hit chance: 65%+ lower relative risk; 55–64% moderate; 40–54% high; below 40% very high. These are display bands, not calibrated safety ratings. Even lower relative risk can lose.</p><p>Verify the line, player availability and contest payout in PrizePicks before entering. This tracker does not submit entries.</p><p><a className="underline text-accent" href="https://the-odds-api.com/sports-odds-data/bookmaker-apis.html" target="_blank" rel="noopener noreferrer">Sports feed coverage</a> · <a className="underline text-accent" href="https://www.prizepicks.com/help-center/payouts" target="_blank" rel="noopener noreferrer">PrizePicks payout and tie rules</a></p></div></details>
  </main>;
}
