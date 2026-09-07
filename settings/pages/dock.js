// settings/pages/dock.js — "Dock" page (ε): a macOS-style dock, driven by app.json's "dock"
// object (DockSettings in AppSettings.cs). The page always renders with sane defaults, PUTs
// `{app:{dock:{...}}}` on every change and reads the saved value straight back from
// ctx.config.app.dock afterwards, same round trip every other page in this shell uses.

import { el, groupCard, settingRow, paletteSwatchField } from "../dom.js";
import { makeScheduler } from "../api.js";

function defaultDock() {
  return {
    enabled: false, monitors: "all", size: 55,
    magnify: true, magnifyMax: 1.6,
    autoHide: false, reserveSpace: false,
    folders: [], showTrash: true, showRunning: true, pinned: [],
    style: { mode: "acrylic", color: "#0B0B0B", opacity: 0.6 },
  };
}

function normalizeHex(v) {
  return typeof v === "string" && /^#[0-9a-fA-F]{6}$/.test(v) ? v : "#0B0B0B";
}

const MODES = ["normal", "clear", "blur", "acrylic", "opaque"];

export function keywords(lang) {
  return lang === "en"
    ? ["dock", "magnify", "auto-hide", "trash", "running", "folders", "pinned"]
    : ["док", "увеличение", "автоскрытие", "корзина", "запущенные", "папки", "закреплённые"];
}

function pathListRow(t, titleKey, list, redraw) {
  const box = el("div", { class: "stack" });
  const draw = () => {
    box.innerHTML = "";
    list.forEach((path, idx) => {
      const input = el("input", { type: "text", value: path, placeholder: "C:\\path" });
      input.addEventListener("input", (e) => { list[idx] = e.target.value; redraw(); });
      const removeBtn = el("button", { class: "ui-btn sm danger", type: "button", text: t("remove") });
      removeBtn.addEventListener("click", () => { list.splice(idx, 1); draw(); redraw(); });
      box.append(el("div", { class: "field-row" }, input, removeBtn));
    });
    const addBtn = el("button", { class: "ui-btn sm", type: "button", text: t("formAdd") });
    addBtn.addEventListener("click", () => { list.push(""); draw(); redraw(); });
    box.append(addBtn);
  };
  draw();
  return el("div", { class: "ui-field" }, el("label", { text: t(titleKey) }), box);
}

export function render(container, ctx) {
  const { t } = ctx;
  const saved = ctx.config.app.dock;
  const dock = { ...defaultDock(), ...(saved || {}) };
  dock.style = { ...defaultDock().style, ...dock.style };
  dock.folders = Array.isArray(dock.folders) ? [...dock.folders] : [];
  dock.pinned = Array.isArray(dock.pinned) ? [...dock.pinned] : [];

  const scheduleSave = makeScheduler(async () => {
    ctx.onStatus(t("statusSaving"), false);
    try {
      await ctx.put({ app: { dock } });
      ctx.config.app.dock = dock;
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  }, 300);

  const page = el("div", { class: "page-wide" });
  container.append(page);

  const enabledMount = el("div");
  const monitorsMount = el("div");
  const sizeMount = el("div");
  const general = groupCard("general", t("dockGeneralSection"),
    settingRow(t("dockEnabled"), null, enabledMount),
    settingRow(t("topbarMonitors"), null, monitorsMount),
    settingRow(t("dockSize"), null, sizeMount));

  window.NNAUI.toggle(enabledMount, { checked: !!dock.enabled, onChange: (on) => { dock.enabled = on; scheduleSave(); } });
  window.NNAUI.select(monitorsMount, {
    value: dock.monitors,
    options: [{ value: "all", label: t("monitorsAll") }, { value: "primary", label: t("monitorsPrimary") }],
    onChange: (v) => { dock.monitors = v; scheduleSave(); },
  });
  window.NNAUI.segmented(sizeMount, { value: String(dock.size), items: [{ value: "34", label: "34" }, { value: "55", label: "55" }, { value: "89", label: "89" }], onChange: (v) => { dock.size = Number(v); scheduleSave(); } });

  const magnifyMount = el("div");
  const magnifyMaxMount = el("div");
  const magnify = groupCard("magnify", t("dockMagnifySection"),
    settingRow(t("dockMagnify"), null, magnifyMount),
    settingRow(t("dockMagnifyMax"), null, magnifyMaxMount));
  window.NNAUI.toggle(magnifyMount, { checked: !!dock.magnify, onChange: (on) => { dock.magnify = on; scheduleSave(); } });
  window.NNAUI.slider(magnifyMaxMount, { min: 1, max: 2, step: 0.05, value: dock.magnifyMax, format: (v) => v.toFixed(2) + "x", onInput: (v) => { dock.magnifyMax = v; scheduleSave(); } });

  const autoHideMount = el("div");
  const reserveMount = el("div");
  const runningMount = el("div");
  const trashMount = el("div");
  const behaviour = groupCard("behaviour", t("dockBehaviourSection"),
    settingRow(t("topbarAutoHide"), null, autoHideMount),
    settingRow(t("topbarReserveSpace"), null, reserveMount),
    settingRow(t("dockShowRunning"), null, runningMount),
    settingRow(t("dockShowTrash"), null, trashMount));
  window.NNAUI.toggle(autoHideMount, { checked: !!dock.autoHide, onChange: (on) => { dock.autoHide = on; scheduleSave(); } });
  window.NNAUI.toggle(reserveMount, { checked: !!dock.reserveSpace, onChange: (on) => { dock.reserveSpace = on; scheduleSave(); } });
  window.NNAUI.toggle(runningMount, { checked: !!dock.showRunning, onChange: (on) => { dock.showRunning = on; scheduleSave(); } });
  window.NNAUI.toggle(trashMount, { checked: !!dock.showTrash, onChange: (on) => { dock.showTrash = on; scheduleSave(); } });

  const items = groupCard("items", t("dockItemsSection"),
    pathListRow(t, "dockFolders", dock.folders, scheduleSave),
    pathListRow(t, "dockPinned", dock.pinned, scheduleSave));

  const modeMount = el("div");
  const colorMount = el("div");
  const opacityMount = el("div");
  const style = groupCard("style", t("surfaceStyleLabel"),
    settingRow(t("surfaceMode"), null, modeMount),
    settingRow(t("surfaceColor"), null, colorMount),
    settingRow(t("surfaceOpacity"), null, opacityMount));
  window.NNAUI.segmented(modeMount, { value: dock.style.mode, items: MODES.map((m) => ({ value: m, label: t("mode_" + m) })), onChange: (v) => { dock.style.mode = v; scheduleSave(); } });
  paletteSwatchField(colorMount, t, normalizeHex(dock.style.color), (v) => { dock.style.color = v; scheduleSave(); });
  window.NNAUI.slider(opacityMount, { min: 0, max: 1, step: 0.05, value: dock.style.opacity, format: (v) => v.toFixed(2), onInput: (v) => { dock.style.opacity = v; scheduleSave(); } });

  page.append(general, magnify, behaviour, items, style);
}
