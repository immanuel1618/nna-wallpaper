// settings/shell.js — the settings window shell: sidebar navigation + search, page header with
// the save-status pill, and the language switch. Boots once, then delegates all page content to
// settings/pages/*.js (see docs/SETTINGS.md "Как добавить страницу"). Ported from the old
// app.js's five hand-rolled tabs (α..ζ) to a macOS System Settings-style layout (owner's chosen
// reference — see docs/DESIGN-SYSTEM.md) without changing how any page talks to the host API.

import { makeT, detectLang } from "./i18n.js";
import { api, put, setStatus } from "./api.js";
import { el } from "./dom.js";
import { iconEl } from "./icons.js";
import { mapTabName, filterSidebar, firstMatch, matchesQuery } from "./shell-logic.js";

import * as layoutPage from "./pages/layout.js";
import * as blocksPage from "./pages/blocks.js";
import * as appearancePage from "./pages/appearance.js";
import * as topbarPage from "./pages/topbar.js";
import * as dockPage from "./pages/dock.js";
import * as taskbarPage from "./pages/taskbar.js";
import * as cursorPage from "./pages/cursor.js";
import * as plannerPage from "./pages/planner.js";
import * as generalPage from "./pages/general.js";
import * as aboutPage from "./pages/about.js";

// Sidebar order per the owner's brief: Layout, Blocks, Appearance, Top bar, Dock, Taskbar,
// Cursor, Planner, General, About — Greek indices alpha..kappa (10 pages) run in this order,
// independent of the historical alpha..zeta letters the old 5-tab shell used (kept alive only
// as ?tab= aliases in shell-logic.js's mapTabName).
const PAGES = [
  { key: "layout", g: "α", navKey: "nav_layout", mod: layoutPage },
  { key: "blocks", g: "β", navKey: "nav_blocks", mod: blocksPage },
  { key: "appearance", g: "γ", navKey: "nav_appearance", mod: appearancePage },
  { key: "topbar", g: "δ", navKey: "nav_topbar", mod: topbarPage },
  { key: "dock", g: "ε", navKey: "nav_dock", mod: dockPage },
  { key: "taskbar", g: "ζ", navKey: "nav_taskbar", mod: taskbarPage },
  { key: "cursor", g: "η", navKey: "nav_cursor", mod: cursorPage },
  { key: "planner", g: "θ", navKey: "nav_planner", mod: plannerPage },
  { key: "general", g: "ι", navKey: "nav_general", mod: generalPage },
  { key: "about", g: "κ", navKey: "nav_about", mod: aboutPage },
];

const params = new URLSearchParams(location.search);

const state = {
  lang: detectLang(params.get("lang")),
  t: null,
  config: null,
  widgets: [],
  activePage: "layout",
  pageSelection: null, // per-page sub-selection (e.g. blocks: "launch" | "events" | widget id)
  query: "",
};
state.t = makeT(state.lang);

function ctx() {
  return {
    t: state.t,
    lang: state.lang,
    api,
    put,
    config: state.config,
    widgets: state.widgets,
    selection: state.pageSelection,
    setSelection: (v) => { state.pageSelection = v; },
    setLang,
    onStatus: setStatus, // (text, isError) — same two-arg contract layout-editor.js already uses
    fetchDefaults: async (monitorId) => (await api("GET", `/config/defaults?monitor=${encodeURIComponent(monitorId)}`)).monitor,
  };
}

function renderShell() {
  document.title = state.t("appTitle");
  const root = document.getElementById("root");
  root.innerHTML = "";
  root.className = "app";

  const searchInput = el("input", {
    type: "search", id: "search-input", class: "search-input",
    placeholder: state.t("searchPlaceholder"),
    oninput: (e) => { state.query = e.target.value; applySearch(); },
    onkeydown: (e) => {
      if (e.key !== "Enter") return;
      const key = firstMatch(navItems(), state.query);
      if (key) showPage(key);
    },
  });

  const nav = el("div", { class: "sidebar-nav", id: "sidebar-nav" });
  for (const page of PAGES) {
    const item = el("button", {
      type: "button", class: "nav-item", "data-key": page.key,
      onclick: () => showPage(page.key),
    },
      iconEl(page.key),
      el("span", { class: "g", text: page.g }),
      el("span", { class: "label", text: state.t(page.navKey) }));
    nav.append(item);
  }

  const langSwitch = el("div", { class: "lang" },
    el("button", { class: state.lang === "ru" ? "on" : "", onclick: () => setLang("ru"), text: "RU" }),
    el("button", { class: state.lang === "en" ? "on" : "", onclick: () => setLang("en"), text: "EN" }));

  const sidebar = el("nav", { class: "sidebar" },
    el("div", { class: "sidebar-search" }, iconEl("search", "util"), searchInput),
    nav,
    el("div", { class: "sidebar-foot" }, langSwitch));

  const statusWrap = el("div", { class: "hdr-status", id: "status-wrap" },
    el("span", { class: "dot" }), el("span", { id: "status", text: state.t("statusIdle") }));

  const header = el("header", { class: "main-header" },
    el("div", { class: "page-title", id: "page-title" }),
    statusWrap);

  const body = el("div", { class: "main-body", id: "page-body" });
  const main = el("div", { class: "main" }, header, body);

  root.append(sidebar, main);
  renderActivePage();
}

function navItems() {
  return PAGES.map((p) => ({ key: p.key, title: state.t(p.navKey), keywords: p.mod.keywords ? p.mod.keywords(state.lang) : [] }));
}

function applySearch() {
  const matched = new Set(filterSidebar(navItems(), state.query));
  for (const el2 of document.querySelectorAll("#sidebar-nav .nav-item")) {
    el2.classList.toggle("dim", matched.size > 0 && !matched.has(el2.dataset.key));
  }
  highlightGroups();
}

function highlightGroups() {
  const q = state.query;
  for (const g of document.querySelectorAll("#page-body .group")) {
    const title = g.querySelector(".group-title");
    const isMatch = !!q && title && matchesQuery(title.textContent, q);
    g.classList.toggle("match", isMatch);
  }
}

function setLang(lang) {
  if (lang === state.lang) return;
  state.lang = lang;
  state.t = makeT(lang);
  put({ app: { language: lang } }).catch(() => { /* still switch the UI even if the save fails */ });
  renderShell();
}

/** Public entry point: switches to a page. Accepts both current keys (layout, blocks, ...) and
 * the old ?tab= values (alpha, launch, theme, ...) via mapTabName. */
function showTab(rawTab) {
  const mapped = mapTabName(rawTab);
  if (mapped.selection) state.pageSelection = mapped.selection;
  showPage(mapped.page);
}

function showPage(key) {
  state.activePage = PAGES.some((p) => p.key === key) ? key : "layout";
  for (const el2 of document.querySelectorAll("#sidebar-nav .nav-item")) {
    el2.classList.toggle("on", el2.dataset.key === state.activePage);
  }
  renderActivePage();
}

function renderActivePage() {
  if (!state.config) return;
  const page = PAGES.find((p) => p.key === state.activePage) || PAGES[0];
  const titleBox = document.getElementById("page-title");
  if (titleBox) {
    titleBox.innerHTML = "";
    titleBox.append(el("span", { class: "g", text: page.g }), el("h1", { text: state.t(page.navKey) }));
  }
  const body = document.getElementById("page-body");
  if (!body) return;
  body.innerHTML = "";
  page.mod.render(body, ctx());
  if (state.query) highlightGroups();
}

async function boot() {
  try {
    state.config = await api("GET", "/config/full");
  } catch (err) {
    document.getElementById("root").innerHTML = "";
    document.getElementById("root").append(el("div", { class: "empty", style: "margin:40px" }, el("div", { class: "txt", text: state.t("statusLoadError") + ": " + err.message })));
    return;
  }
  try {
    const w = await api("GET", "/widgets");
    state.widgets = w.widgets || [];
  } catch { state.widgets = []; }

  renderShell();
  const initialTab = params.get("tab");
  showTab(initialTab || "layout");
}

window.nnaSettings = { showTab };

boot();
