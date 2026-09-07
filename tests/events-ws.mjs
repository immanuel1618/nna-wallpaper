// Smoke test for the /events live-update channel: connects with Node's built-in WebSocket (no npm
// packages), waits for the {"type":"hello",...} handshake, PUTs a config change (theme.dim) and
// waits for a matching {"type":"config-changed","what":"app"} broadcast, checks that a WebSocket
// upgrade from a foreign Origin is refused with 403 (a plain HTTP upgrade attempt, built by hand
// with node:http since the WHATWG WebSocket constructor cannot set custom headers), and checks
// GET /events/stats reports at least one sent message.
//
// Usage: node tests/events-ws.mjs <port>

import http from "node:http";

const port = process.argv[2];
if (!port) {
    console.log("FAIL no port argument");
    process.exit(1);
}

const base = `http://127.0.0.1:${port}`;
let passCount = 0;
let failCount = 0;

function pass(name, detail) {
    passCount++;
    console.log(`PASS ${name}` + (detail ? `: ${detail}` : ""));
}
function fail(name, why) {
    failCount++;
    console.log(`FAIL ${name}: ${why}`);
}

async function getJson(path) {
    const r = await fetch(base + path, { cache: "no-store" });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
}

async function putConfig(token, theme) {
    const r = await fetch(base + "/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json", "X-Token": token },
        body: JSON.stringify({ app: { theme } }),
    });
    if (!r.ok) throw new Error(`PUT /config HTTP ${r.status}`);
    return r.json();
}

// Raw HTTP upgrade attempt with a foreign Origin header — the WHATWG WebSocket constructor cannot
// set custom headers, so this drives the handshake by hand and inspects whether the server upgraded
// the connection (should NOT: LocalApi.Handle refuses a foreign Origin before it ever looks at the
// WebSocket route) or answered with a plain HTTP error response (should: 403).
function attemptForbiddenOriginUpgrade() {
    return new Promise((resolve) => {
        const req = http.request({
            host: "127.0.0.1",
            port: Number(port),
            path: "/events",
            method: "GET",
            headers: {
                Connection: "Upgrade",
                Upgrade: "websocket",
                "Sec-WebSocket-Version": "13",
                "Sec-WebSocket-Key": Buffer.from("nna-events-probe-key16").toString("base64").slice(0, 24),
                Origin: "https://evil.example",
            },
        });
        req.on("upgrade", (res, socket) => {
            resolve({ upgraded: true, status: res.statusCode });
            try { socket.destroy(); } catch { /* ignore */ }
        });
        req.on("response", (res) => {
            let body = "";
            res.on("data", (c) => { body += c; });
            res.on("end", () => resolve({ upgraded: false, status: res.statusCode, body }));
        });
        req.on("error", (err) => resolve({ upgraded: false, error: err.message }));
        req.end();
    });
}

async function main() {
    // ---- connect, wait for hello --------------------------------------------------------------
    const ws = new WebSocket(`ws://127.0.0.1:${port}/events`);
    const helloPromise = new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("timeout waiting for hello")), 3000);
        ws.addEventListener("message", function onMsg(ev) {
            if (typeof ev.data !== "string") return;
            let msg;
            try { msg = JSON.parse(ev.data); } catch { return; }
            if (msg && msg.type === "hello") {
                clearTimeout(timer);
                ws.removeEventListener("message", onMsg);
                resolve(msg);
            }
        });
        ws.addEventListener("error", (ev) => { clearTimeout(timer); reject(new Error("ws error: " + (ev.message ?? ev))); });
        ws.addEventListener("close", (ev) => { clearTimeout(timer); reject(new Error(`ws closed before hello (code=${ev.code})`)); });
    });

    try {
        const hello = await helloPromise;
        pass("connect /events and receive hello", `version=${hello.version}`);
    } catch (err) {
        fail("connect /events and receive hello", err.message);
        try { ws.close(); } catch { /* ignore */ }
        process.exit(finish());
    }

    // ---- PUT /config (theme.dim) and wait for config-changed what=app -------------------------
    const configChangedPromise = new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("timeout (2000ms) waiting for config-changed what=app")), 2000);
        ws.addEventListener("message", function onMsg(ev) {
            if (typeof ev.data !== "string") return;
            let msg;
            try { msg = JSON.parse(ev.data); } catch { return; }
            if (msg && msg.type === "config-changed" && msg.what === "app") {
                clearTimeout(timer);
                ws.removeEventListener("message", onMsg);
                resolve(msg);
            }
        });
    });

    try {
        const cfg = await getJson("/config?monitor=main");
        const token = cfg.token;
        const theme = cfg.theme || {};
        theme.dim = theme.dim === 0.31 ? 0.32 : 0.31; // flip so it is always an actual change
        await putConfig(token, theme);
        await configChangedPromise;
        pass("PUT /config triggers config-changed what=app within 2s");
    } catch (err) {
        fail("PUT /config triggers config-changed what=app within 2s", err.message);
    }

    try { ws.close(); } catch { /* ignore */ }

    // ---- foreign Origin must be refused before upgrade -----------------------------------------
    try {
        const res = await attemptForbiddenOriginUpgrade();
        if (res.upgraded) {
            fail("WS /events with foreign Origin is refused", `upgraded anyway (status=${res.status})`);
        } else if (res.status === 403) {
            pass("WS /events with foreign Origin is refused", "HTTP 403, no upgrade");
        } else if (res.error) {
            fail("WS /events with foreign Origin is refused", `request error: ${res.error}`);
        } else {
            fail("WS /events with foreign Origin is refused", `expected 403, got ${res.status}`);
        }
    } catch (err) {
        fail("WS /events with foreign Origin is refused", err.message);
    }

    // ---- /events/stats ---------------------------------------------------------------------------
    try {
        const stats = await getJson("/events/stats");
        if (typeof stats.sent === "number" && stats.sent >= 1) {
            pass("GET /events/stats.sent >= 1", `clients=${stats.clients} sent=${stats.sent}`);
        } else {
            fail("GET /events/stats.sent >= 1", `got ${JSON.stringify(stats)}`);
        }
    } catch (err) {
        fail("GET /events/stats.sent >= 1", err.message);
    }

    process.exit(finish());
}

function finish() {
    console.log("---");
    console.log(`PASS=${passCount} FAIL=${failCount}`);
    return failCount > 0 ? 1 : 0;
}

main().catch((err) => {
    console.log("FAIL unhandled: " + (err && err.message ? err.message : err));
    process.exit(1);
});
