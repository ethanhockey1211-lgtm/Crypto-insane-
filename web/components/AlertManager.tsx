"use client";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { useAlerts } from "@/lib/store";
import { fmtTime } from "@/lib/format";
import type { AlertCondition, AlertFieldInfo, AlertRule, AlertRuleRequest } from "@/lib/types";

const OPS: Record<string, string> = { Gt: ">", Gte: "≥", Lt: "<", Lte: "≤", Eq: "=", Ne: "≠", CrossesAbove: "crosses above", CrossesBelow: "crosses below" };
const TEXT_OPS = ["Eq", "Ne", "CrossesAbove"];

const EMPTY: AlertRuleRequest = { name: "", enabled: true, symbol: null, conditions: [{ field: "Price", operator: "Gt", value: "" }], holdSeconds: 180, cooldownSeconds: 600, channels: ["browser"], webhookUrl: null, repeatWhileTrue: false };

function describe(r: AlertRule): string {
  return r.conditions.map((c) => `${c.field} ${OPS[c.operator] ?? c.operator} ${c.value}`).join(" AND ");
}

export function AlertManager({ onOpen, presetSymbol }: { onOpen: (s: string) => void; presetSymbol?: string | null }) {
  const events = useAlerts();
  const [rules, setRules] = useState<AlertRule[]>([]);
  const [fields, setFields] = useState<AlertFieldInfo[]>([]);
  const [draft, setDraft] = useState<AlertRuleRequest>({ ...EMPTY, symbol: presetSymbol ?? null });
  const [error, setError] = useState<string | null>(null);
  const [permission, setPermission] = useState<string>("default");

  const load = async () => {
    try { setRules(await api.alerts.list()); } catch (e) { setError(`Alerts unavailable: ${(e as Error).message}`); }
  };
  useEffect(() => {
    void load();
    api.alerts.fields().then((f) => setFields(f.fields)).catch(() => setFields([]));
    if (typeof Notification !== "undefined") setPermission(Notification.permission);
  }, []);
  useEffect(() => { if (presetSymbol) setDraft((d) => ({ ...d, symbol: presetSymbol })); }, [presetSymbol]);

  const kindOf = (f: string) => fields.find((x) => x.name === f)?.kind ?? "number";
  const setCond = (i: number, patch: Partial<AlertCondition>) => setDraft((d) => ({ ...d, conditions: d.conditions.map((c, j) => (j === i ? { ...c, ...patch } : c)) }));

  const submit = async () => {
    setError(null);
    try {
      await api.alerts.create({ ...draft, symbol: draft.symbol?.trim() ? draft.symbol.trim().toUpperCase() : null });
      setDraft({ ...EMPTY, symbol: draft.symbol });
      await load();
    } catch (e) { setError((e as Error).message); }
  };
  const toggle = async (r: AlertRule) => {
    try { await api.alerts.update(r.id, { ...r, enabled: !r.enabled }); await load(); } catch (e) { setError((e as Error).message); }
  };
  const remove = async (r: AlertRule) => {
    try { await api.alerts.remove(r.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  const askPermission = async () => {
    if (typeof Notification === "undefined") return;
    setPermission(await Notification.requestPermission());
  };

  return (
    <section className="panel flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-3 px-3 h-9 border-b border-line">
        <span className="eyebrow">Alerts</span>
        <span className="num text-[11px] text-ink-3">{rules.length} rules · {events.length} fired</span>
        <span className="ml-auto text-[11px] text-ink-3">
          browser notifications: {permission === "granted" ? <span className="up">on</span> : <button className="underline text-ink-2" onClick={askPermission}>enable</button>}
        </span>
      </div>
      <div className="grid md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-0 min-h-0 flex-1 overflow-auto">
        <div className="p-3 border-b md:border-b-0 md:border-r border-line">
          <h3 className="eyebrow mb-2">New rule · all conditions must hold</h3>
          <div className="grid grid-cols-[1fr_110px] gap-2 mb-2">
            <input className="field" placeholder="Name, e.g. XRP breakout confirmed" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} aria-label="Rule name" />
            <input className="field" placeholder="Any symbol" value={draft.symbol ?? ""} onChange={(e) => setDraft({ ...draft, symbol: e.target.value || null })} aria-label="Symbol (optional)" />
          </div>
          <div className="space-y-1.5">
            {draft.conditions.map((c, i) => {
              const kind = kindOf(c.field);
              return (
                <div key={i} className="grid grid-cols-[1fr_120px_1fr_24px] gap-1.5">
                  <select className="field" value={c.field} onChange={(e) => setCond(i, { field: e.target.value, operator: kindOf(e.target.value) === "number" ? "Gt" : "Eq" })} aria-label="Field">
                    {(fields.length ? fields : [{ name: "Price", kind: "number" as const }]).map((f) => <option key={f.name} value={f.name}>{f.name}</option>)}
                  </select>
                  <select className="field" value={c.operator} onChange={(e) => setCond(i, { operator: e.target.value })} aria-label="Operator">
                    {Object.keys(OPS).filter((o) => kind === "number" || TEXT_OPS.includes(o)).map((o) => <option key={o} value={o}>{kind !== "number" && o === "CrossesAbove" ? "becomes" : OPS[o]}</option>)}
                  </select>
                  {kind === "boolean" ? (
                    <select className="field" value={c.value || "true"} onChange={(e) => setCond(i, { value: e.target.value })} aria-label="Value"><option value="true">true</option><option value="false">false</option></select>
                  ) : (
                    <input className="field num" placeholder={kind === "number" ? "value (returns as 0.01 = 1%)" : "e.g. Confirmed, Bullish, Breakout"} value={c.value} onChange={(e) => setCond(i, { value: e.target.value })} aria-label="Value" />
                  )}
                  <button className="text-ink-3 hover:text-down" onClick={() => setDraft({ ...draft, conditions: draft.conditions.filter((_, j) => j !== i) })} aria-label="Remove condition">×</button>
                </div>
              );
            })}
          </div>
          <button className="text-[11px] text-accent mt-1.5" onClick={() => setDraft({ ...draft, conditions: [...draft.conditions, { field: "RelVol", operator: "Gt", value: "1.5" }] })}>+ add condition</button>
          <div className="grid grid-cols-3 gap-2 mt-3">
            <label className="flex flex-col gap-0.5"><span className="eyebrow">Must hold for (s)</span><input className="field num" type="number" min={0} value={draft.holdSeconds} onChange={(e) => setDraft({ ...draft, holdSeconds: parseInt(e.target.value || "0", 10) })} /></label>
            <label className="flex flex-col gap-0.5"><span className="eyebrow">Cooldown (s)</span><input className="field num" type="number" min={0} value={draft.cooldownSeconds} onChange={(e) => setDraft({ ...draft, cooldownSeconds: parseInt(e.target.value || "0", 10) })} /></label>
            <label className="flex items-end gap-1.5 text-[11px] pb-1"><input type="checkbox" checked={draft.repeatWhileTrue} onChange={(e) => setDraft({ ...draft, repeatWhileTrue: e.target.checked })} /> repeat while true</label>
          </div>
          <div className="grid grid-cols-[auto_1fr] gap-2 mt-3 items-center text-[11px]">
            <label className="flex items-center gap-1.5"><input type="checkbox" checked={draft.channels.includes("browser")} onChange={(e) => setDraft({ ...draft, channels: e.target.checked ? [...new Set([...draft.channels, "browser"])] : draft.channels.filter((c) => c !== "browser") })} /> browser</label>
            <div className="flex items-center gap-1.5">
              <input type="checkbox" checked={draft.channels.includes("webhook")} onChange={(e) => setDraft({ ...draft, channels: e.target.checked ? [...new Set([...draft.channels, "webhook"])] : draft.channels.filter((c) => c !== "webhook") })} aria-label="Webhook channel" />
              <input className="field" placeholder="https webhook (Discord, Telegram bridge, email relay)" value={draft.webhookUrl ?? ""} onChange={(e) => setDraft({ ...draft, webhookUrl: e.target.value || null })} disabled={!draft.channels.includes("webhook")} />
            </div>
          </div>
          {error && <p className="warn text-[11.5px] mt-2">{error}</p>}
          <button className="mt-3 px-3 py-1 bg-navy-3 rounded-[3px] text-[12px]" onClick={submit}>Create alert</button>
          <p className="text-[10.5px] text-ink-3 mt-2">Hold time filters single-tick wicks: conditions must stay true for the whole window. Rules fire once per false→true transition unless “repeat while true” is on.</p>
        </div>
        <div className="min-h-0 flex flex-col">
          <div className="p-3 border-b border-line">
            <h3 className="eyebrow mb-2">Rules</h3>
            {rules.length === 0 && <p className="text-ink-3 text-[12px]">No rules yet. Rules live on the scanner server; without a database they reset on restart.</p>}
            <ul className="space-y-1">
              {rules.map((r) => (
                <li key={r.id} className="flex items-start gap-2 text-[12px]">
                  <input type="checkbox" checked={r.enabled} onChange={() => toggle(r)} aria-label={`Enable ${r.name}`} className="mt-1" />
                  <div className="flex-1 min-w-0">
                    <div className={r.enabled ? "" : "text-ink-3"}>{r.name} <span className="text-ink-3">· {r.symbol ?? "any symbol"}</span></div>
                    <div className="text-[11px] text-ink-2 truncate">{describe(r)}{r.holdSeconds ? ` · hold ${r.holdSeconds}s` : ""}{r.cooldownSeconds ? ` · cooldown ${r.cooldownSeconds}s` : ""}{r.lastFiredAt ? ` · last ${fmtTime(r.lastFiredAt)}` : ""}</div>
                  </div>
                  <button className="text-ink-3 hover:text-down" onClick={() => remove(r)} aria-label={`Delete ${r.name}`}>×</button>
                </li>
              ))}
            </ul>
          </div>
          <div className="p-3 min-h-0 flex-1 overflow-auto">
            <h3 className="eyebrow mb-2">Fired</h3>
            {events.length === 0 && <p className="text-ink-3 text-[12px]">Nothing fired yet.</p>}
            <ul className="space-y-1 text-[12px]">
              {events.map((e) => (
                <li key={e.id} className="row-hover cursor-pointer flex gap-2" onClick={() => onOpen(e.symbol)}>
                  <span className="num text-ink-3 shrink-0">{fmtTime(e.at)}</span>
                  <span><span className="text-ink">{e.ruleName}</span> <span className="text-ink-2">{e.message}</span></span>
                </li>
              ))}
            </ul>
          </div>
        </div>
      </div>
    </section>
  );
}
