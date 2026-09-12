"use client";
import { useCallback, useState, type FormEvent } from "react";
import { productRequest } from "@/lib/product-api";
import { Icon } from "./Primitives";
import { Modal } from "./Modal";
export type AuthMode = "login" | "register" | "forgot-password" | "reset-password" | "confirm-email";
export function Auth({ initialMode, onClose, onSuccess }: { initialMode: AuthMode; onClose: () => void; onSuccess: () => Promise<void> }) {
  const [mode, setMode] = useState(initialMode), [busy, setBusy] = useState(false), [error, setError] = useState(""), [message, setMessage] = useState("");
  const params = typeof window === "undefined" ? new URLSearchParams() : new URLSearchParams(window.location.search);
  const close = useCallback(onClose, [onClose]);
  const title = mode === "register" ? "Make room for perspective." : mode === "forgot-password" ? "Reset your password." : mode === "reset-password" ? "Choose a new password." : mode === "confirm-email" ? "Confirm your email." : "Welcome back.";
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); setMessage(""); setBusy(true);
    const data = new FormData(event.currentTarget);
    const body = Object.fromEntries(data.entries());
    if (mode === "reset-password") body.token = params.get("token") ?? "";
    if (mode === "confirm-email") { body.userId = params.get("userId") ?? ""; body.token = params.get("token") ?? ""; }
    try {
      const result = await productRequest<{ message?: string }>(`/auth/${mode}`, "POST", body);
      if (mode === "login" || mode === "register") { await onSuccess(); if (mode === "login") onClose(); else setMessage(result.message ?? "Account created. Check your email to confirm your address, then sign in."); }
      else { setMessage(result.message ?? (mode === "forgot-password" ? "If an eligible account exists, recovery instructions will be sent to its email address." : "Your request was completed.")); await onSuccess(); }
    } catch (e) { setError(e instanceof Error ? e.message : "Could not complete the request."); } finally { setBusy(false); }
  }
  return <Modal title={title} onClose={close}><p className="sw-form-intro">{mode === "register" ? "Create your free account. Save your preferences and build a watchlist that travels with you." : mode === "login" ? "Your watchlist, preferences, and market perspective await." : mode === "confirm-email" ? "Confirm ownership of your email address using the link you received." : "We’ll help you get back to your account securely."}</p><form className="sw-form" onSubmit={submit}>
    {mode === "register" && <label>Your name<input name="displayName" autoComplete="name" maxLength={80} required placeholder="How should we greet you?" /></label>}
    {mode !== "confirm-email" && <label>Email address<input name="email" type="email" autoComplete="email" required defaultValue={params.get("email") ?? ""} placeholder="you@example.com" /></label>}
    {(mode === "login" || mode === "register" || mode === "reset-password") && <label>{mode === "reset-password" ? "New password" : "Password"}<input name="password" type="password" minLength={mode === "login" ? 1 : 12} autoComplete={mode === "login" ? "current-password" : "new-password"} required placeholder={mode === "login" ? "Your password" : "At least 12 characters"} />{mode !== "login" && <small>Use at least 12 characters, including upper and lowercase letters, a number, and a symbol.</small>}</label>}
    {mode === "register" && <p className="sw-caption">Development account. Review the <a href="/legal#terms" target="_blank" rel="noreferrer">terms</a> and <a href="/legal#privacy" target="_blank" rel="noreferrer">privacy drafts</a>. This preview is not yet a public paid service.</p>}
    {error && <div role="alert" className="sw-notice error">{error}</div>}{message && <div role="status" className="sw-notice success">{message}</div>}
    <button className="sw-button primary" disabled={busy}>{busy ? "Please wait…" : mode === "register" ? "Create free account" : mode === "forgot-password" ? "Send recovery instructions" : mode === "reset-password" ? "Save new password" : mode === "confirm-email" ? "Confirm email address" : "Sign in"}<Icon name="arrow" size={18} /></button>
  </form><div className="sw-auth-links">{mode === "login" ? <><button onClick={() => { setMode("forgot-password"); setError(""); setMessage(""); }}>Forgot your password?</button><span>New here? <button onClick={() => { setMode("register"); setError(""); }}>Create an account</button></span></> : <button onClick={() => { setMode("login"); setError(""); setMessage(""); }}>Back to sign in</button>}</div></Modal>;
}
