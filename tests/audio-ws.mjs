// Smoke test for /audio: connects with Node's built-in WebSocket (no npm packages), waits up to
// 3 seconds for a binary frame of 128 little-endian float32 values (512 bytes), and reports
// PASS/FAIL. Text frames (the "hello" handshake) are skipped while waiting for the binary frame.
//
// Usage: node tests/audio-ws.mjs <port>

const port = process.argv[2];
if (!port) {
    console.log("FAIL no port argument");
    process.exit(1);
}

const EXPECTED_BYTES = 512;
const EXPECTED_FLOATS = EXPECTED_BYTES / 4;
const TIMEOUT_MS = 3000;

let finished = false;

function finish(ok, message, ws) {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    console.log((ok ? "PASS " : "FAIL ") + message);
    try { ws?.close(); } catch { /* ignore */ }
    process.exit(ok ? 0 : 1);
}

const ws = new WebSocket(`ws://127.0.0.1:${port}/audio`);
ws.binaryType = "arraybuffer";

const timer = setTimeout(() => finish(false, "timeout waiting for a binary audio frame", ws), TIMEOUT_MS);

ws.addEventListener("message", (event) => {
    if (finished) return;
    const data = event.data;

    if (typeof data === "string") {
        // Text frame (the {"type":"hello",...} handshake) - not what we're waiting for.
        return;
    }

    if (!(data instanceof ArrayBuffer)) {
        finish(false, `unexpected binary message type: ${Object.prototype.toString.call(data)}`, ws);
        return;
    }

    if (data.byteLength !== EXPECTED_BYTES) {
        finish(false, `frame length ${data.byteLength}, expected ${EXPECTED_BYTES}`, ws);
        return;
    }

    const floats = new Float32Array(data);
    let max = -Infinity;
    for (let i = 0; i < floats.length; i++) {
        if (floats[i] > max) max = floats[i];
    }
    finish(true, `audio frame ${EXPECTED_FLOATS} floats, max=${max}`, ws);
});

ws.addEventListener("error", (event) => {
    finish(false, `websocket error: ${event.message ?? event}`, ws);
});

ws.addEventListener("close", (event) => {
    if (!finished) finish(false, `websocket closed before a frame arrived (code=${event.code})`, ws);
});
