// tests/layout-editor-probe.mjs <port> — end-to-end probe for the layout editor (stage 4, run 2).
// Starts its own throwaway --headless host (never touches the owner's live instance on 1618),
// opens settings/?tab=layout in headless Edge over CDP, then drives: drag STATS one cell right
// (live preview via /events/stats.sent + DOM), Cancel (block returns), drag again + Apply
// (/config/full persists + a monitors.json.bak-* appears), delete a block, Ctrl+Z undoes it.
// Usage: node tests/layout-editor-probe.mjs [port]
//
// --headless reports zero live monitors (HeadlessHostApp.Monitors is always empty — see
// HeadlessHostApp.cs), so settings/pages/layout.js falls back to synthesizing "live" monitors
// from monitors.json's saved ids ("{name}|{w}x{h}") when there is no live match. This probe
// exploits exactly that fallback: it writes its own two-monitor monitors.json fixture (a 2-column
// vertical monitor so "one cell to the right" is a real, unambiguous move for STATS) instead of
// reusing the owner's real one, which only has a single-column vertical monitor.

import { spawn } from "node:child_process";
import { setTimeout as sleep } from "node:timers/promises";
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, rmSync, copyFileSync } from "node:fs";
import path from "node:path";

const REPO = "H:\\projects\\.wt\\lay";
const PORT = Number(process.argv[2] || 1633);
const DATA = path.join(process.env.TEMP || "C:\\Temp", `nna-layout-probe-${PORT}`);
const EXE = path.join(REPO, "src\\NNA.Wallpaper\\bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\NNA.Wallpaper.exe");
const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const SHOTS_DIR = "H:\\night-runs\\nna-wallpaper-2\\shots";
const OWNER_CONFIG = path.join(process.env.LOCALAPPDATA || "", "NNA Wallpaper", "config");
const CDP_PORT = 9346;

let passed = 0, failed = 0;
function ok(name, cond, detail = "") {
  if (cond) { passed++; console.log(`PASS ${name}${detail ? " — " + detail : ""}`); }
  else { failed++; console.log(`FAIL ${name}${detail ? " — " + detail : ""}`); }
  return cond;
}

// ── 1. fixture data dir: copy the owner's real config (app.json/launch.json/events.json/widget
// settings — no planner-session.json, which lives one level up in DataDir, not under config/),
// then overwrite monitors.json with a deterministic two-monitor fixture for this probe. ─────────
function setupData() {
  if (existsSync(DATA)) rmSync(DATA, { recursive: true, force: true });
  mkdirSync(path.join(DATA, "config", "widgets"), { recursive: true });
  for (const f of ["app.json", "launch.json", "events.json"]) {
    const src = path.join(OWNER_CONFIG, f);
    if (existsSync(src)) copyFileSync(src, path.join(DATA, "config", f));
  }
  const widgetsDir = path.join(OWNER_CONFIG, "widgets");
  if (existsSync(widgetsDir)) {
    for (const f of readdirSync(widgetsDir)) {
      if (f.endsWith(".json")) copyFileSync(path.join(widgetsDir, f), path.join(DATA, "config", "widgets", f));
    }
  }

  const monitors = {
    version: 1,
    monitors: [
      {
        id: "TESTDISPLAY0|3440x1440",
        name: "Probe Main",
        enabled: true,
        grid: { cols: 3, rows: 16, colWeights: [2.2, 1, 2.2], gap: 24, pad: 48 },
        blocks: [
          { widget: "eq", col: 1, colSpan: 1, row: 1, rowSpan: 4 },
          { widget: "photos", col: 1, colSpan: 1, row: 5, rowSpan: 12 },
          { widget: "focus", col: 2, colSpan: 1, row: 1, rowSpan: 8 },
          { widget: "weather", col: 2, colSpan: 1, row: 9, rowSpan: 8 },
          { widget: "events", col: 3, colSpan: 1, row: 1, rowSpan: 4 },
          { widget: "graph", col: 3, colSpan: 1, row: 9, rowSpan: 8 },
        ],
      },
      {
        // Deliberately 2 columns (the owner's real vertical monitor is single-column, which makes
        // "drag right" meaningless) so the probe can move STATS from col 1 to col 2 unambiguously.
        id: "TESTDISPLAY1|1440x2560",
        name: "Probe Vertical",
        enabled: true,
        grid: { cols: 2, rows: 4, gap: 24, pad: 48 },
        blocks: [
          { widget: "stats", col: 1, colSpan: 1, row: 1, rowSpan: 1 },
          { widget: "planner", col: 1, colSpan: 1, row: 2, rowSpan: 1 },
          { widget: "player", col: 1, colSpan: 1, row: 3, rowSpan: 1 },
          { widget: "launch", col: 1, colSpan: 1, row: 4, rowSpan: 1 },
        ],
      },
    ],
  };
  writeFileSync(path.join(DATA, "config", "monitors.json"), JSON.stringify(monitors, null, 2), "utf8");
}

function readToken() {
  const app = JSON.parse(readFileSync(path.join(DATA, "config", "app.json"), "utf8"));
  return app.apiToken;
}

async function waitForHealth(baseUrl) {
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(`${baseUrl}/health`);
      if (r.ok) { const j = await r.json(); if (j.ok) return true; }
    } catch { /* not up yet */ }
    await sleep(300);
  }
  return false;
}

// ── 2. thin CDP client (same shape as H:\night-runs\nna-wallpaper-2\tools\page-diag.mjs) ────────
async function connectCdp() {
  let targets = [];
  for (let i = 0; i < 30 && !targets.length; i++) {
    await sleep(300);
    try { targets = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json`)).json(); } catch { /* retry */ }
  }
  const page = targets.find((t) => t.type === "page");
  if (!page) throw new Error("no CDP page target");
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve) => { ws.onopen = resolve; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  };
  const send = (method, params = {}) => new Promise((resolve) => {
    const i = ++id;
    pending.set(i, resolve);
    ws.send(JSON.stringify({ id: i, method, params }));
  });
  await send("Runtime.enable");
  await send("Page.enable");
  // --headless=new does not reliably honor --window-size for the actual content viewport (it can
  // fall back to a smaller default), so force it explicitly — the brief calls for testing at a
  // real 1100x720.
  await send("Emulation.setDeviceMetricsOverride", { width: 1100, height: 720, deviceScaleFactor: 1, mobile: false });
  return { ws, send };
}

async function evalJs(cdp, expression) {
  const r = await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (r.result?.exceptionDetails) throw new Error("evaluate failed: " + JSON.stringify(r.result.exceptionDetails));
  return r.result?.result?.value;
}

async function screenshot(cdp, file) {
  const r = await cdp.send("Page.captureScreenshot", { format: "png" });
  writeFileSync(file, Buffer.from(r.result.data, "base64"));
}

async function mouse(cdp, type, x, y, extra = {}) {
  await cdp.send("Input.dispatchMouseEvent", { type, x, y, button: "left", buttons: type === "mouseReleased" ? 0 : 1, clickCount: 1, ...extra });
}

async function dragBlock(cdp, fromExpr) {
  const coords = await evalJs(cdp, fromExpr);
  if (!coords) return null;
  await mouse(cdp, "mouseMoved", coords.startX, coords.startY, { buttons: 0 });
  await mouse(cdp, "mousePressed", coords.startX, coords.startY);
  const steps = 5;
  for (let i = 1; i <= steps; i++) {
    const x = coords.startX + ((coords.targetX - coords.startX) * i) / steps;
    const y = coords.startY + ((coords.targetY - coords.startY) * i) / steps;
    await mouse(cdp, "mouseMoved", x, y);
    await sleep(30);
  }
  await mouse(cdp, "mouseReleased", coords.targetX, coords.targetY);
  return coords;
}

async function clickButtonByText(cdp, text) {
  const expr = `(() => {
    const btns = [...document.querySelectorAll(".lay-actions button")];
    const b = btns.find((x) => x.textContent.trim() === ${JSON.stringify(text)});
    if (!b) return null;
    b.scrollIntoView({ block: "center", inline: "center" });
    const r = b.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2, disabled: b.disabled };
  })()`;
  const pos = await evalJs(cdp, expr);
  if (!pos) return false;
  if (pos.disabled) return false;
  await mouse(cdp, "mouseMoved", pos.x, pos.y);
  await mouse(cdp, "mousePressed", pos.x, pos.y);
  await mouse(cdp, "mouseReleased", pos.x, pos.y);
  return true;
}

async function main() {
  mkdirSync(SHOTS_DIR, { recursive: true });
  setupData();

  const baseUrl = `http://127.0.0.1:${PORT}`;
  console.log(`starting ${EXE} --headless --port ${PORT} --data ${DATA}`);
  const proc = spawn(EXE, ["--headless", "--port", String(PORT), "--data", DATA], { stdio: "ignore" });

  const edgeProfile = path.join(process.env.TEMP || "C:\\Temp", `nna-layout-probe-edge-${PORT}`);
  if (existsSync(edgeProfile)) rmSync(edgeProfile, { recursive: true, force: true }); // avoid stale multi-tab session restore from a previous run
  const edgeProc = spawn(EDGE, [
    "--headless=new", "--disable-gpu", `--remote-debugging-port=${CDP_PORT}`,
    "--user-data-dir=" + edgeProfile,
    "--window-size=1100,720", "about:blank",
  ], { stdio: "ignore" });

  try {
    const healthy = await waitForHealth(baseUrl);
    if (!ok("startup", healthy, `pid=${proc.pid}`)) return;

    const token = readToken();
    const cdp = await connectCdp();
    await cdp.send("Page.navigate", { url: `${baseUrl}/settings/?token=${token}&tab=layout&lang=ru` });
    await sleep(1500);
    let canvasReady = false;
    for (let i = 0; i < 20 && !canvasReady; i++) {
      canvasReady = !!(await evalJs(cdp, `!!document.querySelector('.lay-grid')`));
      if (!canvasReady) await sleep(300);
    }
    ok("canvas-rendered", canvasReady);
    if (!canvasReady) {
      const bodyText = await evalJs(cdp, "document.body.innerText.slice(0,300)");
      console.log("page body:", bodyText);
      return;
    }
    // Wait for the brand webfonts (Roboto Flex / JetBrains Mono) to finish loading — a font swap
    // after the fact changes glyph metrics and reflows the page, which shifts every coordinate we
    // are about to compute for the drag/click gestures below.
    await evalJs(cdp, "document.fonts.ready.then(() => true)");
    await sleep(300);

    // select the vertical (2-col) probe monitor — Main comes first alphabetically in liveMonitors
    // order, so click the monitor card whose tag reads 1440x2560.
    const selectExpr = `(() => {
      const cards = [...document.querySelectorAll(".lay-mon-card")];
      const card = cards.find((c) => c.textContent.includes("1440x2560"));
      if (!card) return false;
      card.click();
      return true;
    })()`;
    ok("select-vertical-monitor", await evalJs(cdp, selectExpr));
    await sleep(300);

    await screenshot(cdp, path.join(SHOTS_DIR, "stage4-layout-01-before.png"));

    const statsRectBefore = await evalJs(cdp, `(() => { const b = document.querySelector('.lay-block[data-widget="stats"]'); return b ? b.getBoundingClientRect().left : null; })()`);
    ok("stats-block-present", statsRectBefore !== null, `left=${statsRectBefore}`);

    const statsBefore = await (await fetch(`${baseUrl}/events/stats`)).json();

    const dragExpr = `(() => {
      const grid = document.querySelector('.lay-grid');
      const block = document.querySelector('.lay-block[data-widget="stats"]');
      if (!grid || !block) return null;
      block.scrollIntoView({ block: "center", inline: "center" });
      const g = grid.getBoundingClientRect();
      const b = block.getBoundingClientRect();
      return { startX: b.left + b.width / 2, startY: b.top + 10, targetX: g.left + g.width * 0.75, targetY: b.top + 10 };
    })()`;
    const dragCoords = await dragBlock(cdp, dragExpr);
    ok("drag-dispatched", !!dragCoords, JSON.stringify(dragCoords));
    await sleep(400); // > 80ms preview throttle

    const statsAfterDrag = await (await fetch(`${baseUrl}/events/stats`)).json();
    ok("preview-sent-grew-on-drag", statsAfterDrag.sent > statsBefore.sent, `${statsBefore.sent} -> ${statsAfterDrag.sent}`);

    const statsRectAfterDrag = await evalJs(cdp, `(() => { const b = document.querySelector('.lay-block[data-widget="stats"]'); return b ? b.getBoundingClientRect().left : null; })()`);
    ok("dom-moved-on-drag", statsRectAfterDrag !== null && statsRectAfterDrag > statsRectBefore, `${statsRectBefore} -> ${statsRectAfterDrag}`);

    await screenshot(cdp, path.join(SHOTS_DIR, "stage4-layout-02-after-drag.png"));

    // Cancel — block should return to its original column.
    ok("cancel-clicked", await clickButtonByText(cdp, "Отменить"));
    await sleep(400);
    const statsRectAfterCancel = await evalJs(cdp, `(() => { const b = document.querySelector('.lay-block[data-widget="stats"]'); return b ? b.getBoundingClientRect().left : null; })()`);
    ok("dom-reverted-on-cancel", Math.abs(statsRectAfterCancel - statsRectBefore) < 2, `${statsRectBefore} -> ${statsRectAfterCancel}`);

    await screenshot(cdp, path.join(SHOTS_DIR, "stage4-layout-03-after-cancel.png"));

    // Drag again, then Apply — /config/full should persist the move and a monitors.json.bak-*
    // should appear.
    const configBefore = await (await fetch(`${baseUrl}/config/full`)).json();
    const backupsBefore = readdirSync(path.join(DATA, "config")).filter((f) => f.startsWith("monitors.json.bak-")).length;

    await dragBlock(cdp, dragExpr);
    await sleep(400);
    ok("apply-clicked", await clickButtonByText(cdp, "Применить"));
    await sleep(500);

    const configAfter = await (await fetch(`${baseUrl}/config/full`)).json();
    const vertAfter = configAfter.monitors?.monitors?.find((m) => m.id === "TESTDISPLAY1|1440x2560");
    const statsBlockAfter = vertAfter?.blocks?.find((b) => b.widget === "stats");
    ok("apply-persisted-move", statsBlockAfter && statsBlockAfter.col === 2, `stats.col=${statsBlockAfter?.col}`);
    ok("config-changed-from-before", JSON.stringify(configAfter.monitors) !== JSON.stringify(configBefore.monitors));

    const backupsAfter = readdirSync(path.join(DATA, "config")).filter((f) => f.startsWith("monitors.json.bak-")).length;
    ok("backup-file-appeared", backupsAfter > backupsBefore, `${backupsBefore} -> ${backupsAfter}`);

    await screenshot(cdp, path.join(SHOTS_DIR, "stage4-layout-04-after-apply.png"));

    // Delete the (now col=2) STATS block, then Ctrl+Z to bring it back.
    const delExpr = `(() => {
      const b = document.querySelector('.lay-block[data-widget="stats"]');
      if (!b) return null;
      b.scrollIntoView({ block: "center", inline: "center" });
      const del = b.querySelector('.lay-block-del');
      const r = del.getBoundingClientRect();
      return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    })()`;
    const delPos = await evalJs(cdp, delExpr);
    if (ok("delete-button-found", !!delPos)) {
      await mouse(cdp, "mouseMoved", delPos.x, delPos.y);
      await mouse(cdp, "mousePressed", delPos.x, delPos.y);
      await mouse(cdp, "mouseReleased", delPos.x, delPos.y);
      await sleep(300);
      const goneAfterDelete = await evalJs(cdp, `!document.querySelector('.lay-block[data-widget="stats"]')`);
      ok("block-removed-on-delete", goneAfterDelete);

      // focus the stage, then Ctrl+Z
      const stageExpr = `(() => { const s = document.querySelector('.lay-stage'); if (!s) return null; s.scrollIntoView({ block: "center", inline: "center" }); s.focus(); const r = s.getBoundingClientRect(); return { x: r.left + r.width/2, y: r.top + r.height/2 }; })()`;
      const stagePos = await evalJs(cdp, stageExpr);
      await mouse(cdp, "mouseMoved", stagePos.x, stagePos.y);
      await mouse(cdp, "mousePressed", stagePos.x, stagePos.y);
      await mouse(cdp, "mouseReleased", stagePos.x, stagePos.y);
      await cdp.send("Input.dispatchKeyEvent", { type: "keyDown", modifiers: 2, key: "z", code: "KeyZ", windowsVirtualKeyCode: 90, nativeVirtualKeyCode: 90 });
      await cdp.send("Input.dispatchKeyEvent", { type: "keyUp", modifiers: 2, key: "z", code: "KeyZ", windowsVirtualKeyCode: 90, nativeVirtualKeyCode: 90 });
      await sleep(300);
      const backAfterUndo = await evalJs(cdp, `!!document.querySelector('.lay-block[data-widget="stats"]')`);
      ok("block-restored-on-undo", backAfterUndo);
    }

    await screenshot(cdp, path.join(SHOTS_DIR, "stage4-layout-05-after-undo.png"));

    cdp.ws.close();
  } finally {
    edgeProc.kill();
    try {
      await fetch(`${baseUrl}/app/exit`, { method: "POST", headers: { "X-Token": readToken() } });
    } catch { /* fall through to hard kill */ }
    await sleep(500);
    try { process.kill(proc.pid); } catch { /* already gone */ }
  }

  console.log(`\nRESULT: ${passed} passed, ${failed} failed`);
  process.exit(failed > 0 ? 1 : 0);
}

main().catch((err) => { console.error(err); process.exit(2); });
