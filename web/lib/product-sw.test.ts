import { describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import vm from "node:vm";

function worker(online = true) {
  const handlers: Record<string, (event: Record<string, unknown>) => void> = {};
  const showNotification = vi.fn(async () => undefined), openWindow = vi.fn(async () => undefined);
  const self = { location: { origin: "https://example.test" }, navigator: { onLine: online },
    addEventListener: (name: string, callback: (event: Record<string, unknown>) => void) => { handlers[name] = callback; },
    registration: { showNotification }, clients: { matchAll: async () => [], openWindow } };
  const cacheMatch = vi.fn(async () => new Response("Offline explanation"));
  const fetchRequest = vi.fn(async () => { throw new Error("offline"); });
  vm.runInNewContext(readFileSync(resolve("public/sw.js"), "utf8"), { self, URL, Date, Number, Promise, Response,
    caches: { match: cacheMatch }, fetch: fetchRequest });
  async function emit(name: string, event: Record<string, unknown>) {
    let pending: Promise<unknown> | undefined;
    handlers[name]({ ...event, waitUntil: (promise: Promise<unknown>) => { pending = promise; } });
    await pending;
  }
  return { handlers, emit, showNotification, openWindow, cacheMatch, fetchRequest };
}

describe("private data and push service worker", () => {
  it("serves the offline explanation immediately when the device reports offline", async () => {
    const sw = worker(false);
    let response: Promise<Response> | undefined;
    sw.handlers.fetch({ request: { url: "https://example.test/app/", method: "GET", mode: "navigate" }, respondWith: (value: Promise<Response>) => { response = value; } });
    expect(await (await response)!.text()).toBe("Offline explanation");
    expect(sw.fetchRequest).not.toHaveBeenCalled();
  });
  it("uses the same offline explanation when an apparently online network fails", async () => {
    const sw = worker(true);
    let response: Promise<Response> | undefined;
    sw.handlers.fetch({ request: { url: "https://example.test/app/", method: "GET", mode: "navigate" }, respondWith: (value: Promise<Response>) => { response = value; } });
    expect(await (await response)!.text()).toBe("Offline explanation");
    expect(sw.fetchRequest).toHaveBeenCalledOnce();
  });
  it("does not intercept API, hub or cross-origin data requests", () => {
    const sw = worker(), respondWith = vi.fn();
    for (const url of ["https://example.test/api/product/me", "https://example.test/api/product/alerts/history", "https://example.test/hubs/market", "https://other.test/data"])
      sw.handlers.fetch({ request: { url, method: "GET", mode: "navigate" }, respondWith });
    expect(respondWith).not.toHaveBeenCalled();
    expect(sw.cacheMatch).not.toHaveBeenCalled();
  });
  it("displays a fresh same-origin notification without asserting delivery", async () => {
    const sw = worker();
    await sw.emit("push", { data: { json: () => ({ title: "Conditional setup", body: "Observed at issue time", url: "/app/?tab=alerts&alert=123", tag: "123", expiresAt: new Date(Date.now() + 60_000).toISOString() }) } });
    expect(sw.showNotification).toHaveBeenCalledOnce();
    expect(sw.showNotification.mock.calls[0]).toEqual(expect.arrayContaining(["Conditional setup"]));
  });
  it("replaces an expired setup with a neutral notice without stale prices", async () => {
    const sw = worker();
    await sw.emit("push", { data: { json: () => ({ title: "BTC target", body: "$100,000", expiresAt: new Date(0).toISOString(), url: "/app/" }) } });
    expect(sw.showNotification).toHaveBeenCalledWith("Stillwatch · update expired", expect.objectContaining({ body: expect.not.stringContaining("100,000") }));
  });
  it("drops invalid and cross-origin pushes", async () => {
    const sw = worker();
    for (const value of [{ expiresAt: "invalid", url: "/app/" }, { expiresAt: new Date(Date.now() + 60_000).toISOString(), url: "https://phishing.example/app/" }])
      await sw.emit("push", { data: { json: () => ({ title: "Setup", body: "Message", ...value }) } });
    expect(sw.showNotification).not.toHaveBeenCalled();
  });
  it("opens only the app's same-origin alert link", async () => {
    const sw = worker(), close = vi.fn();
    await sw.emit("notificationclick", { notification: { close, data: { url: "https://phishing.example/app" } } });
    expect(sw.openWindow).not.toHaveBeenCalled();
    await sw.emit("notificationclick", { notification: { close, data: { url: "/app/?tab=alerts&alert=123" } } });
    expect(sw.openWindow).toHaveBeenCalledWith("https://example.test/app/?tab=alerts&alert=123");
  });
});
