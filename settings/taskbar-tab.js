// ζ — "Taskbar" tab: styling for the real Windows taskbar plus our own always-on-top top bar
// (a macOS-like menu bar), driven by app.json's taskbar/topBar objects and the (not yet shipped)
// /taskbar/* orchestrator endpoints. Every /taskbar/* call is expected to 404 until the C# side
// lands — this module must stay usable (PUT /config always works; the orchestrator calls just
// degrade to a visible "unavailable" note instead of breaking the page.

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
  sw.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
  return sw;
}

function clone(v) { return v === undefined ? v : JSON.parse(JSON.stringify(v)); }

const MODES = ["normal", "clear", "blur", "acrylic", "opaque"];
const MODULE_IDS = ["brand", "date", "clock", "weather", "stats", "media", "planner", "spacer"];
const WINDOWS_TOGGLES = [
  "centered", "hideSearch", "hideTaskView", "hideWidgets", "hideClock",
  "small", "transparency", "oledTransparency", "autoHide",
];

function defaultSurfaceStyle() { return { mode: "normal", color: "#0B0B0B", opacity: 0.5 }; }

const WINDOWS_MODES = ["normal", "autohide", "win-only"];

function defaultTaskbar() {
  return {
    enabled: false,
    preset: "windows",
    normal: defaultSurfaceStyle(),
    maximized: { mode: "opaque", color: "#0B0B0B", opacity: 1 },
    fullscreen: defaultSurfaceStyle(),
    // "mode" (not yet a real field on TaskbarWindowsSettings — lands with a parallel branch,
    // see docs/SETTINGS.md "Панель задач: режим Windows") sits alongside the tri-state toggles
    // below; until the C# side ships this is accepted by PUT /config and silently dropped, same
    // as app.dock (see settings/pages/dock.js) — the select still shows "normal" either way.
    windows: { mode: "normal", ...Object.fromEntries(WINDOWS_TOGGLES.map((k) => [k, null])) },
    secondary: true,
  };
}

function defaultTopBar() {
  return {
    enabled: false,
    monitors: "all",
    height: 30,
    style: { mode: "acrylic", color: "#0B0B0B", opacity: 0.6 },
    fontSize: 12,
    autoHide: false,
    reserveSpace: true,
    modules: [
      { id: "brand", side: "left" }, { id: "date", side: "left" },
      { id: "clock", side: "center" },
      { id: "planner", side: "right" }, { id: "media", side: "right" },
      { id: "weather", side: "right" }, { id: "stats", side: "right" },
    ],
  };
}

function normalizeHex(v) {
  return typeof v === "string" && /^#[0-9a-fA-F]{6}$/.test(v) ? v : "#000000";
}

function slugify(name) {
  const base = String(name || "").trim().toLowerCase()
    .replace(/[^a-z0-9а-яё]+/gi, "-").replace(/^-+|-+$/g, "");
  return base || "preset-" + Date.now();
}

/**
 * Mounts the ζ "taskbar" tab.
 * ctx = { t, put(body), api(method, path, body), taskbar, topBar, onStatus(text, isError) }
 */
export function mountTaskbarTab(container, ctx) {
  const { t } = ctx;
  let taskbar = clone(ctx.taskbar) || defaultTaskbar();
  let topBar = clone(ctx.topBar) || defaultTopBar();
  // Fill any missing pieces so older saved configs (or a hand-edited import) don't crash the form.
  taskbar = { ...defaultTaskbar(), ...taskbar, normal: { ...defaultSurfaceStyle(), ...taskbar.normal }, maximized: { ...defaultTaskbar().maximized, ...taskbar.maximized }, fullscreen: { ...defaultSurfaceStyle(), ...taskbar.fullscreen }, windows: { ...defaultTaskbar().windows, ...taskbar.windows } };
  topBar = { ...defaultTopBar(), ...topBar, style: { ...defaultTopBar().style, ...topBar.style }, modules: Array.isArray(topBar.modules) ? topBar.modules.map((m) => ({ ...m })) : defaultTopBar().modules };

  let taskbarStatus = null;
  let taskbarStatusChecked = false;

  async function applyAll() {
    ctx.onStatus(t("statusSaving"), false);
    try {
      await ctx.put({ app: { taskbar, topBar } });
      try {
        await ctx.api("POST", "/taskbar/apply");
        ctx.onStatus(t("statusSaved"), false);
      } catch (err) {
        // Config is saved either way; the orchestrator just isn't there yet (404) or failed.
        ctx.onStatus(err.status === 404 ? t("statusSaved") + " (" + t("taskbarUnavailable") + ")" : t("statusError", err.message), err.status !== 404);
      }
    } catch (err) {
      ctx.onStatus(t("statusError", err.message), true);
    }
  }

  async function importOnly() {
    ctx.onStatus(t("statusSaving"), false);
    try {
      await ctx.put({ app: { taskbar, topBar } });
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) {
      ctx.onStatus(t("statusError", err.message), true);
    }
  }

  function render() {
    container.innerHTML = "";
    container.append(
      renderPresetSection(),
      el("div", { class: "hr" }),
      renderWindowsSection());
    // topBar (our own top bar) now has its own page (settings/pages/topbar.js); this section stays
    // available for callers that still want it inline (ctx.showTopBar), off by default when unset
    // so a caller has to opt in explicitly.
    if (ctx.showTopBar) container.append(el("div", { class: "hr" }), renderTopBarSection());
  }

  // ── 1. Preset ─────────────────────────────────────────────────

  function renderPresetSection() {
    const box = el("div", { class: "page-narrow" });
    box.append(el("div", { class: "h-sec" }, el("span", { class: "g", text: "ζ" }), t("taskbarPresetSection")));

    const select = el("select");
    select.append(el("option", { value: "", text: t("presetNone") }));
    const entries = new Map(); // value -> { kind, id, file }
    box.append(el("div", { class: "field" }, el("label", { text: t("presetSelectLabel") }), select));

    const applyBtn = el("button", { class: "btn primary", type: "button", text: t("presetApply"), disabled: true });
    const saveAsBtn = el("button", { class: "btn", type: "button", text: t("presetSaveAs") });
    const exportBtn = el("button", { class: "btn", type: "button", text: t("presetExport") });
    box.append(el("div", { class: "rowflex" }, applyBtn, saveAsBtn, exportBtn));

    const exportArea = el("textarea", { rows: "10", readonly: true, style: "font-family:var(--mono);display:none;" });
    box.append(el("div", { class: "field" }, exportArea));

    box.append(el("div", { class: "h-sec" }, t("presetImport")));
    const importArea = el("textarea", { rows: "10", style: "font-family:var(--mono);" });
    const importBtn = el("button", { class: "btn", type: "button", text: t("presetImportBtn") });
    box.append(el("div", { class: "field" }, importArea), el("div", { class: "rowflex" }, importBtn));

    exportBtn.addEventListener("click", () => {
      exportArea.style.display = "";
      exportArea.value = JSON.stringify({ taskbar, topBar }, null, 2);
      exportArea.focus();
      exportArea.select();
    });

    importBtn.addEventListener("click", async () => {
      let parsed;
      try {
        parsed = JSON.parse(importArea.value);
      } catch (err) {
        ctx.onStatus(t("statusError", "JSON: " + err.message), true);
        return;
      }
      const nextTaskbar = parsed && typeof parsed.taskbar === "object" ? parsed.taskbar : (parsed && parsed.windows ? parsed : null);
      const nextTopBar = parsed && typeof parsed.topBar === "object" ? parsed.topBar : null;
      if (nextTaskbar) taskbar = { ...defaultTaskbar(), ...clone(nextTaskbar), windows: { ...defaultTaskbar().windows, ...(nextTaskbar.windows || {}) } };
      if (nextTopBar) topBar = { ...defaultTopBar(), ...clone(nextTopBar) };
      render();
      await importOnly();
    });

    saveAsBtn.addEventListener("click", async () => {
      const name = window.prompt(t("presetNamePrompt"));
      if (!name) return;
      const id = slugify(name);
      try {
        await ctx.api("POST", "/taskbar/presets", { id, name: { ru: name, en: name }, taskbar, topBar });
        ctx.onStatus(t("statusSaved"), false);
        loadPresetList();
      } catch (err) {
        ctx.onStatus(err.status === 404 ? t("statusError", t("taskbarUnavailable")) : t("statusError", err.message), true);
      }
    });

    applyBtn.addEventListener("click", async () => {
      const entry = entries.get(select.value);
      if (!entry) return;
      try {
        const data = entry.kind === "builtin"
          ? await ctx.api("GET", "/presets/taskbar/" + entry.file)
          : await ctx.api("GET", "/taskbar/preset?id=" + encodeURIComponent(entry.id));
        if (data.taskbar) taskbar = { ...defaultTaskbar(), ...clone(data.taskbar), windows: { ...defaultTaskbar().windows, ...(data.taskbar.windows || {}) } };
        if (data.topBar) topBar = { ...defaultTopBar(), ...clone(data.topBar) };
      } catch (err) {
        ctx.onStatus(t("statusError", err.message), true);
        return;
      }
      render();
      await applyAll();
    });

    async function loadPresetList() {
      let builtin = [];
      let user = [];
      try {
        const r = await ctx.api("GET", "/taskbar/presets");
        builtin = r.builtin || [];
        user = r.user || [];
      } catch {
        try {
          const idx = await ctx.api("GET", "/presets/taskbar/index.json");
          builtin = Array.isArray(idx) ? idx : [];
        } catch { /* nothing available — select stays empty */ }
      }
      select.innerHTML = "";
      entries.clear();
      select.append(el("option", { value: "", text: t("presetNone") }));
      if (builtin.length) {
        const group = el("optgroup", { label: t("presetGroupBuiltin") });
        for (const p of builtin) {
          const value = "builtin:" + p.id;
          entries.set(value, { kind: "builtin", id: p.id, file: p.file });
          const label = (p.name && (p.name[ctx.lang] || p.name.ru || p.name.en)) || p.id;
          group.append(el("option", { value, text: label }));
        }
        select.append(group);
      }
      if (user.length) {
        const group = el("optgroup", { label: t("presetGroupUser") });
        for (const p of user) {
          const value = "user:" + p.id;
          entries.set(value, { kind: "user", id: p.id, file: p.file });
          const label = (p.name && (p.name[ctx.lang] || p.name.ru || p.name.en)) || p.id;
          group.append(el("option", { value, text: label }));
        }
        select.append(group);
      }
      applyBtn.disabled = entries.size === 0;
    }
    loadPresetList();

    return box;
  }

  // ── 2. Windows taskbar ───────────────────────────────────────────

  function surfaceStateCard(labelKey, style) {
    const modeSelect = el("select");
    for (const m of MODES) modeSelect.append(el("option", { value: m, selected: m === style.mode, text: t("mode_" + m) }));
    modeSelect.addEventListener("change", (e) => { style.mode = e.target.value; });

    const hex = el("input", { type: "text", value: style.color });
    const picker = el("input", { type: "color", value: normalizeHex(style.color) });
    const syncColor = (v) => { style.color = v; hex.value = v; picker.value = normalizeHex(v); };
    picker.addEventListener("input", (e) => syncColor(e.target.value));
    hex.addEventListener("change", (e) => syncColor(e.target.value));

    const opacityInput = el("input", { type: "range", min: 0, max: 1, step: 0.05, value: style.opacity });
    const opacityTag = el("span", { class: "tag", text: style.opacity });
    opacityInput.addEventListener("input", (e) => { style.opacity = Number(e.target.value); opacityTag.textContent = style.opacity; });

    return el("div", { class: "card stack" },
      el("div", { class: "h-sec", text: t(labelKey) }),
      el("div", { class: "field" }, el("label", { text: t("surfaceMode") }), modeSelect),
      el("div", { class: "field" }, el("label", { text: t("surfaceColor") }), el("div", { class: "field-row" }, picker, hex)),
      el("div", { class: "field" }, el("label", { text: t("surfaceOpacity") }), el("div", { class: "rowflex" }, opacityInput, opacityTag)));
  }

  function triStateField(labelKey, windowsObj, key) {
    const value = windowsObj[key];
    const select = el("select");
    for (const opt of ["null", "true", "false"]) {
      select.append(el("option", {
        value: opt,
        selected: (value === null || value === undefined ? "null" : String(value)) === opt,
        text: opt === "null" ? t("triStateUnset") : opt === "true" ? t("triStateOn") : t("triStateOff"),
      }));
    }
    select.addEventListener("change", (e) => { windowsObj[key] = e.target.value === "null" ? null : e.target.value === "true"; });
    return el("div", { class: "field" }, el("label", { text: t(labelKey) }), select);
  }

  function renderWindowsSection() {
    const box = el("div", { class: "page-narrow" });
    box.append(el("div", { class: "h-sec" }, el("span", { class: "g", text: "ζ" }), t("taskbarWindowsSection")));

    box.append(el("div", { class: "setrow" },
      el("div", { class: "main" }, el("div", { class: "t", text: t("taskbarEnabled") })),
      switchEl(!!taskbar.enabled, (on) => { taskbar.enabled = on; })));

    const modeSelect = el("select");
    for (const m of WINDOWS_MODES) modeSelect.append(el("option", { value: m, selected: m === (taskbar.windows.mode || "normal"), text: t("windowsMode_" + m.replace("-", "")) }));
    modeSelect.addEventListener("change", (e) => { taskbar.windows.mode = e.target.value; });
    box.append(el("div", { class: "field" }, el("label", { text: t("windowsModeLabel") }), modeSelect, el("div", { class: "hint", text: t("windowsModeHint") })));

    const statusNote = el("div", { class: "hint" });
    box.append(statusNote);
    refreshStatusNote(statusNote);

    box.append(el("div", { class: "field-row" },
      surfaceStateCard("stateNormal", taskbar.normal),
      surfaceStateCard("stateMaximized", taskbar.maximized),
      surfaceStateCard("stateFullscreen", taskbar.fullscreen)));

    box.append(el("div", { class: "setrow" },
      el("div", { class: "main" }, el("div", { class: "t", text: t("taskbarSecondary") })),
      switchEl(!!taskbar.secondary, (on) => { taskbar.secondary = on; })));

    box.append(el("div", { class: "h-sec", text: t("winToggles") }));
    const togglesGrid = el("div", { class: "palette-grid" });
    for (const key of WINDOWS_TOGGLES) togglesGrid.append(triStateField("winToggle_" + key, taskbar.windows, key));
    box.append(togglesGrid);

    const applyBtn = el("button", { class: "btn primary", type: "button", text: t("taskbarApply"), onclick: applyAll });
    const restartBtn = el("button", {
      class: "btn", type: "button", text: t("restartExplorer"),
      onclick: async () => {
        if (!window.confirm(t("confirmRestartExplorer"))) return;
        try {
          await ctx.api("POST", "/taskbar/restart-explorer");
          ctx.onStatus(t("statusSaved"), false);
        } catch (err) {
          ctx.onStatus(err.status === 404 ? t("statusError", t("taskbarUnavailable")) : t("statusError", err.message), true);
        }
      },
    });
    const resetBtn = el("button", {
      class: "btn", type: "button", text: t("resetWindows"),
      onclick: async () => {
        try {
          await ctx.api("POST", "/taskbar/reset");
          ctx.onStatus(t("statusSaved"), false);
        } catch (err) {
          ctx.onStatus(err.status === 404 ? t("statusError", t("taskbarUnavailable")) : t("statusError", err.message), true);
        }
      },
    });
    box.append(el("div", { class: "rowflex" }, applyBtn, restartBtn, resetBtn));

    return box;
  }

  async function refreshStatusNote(target) {
    if (!taskbarStatusChecked) {
      try {
        taskbarStatus = await ctx.api("GET", "/taskbar/status");
      } catch {
        taskbarStatus = null;
      }
      taskbarStatusChecked = true;
    }
    target.textContent = taskbarStatus && taskbarStatus.note ? taskbarStatus.note : (taskbarStatus ? "" : t("taskbarUnavailable"));
  }

  // ── 3. Top bar ────────────────────────────────────────────────

  function renderTopBarSection() {
    const box = el("div", { class: "page-narrow" });
    box.append(el("div", { class: "h-sec" }, el("span", { class: "g", text: "ζ" }), t("taskbarTopbarSection")));

    box.append(el("div", { class: "setrow" },
      el("div", { class: "main" }, el("div", { class: "t", text: t("topbarEnabled") })),
      switchEl(!!topBar.enabled, (on) => { topBar.enabled = on; })));

    const monitorsSelect = el("select");
    for (const m of ["all", "primary"]) monitorsSelect.append(el("option", { value: m, selected: m === topBar.monitors, text: t(m === "all" ? "monitorsAll" : "monitorsPrimary") }));
    monitorsSelect.addEventListener("change", (e) => { topBar.monitors = e.target.value; });

    const heightInput = el("input", { type: "number", value: topBar.height, min: 24, max: 48 });
    heightInput.addEventListener("input", (e) => { topBar.height = Number(e.target.value); });

    const fontSizeInput = el("input", { type: "number", value: topBar.fontSize, min: 8, max: 24 });
    fontSizeInput.addEventListener("input", (e) => { topBar.fontSize = Number(e.target.value); });

    box.append(el("div", { class: "field-row" },
      el("div", { class: "field" }, el("label", { text: t("topbarMonitors") }), monitorsSelect),
      el("div", { class: "field" }, el("label", { text: t("topbarHeight") }), heightInput),
      el("div", { class: "field" }, el("label", { text: t("topbarFontSize") }), fontSizeInput)));

    box.append(surfaceStateCard("surfaceStyleLabel", topBar.style));

    box.append(el("div", { class: "setrow" },
      el("div", { class: "main" }, el("div", { class: "t", text: t("topbarAutoHide") })),
      switchEl(!!topBar.autoHide, (on) => { topBar.autoHide = on; })));
    box.append(el("div", { class: "setrow" },
      el("div", { class: "main" }, el("div", { class: "t", text: t("topbarReserveSpace") })),
      switchEl(!!topBar.reserveSpace, (on) => { topBar.reserveSpace = on; })));

    box.append(el("div", { class: "h-sec", text: t("topbarModules") }));
    const table = el("table", { class: "list-table" });
    const redrawModules = () => {
      table.innerHTML = "";
      table.append(el("tr", null, el("th", { text: t("moduleId") }), el("th", { text: t("moduleSide") }), el("th", {})));
      topBar.modules.forEach((mod, idx) => {
        const idSelect = el("select");
        for (const id of MODULE_IDS) idSelect.append(el("option", { value: id, selected: id === mod.id, text: t("module_" + id) }));
        idSelect.addEventListener("change", (e) => { mod.id = e.target.value; });

        const sideSelect = el("select");
        for (const side of ["left", "center", "right"]) sideSelect.append(el("option", { value: side, selected: side === mod.side, text: t("side" + side[0].toUpperCase() + side.slice(1)) }));
        sideSelect.addEventListener("change", (e) => { mod.side = e.target.value; });

        const upBtn = el("button", { class: "btn sm", type: "button", text: "▲", disabled: idx === 0 });
        upBtn.addEventListener("click", () => { [topBar.modules[idx - 1], topBar.modules[idx]] = [topBar.modules[idx], topBar.modules[idx - 1]]; redrawModules(); });
        const downBtn = el("button", { class: "btn sm", type: "button", text: "▼", disabled: idx === topBar.modules.length - 1 });
        downBtn.addEventListener("click", () => { [topBar.modules[idx + 1], topBar.modules[idx]] = [topBar.modules[idx], topBar.modules[idx + 1]]; redrawModules(); });
        const removeBtn = el("button", { class: "btn sm danger", type: "button", text: "✕" });
        removeBtn.addEventListener("click", () => { topBar.modules.splice(idx, 1); redrawModules(); });

        table.append(el("tr", null,
          el("td", null, idSelect), el("td", null, sideSelect),
          el("td", null, el("div", { class: "rowflex" }, upBtn, downBtn, removeBtn))));
      });
    };
    redrawModules();
    const addModuleBtn = el("button", { class: "btn sm", type: "button", text: t("addModule") });
    addModuleBtn.addEventListener("click", () => { topBar.modules.push({ id: "spacer", side: "right" }); redrawModules(); });
    box.append(table, addModuleBtn);

    box.append(el("div", { class: "rowflex" }, el("button", { class: "btn primary", type: "button", text: t("taskbarApply"), onclick: applyAll })));

    return box;
  }

  render();
}
