import { chromium } from "playwright";
const out = process.argv[2];
const browser = await chromium.launch({ executablePath: "/opt/pw-browsers/chromium" }).catch(async () => chromium.launch());
const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, colorScheme: "dark" });
const page = await ctx.newPage();
page.on("pageerror", (e) => console.log("PAGE ERROR", e.message));
await page.goto("http://localhost:3000/", { waitUntil: "networkidle" });
await page.waitForTimeout(1500);
await page.screenshot({ path: `${out}/m-scanner.png` });
await page.screenshot({ path: `${out}/m-scanner-full.png`, fullPage: true });
await page.getByRole("row", { name: /XRP/ }).first().click();
await page.waitForTimeout(2500);
await page.screenshot({ path: `${out}/m-drawer.png` });
await page.screenshot({ path: `${out}/m-drawer-full.png`, fullPage: true });
await page.getByRole("button", { name: "Close setup" }).click();
await page.waitForTimeout(500);
for (const v of ["heatmap", "alerts", "paper", "performance", "backtest", "tape"]) {
  const b = page.getByRole("button", { name: v }).first();
  if (!(await b.count())) { console.log("no nav button", v); continue; }
  await b.click();
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${out}/m-${v}.png` });
}
await browser.close();
