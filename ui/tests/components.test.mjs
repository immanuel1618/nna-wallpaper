import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const { placePopover, sliderStep, nextIndex, typeahead } = require(
  path.join(here, "../logic.js")
);

// ---- placePopover ---------------------------------------------------------

test("placePopover: places below anchor when it fits", () => {
  const anchor = { top: 100, left: 50, right: 150, bottom: 120, width: 100, height: 20 };
  const pop = { width: 200, height: 80 };
  const viewport = { width: 1000, height: 800 };
  const r = placePopover(anchor, pop, viewport, "bottom");
  assert.equal(r.placement, "bottom");
  assert.equal(r.top, 128); // bottom + gap(8)
  assert.equal(r.left, 50);
});

test("placePopover: flips to top when it does not fit below", () => {
  const anchor = { top: 700, left: 50, right: 150, bottom: 750, width: 100, height: 50 };
  const pop = { width: 200, height: 100 };
  const viewport = { width: 1000, height: 800 };
  const r = placePopover(anchor, pop, viewport, "bottom");
  assert.equal(r.placement, "top");
  assert.equal(r.top, 700 - 8 - 100);
});

test("placePopover: clamps left so it never crosses the right edge", () => {
  const anchor = { top: 100, left: 950, right: 1000, bottom: 120, width: 50, height: 20 };
  const pop = { width: 200, height: 80 };
  const viewport = { width: 1000, height: 800 };
  const r = placePopover(anchor, pop, viewport, "bottom");
  assert.ok(r.left + pop.width <= viewport.width - 4 + 0.001);
  assert.ok(r.left >= 4);
});

test("placePopover: clamps top when neither side fully fits", () => {
  const anchor = { top: 10, left: 10, right: 60, bottom: 780, width: 50, height: 770 };
  const pop = { width: 100, height: 700 };
  const viewport = { width: 400, height: 800 };
  const r = placePopover(anchor, pop, viewport, "bottom");
  assert.ok(r.top >= 4);
  assert.ok(r.top + pop.height <= viewport.height - 4 + 0.001);
});

// ---- sliderStep -------------------------------------------------------------

test("sliderStep: arrow key moves by one step", () => {
  assert.equal(sliderStep(10, 0, 100, 5, 1), 15);
  assert.equal(sliderStep(10, 0, 100, 5, -1), 5);
});

test("sliderStep: clamps to min/max", () => {
  assert.equal(sliderStep(98, 0, 100, 5, 1), 100);
  assert.equal(sliderStep(2, 0, 100, 5, -1), 0);
});

test("sliderStep: Home/End jump to min/max via Infinity", () => {
  assert.equal(sliderStep(42, 0, 100, 5, -Infinity), 0);
  assert.equal(sliderStep(42, 0, 100, 5, Infinity), 100);
});

test("sliderStep: PageUp/PageDown style large delta", () => {
  assert.equal(sliderStep(10, 0, 100, 1, 10), 20);
});

// ---- nextIndex ----------------------------------------------------------------

test("nextIndex: ArrowDown/ArrowUp move by one and wrap", () => {
  const list = [{}, {}, {}];
  assert.equal(nextIndex(list, 0, "ArrowDown"), 1);
  assert.equal(nextIndex(list, 2, "ArrowDown"), 0); // wraps
  assert.equal(nextIndex(list, 0, "ArrowUp"), 2); // wraps
});

test("nextIndex: Home/End jump to first/last", () => {
  const list = [{}, {}, {}, {}];
  assert.equal(nextIndex(list, 2, "Home"), 0);
  assert.equal(nextIndex(list, 2, "End"), 3);
});

test("nextIndex: skips disabled items", () => {
  const list = [{}, { disabled: true }, {}];
  assert.equal(nextIndex(list, 0, "ArrowDown"), 2);
  assert.equal(nextIndex(list, 2, "ArrowUp"), 0);
});

test("nextIndex: starting at -1 lands on first/last sensibly", () => {
  const list = [{}, {}, {}];
  assert.equal(nextIndex(list, -1, "ArrowDown"), 0);
  assert.equal(nextIndex(list, -1, "ArrowUp"), 2);
});

// ---- typeahead ------------------------------------------------------------------

test("typeahead: matches case-insensitive prefix", () => {
  const items = [{ label: "Blur" }, { label: "Acrylic" }, { label: "Clear" }, { label: "Opaque" }];
  assert.equal(typeahead(items, "cl"), 2);
  assert.equal(typeahead(items, "A"), 1);
});

test("typeahead: returns -1 when nothing matches or buffer empty", () => {
  const items = [{ label: "Blur" }, { label: "Acrylic" }];
  assert.equal(typeahead(items, "zz"), -1);
  assert.equal(typeahead(items, ""), -1);
});
