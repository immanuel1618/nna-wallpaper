// settings/pages/planner.js — "Planner" page (θ): profile card (avatar/name/tier/session),
// today/week stats, TASKS block section toggles, and voice/push-to-talk settings.
//
// Per the owner's brief this page does not touch settings/i18n.js — every new string lives in the
// local STR dict below; only the handful of keys the old page already used from the shared i18n
// (statusSaved, statusError, loginTelegram, logout, show_*) are still read through ctx.t, since
// they already exist there and are shared with other pages/the wallpaper block.

import { el, groupCard, settingRow } from "../dom.js";

const STR = {
  ru: {
    profile: "Профиль",
    loginHint: "Войдите через Telegram, чтобы включить блок TASKS на обоях.",
    openWeb: "Открыть веб-планировщик",
    openBot: "Открыть бота",
    sessionUntil: "Сессия до",
    stats: "Статистика",
    rangeDay: "Сегодня",
    rangeWeek: "Неделя",
    statTasks: "Задачи",
    statHabits: "Привычки",
    statMeetings: "Встречи",
    statMoney: "Деньги",
    streakShort: "серия",
    statsEmpty: "Нет данных",
    tasksBlock: "Блок TASKS",
    maxTasks: "Число задач",
    refreshInterval: "Интервал обновления",
    voice: "Голос",
    micDevice: "Микрофон",
    micDefault: "По умолчанию",
    hotkey: "Горячая клавиша",
    hotkeyHint: "Нажмите кнопку и затем комбинацию клавиш",
    hotkeyCapture: "Нажмите клавиши…",
    hotkeyEsc: "Esc: отмена",
    hotkeyOk: "Хоткей зарегистрирован",
    hotkeyFail: "Не удалось зарегистрировать хоткей",
    showRecognized: "Показывать распознанный текст перед отправкой",
    testMic: "Проверить микрофон",
    testMicListening: "Слушаю…",
    testMicNoSupport: "Микрофон недоступен в этом окне",
    testMicDenied: "Нет доступа к микрофону",
  },
  en: {
    profile: "Profile",
    loginHint: "Log in with Telegram to enable the TASKS block on the wallpaper.",
    openWeb: "Open web planner",
    openBot: "Open the bot",
    sessionUntil: "Session until",
    stats: "Stats",
    rangeDay: "Today",
    rangeWeek: "Week",
    statTasks: "Tasks",
    statHabits: "Habits",
    statMeetings: "Meetings",
    statMoney: "Money",
    streakShort: "streak",
    statsEmpty: "No data",
    tasksBlock: "TASKS block",
    maxTasks: "Task count",
    refreshInterval: "Refresh interval",
    voice: "Voice",
    micDevice: "Microphone",
    micDefault: "Default",
    hotkey: "Hotkey",
    hotkeyHint: "Click the button, then press a key combination",
    hotkeyCapture: "Press keys…",
    hotkeyEsc: "Esc to cancel",
    hotkeyOk: "Hotkey registered",
    hotkeyFail: "Could not register the hotkey",
    showRecognized: "Show recognized text before sending",
    testMic: "Test microphone",
    testMicListening: "Listening…",
    testMicNoSupport: "Microphone not available in this window",
    testMicDenied: "Microphone access denied",
  },
};

export function keywords(lang) {
  return lang === "en"
    ? ["telegram", "login", "logout", "tasks", "meetings", "habits", "money", "briefing", "profile", "avatar", "stats", "voice", "hotkey", "microphone", "push to talk"]
    : ["телеграм", "вход", "выход", "задачи", "встречи", "привычки", "финансы", "брифинг", "профиль", "аватар", "статистика", "голос", "хоткей", "горячая клавиша", "микрофон"];
}

function ensureCss() {
  if (document.getElementById("planner-css")) return;
  const link = document.createElement("link");
  link.id = "planner-css";
  link.rel = "stylesheet";
  link.href = "/settings/planner.css";
  document.head.appendChild(link);
}

function initialsEl(name) {
  const parts = String(name || "").trim().split(/\s+/).filter(Boolean);
  const letters = parts.slice(0, 2).map((p) => p[0].toUpperCase()).join("") || "?";
  return el("div", { class: "pl-avatar-fallback", text: letters });
}

// JS KeyboardEvent.code -> the System.Windows.Input.Key name NNA.Wallpaper.Hotkeys parses
// server-side (src/NNA.Wallpaper/Hotkeys.cs TryParseHotkey). Keep these two in sync.
function wpfKeyName(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[0-9]$/.test(code)) return "D" + code.slice(5);
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
  const map = {
    Space: "Space", Escape: "Escape", Tab: "Tab", Enter: "Return", Backspace: "Back",
    ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
    Insert: "Insert", Delete: "Delete", Home: "Home", End: "End",
    PageUp: "Prior", PageDown: "Next", CapsLock: "CapsLock",
  };
  return map[code] || null;
}

function startHotkeyCapture(btn, s, onDone) {
  const original = btn.textContent;
  btn.textContent = s.hotkeyCapture;
  btn.classList.add("is-listening");

  function finish(combo) {
    window.removeEventListener("keydown", onKeyDown, true);
    btn.classList.remove("is-listening");
    if (combo) onDone(combo);
    else btn.textContent = original;
  }

  function onKeyDown(e) {
    e.preventDefault();
    e.stopPropagation();
    if (e.key === "Escape") { finish(null); return; }
    if (/^(Control|Shift|Alt|Meta)(Left|Right)?$/.test(e.code)) return; // wait for the non-modifier key
    const main = wpfKeyName(e.code);
    if (!main) return;
    const parts = [];
    if (e.ctrlKey) parts.push("Ctrl");
    if (e.shiftKey) parts.push("Shift");
    if (e.altKey) parts.push("Alt");
    if (e.metaKey) parts.push("Win");
    parts.push(main);
    finish(parts.join("+"));
  }

  window.addEventListener("keydown", onKeyDown, true);
}

function formatMoney(minor, currency) {
  const major = (minor || 0) / 100;
  const sign = major > 0 ? "+" : "";
  return sign + Math.round(major) + " " + (currency || "");
}

function statTile(num, label) {
  return el("div", { class: "pl-stat-card" }, el("div", { class: "pl-stat-num", text: num }), el("div", { class: "pl-stat-label", text: label }));
}

// ── profile card ──────────────────────────────────────────────────────────────────────────────

function renderProfileCard(status, profile, s, ctx, refresh) {
  const card = groupCard("profile", s.profile);

  if (!status || !status.loggedIn) {
    card.append(
      el("div", { class: "empty" }, el("div", { class: "txt", text: s.loginHint })),
      el("div", { class: "rowflex" },
        el("button", {
          class: "btn primary", type: "button", text: ctx.t("loginTelegram"),
          onclick: async () => { try { await ctx.api("POST", "/app/login"); } catch (err) { ctx.onStatus(ctx.t("statusError", err.message), true); } },
        })));
    return card;
  }

  const p = profile || {};
  const name = p.first_name || p.display_name || "";
  const botUsername = (ctx.config.app.planner && ctx.config.app.planner.botUsername) || "NNAplanner_bot";

  const avatarWrap = el("div", { class: "pl-avatar-wrap" });
  if (p.avatar) {
    const img = el("img", { class: "pl-avatar", src: p.avatar, alt: "" });
    img.addEventListener("error", () => { img.remove(); avatarWrap.append(initialsEl(name)); });
    avatarWrap.append(img);
  } else {
    avatarWrap.append(initialsEl(name));
  }

  const expires = p.expires_at ? new Date(p.expires_at * 1000) : null;
  const locale = ctx.lang === "en" ? "en-US" : "ru-RU";
  const info = el("div", { class: "pl-profile-info" },
    el("div", { class: "pl-profile-name", text: name || "·" }),
    p.username ? el("div", { class: "pl-profile-username", text: "@" + p.username }) : null,
    el("div", { class: "pl-profile-meta" },
      el("span", { class: "tag", text: (p.tier || "free").toUpperCase() }),
      expires ? el("span", { class: "pl-profile-expiry", text: s.sessionUntil + " " + expires.toLocaleString(locale) }) : null));

  card.append(
    el("div", { class: "pl-profile-row" }, avatarWrap, info),
    el("div", { class: "rowflex" },
      el("a", { class: "btn ghost sm", href: "https://planner.nna1618.com", target: "_blank", rel: "noreferrer", text: s.openWeb }),
      el("a", { class: "btn ghost sm", href: "https://t.me/" + botUsername, target: "_blank", rel: "noreferrer", text: s.openBot }),
      el("span", { class: "sp" }),
      el("button", {
        class: "btn sm", type: "button", text: ctx.t("logout"),
        onclick: async () => { try { await ctx.api("POST", "/planner/logout"); refresh(); } catch (err) { ctx.onStatus(ctx.t("statusError", err.message), true); } },
      })));
  return card;
}

// ── stats card: own range state (day/week), re-fetches /planner/stats on toggle ────────────────

function renderStatsCard(s, ctx) {
  const card = groupCard("stats", s.stats);
  const rangeMount = el("div");
  const body = el("div", { class: "pl-stats-grid" });
  card.append(el("div", { class: "pl-stats-head" }, el("span", { class: "sp" }), rangeMount), body);

  let range = "day";
  window.NNAUI.segmented(rangeMount, {
    items: [{ value: "day", label: s.rangeDay }, { value: "week", label: s.rangeWeek }],
    value: range,
    onChange: (v) => { range = v; load(); },
  });

  async function load() {
    body.innerHTML = "";
    let stats = null;
    try { stats = await ctx.api("GET", "/planner/stats?range=" + range); } catch { stats = null; }
    if (!stats) { body.append(el("div", { class: "pl-hint", text: s.statsEmpty })); return; }
    if (stats.tasks) body.append(statTile(stats.tasks.done + "/" + stats.tasks.total, s.statTasks));
    if (stats.habits) {
      const label = stats.habits.streak ? s.statHabits + " · " + stats.habits.streak + " " + s.streakShort : s.statHabits;
      body.append(statTile(stats.habits.done + "/" + stats.habits.total, label));
    }
    if (typeof stats.meetings === "number") body.append(statTile(String(stats.meetings), s.statMeetings));
    if (stats.money) body.append(statTile(formatMoney(stats.money.sum, stats.money.currency), s.statMoney));
  }
  load();

  return card;
}

// ── TASKS sections + block tuning ────────────────────────────────────────────────────────────

function renderSectionsCard(s, ctx) {
  const card = groupCard("show", s.tasksBlock);

  function save(patch) {
    const next = { ...ctx.config.app.planner, ...patch };
    return ctx.put({ app: { planner: next } }).then(() => {
      ctx.config.app.planner = next;
      ctx.onStatus(ctx.t("statusSaved"), false);
    }).catch((err) => ctx.onStatus(ctx.t("statusError", err.message), true));
  }

  const show = new Set(ctx.config.app.planner?.show || []);
  for (const key of ["tasks", "meetings", "habits", "money", "briefing"]) {
    const mount = el("div");
    card.append(settingRow(ctx.t("show_" + key), null, mount));
    window.NNAUI.toggle(mount, {
      checked: show.has(key),
      onChange: (on) => { if (on) show.add(key); else show.delete(key); save({ show: [...show] }); },
    });
  }

  const maxMount = el("div", { class: "ui-slider-mount" });
  card.append(settingRow(s.maxTasks, null, maxMount));
  window.NNAUI.slider(maxMount, {
    min: 3, max: 13, step: 1, value: ctx.config.app.planner?.maxTasks || 8,
    format: (v) => String(Math.round(v)),
    onChange: (v) => save({ maxTasks: Math.round(v) }),
  });

  const refreshMount = el("div");
  card.append(settingRow(s.refreshInterval, null, refreshMount));
  window.NNAUI.segmented(refreshMount, {
    items: [{ value: 30, label: "30s" }, { value: 60, label: "60s" }, { value: 120, label: "120s" }],
    value: ctx.config.app.planner?.refreshSec || 60,
    onChange: (v) => save({ refreshSec: v }),
  });

  return card;
}

// ── voice / push-to-talk ─────────────────────────────────────────────────────────────────────

function renderVoiceCard(s, ctx, status) {
  const card = groupCard("voice", s.voice);

  function save(patch) {
    const next = { ...ctx.config.app.planner, ...patch };
    return ctx.put({ app: { planner: next } }).then(() => {
      ctx.config.app.planner = next;
      ctx.onStatus(ctx.t("statusSaved"), false);
    }).catch((err) => ctx.onStatus(ctx.t("statusError", err.message), true));
  }

  // microphone: same GET/PUT /audio/capture-device the wallpaper block's mic picker uses (and the
  // hotkey's host-side VoiceCaptureService reads), so one choice here drives both.
  const micMount = el("div");
  card.append(settingRow(s.micDevice, null, micMount));
  (async () => {
    let devices = { capture: [] };
    let current = { name: null };
    try { devices = await ctx.api("GET", "/audio/devices"); } catch { /* leave empty */ }
    try { current = await ctx.api("GET", "/audio/capture-device"); } catch { /* leave null */ }
    const options = [
      { value: "", label: s.micDefault },
      ...(devices.capture || []).map((d) => ({ value: d.name, label: d.name + (d.default ? " (" + s.micDefault.toLowerCase() + ")" : "") })),
    ];
    window.NNAUI.select(micMount, {
      options, value: current.name || "",
      onChange: async (v) => {
        try { await ctx.api("PUT", "/audio/capture-device", { name: v || null }); ctx.onStatus(ctx.t("statusSaved"), false); }
        catch (err) { ctx.onStatus(ctx.t("statusError", err.message), true); }
      },
    });
  })();

  // hotkey capture field
  const hotkeyRow = el("div", { class: "rowflex" });
  card.append(settingRow(s.hotkey, s.hotkeyHint, hotkeyRow));
  const hotkeyBtn = el("button", { class: "btn pl-hotkey-btn", type: "button", text: ctx.config.app.planner?.hotkey || "Ctrl+Shift+Space" });
  hotkeyRow.append(hotkeyBtn);
  hotkeyBtn.addEventListener("click", () => {
    startHotkeyCapture(hotkeyBtn, s, (combo) => {
      hotkeyBtn.textContent = combo;
      save({ hotkey: combo });
    });
  });

  const hk = status && status.hotkey;
  if (hk) {
    card.append(el("div", {
      class: "pl-hotkey-status " + (hk.registered ? "ok" : "err"),
      text: hk.registered ? s.hotkeyOk : s.hotkeyFail + (hk.error ? " (" + hk.error + ")" : ""),
    }));
  }

  // show recognized text before sending
  const recMount = el("div");
  card.append(settingRow(s.showRecognized, null, recMount));
  window.NNAUI.toggle(recMount, {
    checked: ctx.config.app.planner?.showRecognizedText !== false,
    onChange: (on) => save({ showRecognizedText: on }),
  });

  // test microphone: 3s level meter via getUserMedia in this window (SettingsWindow grants mic
  // permission itself, see src/NNA.Wallpaper/SettingsWindow.xaml.cs PermissionRequested)
  const levelBar = el("div", { class: "pl-level" }, el("div", { class: "pl-level-fill" }));
  const testBtn = el("button", { class: "btn sm", type: "button", text: s.testMic });
  card.append(settingRow(s.testMic, null, el("div", { class: "pl-level-row" }, testBtn, levelBar)));
  testBtn.addEventListener("click", () => runMicTest(testBtn, levelBar, s, ctx));

  return card;
}

function runMicTest(btn, levelBar, s, ctx) {
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    ctx.onStatus(s.testMicNoSupport, true);
    return;
  }
  btn.disabled = true;
  const originalText = btn.textContent;
  btn.textContent = s.testMicListening;

  navigator.mediaDevices.getUserMedia({ audio: true }).then((stream) => {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    const actx = new AudioCtx();
    const source = actx.createMediaStreamSource(stream);
    const analyser = actx.createAnalyser();
    analyser.fftSize = 256;
    source.connect(analyser);
    const data = new Uint8Array(analyser.frequencyBinCount);
    const fill = levelBar.firstChild;
    const until = Date.now() + 3000;

    function tick() {
      analyser.getByteFrequencyData(data);
      let sum = 0;
      for (let i = 0; i < data.length; i++) sum += data[i];
      const avg = sum / data.length / 255;
      fill.style.width = Math.min(100, avg * 220) + "%";
      if (Date.now() < until) {
        requestAnimationFrame(tick);
      } else {
        stream.getTracks().forEach((t) => t.stop());
        actx.close();
        fill.style.width = "0%";
        btn.disabled = false;
        btn.textContent = originalText;
      }
    }
    tick();
  }).catch((err) => {
    const denied = err && (err.name === "NotAllowedError" || err.name === "SecurityError");
    ctx.onStatus(denied ? s.testMicDenied : ctx.t("statusError", err.message || String(err)), true);
    btn.disabled = false;
    btn.textContent = originalText;
  });
}

// ── page ──────────────────────────────────────────────────────────────────────────────────────

export async function render(container, ctx) {
  ensureCss();
  const lang = ctx.lang === "en" ? "en" : "ru";
  const s = STR[lang];
  const page = el("div", { class: "page-narrow pl-page" });
  container.append(page);

  const refresh = () => { container.innerHTML = ""; render(container, ctx); };

  let status = null;
  try { status = await ctx.api("GET", "/planner/status"); } catch { status = null; }
  let profile = null;
  if (status && status.loggedIn) {
    try { profile = await ctx.api("GET", "/planner/profile"); } catch { profile = null; }
  }

  page.append(renderProfileCard(status, profile, s, ctx, refresh));
  if (status && status.loggedIn) page.append(renderStatsCard(s, ctx));
  page.append(renderSectionsCard(s, ctx));
  page.append(renderVoiceCard(s, ctx, status));
}
