// Self-heal gate for the wallpaper page's LAUNCH/TASKS boot problem fixed in hotfix 2C/run 2:
// LAUNCH mounting empty (title + EDIT, no groups/icons) and TASKS stuck on the login screen even
// though /planner/status already answered loggedIn:true, both cleared only by a manual reload.
//
// Drives http://127.0.0.1:<port>/wallpaper/?monitor=vertical (the DefaultVertical layout is
// exactly stats/planner/player/launch — the same four widgets as the real incident) through
// headless Edge via CDP, four scenarios:
//   (a) normal boot — everything mounts and every widget reports ready.
//   (b) Network.emulateNetworkConditions: 2000ms latency for the first 5s — boot must still
//       complete (slow, not stuck).
//   (c) Fetch domain: the *first* request to /launch/list and to /planner/status each fails
//       (Fetch.failRequest), later ones pass through — the page must recover on its own within
//       12s (the N.get retry / ctx.fail + layout.js remount watchdog gate).
//   (d) Fetch domain: /planner/status answers {loggedIn:false} once, then {loggedIn:true,...};
//       /planner/today is faked throughout (no real planner session is copied into the test data
//       dir on purpose) — TASKS must show real task content within 35s (the widget's 30s
//       logged-out recheck).
//
// Each scenario is a fresh headless Edge process (own profile, own remote-debugging port) so
// nothing carries over between runs. Boot/ready evidence is read back from the *host's* own log
// via GET /test/log (only available with --headless/--test-engine, i.e. TestEndpoints=true) as
// well as from the page's DOM, so this also proves PageLogService + layout.js's own logging work
// end-to-end, not just that pixels showed up.
//
// Usage: node tests/page-selfheal.mjs <port>

import { spawn } from "node:child_process";
import { setTimeout as sleep } from "node:timers/promises";

const port = process.argv[2];
if (!port) {
  console.log("FAIL no port argument");
  process.exit(1);
}

const base = `http://127.0.0.1:${port}`;
const edgeExe = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

let passCount = 0;
let failCount = 0;
function pass(name, detail) { passCount++; console.log(`PASS ${name}` + (detail ? `: ${detail}` : "")); }
function fail(name, why) { failCount++; console.log(`FAIL ${name}: ${why}`); }

async function getJson(path) {
  const r = await fetch(base + path, { cache: "no-store" });
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return r.json();
}

async function testLogTail() {
  try { return (await getJson("/test/log")).lines || []; } catch { return []; }
}

// ---- one headless Edge + CDP session ----------------------------------------------------------

let nextCdpPort = 9401;

async function withPage(scenarioName, cdpSetup, body) {
  const cdpPort = nextCdpPort++;
  const proc = spawn(edgeExe, [
    "--headless=new", "--disable-gpu", `--remote-debugging-port=${cdpPort}`,
    "--user-data-dir=" + process.env.TEMP + "\\nna-selfheal-" + cdpPort,
    "--window-size=1440,2560", "about:blank",
  ], { stdio: "ignore" });

  try {
    let targets = [];
    for (let i = 0; i < 40 && !targets.length; i++) {
      await sleep(300);
      try { targets = await (await fetch(`http://127.0.0.1:${cdpPort}/json`)).json(); } catch { /* not up yet */ }
    }
    const target = targets.find((t) => t.type === "page");
    if (!target) throw new Error("no page target from headless Edge");
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      ws.onopen = resolve;
      ws.onerror = () => reject(new Error("cdp ws error"));
    });

    let id = 0;
    const pending = new Map();
    const eventListeners = [];
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && pending.has(m.id)) {
        const { resolve, reject } = pending.get(m.id);
        pending.delete(m.id);
        if (m.error) reject(new Error(m.error.message || "cdp error"));
        else resolve(m.result);
      }
      if (m.method) {
        for (const l of eventListeners) { if (l.method === m.method) l.fn(m.params); }
      }
    };
    const send = (method, params = {}) => new Promise((resolve, reject) => {
      const i = ++id;
      pending.set(i, { resolve, reject });
      ws.send(JSON.stringify({ id: i, method, params }));
    });
    const on = (method, fn) => { eventListeners.push({ method, fn }); };

    const evalPage = async (expression) => {
      const r = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: false });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.text || "eval error");
      return r.result && r.result.value;
    };

    await send("Runtime.enable");
    await send("Page.enable");

    if (cdpSetup) await cdpSetup({ send, on });

    await send("Page.navigate", { url: `${base}/wallpaper/?monitor=vertical` });

    const result = await body({ send, on, evalPage });
    try { ws.close(); } catch { /* ignore */ }
    return result;
  } catch (err) {
    fail(scenarioName, "harness error: " + (err && err.message ? err.message : err));
    return null;
  } finally {
    proc.kill();
  }
}

// ---- DOM polling helpers ------------------------------------------------------------------------

async function pollUntil(evalPage, expression, timeoutMs, stepMs = 400) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await evalPage(expression);
    if (last) return { ok: true, value: last, elapsedMs: timeoutMs - (deadline - Date.now()) };
    await sleep(stepMs);
  }
  return { ok: false, value: last, elapsedMs: timeoutMs };
}

const LAUNCH_HAS_ICONS =
  "!!document.querySelector('.nna-cell[data-widget=\"launch\"] .la-item, .nna-cell[data-widget=\"launch\"] .la-set')";
const TASKS_NOT_LOGIN =
  "(function(){var c=document.querySelector('.nna-cell[data-widget=\"planner\"]');if(!c)return false;return !c.querySelector('.pl-login') && c.querySelectorAll('.pl-grid').length>0;})()";
const TASKS_HAS_TEST_TASK =
  "(function(){var c=document.querySelector('.nna-cell[data-widget=\"planner\"]');return !!(c && c.innerText && c.innerText.indexOf('NNA SELFHEAL TEST TASK')!==-1);})()";

// ---- scenario (a): normal boot -------------------------------------------------------------------

async function scenarioNormal() {
  const name = "(a) normal boot: LAUNCH icons + TASKS mounted, ready logged for all 4 widgets";
  const before = (await testLogTail()).length;
  await withPage(name, null, async ({ evalPage }) => {
    const launchOk = await pollUntil(evalPage, LAUNCH_HAS_ICONS, 10000);
    if (!launchOk.ok) { fail(name, "LAUNCH never showed groups/icons within 10s"); return; }
    const tasksEl = await evalPage("!!document.querySelector('.nna-cell[data-widget=\"planner\"] .nna-planner')");
    if (!tasksEl) { fail(name, "TASKS cell never mounted its .nna-planner body"); return; }

    // ready evidence from the host's own log (proves PageLogService + layout.js logging, not just DOM)
    let tail = [];
    for (let i = 0; i < 15; i++) {
      tail = await testLogTail();
      const newLines = tail.slice(before);
      const hasBoot = newLines.some((l) => l.includes("info: boot: monitor=vertical"));
      const ready = ["stats", "planner", "player", "launch"].filter((w) => newLines.some((l) => l.includes(`ready: widget=${w}`)));
      if (hasBoot && ready.length === 4) {
        pass(name, `boot+ready logged for [${ready.join(",")}]`);
        return;
      }
      await sleep(500);
    }
    const newLines = tail.slice(before);
    fail(name, "missing boot/ready log lines; tail=" + JSON.stringify(newLines.slice(-10)));
  });
}

// ---- scenario (b): 2000ms latency for the first 5s ------------------------------------------------

async function scenarioLatency() {
  const name = "(b) 2000ms network latency for 5s: boot completes, just slow";
  await withPage(name, async ({ send }) => {
    await send("Network.enable");
    await send("Network.emulateNetworkConditions", {
      offline: false, latency: 2000, downloadThroughput: -1, uploadThroughput: -1,
    });
    setTimeout(() => {
      send("Network.emulateNetworkConditions", { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 }).catch(() => {});
    }, 5000);
  }, async ({ evalPage }) => {
    const launchOk = await pollUntil(evalPage, LAUNCH_HAS_ICONS, 20000);
    if (!launchOk.ok) { fail(name, "LAUNCH never showed groups/icons within 20s"); return; }
    const tasksOk = await evalPage("!!document.querySelector('.nna-cell[data-widget=\"planner\"] .nna-planner')");
    if (!tasksOk) { fail(name, "TASKS cell never mounted its .nna-planner body"); return; }
    pass(name, `LAUNCH ready at ~${launchOk.elapsedMs}ms`);
  });
}

// ---- scenario (c): first /launch/list and /planner/status fail, page must self-heal <=12s --------

async function scenarioFirstRequestFails() {
  const name = "(c) first /launch/list + /planner/status fail: self-heal <=12s";
  const seen = new Map(); // path -> count
  await withPage(name, async ({ send, on }) => {
    await send("Fetch.enable", {
      patterns: [
        { urlPattern: "*/launch/list", requestStage: "Request" },
        { urlPattern: "*/planner/status", requestStage: "Request" },
      ],
    });
    on("Fetch.requestPaused", async (p) => {
      const url = new URL(p.request.url);
      const path = url.pathname;
      const n = (seen.get(path) || 0) + 1;
      seen.set(path, n);
      if (n === 1) {
        await send("Fetch.failRequest", { requestId: p.requestId, errorReason: "Failed" }).catch(() => {});
      } else {
        await send("Fetch.continueRequest", { requestId: p.requestId }).catch(() => {});
      }
    });
  }, async ({ evalPage }) => {
    const t0 = Date.now();
    const launchOk = await pollUntil(evalPage, LAUNCH_HAS_ICONS, 12000);
    const elapsed = Date.now() - t0;
    if (!launchOk.ok) { fail(name, `LAUNCH did not recover within 12s (waited ${elapsed}ms)`); return; }
    pass(name, `LAUNCH recovered in ${elapsed}ms after its first /launch/list failed`);
  });
}

// ---- scenario (d): /planner/status false-then-true (faked), /planner/today faked throughout ------

function fulfillJson(obj) {
  return {
    responseCode: 200,
    responseHeaders: [{ name: "Content-Type", value: "application/json; charset=utf-8" }],
    body: Buffer.from(JSON.stringify(obj)).toString("base64"),
  };
}

async function scenarioLoggedOutThenIn() {
  const name = "(d) /planner/status false->true (faked): TASKS shows tasks <=35s";
  const statusCount = { n: 0 };
  await withPage(name, async ({ send, on }) => {
    await send("Fetch.enable", {
      patterns: [
        { urlPattern: "*/planner/status", requestStage: "Request" },
        { urlPattern: "*/planner/today", requestStage: "Request" },
      ],
    });
    on("Fetch.requestPaused", async (p) => {
      const path = new URL(p.request.url).pathname;
      if (path === "/planner/status") {
        statusCount.n++;
        const body = statusCount.n === 1
          ? { loggedIn: false, profile: null }
          : { loggedIn: true, profile: { display_name: "SELFHEAL TEST", timezone: "Europe/Moscow", tier: "test" }, expires_at: Math.floor(Date.now() / 1000) + 3600 };
        await send("Fetch.fulfillRequest", { requestId: p.requestId, ...fulfillJson(body) }).catch(() => {});
        return;
      }
      if (path === "/planner/today") {
        const body = {
          tasks: [{ id: "t1", title: "NNA SELFHEAL TEST TASK", due_at: null, overdue: false, state: "open" }],
          meetings: [], habits: [],
          money: { spent_minor: 0, income_minor: 0, by_category: {} },
          briefing: "selfheal test", generated_at: new Date().toISOString(),
        };
        await send("Fetch.fulfillRequest", { requestId: p.requestId, ...fulfillJson(body) }).catch(() => {});
        return;
      }
      await send("Fetch.continueRequest", { requestId: p.requestId }).catch(() => {});
    });
  }, async ({ evalPage }) => {
    const t0 = Date.now();
    const loginSeen = await pollUntil(evalPage, "!!document.querySelector('.nna-cell[data-widget=\"planner\"] .pl-login')", 8000);
    if (!loginSeen.ok) fail(name + " (setup)", "never saw the fake logged-out screen — cannot prove the recheck path");
    const tasksOk = await pollUntil(evalPage, TASKS_HAS_TEST_TASK, 35000);
    const elapsed = Date.now() - t0;
    if (!tasksOk.ok) { fail(name, `TASKS never showed the fake task within 35s (waited ${elapsed}ms)`); return; }
    pass(name, `TASKS showed the fake task ${elapsed}ms after boot (logged-out recheck path)`);
  });
}

// ---- main --------------------------------------------------------------------------------------

async function main() {
  await scenarioNormal();
  await scenarioLatency();
  await scenarioFirstRequestFails();
  await scenarioLoggedOutThenIn();

  console.log("---");
  console.log(`PASS=${passCount} FAIL=${failCount}`);
  process.exit(failCount > 0 ? 1 : 0);
}

main().catch((err) => {
  console.log("FAIL unhandled: " + (err && err.message ? err.message : err));
  process.exit(1);
});
