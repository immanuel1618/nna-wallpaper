// settings/pages/blocks.js — "Blocks" page (β), stage 5 round 2 (owner decision D17: cards with
// previews, click opens a block page with grouped settings and plain-language help).
//
// Two views, switched through ctx.selection/ctx.setSelection like the rest of the shell:
//  - grid (selection is empty/unknown): one card per installed widget (minus the internal
//    "_test-page" fixture) — preview image, icon + name, one-line description, a toggle that adds
//    or removes the block from the desktop layout, and a small "on N monitors" readout.
//  - block page (selection === a widget id): breadcrumb + back, a large preview, and the widget's
//    settings grouped into cards per widget.json's `groups[]`, each row built from NNAUI controls
//    with the field's `help` text underneath the label. "launch" and "events" additionally get a
//    second tab that holds the old hand-rolled launch.json/events.json editors (see forms.js) —
//    the settings groups only cover the widget's own settings[] (grayIcons/compact, perPage/...).
//
// The desktop toggle writes directly into ctx.config.monitors.monitors[].blocks (see
// AppSettings.cs MonitorLayout/BlockSpec) rather than a separate "enabled" flag — this page has no
// monitor picker, so "the selected/main monitor" from the owner's brief is simplified to the
// first monitor in monitors.monitors (same convention LayoutResolver.cs uses for its own
// fallback). Placement uses layout-model.js's findFreeSlot at the widget's defaultSize; removing
// a block that appears more than once on the layout asks for confirmation first.

import { renderLaunchForm, renderEventsForm } from "../forms.js";
import { el, groupCard, settingRow } from "../dom.js";
import { iconEl } from "../icons.js";
import { makeScheduler } from "../api.js";
import { findFreeSlot } from "../layout-model.js";

// Page-local strings (per the worktree brief: settings/i18n.js is owned by another agent this
// round — new copy for this page lives here instead). tt() falls back to ctx.t() so existing
// generic keys (save, remove, statusSaving, formAdd, ...) keep working unchanged.
const S = {
  ru: {
    onDesktopCount: "на столе: {0}",
    onOneMonitor: "на 1 мониторе",
    notOnDesktop: "не на столе",
    tabSettings: "Настройки",
    tabLaunchItems: "Пункты запуска",
    tabEventsItems: "События",
    showOnDesktop: "Показать на столе",
    confirmRemoveTitle: "Убрать блок?",
    confirmRemoveBody: "Этот блок встречается на раскладке {0} раз. Убрать все вхождения?",
    cancel: "Отмена",
    noMonitor: "В раскладке нет ни одного монитора",
    noFreeSlotShort: "На раскладке нет места для этого блока",
    noSettings: "У этого блока нет настроек",
  },
  en: {
    onDesktopCount: "on desktop: {0}",
    onOneMonitor: "on 1 monitor",
    notOnDesktop: "not on the desktop",
    tabSettings: "Settings",
    tabLaunchItems: "Launch items",
    tabEventsItems: "Events",
    showOnDesktop: "Show on desktop",
    confirmRemoveTitle: "Remove this block?",
    confirmRemoveBody: "This block appears {0} times in the layout. Remove every occurrence?",
    cancel: "Cancel",
    noMonitor: "The layout has no monitor",
    noFreeSlotShort: "No room for this block in the layout",
    noSettings: "This block has no settings",
  },
};

export function keywords(lang) {
  return lang === "en"
    ? ["widgets", "blocks", "launch", "events", "groups", "items", "preview", "desktop"]
    : ["виджеты", "блоки", "запуск", "события", "группы", "пункты", "превью", "стол"];
}

function loc(value, lang) {
  if (!value) return "";
  if (typeof value === "string") return value;
  return value[lang] || value.ru || value.en || "";
}

// blocks.css is specific to this page — loaded on demand instead of folding it into the shared
// design-system.css bundle every other page pays for.
function ensureStylesheet() {
  if (document.getElementById("blocks-css")) return;
  const link = document.createElement("link");
  link.id = "blocks-css";
  link.rel = "stylesheet";
  link.href = "blocks.css";
  document.head.appendChild(link);
}

function previewUrl(widget) {
  return "/widgets/" + encodeURIComponent(widget.id) + "/preview.png";
}

function monitorsList(ctx) {
  return (ctx.config.monitors && ctx.config.monitors.monitors) || [];
}

function countOnDesktop(ctx, id) {
  let n = 0;
  for (const m of monitorsList(ctx)) {
    for (const b of m.blocks || []) if (b.widget === id) n++;
  }
  return n;
}

async function saveMonitors(ctx, monitors) {
  ctx.onStatus(ctx.t("statusSaving"), false);
  try {
    await ctx.put({ monitors: { version: (ctx.config.monitors && ctx.config.monitors.version) || 1, monitors } });
    ctx.config.monitors = ctx.config.monitors || {};
    ctx.config.monitors.monitors = monitors;
    ctx.onStatus(ctx.t("statusSaved"), false);
    return true;
  } catch (err) {
    ctx.onStatus(ctx.t("statusError", err.message), true);
    return false;
  }
}

// Wraps NNAUI.dialog (which has no promise-based confirm helper) into one. Also listens for
// Escape itself since the dialog's own Escape handler just closes the overlay without invoking
// either action.
function confirmDialog(tt, title, body, confirmLabel) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (v) => {
      if (done) return;
      done = true;
      document.removeEventListener("keydown", onEsc, true);
      resolve(v);
    };
    const onEsc = (e) => { if (e.key === "Escape") finish(false); };
    document.addEventListener("keydown", onEsc, true);
    window.NNAUI.dialog({
      title,
      body,
      actions: [
        { label: tt("cancel"), onClick: () => finish(false) },
        { label: confirmLabel, primary: true, onClick: () => finish(true) },
      ],
    });
  });
}

async function setOnDesktop(ctx, tt, widget, turnOn) {
  const monitors = monitorsList(ctx);
  if (turnOn) {
    const target = monitors[0];
    if (!target) { ctx.onStatus(tt("noMonitor"), true); return false; }
    target.blocks = target.blocks || [];
    const size = widget.defaultSize || { cols: 1, rows: 1 };
    const slot = findFreeSlot({ blocks: target.blocks }, target.grid.cols, target.grid.rows, { colSpan: size.cols, rowSpan: size.rows });
    if (!slot) { ctx.onStatus(tt("noFreeSlotShort"), true); return false; }
    target.blocks.push({ widget: widget.id, col: slot.col, row: slot.row, colSpan: size.cols, rowSpan: size.rows });
  } else {
    const count = countOnDesktop(ctx, widget.id);
    if (count > 1) {
      const confirmed = await confirmDialog(tt, tt("confirmRemoveTitle"), tt("confirmRemoveBody", count), ctx.t("remove"));
      if (!confirmed) return false;
    }
    for (const m of monitors) m.blocks = (m.blocks || []).filter((b) => b.widget !== widget.id);
  }
  return saveMonitors(ctx, monitors);
}

function desktopCountLabel(tt, count) {
  if (count <= 0) return tt("notOnDesktop");
  if (count === 1) return tt("onOneMonitor");
  return tt("onDesktopCount", count);
}

export function render(container, ctx) {
  ensureStylesheet();
  const tt = (key, ...args) => {
    const dict = S[ctx.lang] || S.ru;
    let s = Object.prototype.hasOwnProperty.call(dict, key) ? dict[key] : ctx.t(key);
    args.forEach((a, i) => { s = s.replace(`{${i}}`, a); });
    return s;
  };

  container.innerHTML = "";
  const widgets = (ctx.widgets || []).filter((w) => !String(w.id).startsWith("_"));
  const selected = ctx.selection && widgets.find((w) => w.id === ctx.selection);

  if (selected) renderBlockPage(container, ctx, tt, selected);
  else { ctx.setSelection(null); renderGrid(container, ctx, tt, widgets); }
}

// ── grid of cards ──────────────────────────────────────────────────────────────────────────────

function renderGrid(container, ctx, tt, widgets) {
  if (!widgets.length) {
    container.append(el("div", { class: "empty" }, el("div", { class: "txt", text: ctx.t("emptyWidgets") })));
    return;
  }
  const grid = el("div", { class: "blk-grid" });
  for (const w of widgets) grid.append(widgetCard(container, ctx, tt, w));
  container.append(grid);
}

function widgetCard(container, ctx, tt, w) {
  const name = w.name || w.id;
  const desc = loc(w.description, ctx.lang);
  const count = countOnDesktop(ctx, w.id);

  const swMount = el("div", { class: "blk-sw" });
  const card = el("div", { class: "blk-card", tabindex: "0", role: "button", "data-widget": w.id },
    el("div", { class: "blk-thumb" }, el("img", { src: previewUrl(w), alt: "", loading: "lazy" })),
    el("div", { class: "blk-body" },
      el("div", { class: "blk-head" }, iconEl(w.icon || "blocks"), el("div", { class: "blk-name", text: name }), swMount),
      desc ? el("div", { class: "blk-desc", text: desc }) : null,
      el("div", { class: "blk-count", text: desktopCountLabel(tt, count) })));

  let swCtl;
  swCtl = window.NNAUI.toggle(swMount, {
    checked: count > 0,
    onChange: async (on) => {
      const ok = await setOnDesktop(ctx, tt, w, on);
      if (!ok) { swCtl.set(!on); return; }
      render(container, ctx);
    },
  });

  // ctx.selection is a plain snapshot taken when shell.js built this ctx object — setSelection()
  // only updates shell.js's own state for the *next* time it builds a ctx, so this in-page
  // navigation also has to poke ctx.selection directly or render(container, ctx) right below
  // would just redraw the grid it was already showing.
  const open = () => { ctx.setSelection(w.id); ctx.selection = w.id; render(container, ctx); };
  card.addEventListener("click", (e) => { if (!swMount.contains(e.target)) open(); });
  card.addEventListener("keydown", (e) => {
    if (swMount.contains(e.target)) return;
    if (e.key === "Enter" || e.key === " ") { e.preventDefault(); open(); }
  });
  return card;
}

// ── block detail page ─────────────────────────────────────────────────────────────────────────

function renderBlockPage(container, ctx, tt, widget) {
  const isLaunch = widget.id === "launch";
  const isEvents = widget.id === "events";
  let activeTab = "settings";

  const back = () => { ctx.setSelection(null); ctx.selection = null; render(container, ctx); };
  const crumb = el("div", { class: "blk-crumb" },
    el("button", { class: "blk-back", type: "button", onclick: back }, "← " + ctx.t("tab_widgets")),
    el("span", { class: "sep", text: "/" }),
    el("span", { text: widget.name || widget.id }));

  const count = countOnDesktop(ctx, widget.id);
  const hero = el("div", { class: "blk-hero" },
    el("img", { src: previewUrl(widget), alt: "" }),
    el("div", { class: "blk-hero-meta" },
      el("div", { class: "blk-count", text: desktopCountLabel(tt, count) }),
      el("button", {
        class: "btn sm", type: "button", text: tt("showOnDesktop"),
        onclick: () => { location.hash = "#layout?select=" + encodeURIComponent(widget.id); },
      })));

  const content = el("div", { class: "blk-content" });
  const bodyWrap = el("div", { class: "blk-body-wrap" });

  let settingsBtn = null;
  let itemsBtn = null;
  if (isLaunch || isEvents) {
    const setActive = (tab) => {
      activeTab = tab;
      settingsBtn.classList.toggle("on", tab === "settings");
      itemsBtn.classList.toggle("on", tab === "items");
      drawContent();
    };
    settingsBtn = el("button", { class: "blk-tab on", type: "button", text: tt("tabSettings"), onclick: () => setActive("settings") });
    itemsBtn = el("button", { class: "blk-tab", type: "button", text: isLaunch ? tt("tabLaunchItems") : tt("tabEventsItems"), onclick: () => setActive("items") });
    bodyWrap.append(el("div", { class: "blk-tabs" }, settingsBtn, itemsBtn));
  }
  bodyWrap.append(content);

  function drawContent() {
    content.innerHTML = "";
    if (activeTab === "items" && isLaunch) drawItemsTab(content, ctx, "launch");
    else if (activeTab === "items" && isEvents) drawItemsTab(content, ctx, "events");
    else drawSettingsGroups(content, ctx, tt, widget);
  }
  drawContent();

  const page = el("div", { class: "blk-detail", "data-widget": widget.id }, crumb, el("div", { class: "blk-layout" }, hero, bodyWrap));
  container.append(page);
}

function drawSettingsGroups(content, ctx, tt, widget) {
  const schema = widget.settings || [];
  if (!schema.length) {
    content.append(el("div", { class: "empty" }, el("div", { class: "txt", text: tt("noSettings") })));
    return;
  }

  ctx.config.widgetSettings = ctx.config.widgetSettings || {};
  const data = { ...(ctx.config.widgetSettings[widget.id] || {}) };
  for (const f of schema) if (data[f.key] === undefined) data[f.key] = f.default;

  const scheduleSave = makeScheduler(async () => {
    ctx.onStatus(ctx.t("statusSaving"), false);
    try {
      await ctx.put({ widgetSettings: { [widget.id]: data } });
      ctx.config.widgetSettings[widget.id] = data;
      ctx.onStatus(ctx.t("statusSaved"), false);
    } catch (err) {
      ctx.onStatus(ctx.t("statusError", err.message), true);
    }
  }, 300);

  const groupsDef = widget.groups && widget.groups.length
    ? widget.groups
    : [{ id: "settings", label: widget.name || widget.id, keys: schema.map((f) => f.key) }];

  for (const g of groupsDef) {
    const keys = g.keys || [];
    if (!keys.length) continue; // e.g. launch's empty "groups" placeholder card — nothing to show yet
    const rows = [];
    for (const key of keys) {
      const field = schema.find((f) => f.key === key);
      if (!field) continue;
      const labelText = loc(field.label, ctx.lang) || field.key;
      const helpText = loc(field.help, ctx.lang);
      if (field.type === "list") {
        rows.push(el("div", { class: "blk-list-field" },
          el("div", { class: "t", text: labelText }),
          helpText ? el("div", { class: "s", text: helpText }) : null,
          listFieldBlock(field, data, ctx, scheduleSave)));
      } else {
        rows.push(settingRow(labelText, helpText, fieldControl(field, data, ctx, scheduleSave)));
      }
    }
    if (rows.length) content.append(groupCard(g.id, loc(g.label, ctx.lang) || g.id, ...rows));
  }
}

function fieldControl(field, data, ctx, scheduleSave) {
  const mount = el("div");
  switch (field.type) {
    case "bool": {
      window.NNAUI.toggle(mount, { checked: !!data[field.key], onChange: (v) => { data[field.key] = v; scheduleSave(); } });
      break;
    }
    case "select": {
      const options = (field.options || []).map((o) => (typeof o === "object" ? o : { value: o, label: o }));
      window.NNAUI.select(mount, { value: data[field.key], options, onChange: (v) => { data[field.key] = v; scheduleSave(); } });
      break;
    }
    case "number": {
      if (field.min != null && field.max != null) {
        const wholeStep = field.step || (field.max - field.min > 10 ? 1 : 0.01);
        window.NNAUI.slider(mount, {
          min: field.min, max: field.max, step: wholeStep,
          value: data[field.key] ?? field.default ?? field.min,
          format: (v) => (wholeStep >= 1 ? String(Math.round(v)) : v.toFixed(2)),
          onInput: (v) => { data[field.key] = v; scheduleSave(); },
        });
      } else {
        const input = el("input", { type: "number", value: data[field.key] ?? 0, min: field.min, max: field.max, step: field.step || 1 });
        input.addEventListener("input", (e) => { data[field.key] = e.target.value === "" ? null : Number(e.target.value); scheduleSave(); });
        mount.append(input);
      }
      break;
    }
    case "path": {
      const input = el("input", { type: "text", value: data[field.key] || "", placeholder: ctx.t("formPath") });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; scheduleSave(); });
      mount.append(input);
      break;
    }
    case "timezone": {
      const listId = "tz-" + field.key + "-" + Math.random().toString(36).slice(2, 8);
      const input = el("input", { type: "text", value: data[field.key] || "", list: listId });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; scheduleSave(); });
      const datalist = el("datalist", { id: listId });
      try { for (const z of Intl.supportedValuesOf("timeZone")) datalist.append(el("option", { value: z })); } catch { /* older runtimes */ }
      mount.append(input, datalist);
      break;
    }
    case "color": {
      const isHex = (v) => typeof v === "string" && /^#[0-9a-fA-F]{6}$/.test(v);
      const hex = el("input", { type: "text", value: data[field.key] || "#000000" });
      const picker = el("input", { type: "color", value: isHex(data[field.key]) ? data[field.key] : "#000000" });
      const sync = (v) => { data[field.key] = v; hex.value = v; picker.value = isHex(v) ? v : "#000000"; scheduleSave(); };
      picker.addEventListener("input", (e) => sync(e.target.value));
      hex.addEventListener("change", (e) => sync(e.target.value));
      mount.append(picker, hex);
      break;
    }
    case "text": {
      const textarea = el("textarea", { text: data[field.key] || "" });
      textarea.addEventListener("input", (e) => { data[field.key] = e.target.value; scheduleSave(); });
      mount.append(textarea);
      break;
    }
    case "string":
    default: {
      const input = el("input", { type: "text", value: data[field.key] ?? "" });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; scheduleSave(); });
      mount.append(input);
    }
  }
  return mount;
}

function listFieldBlock(field, data, ctx, scheduleSave) {
  const itemSchema = field.item || {};
  const columns = Object.keys(itemSchema);
  if (!Array.isArray(data[field.key])) data[field.key] = Array.isArray(field.default) ? field.default.map((x) => ({ ...x })) : [];
  const rows = data[field.key];

  const table = el("table", { class: "list-table" });
  const redraw = () => {
    table.innerHTML = "";
    table.append(el("tr", null, ...columns.map((c) => el("th", { text: c })), el("th", {})));
    rows.forEach((row, idx) => {
      const cells = columns.map((c) => {
        const type = itemSchema[c];
        const input = el("input", { type: type === "number" ? "number" : "text", value: row[c] ?? "" });
        input.addEventListener("input", (e) => { row[c] = type === "number" ? Number(e.target.value) : e.target.value; scheduleSave(); });
        return el("td", null, input);
      });
      const removeBtn = el("button", { class: "btn sm", type: "button", text: ctx.t("formRemove") });
      removeBtn.addEventListener("click", () => { rows.splice(idx, 1); redraw(); scheduleSave(); });
      table.append(el("tr", null, ...cells, el("td", null, removeBtn)));
    });
  };
  redraw();

  const addBtn = el("button", { class: "btn sm", type: "button", text: ctx.t("formAdd") });
  addBtn.addEventListener("click", () => {
    const blank = {};
    for (const c of columns) blank[c] = itemSchema[c] === "number" ? 0 : "";
    rows.push(blank);
    redraw();
    scheduleSave();
  });

  return el("div", { class: "stack" }, table, addBtn);
}

// ── launch / events items tab (unchanged logic from the old row-list page) ──────────────────────

function drawItemsTab(content, ctx, widgetId) {
  const formBox = el("div");
  const form = widgetId === "launch"
    ? renderLaunchForm(formBox, ctx.config.launch || {}, ctx.t)
    : renderEventsForm(formBox, ctx.config.events || {}, ctx.t);

  const saveBtn = el("button", {
    class: "btn primary", type: "button", text: ctx.t("save"),
    onclick: async () => {
      ctx.onStatus(ctx.t("statusSaving"), false);
      try {
        const value = form.getValue();
        if (widgetId === "launch") { await ctx.put({ launch: value }); ctx.config.launch = value; }
        else { await ctx.put({ events: value }); ctx.config.events = value; }
        ctx.onStatus(ctx.t("statusSaved"), false);
      } catch (err) {
        ctx.onStatus(ctx.t("statusError", err.message), true);
      }
    },
  });

  content.append(formBox, el("div", { class: "rowflex" }, el("div", { class: "sp" }), saveBtn));
}
