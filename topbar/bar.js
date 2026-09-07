/* NNA1618 — верхняя строка: тянет /config, ставит тему, строит три зоны (left/center/right)
   по topbar.modules, монтирует модули из modules.js, держит сообщения хоста (config/pause).
   Свой маленький fetch-хелпер: GET без токена, POST с ?t=<token> из /config. Плюс: один общий
   WS /events (audio-changed/mic-changed/session-changed/network-changed/battery-changed/
   config-changed — раздаются модулям через ctx.on), общий tooltip и мост popup-окна (клик по
   модулю volume/network.../control/nna шлёт хосту {type:'popup', module, anchorX, anchorW},
   TopBarManager.TogglePopup на стороне C# открывает/закрывает TopBar/PopupWindow). ES5-стиль. */
(function () {
  'use strict';

  function qparam(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  var monitorId = qparam('monitor') || 'main';

  var TB = window.TB = {
    token: '',
    lang: 'ru',
    paused: false
  };

  /* ---- fetch-хелпер ------------------------------------------------------ */
  TB.get = function (path) {
    return fetch(path, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  };

  /* как get, но не бросает на не-2xx — отдаёт { status, json }, нужно для /planner/today (401) */
  TB.getStatus = function (path) {
    return fetch(path, { cache: 'no-store' }).then(function (r) {
      return r.json().catch(function () { return null; }).then(function (j) {
        return { status: r.status, json: j };
      });
    });
  };

  TB.post = function (path) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(path + sep + 't=' + encodeURIComponent(TB.token), { method: 'POST' })
      .then(function (r) { return r.json().catch(function () { return {}; }); });
  };

  TB.put = function (path, body) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(path + sep + 't=' + encodeURIComponent(TB.token), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    }).then(function (r) { return r.json().catch(function () { return {}; }); });
  };

  TB.svg = function (pathD, viewBox) {
    var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('viewBox', viewBox || '0 0 24 24');
    var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', pathD);
    s.appendChild(p);
    return s;
  };

  /* ---- шина живых событий: один WS /events на всю страницу, раздача по типу через ctx.on ---- */
  var listeners = {};
  TB.on = function (type, fn) {
    (listeners[type] = listeners[type] || []).push(fn);
  };
  function dispatchEvent_(type, msg) {
    var arr = listeners[type];
    if (!arr) return;
    for (var i = 0; i < arr.length; i++) { try { arr[i](msg); } catch (e) {} }
  }
  function connectEvents() {
    var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/events';
    var ws;
    try { ws = new WebSocket(url); } catch (e) { setTimeout(connectEvents, 2000); return; }
    ws.onmessage = function (ev) {
      if (typeof ev.data !== 'string') return;
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      if (!msg || typeof msg !== 'object' || !msg.type) return;
      if (msg.type === 'config-changed') { location.reload(); return; }
      dispatchEvent_(msg.type, msg);
    };
    ws.onclose = function () { setTimeout(connectEvents, 2000); };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }

  /* ---- мост к хосту: открыть/закрыть поповер под модулем ------------------ */
  TB.openPopup = function (moduleId, anchorEl) {
    try {
      if (!(window.chrome && window.chrome.webview)) return;
      var r = anchorEl.getBoundingClientRect();
      window.chrome.webview.postMessage({ type: 'popup', module: moduleId, anchorX: r.left, anchorW: r.width });
    } catch (e) {}
  };

  /* ---- мини-tooltip: один переиспользуемый div, текст — функция (может меняться со временем) -- */
  TB.tooltip = function (el, getText) {
    var box = null, timer = null;
    function hide() {
      if (timer) { clearTimeout(timer); timer = null; }
      if (box && box.parentNode) box.parentNode.removeChild(box);
      box = null;
    }
    function show() {
      var t = typeof getText === 'function' ? getText() : getText;
      if (!t) return;
      box = document.createElement('div');
      box.className = 'tb-tooltip';
      box.textContent = t;
      document.body.appendChild(box);
      var r = el.getBoundingClientRect();
      var bw = box.offsetWidth, bh = box.offsetHeight;
      var left = Math.max(4, Math.min(r.left + r.width / 2 - bw / 2, window.innerWidth - bw - 4));
      box.style.left = left + 'px';
      box.style.top = (r.bottom + 8) + 'px';
      requestAnimationFrame(function () { if (box) box.classList.add('is-show'); });
    }
    el.addEventListener('mouseenter', function () { hide(); timer = setTimeout(show, 350); });
    el.addEventListener('mouseleave', hide);
  };

  /* ---- тема: только то, что реально использует страница верхней строки --- */
  function setThemeVars(theme) {
    var root = document.documentElement.style;
    var pal = (theme && theme.palette) || {};
    if (pal.bgPage) root.setProperty('--bg-page', pal.bgPage);
    if (pal.fg) root.setProperty('--fg', pal.fg);
    if (pal.fgBody) root.setProperty('--fg-body', pal.fgBody);
    if (pal.fgMuted) root.setProperty('--fg-muted', pal.fgMuted);
    if (pal.border) root.setProperty('--border', pal.border);
    var fonts = (theme && theme.fonts) || {};
    if (fonts.display) root.setProperty('--font-display', "'" + fonts.display + "', 'Segoe UI', sans-serif");
    if (fonts.mono) root.setProperty('--font-mono', "'" + fonts.mono + "', ui-monospace, Consolas, monospace");
  }

  /* ---- зоны и раскладка модулей внутри зоны ------------------------------- */
  var zones = {
    left: document.getElementById('tbLeft'),
    center: document.getElementById('tbCenter'),
    right: document.getElementById('tbRight')
  };

  function relayout(zoneEl) {
    var kids = zoneEl.children;
    var prevVisible = null; /* последний видимый контент-модуль в текущем "забеге" (сбрасывается спейсером) */
    for (var i = 0; i < kids.length; i++) {
      var k = kids[i];
      if (k.classList.contains('tb-spacer')) { prevVisible = null; continue; }
      if (k.hidden) continue;
      if (prevVisible) {
        k.classList.add('tb-sep');
        k.classList.remove('tb-first');
      } else {
        k.classList.remove('tb-sep');
        k.classList.add('tb-first');
      }
      prevVisible = k;
    }
  }

  function relayoutAll() {
    relayout(zones.left); relayout(zones.center); relayout(zones.right);
  }

  var instances = []; /* { el, zone, api } */

  function mountModule(cfgEntry) {
    var id = cfgEntry.id;
    var side = cfgEntry.side === 'left' || cfgEntry.side === 'center' ? cfgEntry.side : 'right';
    var zoneEl = zones[side] || zones.right;

    var el = document.createElement('div');
    el.className = 'tb-mod tb-' + id;
    zoneEl.appendChild(el);

    if (id === 'spacer') {
      el.className = 'tb-mod tb-spacer';
      instances.push({ el: el, zone: zoneEl, api: null });
      return;
    }

    var factory = window.TopBarModules && window.TopBarModules[id];
    if (typeof factory !== 'function') {
      el.parentNode.removeChild(el);
      return;
    }

    var ctx = {
      lang: TB.lang,
      get: TB.get,
      getStatus: TB.getStatus,
      post: TB.post,
      put: TB.put,
      svg: TB.svg,
      on: TB.on,
      tooltip: function (getText) { TB.tooltip(el, getText); },
      openPopup: function (moduleId) { TB.openPopup(moduleId, el); },
      setVisible: function (visible) {
        el.hidden = !visible;
        relayout(zoneEl);
      }
    };

    var api = null;
    try { api = factory(el, ctx) || null; } catch (e) { /* сломанный модуль не должен ронять строку */ }
    instances.push({ el: el, zone: zoneEl, api: api });
  }

  function startAll() {
    for (var i = 0; i < instances.length; i++) {
      var inst = instances[i];
      if (inst.api && typeof inst.api.start === 'function') {
        try { inst.api.start(); } catch (e) {}
      }
    }
  }
  function stopAll() {
    for (var i = 0; i < instances.length; i++) {
      var inst = instances[i];
      if (inst.api && typeof inst.api.stop === 'function') {
        try { inst.api.stop(); } catch (e) {}
      }
    }
  }

  /* ---- загрузка /config и монтаж ------------------------------------------ */
  fetch('/config?monitor=' + encodeURIComponent(monitorId), { cache: 'no-store' })
    .then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    })
    .then(boot)
    .catch(function (err) {
      document.body.textContent = 'CONFIG LOAD FAILED: ' + ((err && err.message) || err);
    });

  /* v2-модули (control/volume/network/battery/layout, nna вместо brand) не знакомы старым
     конфигам/пресетам (TopBarSettings.Modules в AppSettings.cs всё ещё отдаёт v1-список) — здесь
     список из /config достраивается до v2-дефолта, а не только используется как резерв на случай
     пустого конфига. brand -> nna меняется на том же месте (тот же side), новые right-модули
     вставляются перед первым уже существующим right-модулем (обычно planner), чтобы получить
     порядок control, volume, network, battery, layout, planner, media, weather, stats. */
  var V2_NEW_RIGHT = ['rec', 'control', 'volume', 'network', 'battery', 'layout'];
  var DEFAULT_MODULES = [
    { id: 'nna', side: 'left' }, { id: 'date', side: 'left' },
    { id: 'clock', side: 'center' },
    { id: 'rec', side: 'right' }, { id: 'control', side: 'right' }, { id: 'volume', side: 'right' },
    { id: 'network', side: 'right' }, { id: 'battery', side: 'right' }, { id: 'layout', side: 'right' },
    { id: 'planner', side: 'right' }, { id: 'media', side: 'right' },
    { id: 'weather', side: 'right' }, { id: 'stats', side: 'right' }
  ];

  function normalizeModules(list) {
    if (!list || !list.length) return DEFAULT_MODULES.slice();
    list = list.slice();

    var have = {}, brandAt = -1;
    for (var i = 0; i < list.length; i++) {
      have[list[i].id] = true;
      if (list[i].id === 'brand') brandAt = i;
    }
    if (brandAt !== -1 && !have.nna) {
      list[brandAt] = { id: 'nna', side: list[brandAt].side || 'left' };
      have.nna = true;
    }

    var toAdd = [];
    for (var j = 0; j < V2_NEW_RIGHT.length; j++) {
      if (!have[V2_NEW_RIGHT[j]]) toAdd.push({ id: V2_NEW_RIGHT[j], side: 'right' });
    }
    if (toAdd.length) {
      var insertAt = list.length;
      for (var k = 0; k < list.length; k++) {
        if ((list[k].side || 'right') === 'right') { insertAt = k; break; }
      }
      Array.prototype.splice.apply(list, [insertAt, 0].concat(toAdd));
    }
    return list;
  }

  function boot(cfg) {
    TB.token = cfg.token || '';
    TB.lang = cfg.language === 'en' ? 'en' : 'ru';
    setThemeVars(cfg.theme || {});

    var topbar = cfg.topbar || {};
    if (topbar.fontSize) {
      document.documentElement.style.setProperty('--tb-font-size', topbar.fontSize + 'px');
    }

    var modules = normalizeModules(topbar.modules);

    for (var i = 0; i < modules.length; i++) mountModule(modules[i]);
    relayoutAll();
    startAll();
    connectEvents();
  }

  /* ---- сообщения хоста (WebView2) ------------------------------------------ */
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener('message', function (e) {
        var d = e && e.data;
        if (typeof d === 'string') { try { d = JSON.parse(d); } catch (e2) { return; } }
        if (!d || typeof d !== 'object') return;
        if (d.type === 'config') {
          location.reload();
        } else if (d.type === 'pause') {
          TB.paused = !!d.value;
          if (TB.paused) stopAll(); else startAll();
        }
      });
    }
  } catch (e) { /* не внутри WebView2 */ }
})();
