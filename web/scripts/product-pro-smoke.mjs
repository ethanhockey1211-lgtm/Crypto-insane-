/** Local seeded-entitlement editor test. This is NOT payment or provider verification.
 * Requires Node 24, SMOKE_FIXTURE_DATABASE (explicit local preview DB), and
 * SMOKE_ALLOW_LOCAL_SUBSCRIPTION_FIXTURE=yes. The server must use
 * Product:Billing:PriceId=price_local_browser_fixture with billing/email/push disabled.
 * Only freshly created example.test accounts are seeded, then deleted through the API.
 */
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { randomBytes } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { chromium } from "playwright";

assert.equal(process.env.SMOKE_ALLOW_LOCAL_SUBSCRIPTION_FIXTURE, "yes", "Explicit local fixture opt-in is required.");
assert.ok(process.env.SMOKE_FIXTURE_DATABASE, "An explicit local preview database path is required.");
const origin = (process.env.SMOKE_BASE_URL || "http://localhost:5080").replace(/\/$/, "");
assert.ok(["localhost", "127.0.0.1", "[::1]"].includes(new URL(origin).hostname), "Local hosts only.");
const output = process.env.SMOKE_OUTPUT_DIR || path.join(os.tmpdir(), "stillwatch-browser-smoke");
await mkdir(output, { recursive: true });
const password = `LocalOnly!${randomBytes(18).toString("base64url")}42`, suffix = Date.now().toString(36);
const report = { startedAt: new Date().toISOString(), origin, scope: "Local database seeded Pro entitlement only; NOT a real purchase or payment verification.", checks: [], artifacts: [], limitations: ["No actual market alerts, payment events, email or push delivery were exercised."] };
const accounts = [], contexts = [];
let database, browser;
const scrub = value => String(value).replaceAll(password, "[redacted]");
async function check(name, action) { try { await action(); report.checks.push({ name, status: "passed" }); console.log(`PASS ${name}`); } catch (error) { report.checks.push({ name, status: "failed", error: scrub(error.stack || error) }); throw error; } }
async function me(context) { const response = await context.request.get(`${origin}/api/product/me`); assert.equal(response.status(), 200); return response.json(); }
async function write(context, endpoint, method, data) { const csrf = await context.request.get(`${origin}/api/product/auth/csrf`); assert.equal(csrf.status(), 200); return context.request.fetch(`${origin}/api/product${endpoint}`, { method, data, headers: { "X-CSRF-TOKEN": (await csrf.json()).token } }); }
async function rows(context) { const response = await context.request.get(`${origin}/api/product/alerts/rules`); assert.equal(response.status(), 200); return response.json(); }
async function writeClick(page, endpoint, action) { const pending = page.waitForResponse(response => response.url() === `${origin}/api/product${endpoint}` && response.request().method() !== "GET"); await action(); const response = await pending; assert.ok(response.ok(), `${endpoint}: ${response.status()} ${scrub(await response.text())}`); return response; }
async function signup(label) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: "reduce" }); contexts.push(context);
  const page = await context.newPage(); page.setDefaultTimeout(15000);
  const initial = await me(context); assert.equal(initial.capabilities.emailConfigured, false); assert.equal(initial.capabilities.billingConfigured, false); assert.equal(initial.capabilities.liveChargingEnabled, false);
  await page.goto(`${origin}/app/?auth=register`, { waitUntil: "networkidle" });
  const dialog = page.getByRole("dialog"); const email = `pro-fixture-${label}-${suffix}@example.test`;
  await dialog.getByLabel("Your name").fill("Local Pro Fixture"); await dialog.getByLabel("Email address").fill(email); await dialog.getByLabel(/^Password/).fill(password);
  const account = { context, page, email, id: null }; accounts.push(account);
  await writeClick(page, "/auth/register", () => dialog.getByRole("button", { name: "Create free account", exact: true }).click());
  const value = await me(context); account.id = value.user.id;
  const changed = database.prepare("UPDATE Subscriptions SET Status='active', PriceId='price_local_browser_fixture', StripeSubscriptionId='local-browser-fixture', LatestInvoicePaid=1, CurrentPeriodEnd=? WHERE UserId=? AND Status='free' AND StripeCustomerId IS NULL")
    .run(new Date(Date.now() + 3600000).toISOString(), account.id);
  assert.equal(changed.changes, 1, "Only this fresh, unbilled local fixture account may be seeded.");
  assert.equal((await me(context)).entitlements.alerts, true, "The preview server must use the explicit local fixture price.");
  assert.equal((await write(context, "/watchlist", "PUT", { symbols: ["BTC-USD", "ETH-USD"] })).status(), 200);
  await page.goto(`${origin}/app/?tab=alerts`, { waitUntil: "networkidle" });
  return account;
}

try {
  database = new DatabaseSync(path.resolve(process.env.SMOKE_FIXTURE_DATABASE)); database.exec("PRAGMA busy_timeout=10000;");
  browser = await chromium.launch({ headless: true, channel: process.env.SMOKE_BROWSER_CHANNEL || "msedge" });
  let owner, other, rule;
  await check("Two fresh local customers receive explicitly seeded Pro entitlements", async () => { owner = await signup("owner"); other = await signup("other"); });
  await check("Mobile rule editor uses server limits and creates a normalized custom rule", async () => {
    const config = await (await owner.context.request.get(`${origin}/api/product/push/config`)).json();
    assert.equal(config.enabled, false, "Push must be disabled for this fixture test.");
    await owner.page.getByRole("button", { name: "New rule", exact: true }).click();
    const dialog = owner.page.getByRole("dialog");
    await dialog.getByLabel("Rule name", { exact: true }).fill("Local fixture rule");
    await dialog.getByLabel(/^Markets, separated by commas/).fill("BTC, ETH/USD");
    await dialog.getByLabel(/^Conditions to monitor/).selectOption("custom");
    const score = dialog.getByLabel("Minimum score", { exact: true });
    assert.equal(Number(await score.getAttribute("min")), config.ruleLimits.minimumScore);
    await score.fill(String(config.ruleLimits.minimumScore - 1)); assert.equal(await score.evaluate(element => element.validity.rangeUnderflow), true);
    await score.fill(String(Math.max(75, config.ruleLimits.minimumScore)));
    await dialog.getByLabel("Hold condition (seconds)", { exact: true }).fill("30"); await dialog.getByLabel("Cooldown (minutes)", { exact: true }).fill("15");
    assert.ok(await owner.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    const shot = path.join(output, "smoke-pro-rule-editor-local-fixture.png"); await owner.page.screenshot({ path: shot }); report.artifacts.push(shot);
    await writeClick(owner.page, "/alerts/rules", () => dialog.getByRole("button", { name: "Create alert rule", exact: true }).click());
    const list = await rows(owner.context); assert.equal(list.length, 1); rule = list[0]; assert.deepEqual(rule.symbols, ["BTC-USD", "ETH-USD"]); assert.deepEqual(rule.setupTypes, ["Breakout"]);
  });
  await check("Rule edits and pause/resume persist through the real API", async () => {
    await owner.page.getByRole("button", { name: "Edit", exact: true }).click();
    const dialog = owner.page.getByRole("dialog"); await dialog.getByLabel("Rule name", { exact: true }).fill("Edited local fixture");
    await dialog.getByLabel("Hold condition (seconds)", { exact: true }).fill("45");
    await writeClick(owner.page, `/alerts/rules/${rule.id}`, () => dialog.getByRole("button", { name: "Save changes", exact: true }).click());
    rule = (await rows(owner.context))[0]; assert.equal(rule.name, "Edited local fixture"); assert.equal(rule.holdSeconds, 45);
    await writeClick(owner.page, `/alerts/rules/${rule.id}`, () => owner.page.getByRole("button", { name: "Pause", exact: true }).click()); assert.equal((await rows(owner.context))[0].enabled, false);
    await writeClick(owner.page, `/alerts/rules/${rule.id}`, () => owner.page.getByRole("button", { name: "Resume", exact: true }).click()); assert.equal((await rows(owner.context))[0].enabled, true);
  });
  await check("Another Pro customer cannot list, update or delete the owner's rule", async () => {
    assert.deepEqual(await rows(other.context), []);
    assert.equal((await write(other.context, `/alerts/rules/${rule.id}`, "PUT", { ...rule, name: "Forbidden edit" })).status(), 404);
    assert.equal((await write(other.context, `/alerts/rules/${rule.id}`, "DELETE")).status(), 404);
    assert.equal((await rows(owner.context))[0].name, "Edited local fixture");
  });
  await check("Engine score floor and market-data/push launch gates remain enforced for seeded Pro", async () => {
    const config = await (await owner.context.request.get(`${origin}/api/product/push/config`)).json();
    assert.equal((await write(owner.context, `/alerts/rules/${rule.id}`, "PUT", { ...rule, minimumScore: config.ruleLimits.minimumScore - 1 })).status(), 400);
    assert.equal((await owner.context.request.get(`${origin}/api/product/scanner`)).status(), 503);
    assert.equal(await owner.page.getByRole("button", { name: "Enable on this device", exact: true }).isDisabled(), true);
    assert.deepEqual(await (await owner.context.request.get(`${origin}/api/product/alerts/history`)).json(), []);
  });
  await check("Deleting a rule through the UI removes it from persistent customer state", async () => {
    await writeClick(owner.page, `/alerts/rules/${rule.id}`, () => owner.page.getByRole("button", { name: "Delete Edited local fixture", exact: true }).click());
    assert.deepEqual(await rows(owner.context), []);
  });
} catch (error) { report.error = scrub(error.stack || error); console.error(`FAIL ${scrub(error.message || error)}`); process.exitCode = 1; }
finally {
  for (const account of accounts) {
    try {
      const deletion = await write(account.context, "/account", "DELETE", { password }); assert.equal(deletion.status(), 200);
      if (account.id) assert.equal(database.prepare("SELECT COUNT(*) AS total FROM AspNetUsers WHERE Id=?").get(account.id).total, 0);
      report.checks.push({ name: "Seeded fixture account and owned data cleanup", status: "passed" });
    } catch (error) { report.checks.push({ name: "Seeded fixture cleanup", status: "failed", error: scrub(error.message || error) }); process.exitCode = 1; }
  }
  for (const context of contexts) await context.close(); if (browser) await browser.close(); if (database) database.close();
  report.finishedAt = new Date().toISOString(); report.passed = report.checks.filter(value => value.status === "passed").length; report.failed = report.checks.filter(value => value.status === "failed").length;
  await writeFile(path.join(output, "browser-pro-fixture-report.json"), JSON.stringify(report, null, 2));
  console.log(`Local seeded-Pro browser checks: ${report.passed} passed, ${report.failed} failed. This did not verify a payment.`);
}
