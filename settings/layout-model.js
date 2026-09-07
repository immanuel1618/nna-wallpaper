// Pure layout math shared by the settings page (browser, <script type="module">) and its
// node:test suite. No DOM access here — everything is plain data in, plain data out.
//
// A "layout" is { cols, rows, colWeights?, gap?, pad?, blocks: [Block] }.
// A "Block" is { widget, col, colSpan, row, rowSpan, settingsOverride? } — 1-based grid coordinates,
// same shape as monitors.json's blocks[] (see AppSettings.cs / SETTINGS-SCHEMA.md).

/** Clamp a single block so it fits inside a cols x rows grid, spans >= 1. */
export function clampToGrid(block, cols, rows) {
  cols = Math.max(1, Math.floor(cols) || 1);
  rows = Math.max(1, Math.floor(rows) || 1);
  const colSpan = Math.max(1, Math.min(Math.floor(block.colSpan) || 1, cols));
  const rowSpan = Math.max(1, Math.min(Math.floor(block.rowSpan) || 1, rows));
  const col = Math.max(1, Math.min(Math.floor(block.col) || 1, cols - colSpan + 1));
  const row = Math.max(1, Math.min(Math.floor(block.row) || 1, rows - rowSpan + 1));
  return { ...block, col, row, colSpan, rowSpan };
}

function cloneLayout(layout) {
  return { ...layout, blocks: layout.blocks.map((b) => ({ ...b })) };
}

/** Move block at `index` to (col, row), clamped to the grid. Returns a new layout. */
export function moveBlock(layout, index, col, row) {
  const next = cloneLayout(layout);
  const block = next.blocks[index];
  if (!block) return next;
  next.blocks[index] = clampToGrid({ ...block, col, row }, layout.cols, layout.rows);
  return next;
}

/** Resize block at `index` to (colSpan, rowSpan), clamped to the grid from its current position. */
export function resizeBlock(layout, index, colSpan, rowSpan) {
  const next = cloneLayout(layout);
  const block = next.blocks[index];
  if (!block) return next;
  next.blocks[index] = clampToGrid({ ...block, colSpan, rowSpan }, layout.cols, layout.rows);
  return next;
}

/** True when two blocks' grid rectangles intersect. */
export function overlaps(a, b) {
  const aColEnd = a.col + a.colSpan - 1;
  const bColEnd = b.col + b.colSpan - 1;
  const aRowEnd = a.row + a.rowSpan - 1;
  const bRowEnd = b.row + b.rowSpan - 1;
  return a.col <= bColEnd && b.col <= aColEnd && a.row <= bRowEnd && b.row <= aRowEnd;
}

/** All pairs of block indices whose rectangles overlap. */
export function findOverlaps(layout) {
  const pairs = [];
  const blocks = layout.blocks || [];
  for (let i = 0; i < blocks.length; i++) {
    for (let j = i + 1; j < blocks.length; j++) {
      if (overlaps(blocks[i], blocks[j])) pairs.push([i, j]);
    }
  }
  return pairs;
}

/** Structural + overlap validation. Does not mutate the layout. */
export function validateLayout(layout) {
  const errors = [];
  const cols = layout.cols;
  const rows = layout.rows;
  if (!(cols >= 1)) errors.push("cols must be >= 1");
  if (!(rows >= 1)) errors.push("rows must be >= 1");

  const blocks = layout.blocks || [];
  blocks.forEach((b, i) => {
    if (!b.widget || typeof b.widget !== "string" || !b.widget.trim()) {
      errors.push(`block ${i}: widget is required`);
    }
    if (!(b.col >= 1)) errors.push(`block ${i}: col must be >= 1`);
    if (!(b.row >= 1)) errors.push(`block ${i}: row must be >= 1`);
    if (!(b.colSpan >= 1)) errors.push(`block ${i}: colSpan must be >= 1`);
    if (!(b.rowSpan >= 1)) errors.push(`block ${i}: rowSpan must be >= 1`);
    if (cols >= 1 && b.col + b.colSpan - 1 > cols) errors.push(`block ${i}: exceeds grid columns`);
    if (rows >= 1 && b.row + b.rowSpan - 1 > rows) errors.push(`block ${i}: exceeds grid rows`);
  });

  for (const [i, j] of findOverlaps(layout)) {
    errors.push(`blocks ${i} and ${j} overlap`);
  }

  return { ok: errors.length === 0, errors };
}

/**
 * First free col/row (scanning row-major) where a block of `size` (default 1x1) fits without
 * overlapping any existing block and without leaving the grid. Returns null if there is no room.
 */
export function findFreeSlot(layout, cols, rows, size) {
  const colSpan = Math.max(1, (size && size.colSpan) || 1);
  const rowSpan = Math.max(1, (size && size.rowSpan) || 1);
  const blocks = layout.blocks || [];

  for (let row = 1; row + rowSpan - 1 <= rows; row++) {
    for (let col = 1; col + colSpan - 1 <= cols; col++) {
      const candidate = { col, row, colSpan, rowSpan };
      const collides = blocks.some((b) => overlaps(candidate, b));
      if (!collides) return { col, row };
    }
  }
  return null;
}

/**
 * Adds a block for `widget` at the first free slot (row-major scan) sized `size` ({colSpan,
 * rowSpan}, default 1x1). Returns { layout, added, index }: on success `layout` is a new layout
 * with the block appended and `index` is its position in `blocks`; when there is no room, `added`
 * is false and `layout` is the input unchanged (not cloned — callers should keep using the
 * original reference).
 */
export function addBlock(layout, widget, size) {
  const colSpan = Math.max(1, (size && size.colSpan) || 1);
  const rowSpan = Math.max(1, (size && size.rowSpan) || 1);
  const slot = findFreeSlot(layout, layout.cols, layout.rows, { colSpan, rowSpan });
  if (!slot) return { layout, added: false, index: -1 };
  const next = cloneLayout(layout);
  next.blocks.push({ widget, col: slot.col, row: slot.row, colSpan, rowSpan });
  return { layout: next, added: true, index: next.blocks.length - 1 };
}

/** Removes the block at `index`. Returns a new layout; out-of-range index is a no-op clone. */
export function removeBlock(layout, index) {
  const next = cloneLayout(layout);
  if (index >= 0 && index < next.blocks.length) next.blocks.splice(index, 1);
  return next;
}

/** Alias of validateLayout — same structural + overlap validation, named to match the editor's
 * addBlock/removeBlock/validate trio (settings/layout-canvas.js, docs/SETTINGS.md). */
export const validate = validateLayout;

/**
 * A bounded undo/redo stack of plain-data snapshots (deep-cloned via JSON so callers can pass
 * mutable layout objects without aliasing bugs). `push` truncates any redo branch, same as a
 * normal editor undo stack. Limit defaults to 55 (Fibonacci — see ui/tokens.css --sp-55) past
 * states; the current state does not count against the limit.
 */
export function createHistory(limit = 55) {
  const clone = (v) => JSON.parse(JSON.stringify(v));
  const past = [];
  const future = [];
  let current = null;

  return {
    /** Sets the current state without recording an undo step (call once, on load/select). */
    init(state) {
      current = clone(state);
      past.length = 0;
      future.length = 0;
    },
    /** Records `current` onto the past stack (capped at `limit`) and makes `state` current. */
    push(state) {
      if (current !== null) {
        past.push(current);
        if (past.length > limit) past.shift();
      }
      current = clone(state);
      future.length = 0;
    },
    undo() {
      if (!past.length) return current;
      future.push(current);
      current = past.pop();
      return clone(current);
    },
    redo() {
      if (!future.length) return current;
      past.push(current);
      current = future.pop();
      return clone(current);
    },
    canUndo() { return past.length > 0; },
    canRedo() { return future.length > 0; },
    get current() { return current === null ? null : clone(current); },
  };
}

/** Same defaults as LayoutResolver.cs: landscape -> "main", portrait -> "vertical". */
export function defaultLayoutFor(width, height, id) {
  return height > width ? defaultVertical(id) : defaultMain(id);
}

export function defaultMain(id) {
  return {
    id,
    name: "Main",
    enabled: true,
    grid: { cols: 3, rows: 16, colWeights: [2.2, 1, 2.2], gap: 24, pad: 48 },
    blocks: [
      { widget: "eq", col: 1, colSpan: 1, row: 1, rowSpan: 4 },
      { widget: "photos", col: 1, colSpan: 1, row: 5, rowSpan: 12 },
      { widget: "focus", col: 2, colSpan: 1, row: 1, rowSpan: 8 },
      { widget: "weather", col: 2, colSpan: 1, row: 9, rowSpan: 8 },
      { widget: "events", col: 3, colSpan: 1, row: 1, rowSpan: 4 },
      { widget: "launch", col: 3, colSpan: 1, row: 5, rowSpan: 4, settingsOverride: { compact: true } },
      { widget: "graph", col: 3, colSpan: 1, row: 9, rowSpan: 8 },
    ],
  };
}

export function defaultVertical(id) {
  return {
    id,
    name: "Vertical",
    enabled: true,
    grid: { cols: 1, rows: 4, gap: 24, pad: 48 },
    blocks: [
      { widget: "stats", col: 1, colSpan: 1, row: 1, rowSpan: 1 },
      { widget: "planner", col: 1, colSpan: 1, row: 2, rowSpan: 1 },
      { widget: "player", col: 1, colSpan: 1, row: 3, rowSpan: 1 },
      { widget: "launch", col: 1, colSpan: 1, row: 4, rowSpan: 1 },
    ],
  };
}
