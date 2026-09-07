import { makeT, detectLang } from "./i18n.js";
import { mountLayoutTab } from "./layout-editor.js";
import { renderWidgetForm, renderLaunchForm, renderEventsForm } from "./forms.js";
import { mountTaskbarTab } from "./taskbar-tab.js";

const params = new URLSearchParams(location.search);
const TOKEN = params.get("token") || "";

const state = {
  lang: detectLang(params.get("lang")),
  t: null,
  config: null, // last GET /config/full response
  widgets: [], // GET /widgets -> widgets[]
  activeTab: "alpha",
  betaSelection: "launch", // "launch" | "events" | widget id
};
state.t = makeT(state.lang);

const TABS = [
  { key: "alpha", g: "\u03b1", labelKey: "tab_layout" },
  { key: "beta", g: "\u03b2", labelKey: "tab_widgets" },
  { key: "gamma", g: "\u03b3", labelKey: "tab_appearance" },
  { key: "delta", g: "\u03b4", labelKey: "tab_planner" },
  { key: "epsilon", g: "\u03b5", labelKey: "tab_general" },
  { key: "zeta", g: "\u03b6", labelKey: "tab_taskbar" },
];

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
  sw.addEventListener("click", () => { const on = !sw.classList.contains("on"); sw.classList.toggle("on", on); onToggle(on); });
  return sw;
}

async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json; charset=utf-8";
    opts.body = JSON.stringify(body);
  }
  if (method !== "GET") opts.headers["X-Token"] = TOKEN;
  const res = await fetch(path, opts);
  let data = null;
  try { data = await res.json(); } catch { /* no body */ }
  if (!res.ok) {
    const err = new Error((data && (data.error || data.message)) || res.statusText || ("HTTP " + res.status));
    err.status = res.status;
    err.detail = data && data.detail;
    throw err;
  }
  return data;
}

const put = (body) => api("PUT", "/config", body);

function setStatus(text, isError) {
  const box = document.getElementById("status");
  if (!box) return;
  box.textContent = text;
  box.classList.toggle("err", !!isError);
  box.classList.toggle("ok", !isError && text === state.t("statusSaved"));
}

// ── shell ─────────────────────────────────────────────────────────

function renderShell() {
  document.title = state.t("appTitle");
  const root = document.getElementById("root");
  root.innerHTML = "";

  const topbar = el("div", { class: "topbar" },
    el("div", { class: "brand" }, el("span", { class: "g", text: "\u03bd\u03bd\u03b1" }), el("h1", { text: "NNA WALLPAPER" })),
    el("div", { class: "status", id: "status-wrap" },
      el("span", { class: "dot" }), el("span", { id: "status", text: state.t("statusIdle") })),
    el("div", { class: "lang" },
      el("button", { class: state.lang === "ru" ? "on" : "", onclick: () => setLang("ru"), text: "RU" }),
      el("button", { class: state.lang === "en" ? "on" : "", onclick: () => setLang("en"), text: "EN" })));

  const tabsBar = el("div", { class: "tabs" });
  for (const tab of TABS) {
    const tabEl = el("div", { class: "tab" + (tab.key === state.activeTab ? " on" : ""), onclick: () => showTab(tab.key) },
      el("span", { class: "g", text: tab.g }), el("span", { class: "label", text: state.t(tab.labelKey) }));
    tabsBar.append(tabEl);
  }

  const content = el("div", { class: "content" });
  for (const tab of TABS) {
    const panel = el("div", { class: "panel", id: "panel-" + tab.key, hidden: tab.key !== state.activeTab });
    content.append(panel);
  }

  root.append(topbar, tabsBar, content);
  renderActivePanel();
}

function setLang(lang) {
  if (lang === state.lang) return;
  state.lang = lang;
  state.t = makeT(lang);
  put({ app: { language: lang } }).catch(() => { /* still switch UI even if save fails */ });
  renderShell();
}

function showTab(rawTab) {
  const mapped = mapTabName(rawTab);
  state.activeTab = mapped.tab;
  if (mapped.betaSelection) state.betaSelection = mapped.betaSelection;
  for (const tab of TABS) {
    const el2 = document.querySelector(`.tab:nth-child(${TABS.indexOf(tab) + 1})`);
    if (el2) el2.classList.toggle("on", tab.key === state.activeTab);
    const panel = document.getElementById("panel-" + tab.key);
    if (panel) panel.hidden = tab.key !== state.activeTab;
  }
  renderActivePanel();
}

function mapTabName(tab) {
  switch (tab) {
    case "launch": return { tab: "beta", betaSelection: "launch" };
    case "events": return { tab: "beta", betaSelection: "events" };
    case "config": case "nna-config": case "general": return { tab: "epsilon" };
    case "layout": return { tab: "alpha" };
    case "theme": return { tab: "gamma" };
    case "planner": return { tab: "delta" };
    case "taskbar": return { tab: "zeta" };
    case "alpha": case "beta": case "gamma": case "delta": case "epsilon": case "zeta": return { tab };
    default: return { tab: "alpha" };
  }
}

function renderActivePanel() {
  const panel = document.getElementById("panel-" + state.activeTab);
  if (!panel || !state.config) return;
  panel.innerHTML = "";
  switch (state.activeTab) {
    case "alpha": return renderAlpha(panel);
    case "beta": return renderBeta(panel);
    case "gamma": return renderGamma(panel);
    case "delta": return renderDelta(panel);
    case "epsilon": return renderEpsilon(panel);
    case "zeta": return renderZeta(panel);
  }
}

// ── α — monitors and layout ─────────────────────────────────────

function renderAlpha(panel) {
  mountLayoutTab(panel, {
    t: state.t,
    put,
    fetchDefaults: async (monitorId) => (await api("GET", `/config/defaults?monitor=${encodeURIComponent(monitorId)}`)).monitor,
    liveMonitors: state.config.liveMonitors || [],
    monitorsConfig: state.config.monitors,
    widgets: state.widgets,
    onStatus: setStatus,
  });
}

// ── β — widgets / launch / events ────────────────────────────────

function renderBeta(panel) {
  const page = el("div", { class: "widgets-page" });
  const list = el("div", { class: "widget-list list" });
  const formBox = el("div", { class: "widget-form" });

  const entries = [
    { id: "launch", label: state.t("launchTitle") },
    { id: "events", label: state.t("eventsTitle") },
    ...state.widgets.map((w) => ({ id: w.id, label: (w.name || w.id) + "  " + (w.version || ""), source: w.source })),
  ];
  for (const entry of entries) {
    const row = el("div", { class: "row" + (entry.id === state.betaSelection ? " on" : ""), onclick: () => { state.betaSelection = entry.id; renderBeta(panel); } },
      el("div", { class: "main" }, el("div", { class: "t", text: entry.label }), entry.source ? el("div", { class: "m", text: entry.source }) : null));
    list.append(row);
  }
  if (state.widgets.length === 0) {
    list.append(el("div", { class: "empty" }, el("div", { class: "txt", text: state.t("emptyWidgets") })));
  }

  let getValue = null;
  let saveHandler = null;

  if (state.betaSelection === "launch") {
    const form = renderLaunchForm(formBox, state.config.launch || {}, state.t);
    getValue = form.getValue;
    saveHandler = () => put({ launch: getValue() });
  } else if (state.betaSelection === "events") {
    const form = renderEventsForm(formBox, state.config.events || {}, state.t);
    getValue = form.getValue;
    saveHandler = () => put({ events: getValue() });
  } else {
    const widget = state.widgets.find((w) => w.id === state.betaSelection);
    if (widget) {
      const values = state.config.widgetSettings?.[widget.id] || {};
      const form = renderWidgetForm(formBox, widget.settings || [], values, state.t);
      getValue = form.getValue;
      saveHandler = () => put({ widgetSettings: { [widget.id]: getValue() } });
    } else {
      formBox.append(el("div", { class: "empty" }, el("div", { class: "txt", text: state.t("noWidgetSelected") })));
    }
  }

  const footer = saveHandler
    ? el("div", { class: "rowflex" }, el("div", { class: "sp" }), el("button", {
      class: "btn primary", type: "button", text: state.t("save"),
      onclick: async () => {
        setStatus(state.t("statusSaving"), false);
        try {
          await saveHandler();
          if (state.betaSelection === "launch") state.config.launch = getValue();
          else if (state.betaSelection === "events") state.config.events = getValue();
          else state.config.widgetSettings[state.betaSelection] = getValue();
          setStatus(state.t("statusSaved"), false);
        } catch (err) {
          setStatus(state.t("statusError", err.message), true);
        }
      },
    }))
    : null;

  page.append(list, el("div", { class: "widget-form-wrap stack" }, formBox, footer));
  panel.append(page);
}

// ── γ — appearance ────────────────────────────────────────────────

// One fixed brand theme (see ThemeSettings doc comment in AppSettings.cs) — no palette editor or
// preset picker here. This tab only edits the geometry/perf knobs the wallpaper page still reads
// live: dim, radius, gap, pad, blur, fps cap, and the fullscreen-pause toggle. A full redesign of
// this tab is planned for a later stage; keep it to those fields for now.
function renderGamma(panel) {
  panel.replaceChildren(); // re-entering this tab (or any future re-render) must not stack a second copy
  const theme = { ...state.config.app.theme };
  let fpsCap = state.config.app.fpsCap;
  let pauseOnFullscreen = state.config.app.pauseOnFullscreen;
  let saveTimer = null;
  const scheduleSave = () => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(async () => {
      setStatus(state.t("statusSaving"), false);
      try {
        await put({ app: { theme, fpsCap, pauseOnFullscreen } });
        state.config.app.theme = theme; state.config.app.fpsCap = fpsCap; state.config.app.pauseOnFullscreen = pauseOnFullscreen;
        setStatus(state.t("statusSaved"), false);
      } catch (err) { setStatus(state.t("statusError", err.message), true); }
    }, 300);
  };

  const numberField = (labelKey, value, onChange, min, max, step) => {
    const input = el("input", { type: "number", value, min, max, step: step || 1 });
    input.addEventListener("input", (e) => { onChange(Number(e.target.value)); scheduleSave(); });
    return el("div", { class: "field" }, el("label", { text: state.t(labelKey) }), input);
  };

  const dimInput = el("input", { type: "range", min: 0, max: 1, step: 0.05, value: theme.dim });
  const dimValue = el("span", { class: "tag", text: theme.dim });
  dimInput.addEventListener("input", (e) => { theme.dim = Number(e.target.value); dimValue.textContent = theme.dim; scheduleSave(); });

  const pauseSwitch = switchEl(pauseOnFullscreen, (on) => { pauseOnFullscreen = on; scheduleSave(); });

  panel.append(el("div", { class: "page-narrow" },
    el("div", { class: "h-sec", text: state.t("themeGeometry") }),
    el("div", { class: "field" }, el("label", { text: state.t("dim") }), el("div", { class: "rowflex" }, dimInput, dimValue)),
    el("div", { class: "field-row" },
      numberField("radius", theme.radius, (v) => { theme.radius = v; }, 0),
      numberField("gridGap", theme.gap, (v) => { theme.gap = v; }, 0),
      numberField("gridPad", theme.pad, (v) => { theme.pad = v; }, 0)),
    el("div", { class: "field-row" },
      numberField("blur", theme.blur, (v) => { theme.blur = v; }, 0),
      numberField("fpsCap", fpsCap, (v) => { fpsCap = v; }, 1, 240)),
    el("div", { class: "setrow" }, el("div", { class: "main" }, el("div", { class: "t", text: state.t("pauseOnFullscreen") })), pauseSwitch)));
}

// ── δ — planner ───────────────────────────────────────────────────

async function renderDelta(panel) {
  panel.append(el("div", { class: "page-narrow", id: "delta-body" }, el("div", { class: "s", text: state.t("statusIdle") })));
  const body = document.getElementById("delta-body");
  let status = null;
  try { status = await api("GET", "/planner/status"); } catch { status = null; }

  body.innerHTML = "";
  body.append(el("div", { class: "h-sec", text: state.t("plannerStatus") }));
  if (!status) {
    body.append(el("div", { class: "empty" }, el("div", { class: "txt", text: state.t("plannerNotAvailable") })));
    body.append(el("div", { class: "rowflex" },
      el("button", { class: "btn primary", type: "button", disabled: true, text: state.t("loginTelegram") }),
      el("button", { class: "btn", type: "button", disabled: true, text: state.t("logout") })));
  } else {
    body.append(el("div", { class: "card", text: JSON.stringify(status) }));
    body.append(el("div", { class: "rowflex" },
      el("button", {
        class: "btn primary", type: "button", text: state.t("loginTelegram"),
        onclick: async () => { try { await api("POST", "/app/login"); } catch (err) { setStatus(state.t("statusError", err.message), true); } },
      }),
      el("button", {
        class: "btn", type: "button", text: state.t("logout"),
        onclick: async () => { try { await api("POST", "/planner/logout"); renderDelta(panel); } catch (err) { setStatus(state.t("statusError", err.message), true); } },
      })));
  }

  body.append(el("div", { class: "hr" }));
  body.append(el("div", { class: "h-sec", text: state.t("plannerShow") }));
  const show = new Set(state.config.app.planner?.show || []);
  for (const key of ["tasks", "meetings", "habits", "money", "briefing"]) {
    const sw = switchEl(show.has(key), async (on) => {
      if (on) show.add(key); else show.delete(key);
      try {
        await put({ app: { planner: { ...state.config.app.planner, show: [...show] } } });
        state.config.app.planner = { ...state.config.app.planner, show: [...show] };
        setStatus(state.t("statusSaved"), false);
      } catch (err) { setStatus(state.t("statusError", err.message), true); }
    });
    body.append(el("div", { class: "setrow" }, el("div", { class: "main" }, el("div", { class: "t", text: state.t("show_" + key) })), sw));
  }
}

// ── ε — general ───────────────────────────────────────────────────

async function renderEpsilon(panel) {
  const app = state.config.app;
  const body = el("div", { class: "page-narrow" });

  body.append(el("div", { class: "setrow" },
    el("div", { class: "main" }, el("div", { class: "t", text: state.t("autostart") })),
    switchEl(!!app.autostart, async (on) => {
      try { await put({ app: { autostart: on } }); app.autostart = on; setStatus(state.t("statusSaved"), false); }
      catch (err) { setStatus(state.t("statusError", err.message), true); }
    })));

  const langSelect = el("select");
  for (const l of ["ru", "en"]) langSelect.append(el("option", { value: l, selected: l === app.language }, l.toUpperCase()));
  langSelect.addEventListener("change", (e) => setLang(e.target.value));
  body.append(el("div", { class: "field" }, el("label", { text: state.t("language") }), langSelect));

  const portInput = el("input", { type: "number", value: app.apiPort });
  portInput.addEventListener("change", async (e) => {
    const port = Number(e.target.value);
    try { await put({ app: { apiPort: port } }); app.apiPort = port; setStatus(state.t("statusSaved"), false); }
    catch (err) { setStatus(state.t("statusError", err.message), true); }
  });
  body.append(el("div", { class: "field" }, el("label", { text: state.t("apiPort") }), portInput, el("div", { class: "hint", text: state.t("apiPortHint") })));

  body.append(el("div", { class: "h-sec", text: state.t("updates") }));
  const channelSelect = el("select");
  for (const c of ["stable"]) channelSelect.append(el("option", { value: c, selected: c === app.updates?.channel }, c));
  channelSelect.addEventListener("change", async (e) => {
    const updates = { ...app.updates, channel: e.target.value };
    try { await put({ app: { updates } }); app.updates = updates; setStatus(state.t("statusSaved"), false); }
    catch (err) { setStatus(state.t("statusError", err.message), true); }
  });
  const updateResult = el("span", { class: "tag" });
  const checkBtn = el("button", {
    class: "btn", type: "button", text: state.t("checkNow"),
    onclick: async () => {
      try { const r = await api("POST", "/app/check-updates"); updateResult.textContent = JSON.stringify(r); }
      catch (err) { updateResult.textContent = err.message; }
    },
  });
  body.append(el("div", { class: "field-row" },
    el("div", { class: "field" }, el("label", { text: state.t("channel") }), channelSelect),
    el("div", { class: "field" }, el("label", { text: "\u00a0" }), checkBtn)), updateResult);

  body.append(el("div", { class: "h-sec", text: state.t("importFolder") }));
  const importInput = el("input", { type: "text", placeholder: "C:\\path\\to\\folder" });
  const importResult = el("span", { class: "tag" });
  body.append(el("div", { class: "field-row" },
    el("div", { class: "field" }, importInput),
    el("button", {
      class: "btn", type: "button", text: state.t("importBtn"),
      onclick: async () => {
        try { await api("POST", "/app/import?dir=" + encodeURIComponent(importInput.value)); importResult.textContent = state.t("statusSaved"); }
        catch (err) { importResult.textContent = err.status === 404 ? state.t("importHint") : err.message; }
      },
    })), importResult);

  body.append(el("div", { class: "h-sec", text: "\u00a0" }));
  body.append(el("div", { class: "rowflex" },
    el("button", { class: "btn", type: "button", text: state.t("openDataFolder"), onclick: () => api("POST", "/config/open-folder?what=data").catch(() => {}) }),
    el("button", { class: "btn", type: "button", text: state.t("openLog"), onclick: () => api("POST", "/config/open-folder?what=logs").catch(() => {}) })));

  let version = "";
  try { const h = await api("GET", "/health"); version = h.version || h.app_version || ""; } catch { /* ignore */ }
  body.append(el("div", { class: "rowflex" },
    el("span", { class: "tag", text: state.t("version") + " " + version }),
    el("span", { class: "sp" }),
    el("a", { href: "https://github.com/immanuel1618/nna-wallpaper", target: "_blank", class: "tag", text: state.t("repoLink") }),
    el("a", { href: "https://github.com/immanuel1618/nna-wallpaper/releases", target: "_blank", class: "tag", text: state.t("releasesLink") }),
    el("a", { href: "https://github.com/immanuel1618/nna-wallpaper/blob/main/THIRD-PARTY.md", target: "_blank", class: "tag", text: state.t("licenses") })));

  panel.append(body);
}

// ── ζ — taskbar ───────────────────────────────────────────────────

function renderZeta(panel) {
  mountTaskbarTab(panel, {
    t: state.t,
    lang: state.lang,
    put,
    api,
    taskbar: state.config.app.taskbar,
    topBar: state.config.app.topBar,
    onStatus: setStatus,
  });
}

// ── boot ────────────────────────────────────────────────────────

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
  if (initialTab) showTab(initialTab);
}

window.nnaSettings = {
  showTab,
};

boot();
