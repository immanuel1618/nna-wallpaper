/* NNA1618 — верхняя строка: страницы поповеров (volume/calendar/nna/control). Каждый поповер —
   одно окно TopBar/PopupWindow, страница выбирает, что рисовать, по ?module=. Сообщения хосту:
   {type:'size', width, height} после рендера и на каждое изменение контента (ResizeObserver),
   {type:'close'} по Esc. Живые обновления — тот же WS /events, что у topbar/bar.js (audio-changed,
   mic-changed, session-changed, network-changed, battery-changed). ES5-стиль, без сборщика. */
(function () {
  'use strict';

  function qparam(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  var moduleId = qparam('module') || 'volume';
  var monitorId = qparam('monitor') || 'main';

  var PP = window.PP = { token: '', lang: 'ru' };

  /* ---- fetch-хелпер: тот же контракт, что у topbar/bar.js ----------------- */
  PP.get = function (path) {
    return fetch(path, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  };
  PP.post = function (path) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(path + sep + 't=' + encodeURIComponent(PP.token), { method: 'POST' })
      .then(function (r) { return r.json().catch(function () { return {}; }); });
  };
  PP.put = function (path, body) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(path + sep + 't=' + encodeURIComponent(PP.token), {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {})
    }).then(function (r) { return r.json().catch(function () { return {}; }); });
  };
  /* POST с телом (в отличие от PP.post — без тела): /system/power, /taskbar/preset */
  PP.postJson = function (path, body) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(path + sep + 't=' + encodeURIComponent(PP.token), {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {})
    }).then(function (r) { return r.json().catch(function () { return {}; }); });
  };
  /* как get, но не бросает на не-2xx — {status, json}, нужно для /planner/today (401 при разлогине) */
  PP.getStatus = function (path) {
    return fetch(path, { cache: 'no-store' }).then(function (r) {
      return r.json().catch(function () { return null; }).then(function (j) { return { status: r.status, json: j }; });
    });
  };

  /* ---- мост к хосту -------------------------------------------------------- */
  function post(msg) {
    try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg); } catch (e) {}
  }
  function closePopup() { post({ type: 'close' }); }

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { e.preventDefault(); closePopup(); }
  }, true);

  /* ---- живые события: тот же WS /events, что у bar.js --------------------- */
  var listeners = {};
  function on(type, fn) { (listeners[type] = listeners[type] || []).push(fn); }
  function dispatch(type, msg) {
    var arr = listeners[type];
    if (!arr) return;
    for (var i = 0; i < arr.length; i++) { try { arr[i](msg); } catch (e) {} }
  }
  function connectEvents() {
    var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/events';
    var ws;
    try { ws = new WebSocket(url); } catch (e) { return; }
    ws.onmessage = function (ev) {
      if (typeof ev.data !== 'string') return;
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      if (!msg || typeof msg !== 'object' || !msg.type) return;
      dispatch(msg.type, msg);
    };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }

  /* ---- сообщить хосту размер: {type:'size'} после рендера и на каждое изменение ---- */
  var sizeTarget = null;
  function reportSize() {
    if (!sizeTarget) return;
    var r = sizeTarget.getBoundingClientRect();
    post({ type: 'size', width: Math.ceil(r.width), height: Math.ceil(r.height) });
  }
  function watchSize(el) {
    sizeTarget = el;
    reportSize();
    setTimeout(reportSize, 60); /* шрифты/иконки долетают чуть позже первого кадра */
    if (window.ResizeObserver) {
      var ro = new ResizeObserver(function () { reportSize(); });
      ro.observe(el);
    } else {
      setInterval(reportSize, 500);
    }
  }

  /* ---- маленький DOM-хелпер (в духе settings/app.js el(), но ES5) --------- */
  function el(tag, attrs) {
    var node = document.createElement(tag);
    if (attrs) {
      for (var k in attrs) {
        if (!attrs.hasOwnProperty(k)) continue;
        var v = attrs[k];
        if (v === undefined || v === null || v === false) continue;
        if (k === 'class') node.className = v;
        else if (k === 'text') node.textContent = v;
        else if (k.indexOf('on') === 0 && typeof v === 'function') node.addEventListener(k.slice(2), v);
        else node.setAttribute(k, v === true ? '' : v);
      }
    }
    for (var i = 2; i < arguments.length; i++) {
      var c = arguments[i];
      if (c === null || c === undefined) continue;
      node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
    }
    return node;
  }
  function label(text) { return el('div', { class: 'pp-label' }, text); }
  function sep() { return el('div', { class: 'pp-sep' }); }

  var L = {
    ru: {
      volume: 'ГРОМКОСТЬ', output: 'ВЫВОД', apps: 'ПРИЛОЖЕНИЯ', mic: 'МИКРОФОН', noApps: 'НЕТ АКТИВНЫХ',
      about: 'О программе', settings: 'Настройки', pause: 'Пауза обоев', resume: 'Продолжить',
      checkUpdates: 'Проверить обновления', update: 'Обновить', checking: 'Проверка…', upToDate: 'Последняя версия',
      lock: 'Заблокировать экран', sleep: 'Сон', restart: 'Перезагрузка', shutdown: 'Выключение',
      confirmRestart: 'Перезагрузить компьютер?', confirmShutdown: 'Выключить компьютер?', confirm: 'Подтвердить', cancel: 'Отмена',
      brightness: 'ЯРКОСТЬ', dnd: 'НЕ БЕСПОКОИТЬ', preset: 'ПРЕСЕТ ПАНЕЛИ', noMeetings: 'НЕТ ВСТРЕЧ', noEvents: 'НИЧЕГО НА ЭТОТ ДЕНЬ',
      dow: ['ПН', 'ВТ', 'СР', 'ЧТ', 'ПТ', 'СБ', 'ВС'],
      months: ['ЯНВАРЬ', 'ФЕВРАЛЬ', 'МАРТ', 'АПРЕЛЬ', 'МАЙ', 'ИЮНЬ', 'ИЮЛЬ', 'АВГУСТ', 'СЕНТЯБРЬ', 'ОКТЯБРЬ', 'НОЯБРЬ', 'ДЕКАБРЬ']
    },
    en: {
      volume: 'VOLUME', output: 'OUTPUT', apps: 'APPS', mic: 'MICROPHONE', noApps: 'NO ACTIVE APPS',
      about: 'About', settings: 'Settings', pause: 'Pause wallpaper', resume: 'Resume',
      checkUpdates: 'Check for updates', update: 'Update', checking: 'Checking…', upToDate: 'Up to date',
      lock: 'Lock screen', sleep: 'Sleep', restart: 'Restart', shutdown: 'Shut down',
      confirmRestart: 'Restart the computer?', confirmShutdown: 'Shut down the computer?', confirm: 'Confirm', cancel: 'Cancel',
      brightness: 'BRIGHTNESS', dnd: 'DO NOT DISTURB', preset: 'TASKBAR PRESET', noMeetings: 'NO MEETINGS', noEvents: 'NOTHING ON THIS DAY',
      dow: ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU'],
      months: ['JANUARY', 'FEBRUARY', 'MARCH', 'APRIL', 'MAY', 'JUNE', 'JULY', 'AUGUST', 'SEPTEMBER', 'OCTOBER', 'NOVEMBER', 'DECEMBER']
    }
  };
  function t() { return L[PP.lang] || L.ru; }
  function pad2(n) { n = Math.floor(n); return (n < 10 ? '0' : '') + n; }
  function fmtTime(iso) {
    var d = new Date(iso);
    if (isNaN(d)) return '';
    return pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }
  function isoDate(d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }

  /* ================================================================ volume */
  function renderVolume(root) {
    root.appendChild(label(t().volume));
    var master = el('div', { class: 'pp-master' });
    var sliderMount = el('div');
    var muteBtn = el('button', { class: 'ui-icon-btn pp-mute-btn', type: 'button', title: 'Mute' }, muteGlyph());
    master.appendChild(sliderMount);
    master.appendChild(muteBtn);
    root.appendChild(master);

    var slider = window.NNAUI.slider(sliderMount, {
      min: 0, max: 100, step: 1, value: 0,
      format: function (v) { return String(v) + '%'; },
      onChange: function (v) { PP.put('/audio/volume', { volume: v }).catch(function () {}); reportSize(); }
    });

    var muted = false;
    function setMuted(m) {
      muted = !!m;
      muteBtn.classList.toggle('is-muted', muted);
    }
    muteBtn.addEventListener('click', function () {
      setMuted(!muted);
      PP.put('/audio/volume', { muted: muted }).catch(function () {});
    });

    function applyMaster(s) {
      if (!s) return;
      if (typeof s.volume === 'number') slider.set(s.volume);
      if (typeof s.muted === 'boolean') setMuted(s.muted);
    }
    PP.get('/audio/volume').then(applyMaster, function () {});
    on('audio-changed', applyMaster);

    root.appendChild(sep());
    root.appendChild(label(t().output));
    var devices = el('div', { class: 'pp-device-list' });
    root.appendChild(devices);
    function renderDevices(data) {
      devices.textContent = '';
      var list = (data && data.devices) || [];
      list.forEach(function (d) {
        var row = el('div', { class: 'pp-device-row' + (d.default ? ' is-active' : '') },
          el('span', { class: 'dot' }), el('span', { class: 'name' }, d.name || d.id));
        row.addEventListener('click', function () {
          PP.put('/audio/output', { id: d.id }).then(function () { return PP.get('/audio/outputs'); }).then(renderDevices, function () {});
        });
        devices.appendChild(row);
      });
      reportSize();
    }
    PP.get('/audio/outputs').then(renderDevices, function () {});

    root.appendChild(sep());
    root.appendChild(label(t().apps));
    var sessions = el('div', { class: 'pp-session-list' });
    root.appendChild(sessions);
    var sessionSliders = {};
    function renderSessions(data) {
      sessions.textContent = '';
      sessionSliders = {};
      var list = (data && data.sessions) || [];
      if (!list.length) { sessions.appendChild(el('div', { class: 'pp-empty' }, t().noApps)); reportSize(); return; }
      list.forEach(function (s) {
        var top = el('div', { class: 'pp-session-top' });
        var name = el('span', { class: 'pp-session-name' }, s.name || ('PID ' + s.pid));
        var mount = el('div');
        var mBtn = el('button', { class: 'ui-icon-btn', type: 'button' }, muteGlyph());
        top.appendChild(name); top.appendChild(mount); top.appendChild(mBtn);
        var peak = el('div', { class: 'pp-peak' }, el('i'));
        var row = el('div', { class: 'pp-session-row' }, top, peak);
        sessions.appendChild(row);

        var sl = window.NNAUI.slider(mount, {
          min: 0, max: 100, step: 1, value: s.volume || 0,
          format: function (v) { return String(v); },
          onChange: function (v) { PP.put('/audio/session', { id: s.id, volume: v }).catch(function () {}); }
        });
        function setRowMuted(m) { mBtn.classList.toggle('is-muted', !!m); }
        setRowMuted(s.muted);
        mBtn.addEventListener('click', function () {
          var next = !mBtn.classList.contains('is-muted');
          setRowMuted(next);
          PP.put('/audio/session', { id: s.id, muted: next }).catch(function () {});
        });
        peak.firstChild.style.width = Math.round((s.peak || 0) * 100) + '%';
        sessionSliders[s.id] = { slider: sl, peak: peak.firstChild, muteBtn: mBtn };
      });
      reportSize();
    }
    PP.get('/audio/sessions').then(renderSessions, function () {});
    on('session-changed', function () { PP.get('/audio/sessions').then(renderSessions, function () {}); });

    root.appendChild(sep());
    root.appendChild(label(t().mic));
    var micRow = el('div', { class: 'pp-master' });
    var micMount = el('div');
    var micMuteBtn = el('button', { class: 'ui-icon-btn pp-mute-btn', type: 'button' }, muteGlyph());
    micRow.appendChild(micMount); micRow.appendChild(micMuteBtn);
    root.appendChild(micRow);
    var micPeak = el('div', { class: 'pp-peak' }, el('i'));
    root.appendChild(micPeak);

    var micSlider = window.NNAUI.slider(micMount, {
      min: 0, max: 100, step: 1, value: 0,
      format: function (v) { return String(v) + '%'; },
      onChange: function (v) { PP.put('/audio/mic', { volume: v }).catch(function () {}); }
    });
    var micMuted = false;
    function setMicMuted(m) { micMuted = !!m; micMuteBtn.classList.toggle('is-muted', micMuted); }
    micMuteBtn.addEventListener('click', function () {
      setMicMuted(!micMuted);
      PP.put('/audio/mic', { muted: micMuted }).catch(function () {});
    });
    function applyMic(s) {
      if (!s) return;
      if (typeof s.volume === 'number') micSlider.set(s.volume);
      if (typeof s.muted === 'boolean') setMicMuted(s.muted);
      if (typeof s.peak === 'number') micPeak.firstChild.style.width = Math.round(s.peak * 100) + '%';
    }
    PP.get('/audio/mic').then(applyMic, function () {});
    on('mic-changed', applyMic);
  }

  function muteGlyph() {
    var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', 'M4 9 L4 15 L8 15 L13 19 L13 5 L8 9 Z M15.6 8.6 L20.6 15.6 M20.6 8.6 L15.6 15.6');
    p.setAttribute('fill-rule', 'evenodd');
    svg.appendChild(p);
    return svg;
  }

  /* ================================================================ calendar */
  function renderCalendar(root) {
    var today = new Date();
    var viewYear = today.getFullYear(), viewMonth = today.getMonth();
    var selected = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    var dataByDate = { events: {}, meetingsToday: [] };

    var wrap = el('div', { class: 'pp-cal' });
    root.appendChild(wrap);

    var left = el('div', { class: 'pp-cal-left' });
    var head = el('div', { class: 'pp-cal-head' });
    var titleEl = el('div', { class: 'pp-title' }, '');
    var prevBtn = el('button', { class: 'ui-icon-btn', type: 'button', text: '‹' });
    var nextBtn = el('button', { class: 'ui-icon-btn', type: 'button', text: '›' });
    var nav = el('div', { class: 'pp-cal-nav' }, prevBtn, nextBtn);
    head.appendChild(titleEl); head.appendChild(nav);
    var grid = el('div', { class: 'pp-cal-grid' });
    left.appendChild(head); left.appendChild(grid);
    wrap.appendChild(left);

    var right = el('div', { class: 'pp-cal-right' });
    var dayTitle = el('div', { class: 'pp-cal-day-title' }, '');
    var agenda = el('div', { class: 'pp-agenda' });
    right.appendChild(dayTitle); right.appendChild(agenda);
    wrap.appendChild(right);

    function todayKey() { return isoDate(today); }
    function selKey() { return isoDate(selected); }

    function renderGrid() {
      titleEl.textContent = t().months[viewMonth] + ' ' + viewYear;
      grid.textContent = '';
      var dow = t().dow;
      for (var i = 0; i < 7; i++) grid.appendChild(el('div', { class: 'pp-cal-dow' }, dow[i]));

      var first = new Date(viewYear, viewMonth, 1);
      var startOffset = (first.getDay() + 6) % 7; /* Пн = 0 */
      var daysInMonth = new Date(viewYear, viewMonth + 1, 0).getDate();
      var cells = [];
      for (var k = 0; k < startOffset; k++) cells.push(null);
      for (var d = 1; d <= daysInMonth; d++) cells.push(new Date(viewYear, viewMonth, d));
      while (cells.length % 7 !== 0) cells.push(null);

      cells.forEach(function (date) {
        if (!date) { grid.appendChild(el('div', { class: 'pp-cal-cell is-empty' })); return; }
        var key = isoDate(date);
        var isToday = key === todayKey();
        var isSel = key === selKey();
        var hasMark = !!dataByDate.events[key] || (isToday && dataByDate.meetingsToday.length > 0);
        var cell = el('div', {
          class: 'pp-cal-cell' + (isToday ? ' is-today' : '') + (isSel ? ' is-selected' : ''),
          text: String(date.getDate())
        });
        if (hasMark) cell.appendChild(el('span', { class: 'mark' }));
        cell.addEventListener('click', function () {
          selected = date;
          renderGrid();
          renderAgenda();
        });
        grid.appendChild(cell);
      });
      reportSize();
    }

    function renderAgenda() {
      dayTitle.textContent = pad2(selected.getDate()) + ' ' + t().months[selected.getMonth()] + ' ' + selected.getFullYear();
      agenda.textContent = '';
      var items = [];
      var key = selKey();
      if (key === todayKey()) {
        dataByDate.meetingsToday.forEach(function (m) {
          items.push({ time: fmtTime(m.starts_at), text: m.title || m.name || '' });
        });
      }
      var ev = dataByDate.events[key];
      if (ev) ev.forEach(function (e) { items.push({ time: '', text: e.label || 'EVENT' }); });
      items.sort(function (a, b) { return (a.time || '').localeCompare(b.time || ''); });
      if (!items.length) { agenda.appendChild(el('div', { class: 'pp-empty' }, t().noEvents)); reportSize(); return; }
      items.forEach(function (it) {
        agenda.appendChild(el('div', { class: 'pp-agenda-item' },
          el('span', { class: 'pp-agenda-time' }, it.time || '·'),
          el('span', { class: 'pp-agenda-text' }, it.text)));
      });
      reportSize();
    }

    prevBtn.addEventListener('click', function () {
      viewMonth--; if (viewMonth < 0) { viewMonth = 11; viewYear--; }
      renderGrid();
    });
    nextBtn.addEventListener('click', function () {
      viewMonth++; if (viewMonth > 11) { viewMonth = 0; viewYear++; }
      renderGrid();
    });
    document.addEventListener('keydown', function (e) {
      var delta = null;
      if (e.key === 'ArrowLeft') delta = -1;
      else if (e.key === 'ArrowRight') delta = 1;
      else if (e.key === 'ArrowUp') delta = -7;
      else if (e.key === 'ArrowDown') delta = 7;
      if (delta === null) return;
      e.preventDefault();
      selected = new Date(selected.getFullYear(), selected.getMonth(), selected.getDate() + delta);
      if (selected.getMonth() !== viewMonth || selected.getFullYear() !== viewYear) {
        viewMonth = selected.getMonth(); viewYear = selected.getFullYear();
      }
      renderGrid(); renderAgenda();
    });

    function loadEvents() {
      return PP.get('/events').then(function (data) {
        var map = {};
        ((data && data.events) || []).forEach(function (e) {
          if (!e.date) return;
          var key = String(e.date).slice(0, 10);
          (map[key] = map[key] || []).push(e);
        });
        dataByDate.events = map;
      }, function () {});
    }
    function loadMeetings() {
      return PP.getStatus('/planner/today').then(function (res) {
        dataByDate.meetingsToday = (res.status >= 200 && res.status < 300 && res.json && res.json.meetings) || [];
      }, function () {});
    }
    Promise.all([loadEvents(), loadMeetings()]).then(function () { renderGrid(); renderAgenda(); });
  }

  /* ================================================================ nna menu */
  function renderMenu(root) {
    var menu = el('div', { class: 'pp-menu' });
    root.appendChild(menu);

    function item(text, onClick, opts) {
      opts = opts || {};
      /* danger (Перезагрузка/Выключение): текст Ash, слева точка Signal — Signal не набирается
         текстом (см. docs/DESIGN-SYSTEM.md). Точка+текст в одной обёртке, чтобы justify-content:
         space-between у .ui-menu-item не растянуло зазор между ними, а не только до .hint. */
      var labelNode = opts.danger
        ? el('span', { class: 'pp-menu-label' }, el('span', { class: 'pp-menu-dot' }), el('span', { text: text }))
        : el('span', { text: text });
      var row = el('div', { class: 'ui-menu-item' + (opts.danger ? ' is-danger' : ''), role: 'menuitem' },
        labelNode, opts.hint ? el('span', { class: 'hint', text: opts.hint }) : null);
      row.addEventListener('click', function () { onClick(); });
      menu.appendChild(row);
      return row;
    }
    function separator() { menu.appendChild(el('div', { class: 'ui-menu-sep' })); }

    item(t().about, function () { PP.post('/app/settings?tab=about').then(closePopup, closePopup); });
    item(t().settings, function () { PP.post('/app/settings').then(closePopup, closePopup); });

    var pauseRow = item(t().pause, function () {
      var resume = pauseRow.dataset.paused === '1';
      PP.post(resume ? '/app/resume' : '/app/pause').then(closePopup, closePopup);
    });
    PP.get('/health').then(function (h) {
      var paused = !!(h && h.monitors && h.monitors.some(function (m) { return m.paused; }));
      pauseRow.dataset.paused = paused ? '1' : '0';
      pauseRow.firstChild.textContent = paused ? t().resume : t().pause;
    }, function () {});

    /* «Проверить обновления» -> «Обновить (v)», если есть новая версия — один обработчик,
       поведение зависит от текущей фазы (idle/checking/available), а не от второго listener. */
    var updatePhase = 'idle';
    var updateRow = item(t().checkUpdates, function () {
      if (updatePhase === 'available') { PP.post('/app/update').then(closePopup, closePopup); return; }
      updatePhase = 'checking';
      updateRow.firstChild.textContent = t().checking;
      PP.post('/app/check-updates').then(function (r) {
        if (r && r.ok && r.available) {
          updatePhase = 'available';
          updateRow.firstChild.textContent = t().update + ' (' + r.available + ')';
        } else {
          updatePhase = 'idle';
          updateRow.firstChild.textContent = t().upToDate;
        }
        reportSize();
      }, function () { updatePhase = 'idle'; updateRow.firstChild.textContent = t().checkUpdates; });
    });

    separator();
    item(t().lock, function () { PP.postJson('/system/power', { action: 'lock' }).then(closePopup, closePopup); });
    item(t().sleep, function () { PP.postJson('/system/power', { action: 'sleep' }).then(closePopup, closePopup); });
    item(t().restart, function () {
      window.NNAUI.dialog({
        title: t().restart, body: t().confirmRestart,
        actions: [
          { label: t().cancel },
          { label: t().confirm, primary: true, onClick: function () { PP.postJson('/system/power', { action: 'restart', confirm: true }).then(closePopup, closePopup); } }
        ]
      });
    }, { danger: true });
    item(t().shutdown, function () {
      window.NNAUI.dialog({
        title: t().shutdown, body: t().confirmShutdown,
        actions: [
          { label: t().cancel },
          { label: t().confirm, primary: true, onClick: function () { PP.postJson('/system/power', { action: 'shutdown', confirm: true }).then(closePopup, closePopup); } }
        ]
      });
    }, { danger: true });

    reportSize();
  }

  /* ================================================================ control center */
  function renderControl(root) {
    var grid = el('div', { class: 'pp-grid' });
    root.appendChild(grid);

    /* громкость */
    var volTile = el('div', { class: 'pp-tile' }, label(t().volume));
    var volMount = el('div');
    volTile.appendChild(volMount);
    grid.appendChild(volTile);
    var volSlider = window.NNAUI.slider(volMount, {
      min: 0, max: 100, value: 0, format: function (v) { return String(v) + '%'; },
      onChange: function (v) { PP.put('/audio/volume', { volume: v }).catch(function () {}); }
    });
    PP.get('/audio/volume').then(function (s) { if (s && typeof s.volume === 'number') volSlider.set(s.volume); }, function () {});
    on('audio-changed', function (s) { if (s && typeof s.volume === 'number') volSlider.set(s.volume); });

    /* яркость */
    var briTile = el('div', { class: 'pp-tile' }, label(t().brightness));
    var briMount = el('div');
    briTile.appendChild(briMount);
    grid.appendChild(briTile);
    var briSlider = window.NNAUI.slider(briMount, {
      min: 0, max: 100, value: 0, format: function (v) { return String(v) + '%'; },
      onChange: function (v) { PP.put('/system/brightness', { value: v }).catch(function () {}); }
    });
    PP.get('/system/brightness').then(function (b) {
      if (!b || !b.supported) { briTile.hidden = true; reportSize(); return; }
      var m = (b.monitors && b.monitors[0]) || { value: 0 };
      briSlider.set(m.value || 0);
    }, function () { briTile.hidden = true; reportSize(); });

    /* не беспокоить */
    var dndTile = el('div', { class: 'pp-tile' }, label(t().dnd));
    var dndMount = el('div');
    dndTile.appendChild(dndMount);
    grid.appendChild(dndTile);
    var dndToggle = window.NNAUI.toggle(dndMount, {
      onChange: function (on) { PP.put('/system/dnd', { on: on }).catch(function () {}); }
    });
    PP.get('/system/dnd').then(function (d) {
      if (!d || !d.supported) { dndTile.hidden = true; reportSize(); return; }
      dndToggle.set(!!d.on);
    }, function () { dndTile.hidden = true; reportSize(); });

    /* пауза обоев */
    var pauseTile = el('div', { class: 'pp-tile' }, label(t().pause));
    var pauseMount = el('div');
    pauseTile.appendChild(pauseMount);
    grid.appendChild(pauseTile);
    var pauseToggle = window.NNAUI.toggle(pauseMount, {
      onChange: function (on) { PP.post(on ? '/app/pause' : '/app/resume').catch(function () {}); }
    });
    PP.get('/health').then(function (h) {
      var paused = !!(h && h.monitors && h.monitors.some(function (m) { return m.paused; }));
      pauseToggle.set(paused);
    }, function () {});

    /* пресет панели */
    var presetTile = el('div', { class: 'pp-tile full' }, label(t().preset));
    var presetMount = el('select');
    presetTile.appendChild(presetMount);
    grid.appendChild(presetTile);
    var presetSelect = window.NNAUI.select(presetMount, { options: [], onChange: function (id) {
      PP.postJson('/taskbar/preset', { id: id }).catch(function () {});
    } });
    PP.get('/taskbar/presets').then(function (r) {
      var opts = [];
      ((r && r.builtin) || []).forEach(function (p) {
        opts.push({ value: p.id, label: (p.name && (p.name[PP.lang] || p.name.ru)) || p.id });
      });
      ((r && r.user) || []).forEach(function (p) {
        opts.push({ value: p.id, label: (p.name && (p.name[PP.lang] || p.name.ru)) || p.id });
      });
      presetSelect.setOptions(opts);
      if (opts.length) presetSelect.set(opts[0].value);
      reportSize();
    }, function () { presetTile.hidden = true; reportSize(); });

    root.appendChild(sep());
    var settingsBtn = el('button', { class: 'ui-btn', type: 'button', text: t().settings });
    settingsBtn.addEventListener('click', function () { PP.post('/app/settings').then(closePopup, closePopup); });
    root.appendChild(settingsBtn);

    reportSize();
  }

  var RENDERERS = { volume: renderVolume, calendar: renderCalendar, nna: renderMenu, control: renderControl };

  fetch('/config', { cache: 'no-store' })
    .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(boot)
    .catch(function () { boot({}); });

  function boot(cfg) {
    PP.token = cfg.token || '';
    PP.lang = cfg.language === 'en' ? 'en' : 'ru';
    document.body.classList.add('pp-' + moduleId);
    var root = document.getElementById('ppRoot');
    watchSize(root);
    var renderer = RENDERERS[moduleId] || renderVolume;
    try { renderer(root); } catch (e) { root.textContent = 'POPUP RENDER FAILED: ' + ((e && e.message) || e); }
    connectEvents();
  }
})();
