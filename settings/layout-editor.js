// settings/layout-editor.js — the "Layout" page's controller: owns the per-monitor working
// layout state, undo/redo history, the throttled /layout/preview channel and Apply/Cancel/Reset,
// and wires settings/layout-canvas.js (the presentational canvas+palette) to it. settings/pages/
// layout.js is a thin wrapper that only supplies the page's own ru/en dictionary (see
// docs/SETTINGS.md "Раскладка"); everything else lives here, same split as before this stage.

import { el, groupCard, settingRow } from "./dom.js";
import { createLayoutCanvas } from "./layout-canvas.js";
import {
  clampToGrid, moveBlock, resizeBlock, findOverlaps, validate,
  addBlock, removeBlock, createHistory, defaultLayoutFor,
} from "./layout-model.js";

const PREVIEW_THROTTLE_MS = 80;
const HISTORY_LIMIT = 55;

function cloneMonitor(m) {
  return {
    id: m.id,
    name: m.name,
    enabled: m.enabled !== false,
    grid: { ...m.grid, colWeights: m.grid.colWeights ? [...m.grid.colWeights] : undefined },
    blocks: (m.blocks || []).map((b) => ({ ...b })),
  };
}

function toFlat(monitor) {
  return {
    cols: monitor.grid.cols, rows: monitor.grid.rows,
    gap: monitor.grid.gap, pad: monitor.grid.pad, colWeights: monitor.grid.colWeights,
    blocks: monitor.blocks,
  };
}

function fromFlat(monitor, flat) {
  return {
    ...monitor,
    grid: { ...monitor.grid, cols: flat.cols, rows: flat.rows, gap: flat.gap, pad: flat.pad, colWeights: flat.colWeights },
    blocks: flat.blocks,
  };
}

function throttle(fn, wait) {
  let last = 0, timer = null, pendingArgs = null;
  function fire() {
    last = Date.now();
    timer = null;
    const args = pendingArgs;
    pendingArgs = null;
    fn(...args);
  }
  const wrapped = (...args) => {
    pendingArgs = args;
    const remaining = wait - (Date.now() - last);
    if (remaining <= 0) {
      clearTimeout(timer);
      fire();
    } else if (!timer) {
      timer = setTimeout(fire, remaining);
    }
  };
  wrapped.flush = () => { if (timer) { clearTimeout(timer); fire(); } };
  return wrapped;
}

/**
 * Mounts the layout editor. `ctx` = { t (already the page's tt()), put, fetchDefaults, api,
 * liveMonitors, monitorsConfig, widgets, appConfig ({topBar, dock}), onStatus }.
 */
export function mountLayoutTab(container, ctx) {
  const tt = ctx.t;
  const lang = ctx.lang === "en" ? "en" : "ru";
  container.innerHTML = "";

  const widgets = ctx.widgets || [];
  const byId = new Map(); // monitor id -> working record { id, name, enabled, grid, blocks }
  const lastApplied = new Map(); // monitor id -> snapshot as currently persisted on disk
  const historyById = new Map(); // monitor id -> layout-model history (flat shape)
  const dirty = new Set(); // monitor ids with unapplied edits

  for (const m of ctx.monitorsConfig?.monitors || []) {
    byId.set(m.id, cloneMonitor(m));
    lastApplied.set(m.id, cloneMonitor(m));
  }

  let liveMonitors = ctx.liveMonitors || [];
  let selectedId = liveMonitors[0]?.id || null;
  let selectedBlock = -1;
  let applying = false;

  const page = el("div", { class: "lay-page" });
  container.append(page);

  // Движок не запущен (headless) — нет ни одного живого монитора: раньше страница всё равно
  // рисовала карточку МОНИТОРЫ, панель действий и пунктирную заглушку канваса — три пустых
  // контейнера вокруг одного сообщения. Теперь при пустых liveMonitors это единственное, что есть
  // на странице; карточка/канвас/панель действий монтируются только когда монитор хотя бы один есть.
  if (liveMonitors.length === 0) {
    page.append(el("div", { class: "empty" }, el("div", { class: "mark", text: "α" }), el("div", { class: "txt", text: tt("emptyMonitorsEngine") })));
    return;
  }

  const monRow = el("div", { class: "lay-mon-row" });
  const monGroup = groupCard("monitors", tt("monitorsTitle"), monRow);
  const gridGroup = el("div");
  const canvasMount = el("div", { class: "lay-canvas-mount" });
  const actionsRow = el("div", { class: "lay-actions" });
  page.append(monGroup, gridGroup, canvasMount, actionsRow);

  for (const live of liveMonitors) {
    if (!byId.has(live.id)) {
      const def = defaultLayoutFor(live.width, live.height, live.id);
      byId.set(live.id, def);
      lastApplied.set(live.id, cloneMonitor(def));
    }
  }

  function ensureHistory(id) {
    if (!historyById.has(id)) {
      const h = createHistory(HISTORY_LIMIT);
      h.init(toFlat(byId.get(id)));
      historyById.set(id, h);
    }
    return historyById.get(id);
  }
  ensureHistory(selectedId);

  function sendPreview(monitorId, blocks) {
    ctx.api("POST", "/layout/preview", { monitorId, blocks }).catch(() => { /* best-effort */ });
  }
  const throttledPreview = throttle(sendPreview, PREVIEW_THROTTLE_MS);

  function pushHistory(id) {
    ensureHistory(id).push(toFlat(byId.get(id)));
  }

  function buildState() {
    const layout = byId.get(selectedId);
    const flat = layout ? toFlat(layout) : { cols: 1, rows: 1, blocks: [] };
    const check = validate(flat);
    return {
      tt,
      lang,
      monitors: liveMonitors.map((m) => ({ ...m, enabled: byId.get(m.id)?.enabled !== false })),
      selectedId,
      layout: flat,
      widgets,
      selectedBlock,
      conflictIdxs: new Set(findOverlaps(flat).flat()),
      valid: check.ok,
      topBar: (ctx.appConfig && ctx.appConfig.topBar) || { enabled: false, height: 30 },
      dock: (ctx.appConfig && ctx.appConfig.dock) || { enabled: false, size: 55 },
    };
  }

  const canvas = createLayoutCanvas(canvasMount, {
    onMonitorSelect(id) { selectMonitor(id); },
    // No renderAll() here: onBlockSelect fires from inside the block/handle's own pointerdown
    // handler, before it calls setPointerCapture and wires its pointermove/pointerup listeners
    // (see layout-canvas.js wireDrag) — a synchronous full re-render at that point would replace
    // the very DOM node the gesture is about to capture (canvas.update() rebuilds .lay-grid's
    // innerHTML), silently releasing pointer capture and breaking every drag/resize before it
    // starts. The block/handle's own pointerup always ends in a commit=true onBlockMove/
    // onBlockResize call, which does renderAll() — that repaint picks up the new .sel highlight,
    // just after release instead of before.
    onBlockSelect(idx) { selectedBlock = idx; },
    onBlockMove(idx, col, row, commit) {
      mutateSelected((flat) => moveBlock(flat, idx, col, row), commit);
    },
    onBlockResize(idx, colSpan, rowSpan, commit) {
      mutateSelected((flat) => resizeBlock(flat, idx, colSpan, rowSpan), commit);
    },
    onBlockDelete(idx) { deleteBlockAt(idx); },
    onWidgetAdd(widgetId) { addWidgetBlock(widgetId); },
    onUndo() { doUndo(); },
    onRedo() { doRedo(); },
  });

  function mutateSelected(mutateFn, commit) {
    const layout = byId.get(selectedId);
    if (!layout) return;
    const nextFlat = mutateFn(toFlat(layout));
    byId.set(selectedId, fromFlat(layout, nextFlat));
    dirty.add(selectedId);
    throttledPreview(selectedId, nextFlat.blocks);
    if (commit) {
      pushHistory(selectedId);
      renderAll();
    }
  }

  function deleteBlockAt(idx) {
    const layout = byId.get(selectedId);
    if (!layout) return;
    const nextFlat = removeBlock(toFlat(layout), idx);
    byId.set(selectedId, fromFlat(layout, nextFlat));
    if (selectedBlock === idx) selectedBlock = -1;
    else if (selectedBlock > idx) selectedBlock -= 1;
    dirty.add(selectedId);
    pushHistory(selectedId);
    throttledPreview(selectedId, nextFlat.blocks);
    renderAll();
  }

  function addWidgetBlock(widgetId) {
    const layout = byId.get(selectedId);
    if (!layout) return;
    const meta = widgets.find((w) => w.id === widgetId);
    const size = meta && meta.defaultSize
      ? { colSpan: meta.defaultSize.cols || 1, rowSpan: meta.defaultSize.rows || 1 }
      : { colSpan: 1, rowSpan: 1 };
    const flat = toFlat(layout);
    const result = addBlock(flat, widgetId, size);
    if (!result.added) {
      ctx.onStatus(tt("noFreeSlot"), true);
      canvas.flashPaletteNoRoom(widgetId);
      return;
    }
    byId.set(selectedId, fromFlat(layout, result.layout));
    selectedBlock = result.index;
    dirty.add(selectedId);
    pushHistory(selectedId);
    throttledPreview(selectedId, result.layout.blocks);
    renderAll();
  }

  function doUndo() {
    const h = historyById.get(selectedId);
    if (!h || !h.canUndo()) return;
    const flat = h.undo();
    byId.set(selectedId, fromFlat(byId.get(selectedId), flat));
    selectedBlock = -1;
    dirty.add(selectedId);
    throttledPreview(selectedId, flat.blocks);
    renderAll();
  }

  function doRedo() {
    const h = historyById.get(selectedId);
    if (!h || !h.canRedo()) return;
    const flat = h.redo();
    byId.set(selectedId, fromFlat(byId.get(selectedId), flat));
    selectedBlock = -1;
    dirty.add(selectedId);
    throttledPreview(selectedId, flat.blocks);
    renderAll();
  }

  function cancelMonitor() {
    const base = lastApplied.get(selectedId);
    if (!base) return;
    byId.set(selectedId, cloneMonitor(base));
    selectedBlock = -1;
    dirty.delete(selectedId);
    const h = createHistory(HISTORY_LIMIT);
    h.init(toFlat(byId.get(selectedId)));
    historyById.set(selectedId, h);
    sendPreview(selectedId, base.blocks); // immediate, not throttled — the stage should snap back
    renderAll();
  }

  async function applyAll() {
    const allValid = [...byId.values()].every((m) => validate(toFlat(m)).ok);
    if (!allValid) {
      ctx.onStatus(tt("overlapBlocked"), true);
      return;
    }
    applying = true;
    renderAll();
    ctx.onStatus(tt("statusSaving"), false);
    try {
      const monitorsPayload = [...byId.values()].map((m) => cloneMonitor(m));
      await ctx.put({ monitors: { version: 1, monitors: monitorsPayload } });
      for (const [id, m] of byId) lastApplied.set(id, cloneMonitor(m));
      dirty.clear();
      applying = false;
      ctx.onStatus(tt("statusSaved"), false);
      renderAll();
    } catch (err) {
      applying = false;
      ctx.onStatus(tt("statusError", (err && err.message) || String(err)), true);
      renderAll();
    }
  }

  function resetToDefaultConfirm() {
    window.NNAUI.dialog({
      title: tt("resetConfirmTitle"),
      body: tt("resetConfirmBody"),
      actions: [
        { label: tt("resetConfirmCancel") },
        { label: tt("resetConfirmOk"), primary: true, onClick: () => doResetDefault() },
      ],
    });
  }

  async function doResetDefault() {
    const live = liveMonitors.find((m) => m.id === selectedId);
    const def = await ctx.fetchDefaults(selectedId).catch(
      () => defaultLayoutFor((live && live.width) || 16, (live && live.height) || 9, selectedId));
    byId.set(selectedId, { ...def, id: selectedId });
    selectedBlock = -1;
    dirty.add(selectedId);
    pushHistory(selectedId);
    throttledPreview(selectedId, def.blocks);
    renderAll();
  }

  function selectMonitor(id) {
    if (!byId.has(id) || selectedId === id) return;
    selectedId = id;
    selectedBlock = -1;
    ensureHistory(id);
    renderAll();
  }

  function toggleMonitorEnabled(id, on) {
    const layout = byId.get(id);
    if (!layout) return;
    layout.enabled = on;
    dirty.add(id);
    renderAll();
  }

  function renderMonitorRow() {
    monRow.innerHTML = "";
    for (const live of liveMonitors) {
      const layout = byId.get(live.id);
      const toggleMount = el("div");
      const card = el("div", {
        class: "lay-mon-card" + (live.id === selectedId ? " sel" : ""),
        onclick: (e) => { if (e.target.closest(".ui-toggle")) return; selectMonitor(live.id); },
      },
        el("div", { class: "t" },
          el("span", { class: "name", text: (layout && layout.name) || live.name || live.id }),
          el("span", { class: "tag", text: `${live.width}x${live.height}` })),
        el("div", { class: "row-control" }, toggleMount));
      monRow.append(card);
      window.NNAUI.toggle(toggleMount, {
        checked: layout ? layout.enabled !== false : true,
        onChange: (on) => toggleMonitorEnabled(live.id, on),
      });
    }
  }

  function gridField(labelKey, helpKey, key, min) {
    const layout = byId.get(selectedId);
    const input = el("input", { type: "number", min: min ?? 1, value: layout.grid[key] });
    const commitValue = (commitHistory) => {
      const cur = byId.get(selectedId);
      const v = Math.max(min ?? 1, Number(input.value) || (min ?? 1));
      cur.grid[key] = v;
      const flat = toFlat(cur);
      flat.blocks = flat.blocks.map((b) => clampToGrid(b, flat.cols, flat.rows));
      cur.blocks = flat.blocks;
      dirty.add(selectedId);
      throttledPreview(selectedId, flat.blocks);
      if (commitHistory) { pushHistory(selectedId); renderAll(); }
      else canvas.update(buildState());
    };
    input.addEventListener("input", () => commitValue(false));
    input.addEventListener("change", () => commitValue(true));
    return settingRow(tt(labelKey), helpKey ? tt(helpKey) : null, el("div", { class: "ui-field lay-grid-field" }, input));
  }

  function renderGridGroup() {
    gridGroup.innerHTML = "";
    const layout = byId.get(selectedId);
    if (!layout) return;
    const weights = el("input", {
      type: "text",
      value: (layout.grid.colWeights || []).join(", "),
      placeholder: "1, 1, 1",
    });
    weights.addEventListener("change", (e) => {
      const cur = byId.get(selectedId);
      const parts = e.target.value.split(",").map((s) => parseFloat(s.trim())).filter((n) => !Number.isNaN(n));
      cur.grid.colWeights = parts.length === cur.grid.cols ? parts : undefined;
      dirty.add(selectedId);
      pushHistory(selectedId);
      throttledPreview(selectedId, cur.blocks);
      renderAll();
    });
    gridGroup.append(groupCard("grid", tt("gridSection"),
      gridField("gridCols", null, "cols"),
      gridField("gridRows", null, "rows"),
      gridField("gridGap", "gridGapHelp", "gap", 0),
      gridField("gridPad", "gridPadHelp", "pad", 0),
      settingRow(tt("gridWeights"), tt("gridWeightsHelp"), el("div", { class: "ui-field" }, weights))));
  }

  function renderActions() {
    actionsRow.innerHTML = "";
    const allValid = [...byId.values()].every((m) => validate(toFlat(m)).ok);
    const h = historyById.get(selectedId);
    const isDirty = dirty.has(selectedId);
    const applyBtn = el("button", {
      class: "ui-btn primary", type: "button", text: tt("applyBtn"),
      disabled: !allValid || applying, onclick: applyAll,
    });
    const cancelBtn = el("button", {
      class: "ui-btn ghost", type: "button", text: tt("cancelBtn"),
      disabled: !isDirty, onclick: cancelMonitor,
    });
    const undoBtn = el("button", {
      class: "ui-btn ghost sm", type: "button", text: tt("undoLastBtn"),
      disabled: !h || !h.canUndo(), onclick: doUndo,
    });
    const redoBtn = el("button", {
      class: "ui-btn ghost sm", type: "button", text: tt("redoBtn"),
      disabled: !h || !h.canRedo(), onclick: doRedo,
    });
    const resetBtn = el("button", { class: "ui-btn ghost sm", type: "button", text: tt("resetDefault"), onclick: resetToDefaultConfirm });
    if (!allValid) actionsRow.append(el("div", { class: "lay-conflict-note", text: tt("overlapBlocked") }));
    actionsRow.append(applyBtn, cancelBtn, undoBtn, redoBtn, el("span", { class: "sp" }), resetBtn);
  }

  function renderAll() {
    renderMonitorRow();
    renderGridGroup();
    renderActions();
    canvas.update(buildState());
  }

  // ---- leave-page safety net: revert every unapplied preview so the desktop matches disk again
  // (brief: "при уходе со страницы без применения — тоже вернуть"). shell.js has no page-lifecycle
  // hook, so detect removal from the document instead of relying on one.
  function revertAllDirty() {
    for (const id of dirty) {
      const base = lastApplied.get(id);
      if (base) sendPreview(id, base.blocks);
    }
    dirty.clear();
  }
  const leaveObserver = new MutationObserver(() => {
    if (!page.isConnected) {
      leaveObserver.disconnect();
      window.removeEventListener("beforeunload", revertAllDirty);
      revertAllDirty();
    }
  });
  leaveObserver.observe(document.body, { childList: true, subtree: true });
  window.addEventListener("beforeunload", revertAllDirty);

  renderAll();
}
