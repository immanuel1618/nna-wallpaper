// tests/blocks-probe.mjs — CDP probe for the "Blocks" page (β) redesign (stage 5 round 2, owner
// decision D17: card grid with previews + a block detail page with grouped settings and help).
// Opens /settings/?token=<...>&tab=blocks in headless Edge against a running NNA Wallpaper host
// and walks through the flow the owner's brief asks for. Modeled on
// H:\night-runs\nna-wallpaper-2\tools\page-diag.mjs's CDP plumbing (no external deps).
//
// Usage: node tests/blocks-probe.mjs <port>
//
// Checks:
//   1. The grid shows 10 block cards, each with a preview <img> that finished loading
//      (naturalWidth > 0) — /widgets/<id>/preview.png, live or the static fallback.
//   2. Clicking the "stats" card opens its block page (breadcrumb + hero + "no settings" message —
//      the System widget genuinely has no settings[] in its manifest, so there is nothing to
//      group; screenshot stage5-block-stats.png is taken here, matching the brief's filename).
//   3. "Back" returns to the 10-card grid.
//   4. Clicking the "weather" card opens its block page with >= 1 settings group. NOTE: the
//      brief's "change one parameter -> GET /config/full.widgets.stats changed" step names
//      "stats", but stats has zero settings (see #2) — there is nothing to change. This probe
//      exercises that check against "weather" instead (its "clocks" list field, a real
//      settings[] entry with plain <input> elements) and reports GET /config/full's
//      widgetSettings.weather, not .stats. Documented in the stage 5 report as a deviation.
//
// Exits 0 when every check passes, 1 otherwise. Does not touch the owner's port 1618 instance —
// run this against your own --headless instance (see the stage brief).

import { spawn } from "node:child_process";
import { setTimeout as sleep } from "node:timers/promises";
import { writeFileSync, mkdirSync } from "node:fs";
import path from "node:path";

const port = Number(process.argv[2] || 1634);
const cdpPort = Number(process.argv[3] || 9346);
const shotsDir = "H:\\night-runs\\nna-wallpaper-2\\shots";
const edge = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

let passCount = 0;
let failCount = 0;
function check(name, ok, detail) {
  if (ok) { passCount++; console.log("PASS " + name); }
  else { failCount++; console.log("FAIL " + name + (detail ? ": " + detail : "")); }
}

const proc = spawn(edge, [
  "--headless=new", "--disable-gpu", `--remote-debugging-port=${cdpPort}`,
  "--user-data-dir=" + process.env.TEMP + "\\nna-blocks-probe", "--window-size=1100,720", "about:blank",
], { stdio: "ignore" });

async function main() {
  let targets = [];
  for (let i = 0; i < 40 && !targets.length; i++) {
    await sleep(300);
    try { targets = await (await fetch(`http://127.0.0.1:${cdpPort}/json`)).json(); } catch { /* still starting */ }
  }
  const page = targets.find((t) => t.type === "page");
  if (!page) { console.log("FAIL could not reach headless Edge CDP"); process.exitCode = 1; return; }

  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve) => { ws.onopen = resolve; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  };
  const send = (method, params = {}) => new Promise((resolve) => {
    const i = ++id;
    pending.set(i, resolve);
    ws.send(JSON.stringify({ id: i, method, params }));
  });
  const evaluate = async (expression) => {
    const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    return r && r.result ? r.result.value : undefined;
  };
  const screenshot = async (file) => {
    const r = await send("Page.captureScreenshot", { format: "png" });
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, Buffer.from(r.data, "base64"));
    console.log("wrote " + file);
  };

  await send("Runtime.enable");
  await send("Page.enable");

  // The settings page reads its API token from ?token= (see settings/api.js) — GET /config
  // hands it back unauthenticated (see HostServices.cs GetConfig), same trick page-diag.mjs uses.
  let token = "";
  try { token = (await (await fetch(`http://127.0.0.1:${port}/config`)).json()).token || ""; }
  catch (err) { console.log("FAIL could not reach the host on port " + port + ": " + err.message); process.exitCode = 1; ws.close(); proc.kill(); return; }

  await send("Page.navigate", { url: `http://127.0.0.1:${port}/settings/?token=${encodeURIComponent(token)}&tab=blocks` });
  await sleep(2500);

  // ---- 1. grid: 10 cards, each with a loaded preview image ------------------------------------
  let cardsInfo = null;
  for (let i = 0; i < 15 && !cardsInfo; i++) {
    cardsInfo = await evaluate(`(() => {
      const cards = [...document.querySelectorAll('.blk-card')];
      if (!cards.length) return null;
      return cards.map(c => {
        const img = c.querySelector('img');
        return { id: c.dataset.widget, natW: img ? img.naturalWidth : -1, complete: img ? img.complete : false };
      });
    })()`);
    if (!cardsInfo) await sleep(400);
  }
  check("grid renders 10 block cards", Array.isArray(cardsInfo) && cardsInfo.length === 10, cardsInfo ? `found ${cardsInfo.length}` : "no cards found");
  if (Array.isArray(cardsInfo)) {
    // images may still be loading right after navigation — give the slow ones a moment, then recheck
    const pending2 = cardsInfo.filter((c) => c.natW <= 0);
    if (pending2.length) {
      await sleep(1500);
      cardsInfo = await evaluate(`[...document.querySelectorAll('.blk-card')].map(c => { const img = c.querySelector('img'); return { id: c.dataset.widget, natW: img ? img.naturalWidth : -1 }; })`);
    }
    const broken = cardsInfo.filter((c) => c.natW <= 0).map((c) => c.id);
    check("every card's preview <img> loaded (naturalWidth > 0)", broken.length === 0, broken.join(", "));
  }

  await screenshot(path.join(shotsDir, "stage5-blocks.png"));

  // ---- 2. click "stats" -> block page (no settings, so 0 groups is correct here) --------------
  await evaluate(`document.querySelector('[data-widget="stats"].blk-card')?.click()`);
  await sleep(500);
  const statsPage = await evaluate(`(() => { const d = document.querySelector('.blk-detail'); return d ? { widget: d.dataset.widget, groups: d.querySelectorAll('.group').length, hasEmptyMsg: !!d.querySelector('.empty') } : null; })()`);
  check('clicking the "stats" card opens its block page', !!statsPage && statsPage.widget === "stats", JSON.stringify(statsPage));
  check('stats block page shows the "no settings" message (its manifest has settings: [])', !!statsPage && statsPage.hasEmptyMsg, JSON.stringify(statsPage));

  await screenshot(path.join(shotsDir, "stage5-block-stats.png"));

  // ---- 3. back -> grid again -------------------------------------------------------------------
  await evaluate(`document.querySelector('.blk-back')?.click()`);
  await sleep(400);
  const backToGrid = await evaluate(`document.querySelectorAll('.blk-card').length`);
  check('"back" returns to the 10-card grid', backToGrid === 10, `found ${backToGrid} cards`);

  // ---- 4. click "weather" -> block page with >= 1 group, then change a parameter --------------
  // (see the file header: the brief's "change stats' parameter" step is redirected here because
  // stats has no settings to change — this exercises the same GET /config/full round trip against
  // widgetSettings.weather instead.)
  await evaluate(`document.querySelector('[data-widget="weather"].blk-card')?.click()`);
  await sleep(500);
  const weatherPage = await evaluate(`(() => { const d = document.querySelector('.blk-detail'); return d ? { widget: d.dataset.widget, groups: d.querySelectorAll('.group').length } : null; })()`);
  check('clicking the "weather" card opens its block page', !!weatherPage && weatherPage.widget === "weather", JSON.stringify(weatherPage));
  check("weather block page has >= 1 settings group", !!weatherPage && weatherPage.groups >= 1, JSON.stringify(weatherPage));

  const marker = "PROBE-" + Date.now();
  const beforeChange = await (await fetch(`http://127.0.0.1:${port}/config/full`)).json();
  const beforeLabel = beforeChange?.widgetSettings?.weather?.clocks?.[0]?.label;

  await evaluate(`(() => {
    const input = document.querySelector('.blk-detail[data-widget="weather"] .list-table input');
    if (!input) return false;
    input.value = ${JSON.stringify(marker)};
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  })()`);

  // makeScheduler's 300ms debounce + the PUT round trip — poll instead of one fixed sleep, headless
  // CDP round trips can occasionally push this past a single guess.
  let afterLabel = beforeLabel;
  for (let i = 0; i < 10 && afterLabel !== marker; i++) {
    await sleep(400);
    const afterChange = await (await fetch(`http://127.0.0.1:${port}/config/full`)).json();
    afterLabel = afterChange?.widgetSettings?.weather?.clocks?.[0]?.label;
  }
  check("changing a weather setting (clocks[0].label) updates GET /config/full", afterLabel === marker, `before=${beforeLabel} after=${afterLabel}`);

  await evaluate(`document.querySelector('.blk-back')?.click()`);
  await sleep(400);
  const backToGrid2 = await evaluate(`document.querySelectorAll('.blk-card').length`);
  check('"back" from the weather page also returns to the grid', backToGrid2 === 10, `found ${backToGrid2} cards`);

  ws.close();
}

try {
  await main();
} finally {
  proc.kill();
}

console.log(`\n${passCount} passed, ${failCount} failed`);
process.exitCode = failCount > 0 ? 1 : 0;
