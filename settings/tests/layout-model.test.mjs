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
