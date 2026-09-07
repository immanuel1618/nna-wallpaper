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

  /* ---- clock: HH:MM, раз в секунду, без секунд ---------------------------- */
  window.TopBarModules.clock = function (el) {
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
})();
