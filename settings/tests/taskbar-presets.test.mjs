import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const presetsDir = path.resolve(here, "../../presets/taskbar");

const MODES = ["normal", "clear", "blur", "acrylic", "opaque"];
const SIDES = ["left", "center", "right"];
const MONITORS = ["all", "primary"];
const WINDOWS_KEYS = [
  "centered", "hideSearch", "hideTaskView", "hideWidgets", "hideClock",
  "small", "transparency", "oledTransparency", "autoHide",
];

function readJson(file) {
  return JSON.parse(readFileSync(path.join(presetsDir, file), "utf8"));
}

function assertSurfaceStyle(style, label) {
  assert.ok(style && typeof style === "object", `${label} must be an object`);
  assert.ok(MODES.includes(style.mode), `${label}.mode "${style.mode}" must be one of ${MODES.join("|")}`);
  assert.equal(typeof style.color, "string", `${label}.color must be a string`);
  assert.match(style.color, /^#[0-9a-fA-F]{6}$/, `${label}.color must be a hex color`);
  assert.equal(typeof style.opacity, "number", `${label}.opacity must be a number`);
  assert.ok(style.opacity >= 0 && style.opacity <= 1, `${label}.opacity must be within 0..1`);
}

const index = readJson("index.json");

test("index.json lists an entry for every preset file, and vice versa", () => {
  assert.ok(Array.isArray(index), "index.json must be an array");
  const files = readdirSync(presetsDir).filter((f) => f.endsWith(".json") && f !== "index.json");
  const indexedFiles = index.map((e) => e.file);
  assert.deepEqual([...indexedFiles].sort(), [...files].sort());
  for (const entry of index) {
    assert.equal(typeof entry.id, "string");
    assert.ok(entry.id.length > 0);
    assert.ok(entry.name && typeof entry.name.ru === "string" && typeof entry.name.en === "string");
    assert.equal(typeof entry.file, "string");
  }
});

for (const entry of index) {
  test(`preset "${entry.id}" (${entry.file}) is well-formed`, () => {
    const preset = readJson(entry.file);

    assert.equal(typeof preset.id, "string");
    assert.equal(preset.id, entry.id);
    assert.ok(preset.name && typeof preset.name.ru === "string" && typeof preset.name.en === "string");
    assert.ok(preset.taskbar && typeof preset.taskbar === "object", "taskbar is required");
    assert.ok(preset.topBar && typeof preset.topBar === "object", "topBar is required");

    const tb = preset.taskbar;
    assert.equal(typeof tb.enabled, "boolean");
    assert.equal(typeof tb.preset, "string");
    assertSurfaceStyle(tb.normal, "taskbar.normal");
    assertSurfaceStyle(tb.maximized, "taskbar.maximized");
    assertSurfaceStyle(tb.fullscreen, "taskbar.fullscreen");
    assert.equal(typeof tb.secondary, "boolean");

    assert.ok(tb.windows && typeof tb.windows === "object", "taskbar.windows is required");
    for (const key of WINDOWS_KEYS) {
      assert.ok(Object.prototype.hasOwnProperty.call(tb.windows, key), `taskbar.windows.${key} is required`);
      const v = tb.windows[key];
      assert.ok(v === null || typeof v === "boolean", `taskbar.windows.${key} must be null or boolean`);
    }

    const top = preset.topBar;
    assert.equal(typeof top.enabled, "boolean");
    assert.ok(MONITORS.includes(top.monitors), `topBar.monitors "${top.monitors}" must be one of ${MONITORS.join("|")}`);
    assert.equal(typeof top.height, "number");
    assertSurfaceStyle(top.style, "topBar.style");
    assert.equal(typeof top.fontSize, "number");
    assert.equal(typeof top.autoHide, "boolean");
    assert.equal(typeof top.reserveSpace, "boolean");

    assert.ok(Array.isArray(top.modules), "topBar.modules must be an array");
    for (const mod of top.modules) {
      assert.equal(typeof mod.id, "string");
      assert.ok(mod.id.length > 0);
      assert.ok(SIDES.includes(mod.side), `module "${mod.id}" side "${mod.side}" must be one of ${SIDES.join("|")}`);
    }
  });
}
