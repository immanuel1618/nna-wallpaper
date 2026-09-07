// settings/pages/layout.js — "Layout" page (α): thin wrapper that owns this page's own ru/en
// dictionary (settings/i18n.js is being edited in parallel this stage, so new strings live here —
// see docs/SETTINGS.md "Раскладка") and delegates everything else to layout-editor.js's
// mountLayoutTab (monitor cards, grid params, the canvas+palette from layout-canvas.js, and the
// Apply/Cancel/Undo/Redo/Reset action bar — owner decision D16: live preview, no flicker).

import { mountLayoutTab } from "../layout-editor.js";

const S = {
  ru: {
    monitorsTitle: "Мониторы",
    gridSection: "Сетка",
    paletteTitle: "Виджеты",
    applyBtn: "Применить",
    cancelBtn: "Отменить",
    undoLastBtn: "Отмена последнего",
    redoBtn: "Повторить",
    overlapBlocked: "Блоки пересекаются или выходят за сетку — применить нельзя",
    resetConfirmTitle: "Сброс к умолчанию",
    resetConfirmBody: "Вернуть раскладку этого монитора к встроенной по умолчанию. Несохранённые изменения будут потеряны.",
    resetConfirmOk: "Сбросить",
    resetConfirmCancel: "Отмена",
  },
  en: {
    monitorsTitle: "Monitors",
    gridSection: "Grid",
    paletteTitle: "Widgets",
    applyBtn: "Apply",
    cancelBtn: "Cancel",
    undoLastBtn: "Undo last",
    redoBtn: "Redo",
    overlapBlocked: "Blocks overlap or exceed the grid — cannot apply",
    resetConfirmTitle: "Reset to default",
    resetConfirmBody: "Reset this monitor's layout to the built-in default. Unsaved changes will be lost.",
    resetConfirmOk: "Reset",
    resetConfirmCancel: "Cancel",
  },
};

export function keywords(lang) {
  return lang === "en"
    ? ["monitors", "grid", "columns", "rows", "gap", "padding", "blocks", "reset", "apply", "cancel", "undo", "redo", "widgets", "drag", "resize"]
    : ["мониторы", "сетка", "колонки", "строки", "отступ", "поля", "блоки", "сброс", "применить", "отменить", "отмена", "повтор", "виджеты", "перетаскивание", "растянуть"];
}

// A saved monitor's id is "{DeviceName}|{Width}x{Height}" (see AppSettings.cs MonitorLayout.Id /
// docs/SETTINGS.md) — when there is no live match for it (host running --headless, where
// IHostApp.Monitors is always empty — see HeadlessHostApp.cs — or a monitor temporarily
// unplugged), fall back to a synthetic "live" entry parsed out of the id so its layout stays
// editable instead of the page going blank.
function synthesizeLiveMonitors(monitors) {
  let x = 0;
  return monitors.map((m) => {
    const match = /\|(\d+)x(\d+)$/.exec(m.id || "");
    const width = match ? Number(match[1]) : 1920;
    const height = match ? Number(match[2]) : 1080;
    // Real desktops are almost always arranged side by side (left to right), so that — rather
    // than stacking full heights — is the sane default for a fallback minimap.
    const live = { id: m.id, name: m.name || m.id, width, height, x, y: 0, visible: true, paused: false, scale: 1 };
    x += width;
    return live;
  });
}

export async function render(container, ctx) {
  const tt = (k, ...args) => {
    const entry = S[ctx.lang] && S[ctx.lang][k];
    if (entry === undefined) return ctx.t(k, ...args);
    return args.length ? args.reduce((s, a, i) => s.replace(`{${i}}`, a), entry) : entry;
  };

  // GET /config/full is fetched once at shell boot and never refreshed, so its liveMonitors can
  // go stale (a monitor reconnected, resolution changed) — /health carries the same shape fresh
  // on every page visit; fall back to the boot snapshot if it is unreachable.
  let liveMonitors = ctx.config.liveMonitors || [];
  try {
    const health = await ctx.api("GET", "/health");
    if (Array.isArray(health.monitors) && health.monitors.length) liveMonitors = health.monitors;
  } catch { /* keep the boot snapshot */ }

  if (liveMonitors.length === 0) {
    liveMonitors = synthesizeLiveMonitors(ctx.config.monitors?.monitors || []);
  }

  mountLayoutTab(container, {
    t: tt,
    put: ctx.put,
    api: ctx.api,
    fetchDefaults: ctx.fetchDefaults,
    liveMonitors,
    monitorsConfig: ctx.config.monitors,
    appConfig: ctx.config.app,
    widgets: ctx.widgets,
    onStatus: ctx.onStatus,
  });
}
