// settings/layout-canvas.js — the layout editor's canvas: a minimap of every monitor in its real
// mutual position, a big editable grid for the selected monitor (blocks as draggable/resizable
// cards, thin grid lines that honor colWeights/gap/pad, hatch bars for a reserved top bar/dock),
// and a widget palette on the right. Pure presentation + gesture handling — it owns no layout
// state of its own; every structural change is reported to the host (settings/pages/layout.js)
// through `actions`, which decides how to mutate the model (layout-model.js), when to push undo
// history and when to POST /layout/preview. See docs/SETTINGS.md "Раскладка: холст" for the
// state/actions contract.

import { el, widgetLabel } from "./dom.js";
import { ICONS } from "./icons.js";
import { clampToGrid } from "./layout-model.js";

const MAX_STAGE_H = 560;
const MIN_STAGE_H = 220;
const MAX_MINIMAP_H = 96;
const MIN_MINIMAP_MON_W = 144; // --sp-144: a monitor thumbnail on the minimap is never narrower than this

// `iconName` is the widget manifest's own `icon` field (see widgets/<id>/widget.json), not the
// widget id — the two used to be assumed equal (they mostly aren't: "photos" vs. icon "photo",
// "planner" vs. icon "tasks"), which silently fell back to a bare capital-letter initial instead
// of the real glyph. Falls back to the generic "blocks" tile icon, never a letter.
function widgetIcon(iconName) {
  const span = el("span", { class: "lay-block-icon" });
  span.innerHTML = ICONS[iconName] || ICONS.blocks || "";
  return span;
}

/** Column/row start+end pixel pairs for `count` tracks of total pixel size `sizePx`, honoring
 * per-track `weights` (colWeights; rows always use equal weights), `gapPx` between tracks and
 * `padPx` on both outer edges. */
function trackBounds(count, weights, gapPx, padPx, sizePx) {
  const n = Math.max(1, count);
  const ws = Array.isArray(weights) && weights.length === n ? weights : new Array(n).fill(1);
  const totalW = ws.reduce((a, b) => a + (Number(b) || 0), 0) || n;
  const usable = Math.max(0, sizePx - 2 * padPx - gapPx * (n - 1));
  const starts = [];
  const ends = [];
  let x = padPx;
  for (let i = 0; i < n; i++) {
    const w = usable * ((Number(ws[i]) || 0) / totalW);
    starts.push(x);
    x += w;
    ends.push(x);
    x += gapPx;
  }
  return { starts, ends };
}

/** 1-based track index whose [start,end] contains `pos`, snapping anything inside a gap to the
 * nearer side. `pos` outside the whole range clamps to the first/last track. */
function indexAt(pos, bounds) {
  const { starts, ends } = bounds;
  const n = starts.length;
  if (pos <= starts[0]) return 1;
  if (pos >= ends[n - 1]) return n;
  for (let i = 0; i < n; i++) {
    if (pos >= starts[i] && pos <= ends[i]) return i + 1;
    if (i < n - 1 && pos > ends[i] && pos < starts[i + 1]) {
      const midGap = (ends[i] + starts[i + 1]) / 2;
      return pos < midGap ? i + 1 : i + 2;
    }
  }
  return n;
}

function rectFor(bounds, from, span) {
  const { starts, ends } = bounds;
  const i0 = Math.max(0, from - 1);
  const i1 = Math.min(starts.length - 1, from - 1 + span - 1);
  return { start: starts[i0], end: ends[i1] };
}

/**
 * Mounts the layout canvas once into `container`. `actions` is a fixed set of callbacks the host
 * provides at creation time:
 *   onMonitorSelect(id)
 *   onBlockSelect(index)                          // index -1 clears selection
 *   onBlockMove(index, col, row, commit)           // commit=false while dragging (preview-only,
 *                                                   // no re-render), commit=true on release
 *   onBlockResize(index, colSpan, rowSpan, commit)
 *   onBlockDelete(index)
 *   onWidgetAdd(widgetId)
 *   onUndo() / onRedo()
 * Returns { update(state), destroy() }. `state` — see docs/SETTINGS.md "Раскладка: холст":
 *   { tt, monitors, selectedId, layout, widgets, selectedBlock, conflictIdxs, topBar, dock }
 * `layout` is the flat shape from layout-model.js: { cols, rows, gap, pad, colWeights, blocks }.
 */
export function createLayoutCanvas(container, actions) {
  container.innerHTML = "";
  const wrap = el("div", { class: "lay-canvas-wrap" });
  const minimap = el("div", { class: "lay-minimap" });
  const body = el("div", { class: "lay-body" });
  const stage = el("div", { class: "lay-stage", tabindex: "0" });
  const palette = el("div", { class: "lay-palette" });
  body.append(stage, palette);
  wrap.append(minimap, body);
  container.append(wrap);

  let last = null; // last state passed to update(), for keyboard handlers

  stage.addEventListener("keydown", (e) => {
    if (!last) return;
    const idx = last.selectedBlock;
    const isUndo = (e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === "z";
    const isRedo = (e.ctrlKey || e.metaKey) && (e.key.toLowerCase() === "y" || (e.shiftKey && e.key.toLowerCase() === "z"));
    if (isUndo) { e.preventDefault(); actions.onUndo(); return; }
    if (isRedo) { e.preventDefault(); actions.onRedo(); return; }
    if (idx < 0 || !last.layout.blocks[idx]) return;
    const block = last.layout.blocks[idx];
    let dCol = 0, dRow = 0;
    if (e.key === "ArrowLeft") dCol = -1;
    else if (e.key === "ArrowRight") dCol = 1;
    else if (e.key === "ArrowUp") dRow = -1;
    else if (e.key === "ArrowDown") dRow = 1;
    else if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); actions.onBlockDelete(idx); return; }
    else return;
    e.preventDefault();
    if (e.shiftKey) {
      const colSpan = Math.max(1, block.colSpan + dCol);
      const rowSpan = Math.max(1, block.rowSpan + dRow);
      actions.onBlockResize(idx, colSpan, rowSpan, true);
    } else {
      actions.onBlockMove(idx, block.col + dCol, block.row + dRow, true);
    }
  });

  function drawMinimap(state) {
    minimap.innerHTML = "";
    const mons = state.monitors || [];
    if (mons.length <= 1) { minimap.hidden = true; return; }
    minimap.hidden = false;
    const minX = Math.min(...mons.map((m) => m.x || 0));
    const minY = Math.min(...mons.map((m) => m.y || 0));
    const maxX = Math.max(...mons.map((m) => (m.x || 0) + m.width));
    const maxY = Math.max(...mons.map((m) => (m.y || 0) + m.height));
    const bboxW = Math.max(1, maxX - minX);
    const bboxH = Math.max(1, maxY - minY);
    const availW = Math.max(200, minimap.clientWidth || wrap.clientWidth || 600);
    let scale = Math.min(availW / bboxW, MAX_MINIMAP_H / bboxH);
    // Monitors are not duplicated elsewhere on this canvas (the toggle cards above show the same
    // set, this minimap only shows *position*), but a very small monitor next to a huge one could
    // scale down to an unreadable sliver — never let a thumbnail get narrower than 144px.
    const minMonW = Math.min(...mons.map((m) => m.width || 1));
    scale = Math.max(scale, MIN_MINIMAP_MON_W / minMonW);
    minimap.style.height = Math.max(34, Math.round(bboxH * scale)) + "px";
    for (const m of mons) {
      const rect = el("div", {
        class: "lay-minimap-mon" + (m.id === state.selectedId ? " sel" : "") + (m.enabled === false ? " off" : ""),
        style: `left:${Math.round((m.x - minX) * scale)}px;top:${Math.round((m.y - minY) * scale)}px;width:${Math.round(m.width * scale)}px;height:${Math.round(m.height * scale)}px`,
        title: `${m.name || m.id} · ${m.width}x${m.height}`,
        onclick: () => actions.onMonitorSelect(m.id),
      }, el("span", { class: "lay-minimap-label", text: m.name || m.id }));
      minimap.append(rect);
    }
  }

  function drawStage(state) {
    stage.innerHTML = "";
    const { tt, lang, layout, widgets, selectedBlock, conflictIdxs, topBar, dock } = state;
    const monitor = (state.monitors || []).find((m) => m.id === state.selectedId);
    if (!monitor) {
      stage.append(el("div", { class: "empty" }, el("div", { class: "txt", text: tt("emptyMonitors") })));
      return;
    }
    const aspect = (monitor.width || 16) / (monitor.height || 9);
    const availW = Math.max(240, stage.clientWidth || 720) - 2;
    let w = availW, h = w / aspect;
    if (h > MAX_STAGE_H) { h = MAX_STAGE_H; w = h * aspect; }
    if (h < MIN_STAGE_H && aspect < 1) { h = Math.min(MAX_STAGE_H, MIN_STAGE_H); w = h * aspect; }

    const scalePx = w / (monitor.width || w);
    const cols = Math.max(1, layout.cols);
    const rows = Math.max(1, layout.rows);
    const gapPx = (layout.gap || 0) * scalePx;
    const padPx = (layout.pad || 0) * scalePx;

    const grid = el("div", { class: "lay-grid", style: `width:${w}px;height:${h}px` });

    if (topBar && topBar.enabled) {
      grid.append(el("div", { class: "lay-reserved lay-reserved-top", style: `height:${Math.max(2, topBar.height * scalePx)}px` }));
    }
    if (dock && dock.enabled) {
      grid.append(el("div", { class: "lay-reserved lay-reserved-bottom", style: `height:${Math.max(2, dock.size * scalePx)}px` }));
    }

    const colBounds = trackBounds(cols, layout.colWeights, gapPx, padPx, w);
    const rowBounds = trackBounds(rows, null, gapPx, padPx, h);

    for (let i = 1; i < cols; i++) {
      const x = (colBounds.ends[i - 1] + colBounds.starts[i]) / 2;
      grid.append(el("div", { class: "lay-gridline lay-gridline-v", style: `left:${x}px` }));
    }
    for (let i = 1; i < rows; i++) {
      const y = (rowBounds.ends[i - 1] + rowBounds.starts[i]) / 2;
      grid.append(el("div", { class: "lay-gridline lay-gridline-h", style: `top:${y}px` }));
    }

    const conflicts = conflictIdxs || new Set();
    const blocksLayer = el("div", { class: "lay-blocks" });
    grid.append(blocksLayer);

    layout.blocks.forEach((block, idx) => {
      const cr = rectFor(colBounds, block.col, block.colSpan);
      const rr = rectFor(rowBounds, block.row, block.rowSpan);
      const meta = (widgets || []).find((wg) => wg.id === block.widget);
      const box = el("div", {
        class: "lay-block" + (idx === selectedBlock ? " sel" : "") + (conflicts.has(idx) ? " bad" : ""),
        style: `left:${cr.start}px;top:${rr.start}px;width:${cr.end - cr.start}px;height:${rr.end - rr.start}px`,
        "data-widget": block.widget,
        "data-index": String(idx),
      });
      const head = el("div", { class: "lay-block-head" },
        widgetIcon(meta && meta.icon),
        el("span", { class: "lay-block-name", text: widgetLabel(meta, lang) || block.widget }));
      const delBtn = el("button", { class: "lay-block-del", type: "button", title: tt("deleteBlock"), text: "×" });
      delBtn.addEventListener("pointerdown", (e) => e.stopPropagation());
      delBtn.addEventListener("click", (e) => { e.stopPropagation(); actions.onBlockDelete(idx); });
      head.append(delBtn);
      const handle = el("div", { class: "lay-block-handle", title: "resize" });
      box.append(head, handle);

      wireDrag(box, handle, block, idx, { colBounds, rowBounds, cols, rows, stageW: w, stageH: h });
      blocksLayer.append(box);
    });

    stage.append(grid);
  }

  function wireDrag(box, handle, block, idx, geo) {
    box.addEventListener("pointerdown", (e) => {
      if (e.target === handle || e.target.classList.contains("lay-block-del")) return;
      e.preventDefault();
      actions.onBlockSelect(idx);
      box.setPointerCapture(e.pointerId);
      box.classList.add("dragging");
      const ghost = el("div", { class: "lay-block-ghost" });
      box.parentElement.parentElement.append(ghost); // append to .lay-grid, above .lay-blocks
      const startX = e.clientX, startY = e.clientY;
      const baseCr = rectFor(geo.colBounds, block.col, block.colSpan);
      const baseRr = rectFor(geo.rowBounds, block.row, block.rowSpan);
      const placeGhost = (col, row) => {
        const cr = rectFor(geo.colBounds, col, block.colSpan);
        const rr = rectFor(geo.rowBounds, row, block.rowSpan);
        ghost.style.cssText = `left:${cr.start}px;top:${rr.start}px;width:${cr.end - cr.start}px;height:${rr.end - rr.start}px`;
      };
      placeGhost(block.col, block.row);
      let lastCol = block.col, lastRow = block.row;
      const onMove = (ev) => {
        const dx = ev.clientX - startX, dy = ev.clientY - startY;
        box.style.transform = `translate(${dx}px, ${dy}px)`;
        const centerX = baseCr.start + (baseCr.end - baseCr.start) / 2 + dx;
        const centerY = baseRr.start + (baseRr.end - baseRr.start) / 2 + dy;
        const col = indexAt(centerX, geo.colBounds);
        const row = indexAt(centerY, geo.rowBounds);
        const clamped = clampToGrid({ ...block, col, row }, geo.cols, geo.rows);
        if (clamped.col !== lastCol || clamped.row !== lastRow) {
          lastCol = clamped.col; lastRow = clamped.row;
          placeGhost(lastCol, lastRow);
          actions.onBlockMove(idx, lastCol, lastRow, false);
        }
      };
      const onUp = () => {
        box.removeEventListener("pointermove", onMove);
        box.removeEventListener("pointerup", onUp);
        box.classList.remove("dragging");
        box.style.transform = "";
        ghost.remove();
        actions.onBlockMove(idx, lastCol, lastRow, true);
      };
      box.addEventListener("pointermove", onMove);
      box.addEventListener("pointerup", onUp, { once: true });
    });

    handle.addEventListener("pointerdown", (e) => {
      e.preventDefault();
      e.stopPropagation();
      actions.onBlockSelect(idx);
      handle.setPointerCapture(e.pointerId);
      box.classList.add("resizing");
      const gridEl = box.parentElement.parentElement; // .lay-blocks -> .lay-grid
      const gridRect = gridEl.getBoundingClientRect();
      const ghost = el("div", { class: "lay-block-ghost" });
      gridEl.append(ghost);
      const placeGhost = (colSpan, rowSpan) => {
        const cr = rectFor(geo.colBounds, block.col, colSpan);
        const rr = rectFor(geo.rowBounds, block.row, rowSpan);
        ghost.style.cssText = `left:${cr.start}px;top:${rr.start}px;width:${cr.end - cr.start}px;height:${rr.end - rr.start}px`;
      };
      placeGhost(block.colSpan, block.rowSpan);
      let lastColSpan = block.colSpan, lastRowSpan = block.rowSpan;
      const onMove = (ev) => {
        const farCol = indexAt(ev.clientX - gridRect.left, geo.colBounds);
        const farRow = indexAt(ev.clientY - gridRect.top, geo.rowBounds);
        const colSpan = Math.max(1, farCol - block.col + 1);
        const rowSpan = Math.max(1, farRow - block.row + 1);
        const clamped = clampToGrid({ ...block, colSpan, rowSpan }, geo.cols, geo.rows);
        if (clamped.colSpan !== lastColSpan || clamped.rowSpan !== lastRowSpan) {
          lastColSpan = clamped.colSpan; lastRowSpan = clamped.rowSpan;
          placeGhost(lastColSpan, lastRowSpan);
          actions.onBlockResize(idx, lastColSpan, lastRowSpan, false);
        }
      };
      const onUp = () => {
        handle.removeEventListener("pointermove", onMove);
        handle.removeEventListener("pointerup", onUp);
        box.classList.remove("resizing");
        ghost.remove();
        actions.onBlockResize(idx, lastColSpan, lastRowSpan, true);
      };
      handle.addEventListener("pointermove", onMove);
      handle.addEventListener("pointerup", onUp, { once: true });
    });
  }

  function drawPalette(state) {
    palette.innerHTML = "";
    const { tt, lang, widgets } = state;
    palette.append(el("div", { class: "lay-palette-title", text: tt("paletteTitle") }));
    const list = el("div", { class: "lay-palette-list" });
    for (const w of widgets || []) {
      const item = el("div", { class: "lay-palette-item", "data-widget": w.id, onclick: () => actions.onWidgetAdd(w.id) },
        widgetIcon(w.icon),
        el("span", { class: "t", text: widgetLabel(w, lang) }));
      list.append(item);
    }
    if (!widgets || !widgets.length) list.append(el("div", { class: "s", text: tt("emptyWidgets") }));
    palette.append(list);
  }

  function flashPaletteNoRoom(widgetId) {
    const item = palette.querySelector(`.lay-palette-item[data-widget="${CSS.escape(widgetId)}"]`);
    if (!item) return;
    item.classList.add("no-room");
    setTimeout(() => item.classList.remove("no-room"), 900);
  }

  return {
    update(state) {
      last = state;
      drawMinimap(state);
      drawStage(state);
      drawPalette(state);
    },
    flashPaletteNoRoom,
    destroy() {
      container.innerHTML = "";
    },
  };
}
