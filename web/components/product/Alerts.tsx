"use client";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { productRequest, type ProductMe, type ProductAlertEvent, type ProductAlertRule, type ProductPushConfig, type PushDevice } from "@/lib/product-api";
import { normalizeMarketSymbol } from "@/lib/product-model";
import { EmptyState, Icon, money, StatusPill, timeAgo } from "./Primitives";
import { Modal } from "./Modal";
import type { Opportunity } from "@/lib/types";

const setupTypes = ["Breakout", "BreakoutRetest", "RangeBreakout", "TrendPullback", "VwapReclaim", "SupportBounce", "MomentumContinuation", "VolumeExpansion", "VolatilityExpansion", "Reversal"];
const readable = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2").replaceAll("-", " ");

export function Alerts({ me, onUpgrade, onMessage, onOpenEvidence }: { me: ProductMe | null; onUpgrade: () => void; onMessage: (message: string) => void; onOpenEvidence: (opportunity: Opportunity) => void }) {
  const [rules, setRules] = useState<ProductAlertRule[]>([]), [history, setHistory] = useState<ProductAlertEvent[]>([]), [devices, setDevices] = useState<PushDevice[]>([]);
  const [config, setConfig] = useState<ProductPushConfig | null>(null), [loading, setLoading] = useState(false), [error, setError] = useState(""), [busy, setBusy] = useState("");
  const [editor, setEditor] = useState<ProductAlertRule | "new" | null>(null), [conditionMode, setConditionMode] = useState("preferences");
  const [permission, setPermission] = useState("unknown"), [supported, setSupported] = useState(false), [selectedId, setSelectedId] = useState("");
  const refreshVersion = useRef(0);
  const customerId = me?.user?.id, pro = !!me?.entitlements.alerts;
  const refresh = useCallback(async () => {
    const version = ++refreshVersion.current;
    if (!customerId) { setRules([]); setHistory([]); setDevices([]); setConfig(null); setLoading(false); return; }
    setLoading(true);
    const results = await Promise.allSettled([
      pro ? productRequest<ProductAlertRule[]>("/alerts/rules") : Promise.resolve([]),
      pro ? productRequest<ProductAlertEvent[]>("/alerts/history") : Promise.resolve([]),
      productRequest<PushDevice[]>("/push/devices"), productRequest<ProductPushConfig>("/push/config"),
    ]);
    if (version !== refreshVersion.current) return;
    let failure = "";
    if (results[0].status === "fulfilled") setRules(results[0].value);
    if (results[1].status === "fulfilled") {
      const events = results[1].value;
      if (pro && selectedId && !events.some(event => event.id === selectedId)) {
        try {
          const selected = await productRequest<ProductAlertEvent>(`/alerts/history/${encodeURIComponent(selectedId)}`);
          if (version !== refreshVersion.current) return;
          events.unshift(selected);
        } catch { failure = "This alert is unavailable for your account, or it has been deleted."; }
      }
      if (version !== refreshVersion.current) return;
      setHistory(events);
    }
    if (results[2].status === "fulfilled") setDevices(results[2].value);
    if (results[3].status === "fulfilled") setConfig(results[3].value);
    const failed = results.find(result => result.status === "rejected");
    if (failed?.status === "rejected") failure = String(failed.reason?.message ?? "Could not load your alerts.");
    if (failure) setError(failure);
    setLoading(false);
  }, [customerId, pro, selectedId]);
  useEffect(() => {
    setRules([]); setHistory([]); setDevices([]); setConfig(null); setError(""); setEditor(null);
    void refresh(); const timer = setInterval(() => void refresh(), 30000);
    return () => { clearInterval(timer); refreshVersion.current++; };
  }, [refresh]);
  useEffect(() => {
    setSupported("Notification" in window && "serviceWorker" in navigator && "PushManager" in window && window.isSecureContext);
    setPermission("Notification" in window ? Notification.permission : "unsupported");
    const selected = new URLSearchParams(window.location.search).get("alert") ?? "";
    if (/^[0-9a-f-]{36}$/i.test(selected)) setSelectedId(selected);
  }, []);
  useEffect(() => { if (selectedId && history.some(event => event.id === selectedId)) document.getElementById(`alert-${selectedId}`)?.scrollIntoView({ block: "center" }); }, [selectedId, history]);
  const closeEditor = useCallback(() => setEditor(null), []);
  function openEditor(rule: ProductAlertRule | "new") {
    if (!pro) { onUpgrade(); return; }
    setError(""); setConditionMode(rule !== "new" && rule.setupTypes.length ? "custom" : "preferences"); setEditor(rule);
  }
  async function run(key: string, action: () => Promise<void>) {
    setBusy(key); setError("");
    try { await action(); await refresh(); }
    catch (e) { setError(e instanceof Error ? e.message : "Could not complete this action."); }
    finally { setBusy(""); }
  }
  async function saveRule(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget);
    const symbols = [...new Set(String(data.get("symbols") ?? "").split(",").filter(value => value.trim()).map(normalizeMarketSymbol))];
    const types = conditionMode === "custom" ? data.getAll("setupTypes").map(String) : [];
    if (conditionMode === "custom" && !types.length) { setError("Choose a setup type, or use your account conditions."); return; }
    if (symbols.some(symbol => !symbol || !/^[A-Z0-9]+-USD$/.test(symbol))) { setError("Use USD spot pairs, such as BTC, BTC-USD or ETH/USD."); return; }
    const current = editor && editor !== "new" ? editor : null;
    await run("save-rule", async () => {
      await productRequest(current ? `/alerts/rules/${current.id}` : "/alerts/rules", current ? "PUT" : "POST", {
        name: data.get("name"), enabled: data.get("enabled") === "on", symbols, setupTypes: types,
        minimumScore: Number(data.get("minimumScore")), holdSeconds: Number(data.get("holdSeconds")), cooldownMinutes: Number(data.get("cooldownMinutes")),
      });
      setEditor(null); onMessage("Alert rule saved. Monitoring applies the original engine checks and your current watchlist.");
    });
  }
  async function registerDevice() {
    // Permission comes directly from the explicit button gesture, before other awaited work.
    const permissionPromise = Notification.requestPermission();
    await run("device", async () => {
      const granted = await permissionPromise; setPermission(granted);
      if (granted !== "granted") throw new Error("Notification permission was not granted. Change this in browser or device settings.");
      if (!config?.enabled || !config.publicKey) throw new Error("Server push delivery is not configured.");
      await navigator.serviceWorker.register("/sw.js", { scope: "/" });
      let timer: ReturnType<typeof setTimeout> | undefined;
      const registration = await Promise.race([navigator.serviceWorker.ready, new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("App installation has not finished. Reload and try again.")), 20000);
      })]).finally(() => clearTimeout(timer));
      const base64 = config.publicKey.replace(/-/g, "+").replace(/_/g, "/");
      const raw = atob(base64 + "=".repeat((4 - base64.length % 4) % 4));
      const key = Uint8Array.from(raw, character => character.charCodeAt(0));
      const subscription = await registration.pushManager.getSubscription() ?? await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
      const json = subscription.toJSON();
      await productRequest("/push/subscriptions", "POST", { endpoint: json.endpoint, keys: json.keys, deviceName: /iPhone|iPad/.test(navigator.userAgent) ? "iPhone / iPad" : /Android/.test(navigator.userAgent) ? "Android browser" : "Desktop browser" });
      onMessage("This device is registered. Send a test notification to check delivery.");
    });
  }
  const edited = editor && editor !== "new" ? editor : null, limits = config?.ruleLimits;
  const categories = me?.preferences?.conditions ?? [];
  const limitReached = rules.length >= (me?.entitlements.alertRuleLimit ?? 0);
  return <div className="sw-alert-layout">
    {error && <div className="sw-notice error sw-full" role="alert">{error}</div>}
    {!pro && <section className="sw-card sw-pro-banner sw-full"><span className="sw-feature-icon"><Icon name="alerts" size={25} /></span><div><span className="sw-kicker">PRO · MONITOR ON YOUR TERMS</span><h2>Know when to take a closer look.</h2><p>Set a rule once. The server monitors it while you get on with your day, with cooldowns, quiet hours, and context in every alert.</p></div><button className="sw-button primary" onClick={onUpgrade}>Explore Pro <Icon name="arrow" size={17} /></button></section>}
    <section className="sw-card">
      <div className="sw-section-heading"><div><span className="sw-label">WHAT DESERVES YOUR ATTENTION</span><h2>Your alert rules</h2></div><button className="sw-button secondary small" disabled={pro && (limitReached || !limits)} onClick={() => openEditor("new")}><Icon name="plus" size={16} /> New rule</button></div>
      {pro && <p className="sw-caption">{rules.length} / {me?.entitlements.alertRuleLimit} rules. Account conditions: {categories.length ? categories.join(", ") : "none selected — inherited rules will not match"}.</p>}
      {loading && !rules.length ? <div className="sw-loading" role="status">Loading your rules…</div> : rules.length ? <div className="sw-rule-list">{rules.map(rule => <article key={rule.id}>
        <div><StatusPill tone={rule.enabled ? "green" : "neutral"}>{rule.enabled ? "Enabled" : "Paused"}</StatusPill><h3>{rule.name}</h3><p>{rule.symbols.length ? rule.symbols.join(", ") : "Your watchlist"} · Score ≥ {rule.minimumScore} · {rule.cooldownMinutes}m cooldown</p><small>{rule.setupTypes.length ? rule.setupTypes.map(readable).join(", ") : "Uses your account conditions"}. Hold {rule.holdSeconds}s. Scores are heuristics, not win probabilities.</small></div>
        <div className="sw-inline-actions"><button className="sw-button secondary small" disabled={!!busy || !limits} onClick={() => openEditor(rule)}>Edit</button><button className="sw-button secondary small" disabled={!!busy} onClick={() => run(rule.id, async () => { await productRequest(`/alerts/rules/${rule.id}`, "PUT", { ...rule, enabled: !rule.enabled }); })}>{rule.enabled ? "Pause" : "Resume"}</button><button className="sw-icon-button" aria-label={`Delete ${rule.name}`} disabled={!!busy} onClick={() => run(rule.id, async () => { await productRequest(`/alerts/rules/${rule.id}`, "DELETE"); })}><Icon name="close" size={17} /></button></div>
      </article>)}</div> : <EmptyState icon="alerts" title="A quieter feed starts with a rule." action={<button className="sw-text-link" disabled={pro && !limits} onClick={() => openEditor("new")}>Create your first rule <Icon name="arrow" size={16} /></button>}>Choose conditions worth noticing. Rules use your watchlist and only match when the original engine, cost and freshness checks qualify.</EmptyState>}
    </section>
    <section className="sw-card sw-device-card">
      <div className="sw-section-heading"><div><span className="sw-label">REACH YOU, WHEREVER YOU ARE</span><h2>This device</h2></div><Icon name="download" /></div>
      <StatusPill tone={permission === "granted" ? "green" : "amber"}>{permission === "granted" ? "Browser permission granted" : permission === "denied" ? "Permission blocked" : supported ? "Permission not granted yet" : "Browser setup required"}</StatusPill>
      <p>Install the app, allow notifications, register this device, then send a test. Each device needs its own setup.</p>
      <button className="sw-button primary" disabled={!!busy || !supported || !me?.entitlements.backgroundPush || !config?.enabled || permission === "denied"} onClick={registerDevice}>{busy === "device" ? "Registering device…" : "Enable on this device"}<Icon name="alerts" size={17} /></button>
      <p className="sw-caption">{!me?.entitlements.backgroundPush ? "Background push requires Pro." : !config?.enabled ? "Push delivery is not configured on this deployment." : !me.preferences?.pushEnabled ? "Also enable background push in Account preferences." : "Account push is enabled."} {permission === "denied" ? "Allow notifications in browser settings, then reload." : "Permission alone does not confirm registration or delivery."}</p>
      <details><summary>Supported devices & limitations <Icon name="plus" size={16} /></summary><p>{config?.limitations ?? "iOS and iPadOS 16.4+ require installation to the Home Screen, then permission from inside the installed app. Supported Android and desktop browsers can receive web push. HTTPS is required except on localhost. Delivery depends on the browser, operating system, network and push provider; it is never guaranteed or instantaneous."}</p></details>
      {devices.map(device => <div className="sw-device" key={device.id}><div><strong>{device.deviceName}</strong><small>{!device.enabled ? "Disabled" : device.lastError ?? (device.lastAcceptedAt ? `Last accepted by push service ${timeAgo(device.lastAcceptedAt)}` : "No push-service acceptance recorded yet")}</small></div><div className="sw-inline-actions"><button className="sw-button secondary small" disabled={!!busy || !device.enabled || !pro || !config?.enabled} onClick={() => run(device.id, async () => { await productRequest("/push/test", "POST", { deviceId: device.id }); onMessage("Test queued. Check your device and delivery history. Queueing does not confirm delivery."); })}>Test</button><button className="sw-icon-button" aria-label={`Disable ${device.deviceName}`} disabled={!!busy || !device.enabled} onClick={() => run(device.id, async () => { await productRequest(`/push/subscriptions/${device.id}`, "DELETE"); onMessage("Notifications disabled for this device."); })}><Icon name="close" size={16} /></button></div></div>)}
    </section>
    <section className="sw-card sw-full">
      <div className="sw-section-heading"><div><span className="sw-label">THE RECORD, AS IT HAPPENED</span><h2>Alert history</h2></div><button className="sw-icon-button" aria-label="Refresh alert history" onClick={() => { setError(""); void refresh(); }} disabled={loading}><Icon name="refresh" size={18} /></button></div>
      <p className="sw-caption">Push-service acceptance does not prove display or reading. Historical evidence stays as recorded, including unsuccessful observations.</p>
      {loading && !history.length ? <div className="sw-loading" role="status">Loading alert history…</div> : history.length ? <div className="sw-history">{history.map(event => {
        const expired = Date.parse(event.expiresAt) <= Date.now();
        const label = expired ? "Expired" : event.deliveries.length && event.deliveries.every(delivery => delivery.state === "accepted") ? "Accepted by push service" : readable(event.status);
        return <article key={event.id} id={`alert-${event.id}`} className={selectedId === event.id ? "highlighted" : ""}>
          <div className="sw-history-icon"><Icon name={event.issueStatus === "quiet-hours" ? "moon" : "alerts"} size={19} /></div>
          <div><div className="sw-history-heading"><h3>{event.ruleName}</h3><StatusPill>{label}</StatusPill></div><p>{event.message}</p><small>{new Date(event.issuedAt).toLocaleString()} · {expired ? "Expired" : "Expires"} {new Date(event.expiresAt).toLocaleString()}{event.symbol ? ` · ${event.symbol}` : ""}</small>
            <details open={selectedId === event.id || undefined}><summary>Evidence & delivery diagnostics <Icon name="plus" size={15} /></summary>
              <p className="sw-caption">At issue: {readable(event.issueStatus)}. {event.isTest ? "Device test only." : "Saved evidence describes that moment, not current market conditions."}</p>
              {event.outcome && <div className="sw-notice"><div><strong>{readable(event.outcome.status)}</strong><p>{event.outcome.interpretation}</p><p className="sw-caption">Initial quote {money(event.outcome.initialPrice)} · Target reference {money(event.outcome.targetPrice)} · Stop reference {money(event.outcome.stopPrice)}.</p>{event.outcome.observedAt && <p className="sw-caption">Observed {new Date(event.outcome.observedAt).toLocaleString()}{event.outcome.observedPrice !== null ? ` at ${money(event.outcome.observedPrice)}` : ""}.</p>}<p className="sw-caption">Observation window ends {new Date(event.outcome.windowEndsAt).toLocaleString()}.</p>{event.outcome.dataGap && <p>There is a data gap in the observation window. Price touches may have been missed.</p>}</div></div>}
              {event.deliveries.length ? event.deliveries.map(delivery => <p className="sw-caption" key={delivery.deviceId}>{devices.find(device => device.id === delivery.deviceId)?.deviceName ?? "Registered device"} · {delivery.state === "accepted" ? "Accepted by push service" : readable(delivery.state)} · {delivery.attempts} attempt(s){delivery.lastAttemptAt ? ` · ${new Date(delivery.lastAttemptAt).toLocaleString()}` : ""}{delivery.lastError ? ` · ${delivery.lastError}` : ""}</p>) : <p className="sw-caption">No device delivery attempt was recorded. Quiet-hour matches are not sent later as a backlog.</p>}
              {event.evidence?.opportunity && <button className="sw-button secondary small" onClick={() => onOpenEvidence(event.evidence!.opportunity!)}>Open recorded setup <Icon name="arrow" size={15} /></button>}
            </details>
          </div>
        </article>;
      })}</div> : <EmptyState icon="clock" title="A clear record. From the first alert.">{pro ? "Issued alerts, quiet-hour matches and delivery attempts will appear here. Nothing has been recorded for this account yet. Examples are never mixed into your history." : "Pro includes saved alert history and delivery diagnostics. Existing history becomes available when your Pro access is active."}</EmptyState>}
    </section>
    {editor && <Modal title={edited ? "Edit your alert rule" : "What should we watch for?"} onClose={closeEditor}>
      <p className="sw-form-intro">Conditions build on the scanner’s existing criteria. A score is a heuristic, not a prediction.</p>
      <form className="sw-form" onSubmit={saveRule} key={edited?.id ?? "new"}>
        <label>Rule name<input name="name" maxLength={limits?.maximumNameLength ?? 80} defaultValue={edited?.name ?? ""} placeholder="My focused watchlist" required /></label>
        <label className="sw-checkbox-line"><input type="checkbox" name="enabled" defaultChecked={edited?.enabled ?? true} /><span>Enable this rule</span></label>
        <label>Markets, separated by commas<input name="symbols" defaultValue={edited?.symbols.join(", ") ?? ""} placeholder="BTC-USD, ETH/USD or SOL" /><small>Leave empty to use your whole watchlist. Specified coins must also be in your saved watchlist. USD spot pairs only.</small></label>
        <label>Conditions to monitor<select value={conditionMode} onChange={event => setConditionMode(event.target.value)}><option value="preferences">Use my account conditions</option><option value="custom">Choose specific setup types</option></select></label>
        {conditionMode === "custom" ? <fieldset><legend>Setup types</legend><div className="sw-choice-row">{setupTypes.map(type => <label className="sw-choice" key={type}><input type="checkbox" name="setupTypes" value={type} defaultChecked={edited?.setupTypes.includes(type) ?? type === "Breakout"} /><span>{readable(type)}</span></label>)}</div></fieldset> : <div><p className="sw-caption">Current selection: {categories.length ? categories.join(", ") : "none — choose conditions in Account before this rule can match"}. Future account changes apply to this rule.</p><details><summary>Which setups do these conditions include? <Icon name="plus" size={15} /></summary><p>Breakout: breakout, breakout retest and range breakout. Trend: trend pullback, VWAP reclaim and support bounce. Momentum: momentum continuation, volume expansion and volatility expansion. Reversal is available through specific setup types.</p></details></div>}
        <div className="sw-form-columns"><label>Minimum score<input name="minimumScore" type="number" min={limits?.minimumScore ?? 60} max="100" step="1" defaultValue={edited?.minimumScore ?? Math.max(75, limits?.minimumScore ?? 60)} required /></label><label>Hold condition (seconds)<input name="holdSeconds" type="number" min="0" max={limits?.maximumHoldSeconds ?? 3600} step="1" defaultValue={edited?.holdSeconds ?? 30} required /></label></div>
        <label>Cooldown (minutes)<input name="cooldownMinutes" type="number" min={limits?.minimumCooldownMinutes ?? 5} max={limits?.maximumCooldownMinutes ?? 10080} step="1" defaultValue={edited?.cooldownMinutes ?? 30} required /></label>
        <p className="sw-caption">The server enforces its minimum score of {limits?.minimumScore ?? 60}, rejects stale data and records quiet-hour suppression. Edits retain cooldowns. A continuing match does not repeat until conditions reset.</p>
        {error && <div className="sw-notice error" role="alert">{error}</div>}
        <button className="sw-button primary" disabled={!!busy || !limits}>{busy === "save-rule" ? "Saving rule…" : edited ? "Save changes" : "Create alert rule"}<Icon name="check" size={17} /></button>
      </form>
    </Modal>}
  </div>;
}
