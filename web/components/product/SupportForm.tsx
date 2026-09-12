"use client";
import { useState, type FormEvent } from "react";
import { productRequest } from "@/lib/product-api";

export function SupportForm() {
  const [busy, setBusy] = useState(false), [receipt, setReceipt] = useState(""), [error, setError] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = event.currentTarget, data = new FormData(form);
    setBusy(true); setError(""); setReceipt("");
    try {
      const result = await productRequest<{ id: string; message: string }>("/support/", "POST", Object.fromEntries(data));
      setReceipt(`${result.message} Reference: ${result.id}`); form.reset();
    } catch (err) { setError(err instanceof Error ? err.message : "Your request could not be saved. Try again."); }
    finally { setBusy(false); }
  }
  return <details><summary>Save a support request</summary><p className="sw-caption">Signed-in accounts can save a request for the deployment owner. Public support contact and response times still need setup. Do not include passwords, exchange keys or card details.</p>
    <form className="sw-form" onSubmit={submit}>
      <label>Subject<input name="subject" required maxLength={120} /></label>
      <label>How can we help?<textarea name="message" required maxLength={4000} rows={4} /></label>
      <button className="sw-button secondary" disabled={busy}>{busy ? "Saving…" : "Save support request"}</button>
      {error && <p className="sw-notice error" role="alert">{error}</p>}
      {receipt && <p className="sw-notice" role="status">{receipt}</p>}
    </form>
  </details>;
}
