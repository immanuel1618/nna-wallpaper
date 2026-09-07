/* NNA1618 — верхняя строка: тянет /config, ставит тему, строит три зоны (left/center/right)
   по topbar.modules, монтирует модули из modules.js, держит сообщения хоста (config/pause).
   Свой маленький fetch-хелпер: GET без токена, POST с ?t=<token> из /config. ES5-стиль. */
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

  TB.svg = function (pathD, viewBox) {
    var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('viewBox', viewBox || '0 0 24 24');
    var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', pathD);
    s.appendChild(p);
    return s;
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
      svg: TB.svg,
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

  function boot(cfg) {
    TB.token = cfg.token || '';
    TB.lang = cfg.language === 'en' ? 'en' : 'ru';
    setThemeVars(cfg.theme || {});

    var topbar = cfg.topbar || {};
    if (topbar.fontSize) {
      document.documentElement.style.setProperty('--tb-font-size', topbar.fontSize + 'px');
    }

    var modules = (topbar.modules && topbar.modules.length) ? topbar.modules : [
      { id: 'brand', side: 'left' }, { id: 'date', side: 'left' },
      { id: 'clock', side: 'center' },
      { id: 'planner', side: 'right' }, { id: 'media', side: 'right' },
      { id: 'weather', side: 'right' }, { id: 'stats', side: 'right' }
    ];

    for (var i = 0; i < modules.length; i++) mountModule(modules[i]);
    relayoutAll();
    startAll();
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
