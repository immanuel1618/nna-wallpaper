import {
  clampToGrid, moveBlock, resizeBlock, findOverlaps, validateLayout, findFreeSlot, defaultLayoutFor,
} from "./layout-model.js";

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") node.className = v;
      else if (k === "text") node.textContent = v;
      else if (k.startsWith("on") && typeof v === "function") node.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) node.setAttribute(k, v === true ? "" : v);
    }
  }
  for (const c of children) {
    if (c === null || c === undefined) continue;
    node.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return node;
}

function switchEl(checked, onToggle) {
  const sw = el("div", { class: "sw" + (checked ? " on" : ""), role: "switch", tabindex: "0" });
  const toggle = () => { const on = !sw.classList.contains("on"); sw.classList.toggle("on", on); onToggle(on); };
  sw.addEventListener("click", (e) => { e.stopPropagation(); toggle(); });
  return sw;
}

/**
 * Mounts the "monitors and layout" tab.
 * ctx = { t, put(body), fetchDefaults(monitorId), liveMonitors, monitorsConfig, widgets, onStatus }
 */
export function mountLayoutTab(container, ctx) {
  const { t } = ctx;
  container.innerHTML = "";

  // id -> full monitor layout record (id, name, enabled, grid, blocks); seeded from monitors.json,
  // filled in with the built-in default for any live monitor that has no saved entry yet.
  const byId = new Map();
  for (const m of ctx.monitorsConfig?.monitors || []) byId.set(m.id, cloneMonitor(m));

  let lastSaved = null; // snapshot of byId.get(selectedId) as last confirmed written to disk
  let selectedId = null;
  let selectedBlock = -1;
  let saveTimer = null;

  const monList = el("div", { class: "mon-list" });
  const main = el("div", { class: "layout-main" });
  container.append(el("div", { class: "layout-page" }, monList, main));

  const liveMonitors = ctx.liveMonitors || [];
  if (liveMonitors.length === 0) {
    monList.append(el("div", { class: "empty" }, el("div", { class: "mark", text: "α" }), el("div", { class: "txt", text: t("emptyMonitors") })));
  }

  for (const live of liveMonitors) {
    if (!byId.has(live.id)) byId.set(live.id, defaultLayoutFor(live.width, live.height, live.id));
    const layout = byId.get(live.id);
    const item = el("div", { class: "card mon-item", onclick: () => select(live.id) },
      el("div", { class: "t" },
        el("div", null, el("div", { class: "t", text: layout.name || live.name || live.id }), el("div", { class: "s tag", text: live.width + "x" + live.height })),
        switchEl(layout.enabled !== false, (on) => { layout.enabled = on; scheduleSave(); })));
    item.dataset.monitorId = live.id;
    monList.append(item);
  }

  function highlightSelected() {
    for (const child of monList.children) child.classList.toggle("on", child.dataset.monitorId === selectedId);
  }

  function select(id) {
    selectedId = id;
    selectedBlock = -1;
    lastSaved = cloneMonitor(byId.get(id));
    highlightSelected();
    renderMain();
  }

  function scheduleSave() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(commit, 300);
  }

  async function commit() {
    const layout = byId.get(selectedId);
    if (!layout) return;
    const check = validateLayout({ cols: layout.grid.cols, rows: layout.grid.rows, blocks: layout.blocks });
    renderMain(); // refresh red highlighting immediately
    if (!check.ok) {
      ctx.onStatus(t("overlapWarning"), true);
      return;
    }
    ctx.onStatus(t("statusSaving"), false);
    try {
      await ctx.put({ monitors: { version: 1, monitors: [...byId.values()] } });
      lastSaved = cloneMonitor(layout);
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) {
      ctx.onStatus(t("statusError", err.message || err), true);
    }
  }

  function renderMain() {
    main.innerHTML = "";
    if (!selectedId) {
      main.append(el("div", { class: "empty" }, el("div", { class: "txt", text: t("emptyMonitors") })));
      return;
    }
    const layout = byId.get(selectedId);
    const live = liveMonitors.find((m) => m.id === selectedId) || { width: 16, height: 9 };

    main.append(renderToolbar(layout));
    main.append(renderAddRow(layout));
    const wrap = el("div", { class: "canvas-wrap" });
    const canvas = buildCanvas(layout, live);
    wrap.append(canvas);
    main.append(wrap);
    main.append(el("div", { class: "rowflex" },
      mkBtn(t("resetDefault"), async () => {
        const def = await ctx.fetchDefaults(selectedId).catch(() => defaultLayoutFor(live.width, live.height, selectedId));
        byId.set(selectedId, { ...def, id: selectedId });
        renderMain();
        commit();
      }),
      mkBtn(t("undo"), () => {
        if (lastSaved) byId.set(selectedId, cloneMonitor(lastSaved));
        renderMain();
      })));
  }

  function renderToolbar(layout) {
    const numField = (labelKey, key, min) => {
      const input = el("input", { type: "number", min: min ?? 1, value: layout.grid[key] });
      input.addEventListener("input", (e) => {
        layout.grid[key] = Math.max(min ?? 1, Number(e.target.value) || (min ?? 1));
        for (let i = 0; i < layout.blocks.length; i++) {
          layout.blocks[i] = clampToGrid(layout.blocks[i], layout.grid.cols, layout.grid.rows);
        }
        renderMain();
        scheduleSave();
      });
      return el("div", { class: "field" }, el("label", { text: t(labelKey) }), input);
    };
    const weights = el("input", {
      type: "text",
      value: (layout.grid.colWeights || []).join(", "),
      placeholder: "1, 1, 1",
    });
    weights.addEventListener("change", (e) => {
      const parts = e.target.value.split(",").map((s) => parseFloat(s.trim())).filter((n) => !Number.isNaN(n));
      layout.grid.colWeights = parts.length === layout.grid.cols ? parts : undefined;
      renderMain();
      scheduleSave();
    });
    return el("div", { class: "grid-toolbar" },
      numField("gridCols", "cols"),
      numField("gridRows", "rows"),
      numField("gridGap", "gap", 0),
      numField("gridPad", "pad", 0),
      el("div", { class: "field", style: "flex:1;min-width:160px" }, el("label", { text: t("gridWeights") }), weights));
  }

  function renderAddRow(layout) {
    const select = el("select");
    for (const w of ctx.widgets || []) select.append(el("option", { value: w.id }, (w.name || w.id) + " (" + w.id + ")"));
    if (!ctx.widgets || ctx.widgets.length === 0) select.append(el("option", { value: "", disabled: true }, t("emptyWidgets")));
    const addBtn = mkBtn(t("addBlock"), () => {
      const id = select.value;
      if (!id) return;
      const meta = (ctx.widgets || []).find((w) => w.id === id);
      const size = meta?.defaultSize || { cols: 1, rows: 1 };
      const slot = findFreeSlot({ blocks: layout.blocks }, layout.grid.cols, layout.grid.rows, { colSpan: size.cols, rowSpan: size.rows });
      if (!slot) { ctx.onStatus(t("noFreeSlot"), true); return; }
      layout.blocks.push({ widget: id, col: slot.col, row: slot.row, colSpan: size.cols, rowSpan: size.rows });
      renderMain();
      scheduleSave();
    });
    return el("div", { class: "add-row" }, select, addBtn);
  }

  function buildCanvas(layout, live) {
    const cols = layout.grid.cols, rows = layout.grid.rows;
    const aspect = (live.width || 16) / (live.height || 9);
    const maxW = 760, maxH = 480;
    let w = maxW, h = w / aspect;
    if (h > maxH) { h = maxH; w = h * aspect; }
    const canvas = el("div", { class: "grid-canvas", style: `width:${w}px;height:${h}px` });

    const weights = (layout.grid.colWeights && layout.grid.colWeights.length === cols) ? layout.grid.colWeights : new Array(cols).fill(1);
    const total = weights.reduce((a, b) => a + b, 0) || 1;
    const colBoundaries = [0];
    weights.forEach((wt) => colBoundaries.push(colBoundaries[colBoundaries.length - 1] + (wt / total) * w));
    const rowHeight = h / rows;

    const overlapSet = new Set(findOverlaps({ blocks: layout.blocks }).flat());

    layout.blocks.forEach((block, idx) => {
      const left = colBoundaries[block.col - 1] ?? 0;
      const right = colBoundaries[Math.min(block.col - 1 + block.colSpan, cols)] ?? w;
      const top = (block.row - 1) * rowHeight;
      const height = block.rowSpan * rowHeight;

      const box = el("div", {
        class: "grid-block" + (overlapSet.has(idx) ? " bad" : "") + (selectedBlock === idx ? " sel" : ""),
        style: `left:${left}px;top:${top}px;width:${right - left}px;height:${height}px`,
      });
      const delBtn = el("button", { class: "del", type: "button", title: t("deleteBlock"), text: "×" });
      delBtn.addEventListener("click", (e) => { e.stopPropagation(); layout.blocks.splice(idx, 1); renderMain(); scheduleSave(); });
      box.append(el("div", { class: "name", text: block.widget }), delBtn);
      box.append(el("div", { class: "handle handle-e" }), el("div", { class: "handle handle-s" }), el("div", { class: "handle handle-se" }));

      wireDrag(box, block, idx, layout, colBoundaries, rowHeight, w, h);
      canvas.append(box);
    });

    canvas.tabIndex = 0;
    canvas.addEventListener("keydown", (e) => {
      if (e.key === "Delete" && selectedBlock >= 0 && selectedBlock < layout.blocks.length) {
        layout.blocks.splice(selectedBlock, 1);
        selectedBlock = -1;
        renderMain();
        scheduleSave();
      }
    });
    return canvas;
  }

  function wireDrag(box, block, idx, layout, colBoundaries, rowHeight, canvasW, canvasH) {
    const cols = layout.grid.cols, rows = layout.grid.rows;
    const colAt = (x) => {
      for (let i = 0; i < cols; i++) if (x >= colBoundaries[i] && x < colBoundaries[i + 1]) return i + 1;
      return x < colBoundaries[0] ? 1 : cols;
    };
    const rowAt = (y) => Math.min(rows, Math.max(1, Math.floor(y / rowHeight) + 1));

    box.addEventListener("pointerdown", (e) => {
      if (e.target.classList.contains("handle")) return;
      e.stopPropagation();
      selectedBlock = idx;
      box.setPointerCapture(e.pointerId);
      box.classList.add("sel");
      const startX = e.clientX, startY = e.clientY;
      const startCol = block.col, startRow = block.row;
      const onMove = (ev) => {
        const dx = ev.clientX - startX, dy = ev.clientY - startY;
        const targetLeft = (colBoundaries[startCol - 1] ?? 0) + dx;
        const targetTop = (startRow - 1) * rowHeight + dy;
        const col = colAt(Math.max(0, Math.min(canvasW - 1, targetLeft)));
        const row = rowAt(Math.max(0, Math.min(canvasH - 1, targetTop)));
        Object.assign(block, clampToGrid({ ...block, col, row }, cols, rows));
        renderMain();
      };
      const onUp = () => {
        box.removeEventListener("pointermove", onMove);
        box.removeEventListener("pointerup", onUp);
        scheduleSave();
      };
      box.addEventListener("pointermove", onMove);
      box.addEventListener("pointerup", onUp, { once: true });
    });

    const handle = (cls, growCol, growRow) => {
      const h = box.querySelector("." + cls);
      h.addEventListener("pointerdown", (e) => {
        e.stopPropagation();
        h.setPointerCapture(e.pointerId);
        const startX = e.clientX, startY = e.clientY;
        const startColSpan = block.colSpan, startRowSpan = block.rowSpan;
        const onMove = (ev) => {
          const dx = ev.clientX - startX, dy = ev.clientY - startY;
          let colSpan = startColSpan, rowSpan = startRowSpan;
          if (growCol) {
            const rightEdge = (colBoundaries[block.col - 1] ?? 0) + (colBoundaries[block.col - 1 + startColSpan] - colBoundaries[block.col - 1]) + dx;
            colSpan = Math.max(1, colAt(Math.max(0, Math.min(colBoundaries[cols], rightEdge))) - block.col + 1);
          }
          if (growRow) {
            const bottomEdge = (block.row - 1) * rowHeight + startRowSpan * rowHeight + dy;
            rowSpan = Math.max(1, rowAt(Math.max(0, Math.min(canvasH, bottomEdge))) - block.row + 1);
          }
          Object.assign(block, clampToGrid({ ...block, colSpan, rowSpan }, cols, rows));
          renderMain();
        };
        const onUp = () => {
          h.removeEventListener("pointermove", onMove);
          h.removeEventListener("pointerup", onUp);
          scheduleSave();
        };
        h.addEventListener("pointermove", onMove);
        h.addEventListener("pointerup", onUp, { once: true });
      });
    };
    handle("handle-e", true, false);
    handle("handle-s", false, true);
    handle("handle-se", true, true);
  }

  function mkBtn(label, onClick) {
    return el("button", { class: "btn", type: "button", text: label, onclick: onClick });
  }

  if (liveMonitors.length > 0) select(liveMonitors[0].id);
}

function cloneMonitor(m) {
  return {
    id: m.id,
    name: m.name,
    enabled: m.enabled !== false,
    grid: { ...m.grid, colWeights: m.grid.colWeights ? [...m.grid.colWeights] : undefined },
    blocks: (m.blocks || []).map((b) => ({ ...b })),
  };
}
