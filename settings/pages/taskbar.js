// settings/pages/taskbar.js — "Taskbar" page (ζ): the real Windows taskbar (preset picker + per-
// state styling + Windows toggles, including the new Windows.Mode select), moved in from the old
// ζ tab (taskbar-tab.js) unchanged apart from no longer also rendering the top bar section — that
// now has its own page, settings/pages/topbar.js.

import { mountTaskbarTab } from "../taskbar-tab.js";

export function keywords(lang) {
  return lang === "en"
    ? ["taskbar", "windows", "preset", "maximized", "fullscreen", "auto-hide", "centered"]
    : ["панель задач", "windows", "пресет", "развёрнуто", "полноэкранное", "автоскрытие", "по центру"];
}

export function render(container, ctx) {
  mountTaskbarTab(container, {
    t: ctx.t,
    lang: ctx.lang,
    put: ctx.put,
    api: ctx.api,
    taskbar: ctx.config.app.taskbar,
    topBar: ctx.config.app.topBar,
    showTopBar: false,
    onStatus: ctx.onStatus,
  });
}
