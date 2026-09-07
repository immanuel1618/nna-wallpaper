import test from "node:test";
import assert from "node:assert/strict";
import {
  clampToGrid,
  moveBlock,
  resizeBlock,
  overlaps,
  findOverlaps,
  validateLayout,
  findFreeSlot,
  addBlock,
  removeBlock,
  validate,
  createHistory,
  defaultLayoutFor,
  defaultMain,
  defaultVertical,
} from "../layout-model.js";

function sampleLayout() {
  return {
    cols: 3,
    rows: 8,
    blocks: [
      { widget: "eq", col: 1, colSpan: 1, row: 1, rowSpan: 4 },
      { widget: "focus", col: 2, colSpan: 1, row: 1, rowSpan: 4 },
      { widget: "weather", col: 3, colSpan: 1, row: 1, rowSpan: 4 },
    ],
  };
}

test("moveBlock moves a block and clamps it into the grid", () => {
  const layout = sampleLayout();
  const moved = moveBlock(layout, 0, 2, 5);
  assert.equal(moved.blocks[0].col, 2);
  assert.equal(moved.blocks[0].row, 5);
  // original untouched
  assert.equal(layout.blocks[0].col, 1);
  assert.equal(layout.blocks[0].row, 1);

  // moving past the edge clamps back so the block stays fully inside the grid
  const clamped = moveBlock(layout, 0, 99, 99);
  assert.equal(clamped.blocks[0].col, layout.cols - clamped.blocks[0].colSpan + 1);
  assert.equal(clamped.blocks[0].row, layout.rows - clamped.blocks[0].rowSpan + 1);
});

test("resizeBlock changes the span and clamps it into the grid", () => {
  const layout = sampleLayout();
  const resized = resizeBlock(layout, 0, 2, 6);
  assert.equal(resized.blocks[0].colSpan, 2);
  assert.equal(resized.blocks[0].rowSpan, 6);

  // growing past the grid edge clamps both the span and, if needed, the position
  const grown = resizeBlock(layout, 2, 10, 10);
  assert.equal(grown.blocks[2].colSpan, layout.cols);
  assert.equal(grown.blocks[2].rowSpan, layout.rows);
  assert.ok(grown.blocks[2].col + grown.blocks[2].colSpan - 1 <= layout.cols);
  assert.ok(grown.blocks[2].row + grown.blocks[2].rowSpan - 1 <= layout.rows);
});

test("overlaps detects intersecting rectangles and rejects disjoint ones", () => {
  const a = { col: 1, colSpan: 2, row: 1, rowSpan: 2 };
  const b = { col: 2, colSpan: 2, row: 2, rowSpan: 2 };
  const c = { col: 3, colSpan: 1, row: 3, rowSpan: 1 };
  assert.equal(overlaps(a, b), true);
  assert.equal(overlaps(a, c), false);
});

test("validateLayout rejects overlapping blocks and findOverlaps lists the pair", () => {
  const ok = sampleLayout();
  assert.deepEqual(validateLayout(ok), { ok: true, errors: [] });
  assert.deepEqual(findOverlaps(ok), []);

  const bad = moveBlock(ok, 1, 1, 1); // move "focus" onto "eq"
  const result = validateLayout(bad);
  assert.equal(result.ok, false);
  assert.ok(result.errors.some((e) => e.includes("overlap")));
  assert.deepEqual(findOverlaps(bad), [[0, 1]]);
});

test("validateLayout flags out-of-grid blocks and a missing widget id", () => {
  const layout = {
    cols: 2,
    rows: 2,
    blocks: [{ widget: "", col: 1, colSpan: 3, row: 1, rowSpan: 1 }],
  };
  const result = validateLayout(layout);
  assert.equal(result.ok, false);
  assert.ok(result.errors.some((e) => e.includes("widget is required")));
  assert.ok(result.errors.some((e) => e.includes("exceeds grid columns")));
});

test("clampToGrid snaps out-of-range blocks back inside the grid", () => {
  const clamped = clampToGrid({ widget: "x", col: -3, row: 50, colSpan: 99, rowSpan: 99 }, 4, 6);
  assert.equal(clamped.colSpan, 4);
  assert.equal(clamped.rowSpan, 6);
  assert.equal(clamped.col, 1);
  assert.equal(clamped.row, 1);

  const withinBounds = clampToGrid({ widget: "x", col: 4, row: 6, colSpan: 2, rowSpan: 2 }, 4, 6);
  assert.equal(withinBounds.col, 3); // 4 -> pulled back so col+colSpan-1 <= cols
  assert.equal(withinBounds.row, 5);
});

test("findFreeSlot finds the first open cell and returns null when full", () => {
  const layout = sampleLayout(); // 3 cols x 8 rows, top 4 rows fully occupied across all columns
  const free = findFreeSlot(layout, 3, 8, { colSpan: 1, rowSpan: 1 });
  assert.deepEqual(free, { col: 1, row: 5 });

  const free2x2 = findFreeSlot(layout, 3, 8, { colSpan: 2, rowSpan: 2 });
  assert.deepEqual(free2x2, { col: 1, row: 5 });

  const fullLayout = {
    cols: 1,
    rows: 1,
    blocks: [{ widget: "x", col: 1, colSpan: 1, row: 1, rowSpan: 1 }],
  };
  assert.equal(findFreeSlot(fullLayout, 1, 1, { colSpan: 1, rowSpan: 1 }), null);
});

test("defaultLayoutFor mirrors LayoutResolver: landscape -> main, portrait -> vertical", () => {
  const landscape = defaultLayoutFor(3440, 1440, "mon-a");
  assert.equal(landscape.grid.cols, 3);
  assert.equal(landscape.grid.rows, 16);
  assert.deepEqual(landscape, defaultMain("mon-a"));

  const portrait = defaultLayoutFor(1440, 2560, "mon-b");
  assert.equal(portrait.grid.cols, 1);
  assert.equal(portrait.grid.rows, 4);
  assert.deepEqual(portrait, defaultVertical("mon-b"));

  assert.equal(validateLayout({ cols: landscape.grid.cols, rows: landscape.grid.rows, blocks: landscape.blocks }).ok, true);
  assert.equal(validateLayout({ cols: portrait.grid.cols, rows: portrait.grid.rows, blocks: portrait.blocks }).ok, true);
});

test("addBlock places a new block in the first free slot and does not mutate the input", () => {
  const layout = sampleLayout(); // 3 cols x 8 rows, top 4 rows fully occupied across all columns
  const { layout: next, added, index } = addBlock(layout, "stats", { colSpan: 1, rowSpan: 1 });
  assert.equal(added, true);
  assert.equal(index, 3);
  assert.equal(next.blocks.length, 4);
  assert.deepEqual(next.blocks[3], { widget: "stats", col: 1, row: 5, colSpan: 1, rowSpan: 1 });
  assert.equal(layout.blocks.length, 3); // original untouched

  // default size is 1x1 when no size is given
  const { layout: next2 } = addBlock(layout, "eq");
  assert.deepEqual(next2.blocks[3].colSpan, 1);
  assert.deepEqual(next2.blocks[3].rowSpan, 1);
});

test("addBlock reports added:false and returns the same layout reference when there is no room", () => {
  const fullLayout = { cols: 1, rows: 1, blocks: [{ widget: "x", col: 1, colSpan: 1, row: 1, rowSpan: 1 }] };
  const result = addBlock(fullLayout, "y", { colSpan: 1, rowSpan: 1 });
  assert.equal(result.added, false);
  assert.equal(result.index, -1);
  assert.equal(result.layout, fullLayout);
});

test("removeBlock drops the block at index and does not mutate the input", () => {
  const layout = sampleLayout();
  const next = removeBlock(layout, 1);
  assert.equal(next.blocks.length, 2);
  assert.deepEqual(next.blocks.map((b) => b.widget), ["eq", "weather"]);
  assert.equal(layout.blocks.length, 3); // original untouched

  // out-of-range index is a no-op clone
  const untouched = removeBlock(layout, 99);
  assert.deepEqual(untouched.blocks.map((b) => b.widget), ["eq", "focus", "weather"]);
});

test("validate is the same check as validateLayout", () => {
  const ok = sampleLayout();
  assert.deepEqual(validate(ok), validateLayout(ok));
  const bad = moveBlock(ok, 1, 1, 1);
  assert.deepEqual(validate(bad), validateLayout(bad));
});

test("createHistory: push records undo steps, undo/redo walk them, push after undo drops the redo branch", () => {
  const h = createHistory(55);
  h.init({ n: 0 });
  h.push({ n: 1 });
  h.push({ n: 2 });
  h.push({ n: 3 });
  assert.equal(h.current.n, 3);
  assert.equal(h.canUndo(), true);
  assert.equal(h.canRedo(), false);

  assert.equal(h.undo().n, 2);
  assert.equal(h.undo().n, 1);
  assert.equal(h.canRedo(), true);
  assert.equal(h.redo().n, 2);

  // pushing a new state after undoing drops the abandoned redo branch
  h.push({ n: 99 });
  assert.equal(h.canRedo(), false);
  assert.equal(h.current.n, 99);

  // undo() on an empty past stack is a no-op that returns the current state
  const empty = createHistory();
  empty.init({ n: "start" });
  assert.equal(empty.undo().n, "start");
  assert.equal(empty.canUndo(), false);
});

test("createHistory: caps the undo stack at `limit` past states", () => {
  const h = createHistory(3);
  h.init({ n: 0 });
  for (let i = 1; i <= 10; i++) h.push({ n: i });
  // only the last 3 past states survive; undoing further than that just stops at the oldest kept
  assert.equal(h.undo().n, 9);
  assert.equal(h.undo().n, 8);
  assert.equal(h.undo().n, 7);
  assert.equal(h.canUndo(), false);
});

test("createHistory: snapshots are deep clones, not references to caller-owned objects", () => {
  const h = createHistory();
  const state = { blocks: [{ widget: "eq" }] };
  h.init(state);
  state.blocks[0].widget = "mutated-after-init";
  assert.equal(h.current.blocks[0].widget, "eq");

  const pushed = { blocks: [{ widget: "focus" }] };
  h.push(pushed);
  pushed.blocks[0].widget = "mutated-after-push";
  h.push({ blocks: [] });
  assert.equal(h.undo().blocks[0].widget, "focus");
});
