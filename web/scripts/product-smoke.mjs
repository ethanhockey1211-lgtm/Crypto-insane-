/**
 * Local browser smoke verification. Start the API and web app first, then run:
 * SMOKE_BASE_URL=http://localhost:3000 SMOKE_API_URL=http://localhost:5080 node scripts/product-smoke.mjs
 * Optional: SMOKE_OUTPUT_DIR and SMOKE_BROWSER_CHANNEL (msedge/chrome).
 * Creates isolated example.test accounts and deletes them in finally. Never uses payment or push providers.
 */
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { mkdir, unlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { chromium } from "playwright";

const base = (process.env.SMOKE_BASE_URL || "http://localhost:3000").replace(/\/$/, "");
const api = (process.env.SMOKE_API_URL || "http://localhost:5080").replace(/\/$/, "");
for (const origin of [base, api]) {
  assert.ok(["localhost", "127.0.0.1", "[::1]"].includes(new URL(origin).hostname), "This smoke script only runs against local development hosts.");
}
const output = process.env.SMOKE_OUTPUT_DIR || path.join(os.tmpdir(), "stillwatch-browser-smoke");
const deferLayout = process.env.SMOKE_DEFER_LAYOUT === "1";
await mkdir(output, { recursive: true });
const runId = Date.now().toString(36);
const password = "LocalOnly!" + randomBytes(18).toString("base64url") + "42";
const report = {
  startedAt: new Date().toISOString(), base, api, browser: "", layoutChecksDeferred: deferLayout, checks: [], artifacts: [],
  limitations: ["Local development smoke test only.", "No actual SMTP, Stripe checkout, payment, OS installation or device push delivery was tested."],
};
const accounts = [];
const contexts = [];
let browser;
let currentPage;
let deliberatelyOffline = false;
const pageErrors = [];
const scrub = value => String(value).replaceAll(password, "[redacted]");
async function check(name, test) {
  const began = Date.now();
  try { await test(); report.checks.push({ name, status: "passed", durationMs: Date.now() - began }); console.log(`PASS ${name}`); }
  catch (error) { report.checks.push({ name, status: "failed", error: scrub(error.stack || error), durationMs: Date.now() - began }); throw error; }
}
async function screenshot(page, name, fullPage = false) {
  if (deferLayout && name !== "failure") return;
  const file = path.join(output, `smoke-${name}.png`);
  if (!fullPage) await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: file, fullPage }); report.artifacts.push(file);
}
async function me(context) { const response = await context.request.get(`${api}/api/product/me`); assert.equal(response.status(), 200); return response.json(); }
async function writeApi(context, endpoint, method, data) {
  const token = await context.request.get(`${api}/api/product/auth/csrf`);
  assert.equal(token.status(), 200);
  return context.request.fetch(`${api}/api/product${endpoint}`, { method, data, headers: { "X-CSRF-TOKEN": (await token.json()).token } });
}
async function waitMe(context, predicate) {
  for (let count = 0; count < 40; count++) { const value = await me(context); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw new Error("Account state did not reach the expected value.");
}
async function newContext(viewport = { width: deferLayout ? 1440 : 390, height: 844 }) {
  const context = await browser.newContext({ viewport, deviceScaleFactor: 1, locale: "en-US", timezoneId: "America/Chicago", reducedMotion: "reduce" });
  contexts.push(context);
  const page = await context.newPage(); page.setDefaultTimeout(15000);
  page.on("pageerror", error => { if (!deliberatelyOffline) pageErrors.push(scrub(error.message)); });
  return { context, page };
}
async function assertNoOverflow(page, location) {
  if (deferLayout) return;
  const dimensions = await page.evaluate(() => ({ inner: innerWidth, body: document.body.scrollWidth, document: document.documentElement.scrollWidth }));
  assert.ok(Math.max(dimensions.body, dimensions.document) <= dimensions.inner + 1, `${location} overflows horizontally: ${JSON.stringify(dimensions)}`);
}
async function clickWrite(page, endpoint, action) {
  const pending = page.waitForResponse(response => response.url().split("?")[0].replace(/\/$/, "") === `${api}/api/product${endpoint}`.replace(/\/$/, "") && response.request().method() !== "GET");
  await action(); const response = await pending;
  assert.ok(response.ok(), `${endpoint} returned ${response.status()}: ${scrub(await response.text())}`);
  return response;
}
async function signUp(context, page, email) {
  await page.goto(`${base}/app/?auth=register`, { waitUntil: "domcontentloaded" });
  const dialog = page.getByRole("dialog"); await dialog.waitFor();
  await dialog.getByLabel("Your name").fill("Preview Tester");
  await dialog.getByLabel("Email address").fill(email);
  await dialog.getByLabel(/^Password/).fill(password);
  const account = { context, email, created: false }; accounts.push(account);
  await clickWrite(page, "/auth/register", () => dialog.getByRole("button", { name: "Create free account", exact: true }).click());
  account.created = true;
  await waitMe(context, value => value.authenticated);
  await dialog.waitFor({ state: "hidden" });
  await page.getByRole("heading", { name: "Set up your monitoring", exact: true }).waitFor();
}
async function tab(page, label) {
  const mobile = page.getByRole("navigation", { name: "Mobile workspace", exact: true });
  const navigation = await mobile.isVisible() ? mobile : page.getByRole("navigation", { name: "Workspace", exact: true });
  await navigation.getByRole("button", { name: new RegExp(`^${label}(?:\\s+PRO)?$`) }).click();
}

try {
  try { browser = await chromium.launch({ headless: true, ...(process.env.SMOKE_BROWSER_CHANNEL ? { channel: process.env.SMOKE_BROWSER_CHANNEL } : {}) }); }
  catch { browser = await chromium.launch({ headless: true, channel: process.env.SMOKE_BROWSER_CHANNEL || (process.platform === "win32" ? "msedge" : "chrome") }); }
  report.browser = browser.version();
  const mobile = await newContext(); currentPage = mobile.page;
  await check("Local test configuration has no outbound account email delivery", async () => {
    assert.equal((await me(mobile.context)).capabilities.emailConfigured, false, "Disable Product:Mail in the local smoke environment before creating temporary accounts.");
  });
  await check(deferLayout ? "Landing renders at mobile/desktop widths (geometry deferred)" : "Landing renders at 360, 390, 1366 and 1440px without page overflow", async () => {
    await mobile.page.goto(base, { waitUntil: "networkidle" });
    await mobile.page.getByRole("heading", { level: 1, name: /Stay in the loop/ }).waitFor();
    for (const width of [360, 390, 1440, 1366]) { await mobile.page.setViewportSize({ width, height: 844 }); await assertNoOverflow(mobile.page, `Landing ${width}px`); }
    await screenshot(mobile.page, "desktop-landing");
    await mobile.page.setViewportSize({ width: deferLayout ? 1440 : 390, height: 844 }); await screenshot(mobile.page, "mobile-landing");
  });
  await check("Free app and example analysis render without fabricated live claims", async () => {
    await mobile.page.goto(`${base}/app/`, { waitUntil: "networkidle" });
    await mobile.page.getByRole("button", { name: "Read example", exact: true }).waitFor();
    assert.equal((await me(mobile.context)).authenticated, false);
    await assertNoOverflow(mobile.page, "Home 390px"); await screenshot(mobile.page, "mobile-home");
    if (!deferLayout) {
      await mobile.page.setViewportSize({ width: 360, height: 800 }); await assertNoOverflow(mobile.page, "Home 360px"); await screenshot(mobile.page, "mobile-home-360");
      await mobile.page.setViewportSize({ width: 1366, height: 900 }); await assertNoOverflow(mobile.page, "Home 1366px"); await screenshot(mobile.page, "desktop-home");
      await mobile.page.setViewportSize({ width: 390, height: 844 });
    }
    await mobile.page.getByRole("button", { name: "Read example", exact: true }).click();
    await mobile.page.getByRole("dialog").waitFor(); await assertNoOverflow(mobile.page, "Example dialog 390px");
    await mobile.page.getByRole("button", { name: "Close dialog" }).click();
  });
  await check("Auth input keeps focus across refresh ticks and Escape restores the opener", async () => {
    const opener = mobile.page.getByRole("button", { name: "Sign in", exact: true });
    await opener.click();
    const dialog = mobile.page.getByRole("dialog");
    const email = dialog.getByLabel("Email address");
    await email.fill("focus-check@example.test");
    await mobile.page.waitForTimeout(2200);
    assert.equal(await email.evaluate(element => document.activeElement === element), true, "Background updates must not steal input focus");
    await mobile.page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
    assert.equal(await opener.evaluate(element => document.activeElement === element), true, "Escape must restore focus to the opening control");
  });
  await check("Real signup creates a private authenticated cookie session", async () => {
    await signUp(mobile.context, mobile.page, `smoke-${runId}@example.test`);
    const cookie = (await mobile.context.cookies(api)).find(value => value.name === "scanner.session");
    assert.ok(cookie?.httpOnly, "Authentication cookie must be HttpOnly");
    assert.equal(cookie.sameSite, "Lax");
    assert.equal((await me(mobile.context)).user.email, `smoke-${runId}@example.test`);
  });
  await check("Onboarding saves exchange, IANA time zone, quiet hours and fee assumptions", async () => {
    await mobile.page.getByLabel("Time zone", { exact: true }).fill("America/Chicago");
    await mobile.page.getByLabel("Taker fee (bps)", { exact: true }).fill("35");
    await mobile.page.getByLabel("Maker fee (bps)", { exact: true }).fill("20");
    await mobile.page.getByLabel("Slippage (bps)", { exact: true }).fill("12");
    await mobile.page.getByLabel("Pause notifications during quiet hours").check();
    await clickWrite(mobile.page, "/preferences", () => mobile.page.getByRole("button", { name: "Save and finish setup", exact: true }).click());
    const value = await waitMe(mobile.context, result => result.preferences.onboardingComplete);
    assert.equal(value.preferences.timeZone, "America/Chicago"); assert.equal(value.preferences.takerFeeBps, 35);
    await assertNoOverflow(mobile.page, "Account 390px"); await screenshot(mobile.page, "mobile-account");
  });
  await check("Watchlist form saves canonical USD markets", async () => {
    await tab(mobile.page, "Watchlist");
    await mobile.page.getByRole("textbox", { name: "Market to add to watchlist" }).fill("BTC");
    await clickWrite(mobile.page, "/watchlist", () => mobile.page.getByRole("button", { name: "Add market", exact: true }).click());
    await waitMe(mobile.context, result => result.watchlist.includes("BTC-USD"));
    await mobile.page.getByRole("textbox", { name: "Market to add to watchlist" }).fill("ETH/USD");
    await clickWrite(mobile.page, "/watchlist", () => mobile.page.getByRole("button", { name: "Add market", exact: true }).click());
    const value = await me(mobile.context); assert.deepEqual(value.watchlist, ["BTC-USD", "ETH-USD"]);
    await assertNoOverflow(mobile.page, "Watchlist 390px"); await screenshot(mobile.page, "mobile-watchlist");
  });
  let secondDevice;
  await check("A fresh browser context signs in and synchronizes saved preferences/watchlist", async () => {
    secondDevice = await newContext();
    await secondDevice.page.goto(`${base}/app/?auth=login&tab=watchlist`, { waitUntil: "domcontentloaded" });
    const dialog = secondDevice.page.getByRole("dialog"); await dialog.waitFor();
    await dialog.getByLabel("Email address").fill(`smoke-${runId}@example.test`);
    await dialog.getByLabel(/^Password/).fill(password);
    await clickWrite(secondDevice.page, "/auth/login", () => dialog.getByRole("button", { name: "Sign in", exact: true }).click());
    const value = await waitMe(secondDevice.context, result => result.authenticated);
    assert.deepEqual(value.watchlist, ["BTC-USD", "ETH-USD"]); assert.equal(value.preferences.takerFeeBps, 35);
    await secondDevice.page.getByRole("button", { name: "Remove BTC-USD from watchlist" }).waitFor();
  });
  await check("Support submission displays a durable receipt without claiming email delivery", async () => {
    await tab(mobile.page, "Account");
    await mobile.page.getByText("Save a support request", { exact: true }).click();
    await mobile.page.getByLabel("Subject", { exact: true }).fill("Local smoke verification");
    await mobile.page.getByLabel("How can we help?", { exact: true }).fill("Automated local browser verification. This temporary account will be deleted.");
    const response = await clickWrite(mobile.page, "/support/", () => mobile.page.getByRole("button", { name: "Save support request", exact: true }).click());
    const receipt = await response.json(); assert.ok(receipt.id);
    await mobile.page.getByText(new RegExp(`Reference: ${receipt.id}`)).waitFor();
    assert.match(receipt.message, /no email has been sent/i);
  });
  await check("A second customer cannot see the first customer's watchlist or support requests", async () => {
    const other = await newContext();
    await signUp(other.context, other.page, `smoke-other-${runId}@example.test`);
    assert.deepEqual((await me(other.context)).watchlist, []);
    const tickets = await other.context.request.get(`${api}/api/product/support/`); assert.equal(tickets.status(), 200); assert.deepEqual(await tickets.json(), []);
    const privateRows = await other.context.request.get(`${api}/api/product/watchlist?userId=${encodeURIComponent((await me(mobile.context)).user.id)}`);
    assert.deepEqual((await privateRows.json()).symbols, []);
  });
  await check("Checkout success query cannot unlock Pro; live charging remains disabled", async () => {
    await mobile.page.goto(`${base}/app/?tab=account&checkout=success`, { waitUntil: "networkidle" });
    const value = await me(mobile.context); assert.equal(value.subscription.plan, "Free"); assert.equal(value.entitlements.scanner, false);
    assert.equal(value.capabilities.liveChargingEnabled, false); assert.equal(value.capabilities.billingMode, "test");
    assert.equal((await mobile.context.request.get(`${api}/api/scanner/`)).status(), 403);
    assert.equal((await mobile.context.request.post(`${api}/hubs/market/negotiate?negotiateVersion=1`)).status(), 404);
    if (!value.capabilities.billingConfigured) assert.equal(await mobile.page.getByRole("button", { name: "Try Pro checkout in test mode", exact: true }).isDisabled(), true);
  });
  await check(deferLayout ? "Scanner/alerts navigation renders (geometry deferred)" : "Mobile scanner/alerts and desktop app remain within the viewport", async () => {
    for (const name of ["Scanner", "Alerts", "Home"]) { await tab(mobile.page, name); await assertNoOverflow(mobile.page, `${name} 390px`); }
    await tab(mobile.page, "Scanner"); await screenshot(mobile.page, "mobile-scanner");
    await mobile.page.setViewportSize({ width: 1440, height: 960 }); await assertNoOverflow(mobile.page, "Scanner desktop"); await screenshot(mobile.page, "desktop-scanner");
    await mobile.page.setViewportSize({ width: deferLayout ? 1440 : 390, height: 844 });
  });
  await check("Logout clears private API access while the other device keeps its own session", async () => {
    await tab(mobile.page, "Account");
    await clickWrite(mobile.page, "/auth/logout", () => mobile.page.getByRole("button", { name: "Sign out", exact: true }).click());
    await waitMe(mobile.context, result => !result.authenticated);
    assert.equal((await mobile.context.request.get(`${api}/api/product/watchlist`)).status(), 401);
    assert.equal((await me(secondDevice.context)).authenticated, true);
  });
  await check("Service worker installs, avoids private caching and serves an honest offline fallback", async () => {
    await mobile.page.waitForFunction(() => Boolean(navigator.serviceWorker.controller), null, { timeout: 20000 });
    const manifestResponse = await mobile.context.request.get(`${base}/manifest.webmanifest`);
    const manifest = await manifestResponse.json(); assert.equal(manifest.display, "standalone"); assert.ok(manifest.icons.some(icon => icon.sizes === "192x192"));
    const urls = await mobile.page.evaluate(async () => (await Promise.all((await caches.keys()).map(async key => (await (await caches.open(key)).keys()).map(request => request.url)))).flat());
    assert.ok(urls.some(url => new URL(url).pathname === "/offline/"));
    assert.ok(urls.every(url => !new URL(url).pathname.startsWith("/api/") && !new URL(url).pathname.startsWith("/app")), "Service worker must not cache private or live app data");
    deliberatelyOffline = true; await mobile.context.setOffline(true);
    await mobile.page.goto(`${base}/app/?offline-probe=${runId}`, { waitUntil: "domcontentloaded" });
    await mobile.page.getByRole("heading", { name: "You’re offline.", exact: true }).waitFor();
    await screenshot(mobile.page, "mobile-offline");
    await mobile.context.setOffline(false); deliberatelyOffline = false;
  });
  await check("No uncaught browser application errors during online flows", async () => { assert.deepEqual(pageErrors, []); });
} catch (error) {
  console.error(`FAIL ${scrub(error.message || error)}`);
  if (!report.checks.length || report.checks.at(-1).status !== "failed") report.checks.push({ name: "Browser session setup", status: "failed", error: scrub(error.message || error) });
  if (currentPage) { try { await screenshot(currentPage, "failure", true); } catch { /* Context may be unavailable. */ } }
  report.error = scrub(error.stack || error); process.exitCode = 1;
} finally {
  for (const account of accounts) {
    try {
      await account.context.setOffline(false);
      const value = await me(account.context);
      if (!value.authenticated || value.user.email !== account.email) {
        const login = await writeApi(account.context, "/auth/login", "POST", { email: account.email, password });
        if (login.status() === 401 && !account.created) { report.checks.push({ name: "Unconfirmed signup cleanup: no accessible account", status: "passed" }); continue; }
        assert.equal(login.status(), 200);
      }
      const deletion = await writeApi(account.context, "/account", "DELETE", { password });
      report.checks.push({ name: `Temporary account cleanup ${account.email.startsWith("smoke-other") ? "second customer" : "first customer"}`, status: deletion.ok() ? "passed" : "failed", statusCode: deletion.status() });
      if (!deletion.ok()) process.exitCode = 1;
    } catch (error) { report.checks.push({ name: "Temporary account cleanup", status: "failed", error: scrub(error.message || error) }); process.exitCode = 1; }
  }
  for (const context of contexts) await context.close();
  if (browser) await browser.close();
  report.finishedAt = new Date().toISOString(); report.passed = report.checks.filter(value => value.status === "passed").length; report.failed = report.checks.filter(value => value.status === "failed").length;
  if (report.failed === 0 && !report.error) await unlink(path.join(output, "smoke-failure.png")).catch(() => {});
  await writeFile(path.join(output, "browser-smoke-report.json"), JSON.stringify(report, null, 2));
  console.log(`Browser smoke: ${report.passed} passed, ${report.failed} failed. Report: ${path.join(output, "browser-smoke-report.json")}`);
}
