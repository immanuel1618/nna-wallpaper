import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";

// model.js is UMD (also loaded as a plain <script> by the wallpaper page), so it exports via
// module.exports rather than ESM `export` — pull it in through createRequire from this .mjs test.
const require = createRequire(import.meta.url);
const Model = require("../model.js");

test("initial() starts at WORK with zero completed cycles", () => {
  const s = Model.initial({ work: 25, chill: 5, cycles: 2 });
  assert.equal(s.phase, "work");
  assert.equal(s.completedWork, 0);
  assert.deepEqual(s.cfg, { work: 25, chill: 5, cycles: 2 });
});

test("cycles=2: WORK -> CHILL -> WORK -> CHILL -> DONE, then DONE is terminal", () => {
  let s = Model.initial({ work: 25, chill: 5, cycles: 2 });
  const phases = [s.phase];

  for (let i = 0; i < 6; i++) {
    s = Model.advance(s);
    phases.push(s.phase);
  }

  assert.deepEqual(phases, ["work", "chill", "work", "done", "done", "done", "done"]);
  assert.equal(s.completedWork, 2, "two WORK phases must have completed by the time the session is done");
  assert.equal(Model.isDone(s), true);
});

test("completedWork only increments when leaving a WORK phase, never a CHILL one", () => {
  let s = Model.initial({ work: 10, chill: 1, cycles: 3 });
  assert.equal(s.completedWork, 0);
  s = Model.advance(s); // work -> chill
  assert.equal(s.phase, "chill");
  assert.equal(s.completedWork, 1);
  s = Model.advance(s); // chill -> work
  assert.equal(s.phase, "work");
  assert.equal(s.completedWork, 1);
});

test("cycles=1: a single WORK phase finishes the session directly (no CHILL)", () => {
  let s = Model.initial({ work: 90, chill: 20, cycles: 1 });
  s = Model.advance(s);
  assert.equal(s.phase, "done");
  assert.equal(s.completedWork, 1);
});

test("phaseMinutes() reads work/chill from cfg and 0 once done", () => {
  let s = Model.initial({ work: 50, chill: 10, cycles: 2 });
  assert.equal(Model.phaseMinutes(s), 50);
  s = Model.advance(s);
  assert.equal(Model.phaseMinutes(s), 10);
  s = Model.advance(s);
  s = Model.advance(s);
  assert.equal(s.phase, "done");
  assert.equal(Model.phaseMinutes(s), 0);
});

test("normalizeCfg() clamps to the WORK/CHILL/CYCLES bounds and falls back on bad input", () => {
  assert.deepEqual(Model.normalizeCfg({ work: 0, chill: 999, cycles: -3 }), { work: 1, chill: 60, cycles: 1 });
  assert.deepEqual(Model.normalizeCfg({ work: 300, chill: 5, cycles: 40 }), { work: 180, chill: 5, cycles: 12 });
  assert.deepEqual(Model.normalizeCfg({}), Model.defaults());
  assert.deepEqual(Model.normalizeCfg({ work: "not a number" }), { work: Model.defaults().work, chill: 5, cycles: 4 });
});

test("withConfig() re-applies bounds but keeps phase/completedWork (an in-session edit, not a preset reset)", () => {
  let s = Model.initial({ work: 25, chill: 5, cycles: 4 });
  s = Model.advance(s); // now in chill, completedWork=1
  const edited = Model.withConfig(s, { work: 40, chill: 8, cycles: 4 });
  assert.equal(edited.phase, "chill");
  assert.equal(edited.completedWork, 1);
  assert.deepEqual(edited.cfg, { work: 40, chill: 8, cycles: 4 });
});
