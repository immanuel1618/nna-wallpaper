/* NNA1618 — модули верхней строки: brand, date, clock, weather, stats, media, planner.
   Каждый модуль — фабрика window.TopBarModules[id] = function (el, ctx) { ...; return { start, stop }; }.
   el — уже созданный контейнер <div class="tb-mod tb-<id>"> (bar.js добавляет tb-first/tb-sep сам).
   ctx.get/ctx.post — fetch-хелпер из bar.js (GET без токена, POST с ?t=<token>).
   ctx.setVisible(bool) — показать/скрыть модуль (спейсер и раскладку зон пересчитывает bar.js).
   ES5-стиль, без сборщика. */
(function () {
  'use strict';

  window.TopBarModules = window.TopBarModules || {};

  function pad2(n) { n = Math.floor(n); return (n < 10 ? '0' : '') + n; }
  function dot() { var s = document.createElement('span'); s.className = 'tb-dot'; s.textContent = '·'; return s; }
  function text(s) { return document.createTextNode(s == null ? '' : String(s)); }

  var RU_DAYS = ['ВС', 'ПН', 'ВТ', 'СР', 'ЧТ', 'ПТ', 'СБ'];
  var RU_MONTHS = ['ЯНВ', 'ФЕВ', 'МАР', 'АПР', 'МАЙ', 'ИЮН', 'ИЮЛ', 'АВГ', 'СЕН', 'ОКТ', 'НОЯ', 'ДЕК'];
  var EN_DAYS = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
  var EN_MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];

  var ICON = {
    play: 'M8 5v14l11-7z',
    pause: 'M6 5h4v14H6zm8 0h4v14h-4z'
  };

  var SVG_NS = 'http://www.w3.org/2000/svg';
  function svgTag(tag, attrs) {
    var el = document.createElementNS(SVG_NS, tag);
    for (var k in attrs) { if (attrs.hasOwnProperty(k)) el.setAttribute(k, attrs[k]); }
    return el;
  }
  function svgRoot(viewBox) { return svgTag('svg', { viewBox: viewBox || '0 0 24 24' }); }

  /* ---- brand: «NNA», клик -> POST /app/settings -------------------------- */
  window.TopBarModules.brand = function (el, ctx) {
    el.classList.add('tb-clickable');
    el.appendChild(text('NNA'));
    el.addEventListener('click', function () { ctx.post('/app/settings').catch(function () {}); });
    return { start: function () {}, stop: function () {} };
  };

  /* ---- date: «ПН · 07 СЕН», клик -> POST /app/settings?tab=layout -------- */
  window.TopBarModules.date = function (el, ctx) {
    el.classList.add('tb-clickable');
    el.addEventListener('click', function () { ctx.post('/app/settings?tab=layout').catch(function () {}); });
    var timer = null;

    function render() {
      var d = new Date();
      var days = ctx.lang === 'en' ? EN_DAYS : RU_DAYS;
      var months = ctx.lang === 'en' ? EN_MONTHS : RU_MONTHS;
      el.textContent = days[d.getDay()] + ' · ' + pad2(d.getDate()) + ' ' + months[d.getMonth()];
    }

    return {
      start: function () { render(); clearInterval(timer); timer = setInterval(render, 30000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- clock: HH:MM, раз в секунду, без секунд; клик -> поповер calendar ---- */
  window.TopBarModules.clock = function (el, ctx) {
    el.classList.add('tb-clickable');
    el.addEventListener('click', function () { ctx.openPopup('calendar'); });
    var timer = null;
    function render() {
      var d = new Date();
      el.textContent = pad2(d.getHours()) + ':' + pad2(d.getMinutes());
    }
    return {
      start: function () { render(); clearInterval(timer); timer = setInterval(render, 1000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- weather: «14° · ROSTOV-ON-DON», GET /weather раз в 10 мин --------- */
  window.TopBarModules.weather = function (el, ctx) {
    var timer = null;
    function render(w) {
      el.textContent = '';
      if (!w || w.ok === false) { el.appendChild(text('—')); return; }
      el.appendChild(text(Math.round(w.temp) + '°'));
      el.appendChild(dot());
      el.appendChild(text(String(w.name || '').toUpperCase()));
    }
    function poll() {
      ctx.get('/weather').then(render, function () { render(null); });
    }
    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 600000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- stats: «CPU 9% · RAM 21% · GPU 31%», GET /stats раз в 2 с --------- */
  window.TopBarModules.stats = function (el, ctx) {
    var timer = null;
    function render(s) {
      el.textContent = '';
      if (!s) { el.appendChild(text('—')); return; }
      var cpu = s.cpu || {}, mem = s.mem || {}, gpu = s.gpu || {};
      el.appendChild(text('CPU ' + Math.round(cpu.percent || 0) + '%'));
      el.appendChild(dot());
      el.appendChild(text('RAM ' + Math.round(mem.percent || 0) + '%'));
      if (gpu && gpu.ok) {
        el.appendChild(dot());
        el.appendChild(text('GPU ' + Math.round(gpu.util || 0) + '%'));
      }
    }
    function poll() {
      ctx.get('/stats').then(render, function () {});
    }
    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 2000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- media: «[icon] Title — Artist», GET /media раз в 2 с -------------- */
  window.TopBarModules.media = function (el, ctx) {
    var timer = null, busy = false;
    var iconWrap = document.createElement('span');
    iconWrap.className = 'tb-icon tb-media-icon';
    var iconSvg = ctx.svg(ICON.play);
    iconWrap.appendChild(iconSvg);
    var textEl = document.createElement('span');
    textEl.className = 'tb-media-text';
    el.appendChild(iconWrap);
    el.appendChild(textEl);

    var lastIcon = null;
    function setIcon(name) {
      if (lastIcon === name) return;
      lastIcon = name;
      iconWrap.removeChild(iconSvg);
      iconSvg = ctx.svg(ICON[name]);
      iconWrap.appendChild(iconSvg);
    }

    iconWrap.addEventListener('click', function (e) {
      e.stopPropagation();
      if (busy) return;
      busy = true;
      ctx.post('/media/toggle').catch(function () {}).then(function () { busy = false; setTimeout(poll, 250); });
    });
    textEl.addEventListener('click', function (e) {
      e.stopPropagation();
      ctx.post('/media/next').catch(function () {}).then(function () { setTimeout(poll, 250); });
    });

    function render(s) {
      var hasTrack = !!(s && s.active && (s.title || s.artist));
      ctx.setVisible(hasTrack);
      if (!hasTrack) return;
      setIcon(s.playing ? 'pause' : 'play');
      textEl.textContent = [s.title || '', s.artist || ''].filter(function (v) { return v; }).join(' — ');
    }
    function poll() {
      ctx.get('/media').then(render, function () { ctx.setVisible(false); });
    }
    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 2000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- planner: «ЗАДАЧИ 2 · ПРОСРОЧЕНО 0» / «ПЛАНЕР: ВОЙТИ» -------------- */
  window.TopBarModules.planner = function (el, ctx) {
    var timer = null;
    var loggedOut = false;
    el.classList.add('tb-clickable');

    function renderLoggedOut() {
      loggedOut = true;
      el.textContent = '';
      el.appendChild(text(ctx.lang === 'en' ? 'PLANNER: SIGN IN' : 'ПЛАНЕР: ВОЙТИ'));
    }
    function renderTasks(data) {
      loggedOut = false;
      var tasks = data.tasks || [];
      var open = tasks.filter(function (t) { return t.state !== 'done'; });
      var overdue = open.filter(function (t) { return t.overdue; }).length;
      el.textContent = '';
      el.appendChild(text((ctx.lang === 'en' ? 'TASKS ' : 'ЗАДАЧИ ') + open.length));
      el.appendChild(dot());
      el.appendChild(text((ctx.lang === 'en' ? 'OVERDUE ' : 'ПРОСРОЧЕНО ') + overdue));
    }

    el.addEventListener('click', function () {
      if (loggedOut) { ctx.get('/planner/login').catch(function () {}); return; }
      ctx.post('/app/settings?tab=planner').catch(function () {});
    });

    function poll() {
      ctx.getStatus('/planner/today').then(function (res) {
        if (res.status === 401) { renderLoggedOut(); return; }
        if (res.status >= 200 && res.status < 300 && res.json) { renderTasks(res.json); return; }
        /* сетевой сбой/прочее — оставляем последний рендер */
      }, function () {});
    }
    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 60000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- volume: иконка динамика (3 уровня + mute), клик -> поповер volume ------------------
     Живое обновление по WS-событию 'audio-changed' (без опроса). Колесо над модулем — громкость
     шагом 2, средняя кнопка — mute; оба идут в PUT /audio/volume напрямую со страницы строки
     (поповер здесь не нужен — быстрый доступ с самой панели, как в macOS). */
  window.TopBarModules.volume = function (el, ctx) {
    el.classList.add('tb-clickable');
    var iconWrap = document.createElement('span');
    iconWrap.className = 'tb-icon';
    el.appendChild(iconWrap);

    var state = { volume: 0, muted: false };
    var wavePaths = [], muteX = null;

    (function build() {
      var svg = svgRoot('0 0 24 24');
      svg.appendChild(svgTag('path', { d: 'M4 9 L4 15 L8 15 L13 19 L13 5 L8 9 Z' }));
      var waves = [
        'M15.3 9.3 A4 4 0 0 1 15.3 14.7',
        'M17.3 7.1 A7 7 0 0 1 17.3 16.9',
        'M19.3 4.9 A10 10 0 0 1 19.3 19.1'
      ];
      for (var i = 0; i < waves.length; i++) {
        var w = svgTag('path', { d: waves[i], fill: 'none', stroke: 'currentColor', 'stroke-width': '1.6', 'stroke-linecap': 'round' });
        svg.appendChild(w);
        wavePaths.push(w);
      }
      muteX = svgTag('path', { d: 'M15.6 8.6 L20.6 15.6 M20.6 8.6 L15.6 15.6', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.6', 'stroke-linecap': 'round' });
      svg.appendChild(muteX);
      iconWrap.appendChild(svg);
    })();

    function render() {
      var level = (state.muted || state.volume <= 0) ? 0 : (state.volume < 34 ? 1 : (state.volume < 67 ? 2 : 3));
      for (var i = 0; i < wavePaths.length; i++) wavePaths[i].style.display = (i < level) ? '' : 'none';
      muteX.style.display = state.muted ? '' : 'none';
    }
    function setState(s) {
      if (!s) return;
      if (typeof s.volume === 'number') state.volume = Math.max(0, Math.min(100, s.volume));
      if (typeof s.muted === 'boolean') state.muted = s.muted;
      render();
    }

    el.addEventListener('click', function () { ctx.openPopup('volume'); });
    el.addEventListener('mousedown', function (e) { if (e.button === 1) e.preventDefault(); });
    el.addEventListener('auxclick', function (e) {
      if (e.button !== 1) return;
      e.preventDefault();
      var muted = !state.muted;
      setState({ muted: muted });
      ctx.put('/audio/volume', { muted: muted }).catch(function () {});
    });
    el.addEventListener('wheel', function (e) {
      e.preventDefault();
      var v = Math.max(0, Math.min(100, state.volume + (e.deltaY > 0 ? -2 : 2)));
      setState({ volume: v });
      ctx.put('/audio/volume', { volume: v }).catch(function () {});
    }, { passive: false });

    ctx.on('audio-changed', setState);

    return {
      start: function () { ctx.get('/audio/volume').then(setState, function () {}); },
      stop: function () {}
    };
  };

  /* ---- network: wifi (уровень сигнала) / ethernet / offline, hover -> tooltip с именем ----- */
  window.TopBarModules.network = function (el, ctx) {
    var iconWrap = document.createElement('span');
    iconWrap.className = 'tb-icon';
    el.appendChild(iconWrap);
    var state = { up: false, kind: 'none', name: '', signal: 0 };
    var timer = null;

    function buildWifi(level) {
      iconWrap.innerHTML = '';
      var svg = svgRoot('0 0 24 24');
      svg.appendChild(svgTag('circle', { cx: '12', cy: '18', r: '1.6' }));
      var arcs = [
        { d: 'M8.3 14.8 A5.2 5.2 0 0 1 15.7 14.8', min: 1 },
        { d: 'M5.4 11.6 A9.4 9.4 0 0 1 18.6 11.6', min: 2 },
        { d: 'M2.6 8.5 A13.6 13.6 0 0 1 21.4 8.5', min: 3 }
      ];
      for (var i = 0; i < arcs.length; i++) {
        var p = svgTag('path', { d: arcs[i].d, fill: 'none', stroke: 'currentColor', 'stroke-width': '1.7', 'stroke-linecap': 'round' });
        if (level < arcs[i].min) p.style.opacity = '0.32';
        svg.appendChild(p);
      }
      iconWrap.appendChild(svg);
    }
    function buildEthernet() {
      iconWrap.innerHTML = '';
      var svg = svgRoot('0 0 24 24');
      svg.appendChild(svgTag('path', { d: 'M6 3v4h2v3h2v3h4v-3h2v-3h2V3h-3v2h-2V3h-4v2H9V3z' }));
      iconWrap.appendChild(svg);
    }
    function buildOff() {
      iconWrap.innerHTML = '';
      var svg = svgRoot('0 0 24 24');
      svg.appendChild(svgTag('circle', { cx: '12', cy: '18', r: '1.6' }));
      svg.appendChild(svgTag('path', { d: 'M4 4 L20 20', stroke: 'currentColor', 'stroke-width': '1.7', 'stroke-linecap': 'round' }));
      iconWrap.appendChild(svg);
    }
    function render() {
      if (!state.up) { buildOff(); return; }
      if (state.kind === 'ethernet') { buildEthernet(); return; }
      var level = state.signal >= 67 ? 3 : (state.signal >= 34 ? 2 : (state.signal > 0 ? 1 : 0));
      buildWifi(level);
    }

    ctx.tooltip(function () {
      if (!state.up) return ctx.lang === 'en' ? 'OFFLINE' : 'НЕТ СЕТИ';
      return state.name || (state.kind === 'ethernet' ? 'ETHERNET' : 'WI-FI');
    });

    function poll() { ctx.get('/system/network').then(function (s) { state = s || state; render(); }, function () {}); }
    ctx.on('network-changed', function (msg) { state = msg || state; render(); });

    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 30000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- battery: капсула с заливкой по уровню, скрыт если present:false --------------------- */
  window.TopBarModules.battery = function (el, ctx) {
    var iconWrap = document.createElement('span');
    iconWrap.className = 'tb-icon';
    el.appendChild(iconWrap);
    var timer = null;

    var svg = svgRoot('0 0 26 16');
    svg.appendChild(svgTag('rect', { x: '1', y: '1.5', width: '20', height: '13', rx: '2.4', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.4' }));
    svg.appendChild(svgTag('rect', { x: '21.6', y: '5.5', width: '2', height: '5', rx: '1' }));
    var fillRect = svgTag('rect', { x: '3', y: '3.5', width: '0', height: '9', rx: '1' });
    svg.appendChild(fillRect);
    iconWrap.appendChild(svg);

    function render(s) {
      if (!s || s.present === false) { ctx.setVisible(false); return; }
      ctx.setVisible(true);
      var pct = Math.max(0, Math.min(100, s.percent || 0));
      fillRect.setAttribute('width', String(Math.round(16 * pct / 100)));
      el.classList.toggle('is-charging', !!s.charging);
    }
    function poll() { ctx.get('/system/battery').then(render, function () { ctx.setVisible(false); }); }
    ctx.on('battery-changed', render);

    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 30000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- layout: «RU»/«EN», моно 10px (см. .tb-layout в bar.css), опрос раз в 30с ------------ */
  window.TopBarModules.layout = function (el, ctx) {
    var timer = null;
    function render(s) { el.textContent = ((s && s.lang) || 'ru').toUpperCase(); }
    function poll() { ctx.get('/system/layout').then(render, function () {}); }
    return {
      start: function () { poll(); clearInterval(timer); timer = setInterval(poll, 30000); },
      stop: function () { clearInterval(timer); timer = null; }
    };
  };

  /* ---- control: «два переключателя», клик -> поповер Control Center ------------------------ */
  window.TopBarModules.control = function (el, ctx) {
    el.classList.add('tb-clickable');
    var iconWrap = document.createElement('span');
    iconWrap.className = 'tb-icon';
    var svg = svgRoot('0 0 24 24');
    svg.appendChild(svgTag('line', { x1: '4', y1: '8', x2: '20', y2: '8', stroke: 'currentColor', 'stroke-width': '1.6', 'stroke-linecap': 'round' }));
    svg.appendChild(svgTag('circle', { cx: '9', cy: '8', r: '2.1' }));
    svg.appendChild(svgTag('line', { x1: '4', y1: '16', x2: '20', y2: '16', stroke: 'currentColor', 'stroke-width': '1.6', 'stroke-linecap': 'round' }));
    svg.appendChild(svgTag('circle', { cx: '15', cy: '16', r: '2.1' }));
    iconWrap.appendChild(svg);
    el.appendChild(iconWrap);
    el.addEventListener('click', function () { ctx.openPopup('control'); });
    return { start: function () {}, stop: function () {} };
  };

  /* ---- nna: знак кластера (H:\brand\nna1618_mark_v2\nna1618_mark_white.svg), заменяет brand,
     клик -> поповер-меню NNA («О программе», «Настройки», пауза обоев, обновления, питание) --- */
  var NNA_MARK_D = 'M 81.13,8.09 C 80.03,8.22 78.70,9.87 72.67,18.66 C 68.89,24.18 67.52,26.04 66.67,26.90 '
    + 'C 65.57,27.99 63.77,27.91 62.06,26.69 C 61.39,26.21 61.40,26.23 58.35,22.42 C 53.18,15.96 51.41,14.04 50.49,13.91 '
    + 'C 48.53,13.65 43.48,18.47 42.38,21.65 C 41.69,23.65 42.37,24.98 45.96,28.56 C 47.16,29.76 47.70,30.27 50.17,32.55 '
    + 'C 52.73,34.91 53.92,36.09 54.50,36.87 C 55.46,38.15 55.48,39.50 54.53,40.11 C 54.46,40.16 54.02,40.56 53.56,41.00 '
    + 'C 50.14,44.31 48.26,45.88 46.90,46.55 C 44.88,47.55 43.78,47.61 42.45,46.82 C 41.53,46.26 40.72,45.41 34.86,38.82 '
    + 'C 30.07,33.44 28.61,31.84 27.04,30.28 C 25.50,28.76 25.10,28.49 24.38,28.46 C 21.65,28.37 16.47,33.71 17.05,36.01 '
    + 'C 17.30,36.98 19.45,39.41 24.53,44.47 C 27.24,47.17 31.84,51.61 33.77,53.39 C 34.26,53.84 34.61,55.06 34.46,55.79 '
    + 'C 34.24,56.82 33.71,57.47 32.34,58.39 C 29.44,60.34 24.27,63.90 22.48,65.18 C 22.32,65.30 21.68,65.75 21.07,66.18 '
    + 'C 11.57,72.93 6.44,77.16 5.65,78.90 C 5.33,79.60 5.51,80.74 6.16,82.03 C 8.12,85.93 13.81,90.75 15.92,90.29 '
    + 'C 17.76,89.89 22.40,85.88 30.16,78.00 C 33.44,74.66 38.94,68.87 40.73,66.87 C 40.87,66.71 40.90,66.69 41.02,66.65 '
    + 'C 42.10,66.31 43.30,66.64 44.25,67.54 C 44.69,67.95 45.05,68.36 49.39,73.32 C 59.94,85.40 65.59,91.04 67.90,91.81 '
    + 'C 69.73,92.41 72.75,90.54 75.61,87.02 C 77.82,84.32 78.79,82.08 78.16,81.13 C 77.69,80.42 74.05,77.26 67.23,71.62 '
    + 'C 64.84,69.63 63.07,68.19 60.07,65.74 C 59.69,65.43 59.18,65.01 58.94,64.82 C 58.11,64.14 56.41,62.76 55.73,62.22 '
    + 'C 54.74,61.43 54.59,61.27 54.38,60.92 C 53.86,60.02 54.03,58.78 54.87,57.52 C 55.06,57.24 55.06,57.23 56.69,55.49 '
    + 'C 57.23,54.92 58.50,53.56 59.52,52.48 C 63.47,48.27 63.02,48.72 63.39,48.60 C 64.40,48.27 65.45,48.85 67.10,50.64 '
    + 'C 67.93,51.55 68.34,52.01 71.76,55.87 C 80.79,66.09 83.80,69.15 85.02,69.39 C 86.20,69.62 88.54,68.06 91.14,65.29 '
    + 'C 93.70,62.57 94.85,60.60 94.40,59.69 C 93.82,58.51 86.44,51.48 75.08,41.28 C 73.98,40.30 73.98,40.30 73.86,40.04 '
    + 'C 73.35,38.90 73.42,38.41 74.26,37.24 C 75.33,35.76 75.55,35.54 79.73,31.79 C 82.08,29.69 82.77,29.07 83.57,28.35 '
    + 'C 89.95,22.60 92.87,19.52 93.35,18.07 C 93.50,17.59 93.44,17.17 93.14,16.54 C 91.72,13.61 84.58,8.37 81.65,8.09 '
    + 'C 81.38,8.07 81.37,8.07 81.13,8.09';

  window.TopBarModules.nna = function (el, ctx) {
    el.classList.add('tb-clickable');
    var svg = svgRoot('0 0 100 100');
    svg.appendChild(svgTag('path', { d: NNA_MARK_D }));
    el.appendChild(svg);
    el.addEventListener('click', function () { ctx.openPopup('nna'); });
    return { start: function () {}, stop: function () {} };
  };

  /* ---- rec: push-to-talk indicator — a pulsing Signal dot plus mm:ss, hidden until the host
     broadcasts {"type":"voice-rec","on":true|false} over /events (NNA.Wallpaper.Hotkeys, on hotkey
     press/release; see docs/PLANNER.md). Not in bar.js's DEFAULT_MODULES (out of this stage's file
     scope) — add {"id":"rec","side":"right"} to app.json's topbar.modules (or the top bar settings
     page's module editor) to show it. -------------------------------------------------------- */
  window.TopBarModules.rec = function (el, ctx) {
    el.classList.add('tb-rec');
    // Inline-styled (not a topbar/bar.css class: bar.css is out of this stage's edit scope) — a
    // small red Signal-red dot plus mm:ss, animated with a plain CSS custom property-free pulse via
    // opacity so it needs no external keyframes rule either.
    var dot = document.createElement('span');
    dot.style.cssText = 'display:inline-block;width:7px;height:7px;border-radius:50%;background:#b3261e;flex:0 0 auto;';
    var pulseOn = true;
    var pulseTimer = setInterval(function () { pulseOn = !pulseOn; dot.style.opacity = pulseOn ? '1' : '.35'; }, 500);
    var timeEl = document.createElement('span');
    el.appendChild(dot);
    el.appendChild(timeEl);
    ctx.setVisible(false);

    var startedAt = 0, timer = null;

    function render() {
      var s = Math.max(0, Math.floor((Date.now() - startedAt) / 1000));
      timeEl.textContent = Math.floor(s / 60) + ':' + pad2(s % 60);
    }
    function onRec(msg) {
      var on = !!(msg && msg.on);
      ctx.setVisible(on);
      clearInterval(timer);
      timer = null;
      if (on) {
        startedAt = Date.now();
        render();
        timer = setInterval(render, 1000);
      }
    }
    ctx.on('voice-rec', onRec);

    return {
      start: function () {},
      stop: function () { clearInterval(timer); timer = null; clearInterval(pulseTimer); }
    };
  };
})();
