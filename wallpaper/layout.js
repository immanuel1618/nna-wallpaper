/* NNA1618 — страница обоев v2: тянет /config, ставит тему и глобалы, строит CSS grid,
   грузит виджеты (module в общем документе, page в изолированном iframe с мостом),
   держит здоровье помощника, аудио-поток и сообщения хоста. ES5-стиль, без сборщика. */
(function () {
  'use strict';

  function qparam(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  var monitorId = qparam('monitor') || 'main';

  /* дефолты, совпадающие с nna-config.js — используются, пока
     хост не пришлёт своих (host отдаёт только dim/poll через тему и настройки виджетов). */
  var DEFAULT_LABELS = {
    player: 'PLAYER', tasks: 'TASKS', system: 'SYSTEM', launch: 'LAUNCH', graph: 'GRAPH',
    focus: 'FOCUS', events: 'EVENTS', weather: 'WEATHER', eq: 'AUDIO', photo: 'PHOTO',
    idle: 'NOTHING PLAYING', tasksSoon: 'NNA PLANNER', tasksSub: 'SOON'
  };
  var DEFAULT_CLOCKS = [
    { label: 'MOSCOW', tz: 'Europe/Moscow' },
    { label: 'LOS ANGELES', tz: 'America/Los_Angeles' },
    { label: 'VLADIVOSTOK', tz: 'Asia/Vladivostok' }
  ];

  /* карта первого сегмента пути -> категория needs (см. WIDGET-SDK.md); /icon* относится к launch */
  var PATH_CATEGORY_ALIAS = { icon: 'launch' };

  function pathCategory(path) {
    var clean = String(path || '').split('?')[0];
    var parts = clean.split('/');
    var seg = '';
    for (var i = 0; i < parts.length; i++) { if (parts[i]) { seg = parts[i]; break; } }
    return PATH_CATEGORY_ALIAS.hasOwnProperty(seg) ? PATH_CATEGORY_ALIAS[seg] : seg;
  }

  /* приоритет: override (settingsOverride блока) > файл (config/widgets/<id>.json) > default манифеста */
  function mergeSettings(manifest, fileCfg, override) {
    var out = {}, i, k;
    var defs = (manifest && manifest.settings) || [];
    for (i = 0; i < defs.length; i++) out[defs[i].key] = defs[i]['default'];
    fileCfg = fileCfg || {};
    for (k in fileCfg) if (fileCfg.hasOwnProperty(k)) out[k] = fileCfg[k];
    override = override || {};
    for (k in override) if (override.hasOwnProperty(k)) out[k] = override[k];
    return out;
  }

  function setThemeVars(theme) {
    var root = document.documentElement.style;
    var pal = theme.palette || {};
    if (pal.bgPage) root.setProperty('--bg-page', pal.bgPage);
    if (pal.bgSurface) root.setProperty('--bg-surface', pal.bgSurface);
    if (pal.fg) root.setProperty('--fg', pal.fg);
    if (pal.fgBody) root.setProperty('--fg-body', pal.fgBody);
    if (pal.fgMuted) root.setProperty('--fg-muted', pal.fgMuted);
    if (pal.fgGhost) root.setProperty('--fg-ghost', pal.fgGhost);
    if (pal.border) root.setProperty('--border', pal.border);
    if (pal.glass) root.setProperty('--glass', pal.glass);
    var fonts = theme.fonts || {};
    if (fonts.display) root.setProperty('--font-display', "'" + fonts.display + "', 'Segoe UI', sans-serif");
    if (fonts.mono) root.setProperty('--font-mono', "'" + fonts.mono + "', ui-monospace, Consolas, monospace");
    if (theme.radius != null) root.setProperty('--radius', theme.radius + 'px');
    if (theme.gap != null) root.setProperty('--gap', theme.gap + 'px');
    if (theme.pad != null) root.setProperty('--pad', theme.pad + 'px');
    if (theme.blur != null) root.setProperty('--blur', theme.blur + 'px');
    if (theme.dim != null) root.setProperty('--dim', String(theme.dim));
  }

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = src;
      s.onload = function () { resolve(); };
      s.onerror = function () { reject(new Error('load failed: ' + src)); };
      document.head.appendChild(s);
    });
  }

  function fetchJson(url) {
    return fetch(url, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  }

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
    var theme = cfg.theme || {};
    var monitor = cfg.monitor || { grid: { cols: 1, rows: 1 }, blocks: [] };
    var grid = monitor.grid || { cols: 1, rows: 1, gap: 24, pad: 48 };
    var blocks = monitor.blocks || [];
    var widgetsCfg = cfg.widgets || {};

    window.NNA_HELPER = { url: location.origin, token: cfg.token };
    window.NNA_CONFIG = {
      dim: theme.dim,
      poll: { stats: 1000, media: 1000, health: 5000 },
      labels: DEFAULT_LABELS
    };

    setThemeVars(theme);

    /* ---- CSS grid контейнер и ячейки -------------------------------------- */
    var gridEl = document.createElement('div');
    gridEl.className = 'nna-grid';
    var cols = grid.cols || 1, rows = grid.rows || 1;
    if (grid.colWeights && grid.colWeights.length) {
      var cw = [];
      for (var ci = 0; ci < grid.colWeights.length; ci++) cw.push(grid.colWeights[ci] + 'fr');
      gridEl.style.gridTemplateColumns = cw.join(' ');
    } else {
      gridEl.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
    }
    gridEl.style.gridTemplateRows = 'repeat(' + rows + ', minmax(0, 1fr))';
    gridEl.style.gap = (grid.gap != null ? grid.gap : 24) + 'px';
    gridEl.style.padding = (grid.pad != null ? grid.pad : 48) + 'px';
    document.body.appendChild(gridEl);

    var cellsByWidget = {};
    var uniqueIds = [];
    var i, blk, cell;
    for (i = 0; i < blocks.length; i++) {
      blk = blocks[i];
      cell = document.createElement('div');
      cell.className = 'nna-cell';
      cell.setAttribute('data-widget', blk.widget);
      cell.style.gridArea = blk.row + ' / ' + blk.col + ' / span ' + blk.rowSpan + ' / span ' + blk.colSpan;
      gridEl.appendChild(cell);
      if (!cellsByWidget[blk.widget]) { cellsByWidget[blk.widget] = []; uniqueIds.push(blk.widget); }
      cellsByWidget[blk.widget].push({ cell: cell, block: blk });
    }

    function markNotFound(id) {
      var arr = cellsByWidget[id] || [];
      for (var j = 0; j < arr.length; j++) {
        var el = document.createElement('div');
        el.className = 'nna-widget-missing';
        el.textContent = 'WIDGET ' + String(id).toUpperCase() + ' NOT FOUND';
        arr[j].cell.appendChild(el);
      }
    }

    /* ---- только после установки глобалов подключаем ядро ------------------ */
    loadScript('/wallpaper/nna-core.js').then(function () {
      window.NNA.widgets = window.NNA.widgets || {};
      window.NNA.fpsCap = cfg.fpsCap;
      window.NNA.paused = false;
      window.NNA.watchHealth();

      var manifests = {};
      var pageFrames = {};     /* widgetId -> [iframe, ...] */
      var needsAudio = {};

      function mountModule(id, entries, settings) {
        var fn = window.NNA.widgets[id];
        if (typeof fn !== 'function') { markNotFound(id); return; }
        for (var j = 0; j < entries.length; j++) {
          var ent = entries[j];
          var ctx = {
            settings: settings, monitor: monitorId, block: ent.block,
            helper: { get: window.NNA.get, post: window.NNA.post }, theme: theme
          };
          try { fn(ent.cell, ctx); } catch (e) { markNotFound(id); }
        }
      }

      function mountPage(id, entries, settings, manifest) {
        needsAudio[id] = (manifest.needs || []).indexOf('audio') !== -1;
        pageFrames[id] = pageFrames[id] || [];
        for (var j = 0; j < entries.length; j++) {
          (function (ent) {
            var iframe = document.createElement('iframe');
            iframe.setAttribute('sandbox', 'allow-scripts');
            iframe.src = '/widgets/' + id + '/index.html';
            ent.cell.appendChild(iframe);
            pageFrames[id].push(iframe);
            iframe.addEventListener('load', function () {
              try {
                iframe.contentWindow.postMessage({
                  type: 'ctx',
                  ctx: { settings: settings, monitor: monitorId, block: ent.block, theme: theme }
                }, '*');
              } catch (e) { /* noop */ }
            });
          })(entries[j]);
        }
      }

      function findWidgetIdBySource(win) {
        for (var wid in pageFrames) {
          if (!pageFrames.hasOwnProperty(wid)) continue;
          var arr = pageFrames[wid];
          for (var j = 0; j < arr.length; j++) if (arr[j].contentWindow === win) return wid;
        }
        return null;
      }

      /* ---- мост page <-> хост: разрешаем только пути из needs манифеста --- */
      window.addEventListener('message', function (e) {
        var d = e.data;
        if (!d || typeof d !== 'object') return;
        var id = findWidgetIdBySource(e.source);
        if (!id) return;
        if (d.type === 'bridge') {
          var manifest = manifests[id];
          var needs = (manifest && manifest.needs) || [];
          var cat = pathCategory(d.path);
          if (needs.indexOf(cat) === -1) {
            console.log('nna-bridge forbidden ' + id + ' ' + d.path);
            var entries = cellsByWidget[id] || [];
            for (var ci2 = 0; ci2 < entries.length; ci2++) {
              entries[ci2].cell.setAttribute('data-bridge-last', 'forbidden:' + id + ':' + d.path);
            }
            try { e.source.postMessage({ type: 'bridge-reply', id: d.id, result: { error: 'forbidden' } }, '*'); } catch (er) {}
            return;
          }
          var reqFn = d.method === 'post' ? window.NNA.post : window.NNA.get;
          reqFn(d.path).then(function (res) {
            try { e.source.postMessage({ type: 'bridge-reply', id: d.id, result: res }, '*'); } catch (er) {}
          }, function (err) {
            try { e.source.postMessage({ type: 'bridge-reply', id: d.id, result: { error: String((err && err.message) || err) } }, '*'); } catch (er) {}
          });
        } else if (d.type === 'toast') {
          window.NNA.toast(d.text);
        }
      });

      /* ---- аудио: WS с автопереподключением, только если кому-то нужно ---- */
      function floatArrayFromBuffer(buf) {
        return Array.prototype.slice.call(new Float32Array(buf));
      }
      function dispatchAudio(arr) {
        window.dispatchEvent(new CustomEvent('weAudio', { detail: arr }));
        for (var wid in pageFrames) {
          if (!pageFrames.hasOwnProperty(wid) || !needsAudio[wid]) continue;
          var arr2 = pageFrames[wid];
          for (var j = 0; j < arr2.length; j++) {
            try { arr2[j].contentWindow.postMessage({ type: 'event', name: 'audio', detail: arr }, '*'); } catch (e) {}
          }
        }
      }
      function connectAudio() {
        var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/audio';
        var ws;
        try { ws = new WebSocket(url); } catch (e) { setTimeout(connectAudio, 2000); return; }
        ws.binaryType = 'arraybuffer';
        ws.onmessage = function (ev) {
          if (window.NNA && window.NNA.paused) return;
          if (typeof ev.data === 'string') return;   /* текстовый {"type":"hello",...} — игнор */
          if (ev.data instanceof ArrayBuffer) {
            dispatchAudio(floatArrayFromBuffer(ev.data));
          } else if (ev.data && typeof ev.data.arrayBuffer === 'function') {
            ev.data.arrayBuffer().then(function (buf) { dispatchAudio(floatArrayFromBuffer(buf)); });
          }
        };
        ws.onclose = function () { setTimeout(connectAudio, 2000); };
        ws.onerror = function () { try { ws.close(); } catch (e) {} };
      }

      /* ---- последовательная загрузка манифестов + скриптов ----------------- */
      var chain = Promise.resolve();
      uniqueIds.forEach(function (id) {
        chain = chain.then(function () {
          return fetchJson('/widgets/' + id + '/widget.json').then(function (manifest) {
            manifests[id] = manifest;
            var entries = cellsByWidget[id] || [];
            var override = (entries[0] && entries[0].block.settingsOverride) || {};
            var settings = mergeSettings(manifest, widgetsCfg[id], override);
            window.NNA_CONFIG[id] = settings;
            if (id === 'photos') window.NNA_CONFIG.pins = settings;
            if (id === 'weather') window.NNA_CONFIG.clocks = settings.clocks || DEFAULT_CLOCKS;
            var kind = manifest.entry && manifest.entry.kind;
            if (kind === 'module') {
              return loadScript('/widgets/' + id + '/widget.js').then(function () {
                mountModule(id, entries, settings);
              }, function () { markNotFound(id); });
            } else if (kind === 'page') {
              mountPage(id, entries, settings, manifest);
              return null;
            }
            markNotFound(id);
            return null;
          }, function () { markNotFound(id); });
        });
      });

      chain.then(function () {
        if (!window.NNA_CONFIG.clocks) window.NNA_CONFIG.clocks = DEFAULT_CLOCKS;
        var anyAudio = false;
        for (var wid2 in needsAudio) if (needsAudio.hasOwnProperty(wid2) && needsAudio[wid2]) anyAudio = true;
        if (anyAudio) connectAudio();
      });
    }, function (err) {
      document.body.textContent = 'CORE LOAD FAILED: ' + ((err && err.message) || err);
    });

    /* ---- сообщения хоста (WebView2) --------------------------------------- */
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (!d || typeof d !== 'object') return;
        if (d.type === 'config') {
          location.reload();
        } else if (d.type === 'fps') {
          if (window.NNA) window.NNA.fpsCap = d.value;
        } else if (d.type === 'pause') {
          if (window.NNA) window.NNA.paused = d.value;
        } else if (d.type === 'input') {
          window.dispatchEvent(new CustomEvent('nnaInput', { detail: { text: d.text, target: d.target } }));
        }
      });
    }
  }
})();
