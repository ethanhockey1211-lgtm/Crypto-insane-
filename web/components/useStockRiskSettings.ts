"use client";
import { useEffect, useState } from "react";

export type StockRiskDraft = { account: string; cash: string; riskPct: string; cost: string };
const defaults: StockRiskDraft = { account: "", cash: "", riskPct: "0.5", cost: "0.02" };
const storageKey = "kraken.stock-risk-settings.v1";

/** One owner for the scanner and planner, so edits never overwrite one another. */
export function useStockRiskSettings() {
  const [risk, setRisk] = useState<StockRiskDraft>(defaults);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    try {
      const saved: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "null");
      if (saved && typeof saved === "object") {
        const next = { ...defaults };
        for (const key of Object.keys(defaults) as (keyof StockRiskDraft)[]) {
          const value = (saved as Record<string, unknown>)[key];
          if (typeof value === "string" && (key !== "cost" || value.trim() !== "") && value.length <= 30 && Number.isFinite(Number(value)) && Number(value) >= 0) next[key] = value;
        }
        setRisk(next);
      }
    } catch { /* Storage is optional. */ }
    setLoaded(true);
  }, []);
  useEffect(() => {
    if (!loaded) return;
    try { localStorage.setItem(storageKey, JSON.stringify(risk)); } catch { /* Keep edits in memory. */ }
  }, [risk, loaded]);
  return [risk, setRisk] as const;
}
