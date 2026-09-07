// settings/pages/topbar.js — "Top bar" page (δ): our own always-on-top bar (a macOS-like menu
// bar), driven by app.json's topBar object (TopBarSettings in AppSettings.cs). New page: the old
// ζ "taskbar" tab used to render this inline via a plain up/down table (see taskbar-tab.js); this
// page replaces that editor with drag-and-drop module ordering across the three zones and
// NNAUI-driven controls, matching the owner's macOS-System-Settings brief. taskbar.js (the
// Windows-taskbar page) still owns Preset/Windows sections; this page owns app.json's topBar only.

import { el, groupCard, settingRow, paletteSwatchField } from "../dom.js";
import { iconEl } from "../icons.js";
import { makeScheduler } from "../api.js";

const MODES = ["normal", "clear", "blur", "acrylic", "opaque"];
const MODULE_IDS = ["nna", "date", "clock", "weather", "stats", "media", "planner", "volume", "network", "battery", "layout", "control"];
const SIDES = ["left", "center", "right"];

export function keywords(lang) {
  return lang === "en"
    ? ["top bar", "menu bar", "modules", "monitors", "height", "style", "auto-hide"]
    : ["верхняя строка", "меню", "модули", "мониторы", "высота", "стиль", "автоскрытие"];
}

function defaultTopBar() {
  return {
    enabled: false, monitors: "all", height: 30,
    style: { mode: "acrylic", color: "#0B0B0B", opacity: 0.6 },
    autoHide: false, reserveSpace: true,
    modules: [
      { id: "nna", side: "left" }, { id: "date", side: "left" },
      { id: "clock", side: "center" },
      { id: "planner", side: "right" }, { id: "media", side: "right" }, { id: "weather", side: "right" }, { id: "stats", side: "right" },
    ],
  };
}

function normalizeHex(v) {
  return typeof v === "string" && /^#[0-9a-fA-F]{6}$/.test(v) ? v : "#0B0B0B";
}

let keySeq = 0;
function withKeys(modules) {
  return (modules || []).map((m) => ({ ...m, _key: "m" + (keySeq++) }));
}
function stripKeys(modules) {
  return modules.map(({ _key, ...m }) => m);
}

export function render(container, ctx) {
  const { t } = ctx;
  let topBar = { ...defaultTopBar(), ...(ctx.config.app.topBar || {}) };
  topBar.style = { ...defaultTopBar().style, ...topBar.style };
  topBar.modules = withKeys(Array.isArray(topBar.modules) ? topBar.modules : defaultTopBar().modules);

  const scheduleSave = makeScheduler(save, 300);
  async function save() {
    ctx.onStatus(t("statusSaving"), false);
    try {
      const payload = { ...topBar, modules: stripKeys(topBar.modules) };
      await ctx.put({ app: { topBar: payload } });
      ctx.config.app.topBar = payload;
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  }

  const page = el("div", { class: "page-wide" });
  container.append(page);

  // ── general ────────────────────────────────────────────────────────────
  const enabledMount = el("div");
  const monitorsMount = el("div");
  const heightMount = el("div");
  const general = groupCard("general", t("topbarGeneralSection"),
    settingRow(t("topbarEnabled"), null, enabledMount),
    settingRow(t("topbarMonitors"), null, monitorsMount),
    settingRow(t("topbarHeight"), null, heightMount));

  window.NNAUI.toggle(enabledMount, { checked: !!topBar.enabled, onChange: (on) => { topBar.enabled = on; scheduleSave(); } });
  window.NNAUI.select(monitorsMount, {
    value: topBar.monitors,
    options: [{ value: "all", label: t("monitorsAll") }, { value: "primary", label: t("monitorsPrimary") }],
    onChange: (v) => { topBar.monitors = v; scheduleSave(); },
  });
  window.NNAUI.slider(heightMount, { min: 21, max: 55, step: 1, value: topBar.height, format: (v) => Math.round(v) + " px", onInput: (v) => { topBar.height = v; scheduleSave(); } });

  // ── style ─────────────────────────────────────────────────────────────
  const modeMount = el("div");
  const colorMount = el("div");
  const opacityMount = el("div");

  const style = groupCard("style", t("surfaceStyleLabel"),
    settingRow(t("surfaceMode"), null, modeMount),
    settingRow(t("surfaceColor"), null, colorMount),
    settingRow(t("surfaceOpacity"), null, opacityMount));

  paletteSwatchField(colorMount, t, normalizeHex(topBar.style.color), (v) => { topBar.style.color = v; scheduleSave(); });

  window.NNAUI.segmented(modeMount, { value: topBar.style.mode, items: MODES.map((m) => ({ value: m, label: t("mode_" + m) })), onChange: (v) => { topBar.style.mode = v; scheduleSave(); } });
  window.NNAUI.slider(opacityMount, { min: 0, max: 1, step: 0.05, value: topBar.style.opacity, format: (v) => v.toFixed(2), onInput: (v) => { topBar.style.opacity = v; scheduleSave(); } });

  // ── behaviour ────────────────────────────────────────────────────────
  const autoHideMount = el("div");
  const reserveMount = el("div");
  const behaviour = groupCard("behaviour", t("topbarBehaviourSection"),
    settingRow(t("topbarAutoHide"), null, autoHideMount),
    settingRow(t("topbarReserveSpace"), null, reserveMount));
  window.NNAUI.toggle(autoHideMount, { checked: !!topBar.autoHide, onChange: (on) => { topBar.autoHide = on; scheduleSave(); } });
  window.NNAUI.toggle(reserveMount, { checked: !!topBar.reserveSpace, onChange: (on) => { topBar.reserveSpace = on; scheduleSave(); } });

  // ── modules: drag-and-drop across three zones ───────────────────────
  const modulesBody = el("div", { class: "dnd-board" });
  const modulesCard = groupCard("modules", t("topbarModules"), modulesBody);

  function redrawModules() {
    modulesBody.innerHTML = "";
    for (const side of SIDES) {
      const col = el("div", { class: "dnd-col", "data-side": side }, el("div", { class: "dnd-col-title", text: t("side" + side[0].toUpperCase() + side.slice(1)) }));
      for (const mod of topBar.modules.filter((m) => (m.side || "right") === side)) {
        col.append(chip(mod));
      }
      col.append(addRow(side));
      col.addEventListener("dragover", (e) => { e.preventDefault(); });
      col.addEventListener("drop", (e) => { e.preventDefault(); dropInto(side, null); });
      modulesBody.append(col);
    }
  }

  function chip(mod) {
    const el2 = el("div", { class: "dnd-chip", draggable: "true", "data-key": mod._key },
      iconEl("drag", "util"),
      el("span", { class: "t", text: t("module_" + mod.id) || mod.id }));
    const removeBtn = el("button", { class: "dnd-remove", type: "button", text: "×" });
    removeBtn.addEventListener("click", () => { topBar.modules = topBar.modules.filter((m) => m._key !== mod._key); redrawModules(); scheduleSave(); });
    el2.append(removeBtn);
    el2.addEventListener("dragstart", (e) => { e.dataTransfer.setData("text/plain", mod._key); e.dataTransfer.effectAllowed = "move"; });
    el2.addEventListener("dragover", (e) => { e.preventDefault(); e.stopPropagation(); });
    el2.addEventListener("drop", (e) => {
      e.preventDefault(); e.stopPropagation();
      const side = el2.closest(".dnd-col").dataset.side;
      dropInto(side, mod._key);
    });
    return el2;
  }

  function addRow(side) {
    const select = el("select", { class: "dnd-add-select" });
    for (const id of MODULE_IDS) select.append(el("option", { value: id, text: t("module_" + id) || id }));
    const addBtn = el("button", { class: "ui-btn sm", type: "button", text: "+" });
    addBtn.addEventListener("click", () => {
      topBar.modules.push({ id: select.value, side, _key: "m" + (keySeq++) });
      redrawModules();
      scheduleSave();
    });
    return el("div", { class: "dnd-add" }, select, addBtn);
  }

  function dropInto(side, beforeKey) {
    const key = window.__nnaDragKey;
    if (!key) return;
    const idx = topBar.modules.findIndex((m) => m._key === key);
    if (idx === -1) return;
    const [item] = topBar.modules.splice(idx, 1);
    item.side = side;
    if (beforeKey) {
      const insertIdx = topBar.modules.findIndex((m) => m._key === beforeKey);
      topBar.modules.splice(insertIdx === -1 ? topBar.modules.length : insertIdx, 0, item);
    } else {
      topBar.modules.push(item);
    }
    redrawModules();
    scheduleSave();
  }

  // dataTransfer.getData in "drop" only works reliably for the same-document drag in some
  // browsers when read from the drop handler directly; WebView2/Chromium supports it fine, but
  // we also track the key on window as a defensive fallback (dragstart always fires first).
  modulesBody.addEventListener("dragstart", (e) => { window.__nnaDragKey = e.target.closest(".dnd-chip")?.dataset.key || null; });

  redrawModules();
  page.append(general, style, behaviour, modulesCard);
}
