// settings/pages/layout.js — "Layout" page (α): thin wrapper around the existing monitor/grid
// editor. layout-editor.js and layout-model.js are owned by the next stage (block-card redesign)
// and are wired in here unchanged, exactly as the old app.js's renderAlpha did.

import { mountLayoutTab } from "../layout-editor.js";

export function keywords(lang) {
  return lang === "en"
    ? ["monitors", "grid", "columns", "rows", "gap", "padding", "blocks", "reset"]
    : ["мониторы", "сетка", "колонки", "строки", "отступ", "поля", "блоки", "сброс"];
}

export function render(container, ctx) {
  mountLayoutTab(container, {
    t: ctx.t,
    put: ctx.put,
    fetchDefaults: ctx.fetchDefaults,
    liveMonitors: ctx.config.liveMonitors || [],
    monitorsConfig: ctx.config.monitors,
    widgets: ctx.widgets,
    onStatus: ctx.onStatus,
  });
}
