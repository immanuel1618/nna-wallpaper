// settings/pages/general.js — "General" page (ι): autostart, language, API port, updates, the
// planner voice block's microphone pick (new — GET/PUT /audio/capture-device, see
// AudioDevicesService.cs), import from folder, data/log folders. Carried over from the old
// renderEpsilon; version/links moved out to their own about.js page.

import { el, groupCard, settingRow } from "../dom.js";

export function keywords(lang) {
  return lang === "en"
    ? ["autostart", "language", "port", "updates", "microphone", "import", "data folder", "log"]
    : ["автозапуск", "язык", "порт", "обновления", "микрофон", "импорт", "папка данных", "лог"];
}

export async function render(container, ctx) {
  const { t } = ctx;
  const app = ctx.config.app;
  const page = el("div", { class: "page-wide" });
  container.append(page);

  // ── general ──────────────────────────────────────────────────────────
  const autostartMount = el("div");
  const langMount = el("div");
  const portInput = el("input", { type: "number", value: app.apiPort });
  const general = groupCard("general", t("generalSection"),
    settingRow(t("autostart"), null, autostartMount),
    settingRow(t("language"), null, langMount),
    settingRow(t("apiPort"), t("apiPortHint"), el("div", { class: "ui-field" }, portInput)));

  window.NNAUI.toggle(autostartMount, {
    checked: !!app.autostart,
    onChange: async (on) => {
      try { await ctx.put({ app: { autostart: on } }); app.autostart = on; ctx.onStatus(t("statusSaved"), false); }
      catch (err) { ctx.onStatus(t("statusError", err.message), true); }
    },
  });
  window.NNAUI.select(langMount, {
    value: ctx.lang,
    options: [{ value: "ru", label: "RU" }, { value: "en", label: "EN" }],
    onChange: (v) => ctx.setLang(v),
  });
  portInput.addEventListener("change", async (e) => {
    const port = Number(e.target.value);
    try { await ctx.put({ app: { apiPort: port } }); app.apiPort = port; ctx.onStatus(t("statusSaved"), false); }
    catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  });

  // ── microphone ───────────────────────────────────────────────────────
  const micMount = el("div");
  const micHint = el("div", { class: "hint", text: t("micHint") });
  const micGroup = groupCard("mic", t("micSection"), settingRow(t("micDevice"), null, micMount), micHint);

  (async () => {
    let devices = { capture: [] };
    let current = { name: null };
    try { devices = await ctx.api("GET", "/audio/devices"); } catch { /* leave empty */ }
    try { current = await ctx.api("GET", "/audio/capture-device"); } catch { /* leave null */ }
    const options = [{ value: "", label: t("micDefault") }, ...(devices.capture || []).map((d) => ({ value: d.name, label: d.name + (d.default ? " (" + t("micDefault").toLowerCase() + ")" : "") }))];
    window.NNAUI.select(micMount, {
      value: current.name || "",
      options,
      onChange: async (v) => {
        try { await ctx.api("PUT", "/audio/capture-device", { name: v || null }); ctx.onStatus(t("statusSaved"), false); }
        catch (err) { ctx.onStatus(t("statusError", err.message), true); }
      },
    });
  })();

  // ── updates ──────────────────────────────────────────────────────────
  const channelMount = el("div");
  const updateResult = el("span", { class: "tag" });
  const checkBtn = el("button", {
    class: "ui-btn", type: "button", text: t("checkNow"),
    onclick: async () => {
      try { const r = await ctx.api("POST", "/app/check-updates"); updateResult.textContent = JSON.stringify(r); }
      catch (err) { updateResult.textContent = err.message; }
    },
  });
  const updates = groupCard("updates", t("updates"),
    settingRow(t("channel"), null, channelMount),
    el("div", { class: "rowflex" }, checkBtn, updateResult));
  window.NNAUI.select(channelMount, { value: app.updates?.channel || "stable", options: [{ value: "stable", label: t("channelStable") }], onChange: async (v) => {
    const upd = { ...app.updates, channel: v };
    try { await ctx.put({ app: { updates: upd } }); app.updates = upd; ctx.onStatus(t("statusSaved"), false); }
    catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  } });

  // ── import / folders ────────────────────────────────────────────────
  const importInput = el("input", { type: "text", placeholder: "C:\\path\\to\\folder" });
  const importResult = el("span", { class: "tag" });
  const foldersGroup = groupCard("folders", t("importFolder"),
    el("div", { class: "field-row" },
      el("div", { class: "ui-field" }, importInput),
      el("button", {
        class: "ui-btn", type: "button", text: t("importBtn"),
        onclick: async () => {
          try { await ctx.api("POST", "/app/import?dir=" + encodeURIComponent(importInput.value)); importResult.textContent = t("statusSaved"); }
          catch (err) { importResult.textContent = err.status === 404 ? t("importHint") : err.message; }
        },
      })), importResult,
    el("div", { class: "rowflex" },
      el("button", { class: "ui-btn", type: "button", text: t("openDataFolder"), onclick: () => ctx.api("POST", "/config/open-folder?what=data").catch(() => {}) }),
      el("button", { class: "ui-btn", type: "button", text: t("openLog"), onclick: () => ctx.api("POST", "/config/open-folder?what=logs").catch(() => {}) })));

  page.append(general, micGroup, updates, foldersGroup);
}
