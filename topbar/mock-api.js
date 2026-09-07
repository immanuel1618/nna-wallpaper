/* NNA1618 — заглушка хостового API для разработки topbar/popup без готового бэкенда.
   Включается параметром ?mock=1 в URL страницы (topbar/index.html или topbar/popup/index.html).
   Патчит window.fetch: запросы к путям из контракта стадии 8B (см. docs/TOPBAR.md) отвечают
   каноническими JSON-заглушками из памяти (PUT/POST меняют состояние, следующий GET его видит).
   Всё остальное (WS /events, любой путь вне списка) уходит в настоящий fetch без изменений —
   WS /events уже реализован хостом, так что живые события можно получать и под mock=1.
   ES5-стиль, без сборщика, без внешних зависимостей. Финальная страница ходит в настоящие
   маршруты — этот файл не подключается вне разработки/тестов (index.html грузит его безусловно,
   но он не трогает fetch, если в URL нет mock=1). */
(function () {
  'use strict';

  function qs(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  if (qs('mock') !== '1') return;
  if (!window.fetch || window.fetch.__nnaMocked) return;

  var realFetch = window.fetch.bind(window);

  /* ---- состояние, живёт на время жизни страницы ------------------------- */
  var state = {
    audio: {
      volume: 42, muted: false,
      device: { id: 'spk-1', name: 'Realtek — Динамики' },
    },
    outputs: [
      { id: 'spk-1', name: 'Realtek — Динамики', default: true },
      { id: 'spk-2', name: 'NNA Monitor — HDMI', default: false },
      { id: 'hp-1', name: 'Наушники (Bluetooth)', default: false },
    ],
    sessions: [
      { id: 'sess-yandex', pid: 4110, name: 'Яндекс Музыка', icon: '', volume: 78, muted: false, peak: 0.34, system: false },
      { id: 'sess-chrome', pid: 8820, name: 'Chrome', icon: '', volume: 55, muted: false, peak: 0.05, system: false },
      { id: 'sess-system', pid: 4, name: 'Системные звуки', icon: '', volume: 60, muted: false, peak: 0, system: true },
    ],
    mic: { volume: 65, muted: true, device: { id: 'mic-1', name: 'Микрофон (USB)' }, peak: 0.0 },
    network: { up: true, kind: 'wifi', name: 'NNA1618-5G', signal: 82, ipv4: '192.168.1.42' },
    battery: { present: false, percent: 100, charging: false, remainingMin: null },
    layout: { lang: 'ru', hkl: '00000419' },
    brightness: { supported: true, monitors: [{ id: 'main', name: 'Main', value: 70 }] },
    dnd: { supported: true, on: false },
    health: {
      ok: true, app: 'NNA Wallpaper', version: '0.3.0', app_version: '0.3.0', installed: true,
      monitors: [{ id: 'main', name: 'Main', width: 1920, height: 1080, x: 0, y: 0, visible: true, paused: false, scale: 1 }],
    },
    events: {
      events: [
        { label: 'ДЕНЬ РОЖДЕНИЯ', date: futureDate(9) },
        { label: 'NEW YEAR', date: '2027-01-01' },
      ],
      daily: [{ label: 'SLEEP', time: '23:30' }],
    },
    plannerToday: {
      tasks: [{ id: 't1', title: 'Проверить релиз v0.3', state: 'open', overdue: false }],
      meetings: [
        { id: 'm1', title: 'Синк по topbar', starts_at: todayAt(15, 0) },
        { id: 'm2', title: 'Ревью дизайна', starts_at: todayAt(18, 30) },
      ],
      briefing: 'Сегодня фокус на поповерах верхней строки: громкость, календарь, меню NNA, Control Center.',
    },
    taskbarPresets: {
      builtin: [
        { id: 'windows', file: 'windows.json', name: { ru: 'Как в Windows', en: 'Windows-like' } },
        { id: 'mac', file: 'mac.json', name: { ru: 'Как на Mac', en: 'Mac-like' } },
        { id: 'minimal', file: 'minimal.json', name: { ru: 'Минимум', en: 'Minimal' } },
        { id: 'clear', file: 'clear.json', name: { ru: 'Прозрачная', en: 'Clear' } },
        { id: 'night', file: 'night.json', name: { ru: 'Ночная', en: 'Night' } },
      ],
      user: [],
    },
    taskbarStatus: { note: 'Пресет: mac' },
    updates: { ok: true, installed: true, current: '0.3.0', available: null, message: 'ПОСЛЕДНЯЯ ВЕРСИЯ' },
    config: null, /* заполняется из настоящего /config при первом запросе (см. ниже) */
  };

  function futureDate(days) {
    var d = new Date(Date.now() + days * 86400000);
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
  }
  function todayAt(h, m) {
    var d = new Date();
    d.setHours(h, m, 0, 0);
    return d.toISOString();
  }
  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function json(body, status) {
    return new Response(JSON.stringify(body), {
      status: status || 200,
      headers: { 'Content-Type': 'application/json; charset=utf-8' },
    });
  }

  function parsePath(url) {
    try {
      var u = new URL(url, location.href);
      return { path: u.pathname, query: u.searchParams };
    } catch (e) {
      var qIdx = url.indexOf('?');
      return { path: qIdx === -1 ? url : url.slice(0, qIdx), query: new URLSearchParams(qIdx === -1 ? '' : url.slice(qIdx)) };
    }
  }

  function readBody(init) {
    if (!init || !init.body) return {};
    try { return JSON.parse(init.body); } catch (e) { return {}; }
  }

  /* ---- маршруты ----------------------------------------------------------- */
  var ROUTES = [
    { m: 'GET', p: '/audio/volume', h: function () {
      return json({ volume: state.audio.volume, muted: state.audio.muted, device: state.audio.device });
    } },
    { m: 'PUT', p: '/audio/volume', h: function (req) {
      var b = readBody(req.init);
      if (typeof b.volume === 'number') state.audio.volume = Math.max(0, Math.min(100, Math.round(b.volume)));
      if (typeof b.muted === 'boolean') state.audio.muted = b.muted;
      return json({ volume: state.audio.volume, muted: state.audio.muted, device: state.audio.device });
    } },
    { m: 'GET', p: '/audio/outputs', h: function () { return json({ devices: state.outputs }); } },
    { m: 'PUT', p: '/audio/output', h: function (req) {
      var b = readBody(req.init);
      state.outputs.forEach(function (d) { d.default = d.id === b.id; });
      var picked = state.outputs.filter(function (d) { return d.id === b.id; })[0];
      if (picked) state.audio.device = { id: picked.id, name: picked.name };
      return json({ ok: true, devices: state.outputs });
    } },
    { m: 'GET', p: '/audio/sessions', h: function () { return json({ sessions: state.sessions }); } },
    { m: 'PUT', p: '/audio/session', h: function (req) {
      var b = readBody(req.init);
      var s = state.sessions.filter(function (x) { return x.id === b.id; })[0];
      if (s) {
        if (typeof b.volume === 'number') s.volume = Math.max(0, Math.min(100, Math.round(b.volume)));
        if (typeof b.muted === 'boolean') s.muted = b.muted;
      }
      return json({ ok: true, sessions: state.sessions });
    } },
    { m: 'GET', p: '/audio/mic', h: function () { return json(state.mic); } },
    { m: 'PUT', p: '/audio/mic', h: function (req) {
      var b = readBody(req.init);
      if (typeof b.volume === 'number') state.mic.volume = Math.max(0, Math.min(100, Math.round(b.volume)));
      if (typeof b.muted === 'boolean') state.mic.muted = b.muted;
      return json(state.mic);
    } },
    { m: 'GET', p: '/system/network', h: function () { return json(state.network); } },
    { m: 'GET', p: '/system/battery', h: function () { return json(state.battery); } },
    { m: 'GET', p: '/system/layout', h: function () { return json(state.layout); } },
    { m: 'GET', p: '/system/brightness', h: function () { return json(state.brightness); } },
    { m: 'PUT', p: '/system/brightness', h: function (req) {
      var b = readBody(req.init);
      state.brightness.monitors.forEach(function (mon) {
        if (!b.id || mon.id === b.id) mon.value = Math.max(0, Math.min(100, Math.round(b.value)));
      });
      return json(state.brightness);
    } },
    { m: 'GET', p: '/system/dnd', h: function () { return json(state.dnd); } },
    { m: 'PUT', p: '/system/dnd', h: function (req) {
      var b = readBody(req.init);
      if (typeof b.on === 'boolean') state.dnd.on = b.on;
      return json(state.dnd);
    } },
    { m: 'POST', p: '/system/power', h: function (req) {
      var b = readBody(req.init);
      return json({ ok: true, action: b.action, confirm: !!b.confirm });
    } },
    { m: 'GET', p: '/health', h: function () { return json(state.health); } },
    { m: 'GET', p: '/events', h: function () { return json(state.events); } },
    { m: 'GET', p: '/planner/today', h: function () { return json(state.plannerToday); } },
    { m: 'GET', p: '/taskbar/presets', h: function () { return json(state.taskbarPresets); } },
    { m: 'GET', p: '/taskbar/status', h: function () { return json(state.taskbarStatus); } },
    { m: 'POST', p: '/taskbar/preset', h: function (req) {
      var b = readBody(req.init);
      state.taskbarStatus = { note: 'Пресет: ' + (b.id || '—') };
      return json({ ok: true });
    } },
    { m: 'POST', p: '/app/settings', h: function () { return json({ ok: true }); } },
    { m: 'POST', p: '/app/pause', h: function () {
      state.health.monitors.forEach(function (mm) { mm.paused = true; });
      return json({ ok: true });
    } },
    { m: 'POST', p: '/app/resume', h: function () {
      state.health.monitors.forEach(function (mm) { mm.paused = false; });
      return json({ ok: true });
    } },
    { m: 'POST', p: '/app/check-updates', h: function () { return json(state.updates); } },
    { m: 'POST', p: '/app/update', h: function () { return json({ ok: true }); } },
  ];

  function findRoute(method, path) {
    for (var i = 0; i < ROUTES.length; i++) {
      if (ROUTES[i].m === method && ROUTES[i].p === path) return ROUTES[i];
    }
    return null;
  }

  window.fetch = function (input, init) {
    var url = typeof input === 'string' ? input : (input && input.url) || '';
    var method = ((init && init.method) || (typeof input === 'object' && input.method) || 'GET').toUpperCase();
    var parsed = parsePath(url);

    /* /config: не мокаем содержимое (тема/токен приходят из настоящего конфига хоста), просто
       не даём странице упасть, если хост ещё не поднят — отдаём минимальный рабочий конфиг. */
    if (method === 'GET' && parsed.path === '/config') {
      return realFetch(input, init).catch(function () {
        return json({
          token: 'mock', language: 'ru',
          theme: { palette: {}, fonts: { display: 'Roboto Flex', mono: 'JetBrains Mono' } },
          topbar: {}, topBar: {},
        });
      });
    }

    var route = findRoute(method, parsed.path);
    if (route) {
      try { return Promise.resolve(route.h({ init: init, query: parsed.query })); }
      catch (e) { return Promise.resolve(json({ ok: false, error: String(e) }, 500)); }
    }
    return realFetch(input, init);
  };
  window.fetch.__nnaMocked = true;
})();
