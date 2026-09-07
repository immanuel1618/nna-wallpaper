// tests/planner-page-probe.mjs — headless-Edge CDP probe for settings/pages/planner.js.
//
// Assumes an NNA.Wallpaper host is already running headless on <port> (same convention as
// tests/audio-ws.mjs / tests/events-ws.mjs: this script does not start the exe itself — start it
// with `NNA.Wallpaper.exe --headless --port <port> --data <tmp-dir>` first, same as
// tests/planner-probe.ps1 does). It reads the API token off GET /config (no auth needed for GET,
// same as topbar/bar.js), drives a headless Microsoft Edge over the Chrome DevTools Protocol
// (raw WebSocket JSON-RPC, no npm packages — same house rule as every other tests/*.mjs) to
// /settings/?token=...&lang=ru&tab=planner, and checks:
//   1. the page loads and the profile/TASKS/voice group cards are present
//   2. with no session in the fresh --data dir, the profile card shows the "log in" state
//   3. dragging the "max tasks" slider (synthetic PointerEvent, in-page — no OS input needed)
//      changes app.planner.maxTasks, verified against GET /config/full afterwards
// and saves a screenshot to H:\night-runs\nna-wallpaper-2\shots\stage6-planner-page.png.
//
// Usage: node tests/planner-page-probe.mjs <port> [msedge.exe path]

import { spawn } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const port = process.argv[2];
const edgePath = process.argv[3] || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const shotPath = "H:\\night-runs\\nna-wallpaper-2\\shots\\stage6-planner-page.png";

if (!port) {
    console.log("FAIL no port argument");
    process.exit(1);
}

let passCount = 0;
let failCount = 0;
function pass(name, detail) { passCount++; console.log(`PASS ${name}` + (detail ? `: ${detail}` : "")); }
function fail(name, why) { failCount++; console.log(`FAIL ${name}: ${why}`); }
function skip(name, why) { console.log(`SKIP ${name}: ${why}`); }

async function getJson(url) {
    const r = await fetch(url, { cache: "no-store" });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
}

async function waitFor(fn, timeoutMs, intervalMs = 300) {
    const deadline = Date.now() + timeoutMs;
    let lastErr;
    while (Date.now() < deadline) {
        try { return await fn(); } catch (err) { lastErr = err; }
        await new Promise((r) => setTimeout(r, intervalMs));
    }
    throw lastErr || new Error("timeout");
}

// ── minimal CDP client: id-keyed request/response over one page-level WebSocket ────────────────

class Cdp {
    constructor(ws) {
        this.ws = ws;
        this.nextId = 1;
        this.pending = new Map();
        this.eventHandlers = [];
        ws.addEventListener("message", (ev) => {
            let msg;
            try { msg = JSON.parse(ev.data); } catch { return; }
            if (msg.id !== undefined && this.pending.has(msg.id)) {
                const { resolve, reject } = this.pending.get(msg.id);
                this.pending.delete(msg.id);
                if (msg.error) reject(new Error(msg.error.message || "CDP error"));
                else resolve(msg.result);
            } else if (msg.method) {
                for (const h of this.eventHandlers) h(msg.method, msg.params);
            }
        });
    }
    send(method, params = {}) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            this.ws.send(JSON.stringify({ id, method, params }));
        });
    }
    onEvent(handler) { this.eventHandlers.push(handler); }
    waitEvent(method, timeoutMs = 15000) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), timeoutMs);
            const handler = (m, p) => { if (m === method) { clearTimeout(timer); resolve(p); } };
            this.onEvent(handler);
        });
    }
}

async function evaluate(cdp, expression) {
    const result = await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.text || "evaluate threw");
    return result.result?.value;
}

// ── main ──────────────────────────────────────────────────────────────────────────────────────

let edgeProc = null;
let userDataDir = null;

async function main() {
    const base = `http://127.0.0.1:${port}`;
    let token = "";
    try {
        const cfg = await getJson(`${base}/config`);
        token = cfg.token || "";
        pass("host-reachable", `GET /config ok, token len=${token.length}`);
    } catch (err) {
        fail("host-reachable", err.message);
        return; // nothing else can run without the host
    }

    const cdpPort = 9700 + (Number(port) % 200);
    userDataDir = mkdtempSync(path.join(tmpdir(), "nna-planner-page-probe-"));

    try {
        edgeProc = spawn(edgePath, [
            "--headless=new",
            "--disable-gpu",
            `--remote-debugging-port=${cdpPort}`,
            `--user-data-dir=${userDataDir}`,
            "--window-size=1280,900",
            "about:blank",
        ], { stdio: "ignore" });
    } catch (err) {
        skip("browser-launch", `could not spawn msedge at ${edgePath}: ${err.message}`);
        return;
    }

    let target;
    try {
        target = await waitFor(async () => {
            const url = `http://127.0.0.1:${cdpPort}/json/new?` +
                encodeURIComponent(`${base}/settings/?token=${encodeURIComponent(token)}&lang=ru&tab=planner`);
            const r = await fetch(url, { method: "PUT" });
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            return r.json();
        }, 15000);
        pass("browser-launch", `cdp port ${cdpPort}, target ${target.id}`);
    } catch (err) {
        fail("browser-launch", err.message);
        return;
    }

    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
        ws.addEventListener("open", resolve, { once: true });
        ws.addEventListener("error", () => reject(new Error("ws connect failed")), { once: true });
    });
    const cdp = new Cdp(ws);
    await cdp.send("Page.enable");
    await cdp.send("Runtime.enable");

    try {
        await waitFor(() => evaluate(cdp, "document.readyState === 'complete' ? true : Promise.reject('loading')"), 15000, 400);
        pass("page-loaded", "document.readyState complete");
    } catch (err) {
        fail("page-loaded", err.message);
    }

    // Give the async render() (GET /planner/status -> /planner/profile -> build DOM) a moment.
    await new Promise((r) => setTimeout(r, 800));

    try {
        const shape = await evaluate(cdp, `JSON.stringify({
            hasProfile: !!document.querySelector('[data-group="profile"]'),
            hasShow: !!document.querySelector('[data-group="show"]'),
            hasVoice: !!document.querySelector('[data-group="voice"]'),
            // .ui-btn (not the old .btn) since settings/pages/planner.js moved onto the shared
            // .ui-btn/.ui-field control system (polish round 2, item 2 — "one control system").
            hasLoginBtn: !!document.querySelector('[data-group="profile"] .ui-btn.primary'),
        })`);
        const parsed = JSON.parse(shape);
        if (parsed.hasProfile && parsed.hasShow && parsed.hasVoice) {
            pass("groups-present", "profile/show/voice group cards found");
        } else {
            fail("groups-present", shape);
        }
        // A fresh --data dir has no planner-session.json, so /planner/status.loggedIn is false and
        // the profile card must render the "log in with Telegram" button, not a profile.
        if (parsed.hasLoginBtn) pass("logged-out-state", "login button shown (no session in fresh --data dir)");
        else fail("logged-out-state", shape);
    } catch (err) {
        fail("groups-present", err.message);
    }

    // ── drag the "max tasks" slider (synthetic in-page PointerEvent, no OS input needed) ────────
    let maxTasksBefore = null;
    try {
        const full = await getJson(`${base}/config/full`);
        maxTasksBefore = full.app?.planner?.maxTasks;
    } catch (err) {
        fail("config-full-before", err.message);
    }

    try {
        const dragResult = await evaluate(cdp, `(function () {
            var track = document.querySelector('[data-group="show"] .ui-slider .ui-slider-track');
            if (!track) return JSON.stringify({ ok: false, reason: "no-track" });
            var r = track.getBoundingClientRect();
            var x = r.left + r.width * 0.95;
            var y = r.top + r.height / 2;
            track.dispatchEvent(new PointerEvent("pointerdown", { clientX: x, clientY: y, bubbles: true, cancelable: true, pointerId: 1, isPrimary: true }));
            window.dispatchEvent(new PointerEvent("pointerup", { clientX: x, clientY: y, bubbles: true, cancelable: true, pointerId: 1, isPrimary: true }));
            var valueEl = document.querySelector('[data-group="show"] .ui-slider .ui-slider-value');
            return JSON.stringify({ ok: true, shown: valueEl ? valueEl.textContent : null });
        })()`);
        const parsed = JSON.parse(dragResult);
        if (!parsed.ok) {
            fail("slider-drag", parsed.reason);
        } else {
            pass("slider-drag", "dispatched pointerdown/up on the max-tasks slider, shown=" + parsed.shown);
            await new Promise((r) => setTimeout(r, 500)); // let the PUT /config debounce/commit land

            const full = await getJson(`${base}/config/full`);
            const maxTasksAfter = full.app?.planner?.maxTasks;
            if (maxTasksAfter !== maxTasksBefore) {
                pass("config-changed", `app.planner.maxTasks ${maxTasksBefore} -> ${maxTasksAfter}`);
            } else {
                fail("config-changed", `app.planner.maxTasks unchanged (${maxTasksBefore})`);
            }
        }
    } catch (err) {
        fail("slider-drag", err.message);
    }

    // ── screenshot ────────────────────────────────────────────────────────────────────────────
    try {
        const { data } = await cdp.send("Page.captureScreenshot", { format: "png" });
        mkdirSync(path.dirname(shotPath), { recursive: true });
        writeFileSync(shotPath, Buffer.from(data, "base64"));
        pass("screenshot", shotPath);
    } catch (err) {
        fail("screenshot", err.message);
    }

    try { ws.close(); } catch { /* ignore */ }
}

try {
    await main();
} catch (err) {
    fail("probe-crashed", err.stack || String(err));
} finally {
    if (edgeProc && !edgeProc.killed) {
        try { edgeProc.kill(); } catch { /* ignore */ }
    }
    if (userDataDir) {
        try { rmSync(userDataDir, { recursive: true, force: true }); } catch { /* ignore */ }
    }
}

console.log("");
console.log(`RESULT: ${passCount} passed, ${failCount} failed`);
process.exit(failCount > 0 ? 1 : 0);
