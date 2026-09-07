/* NNA1618 — страница обоев v2: тянет /config, ставит тему и глобалы, строит CSS grid,
   грузит виджеты (module в общем документе, page в изолированном iframe с мостом),
   держит здоровье помощника, аудио-поток и live-канал /events. ES5-стиль, без сборщика.

   Live-обновления: после первой загрузки страница больше не делает location.reload() на
   config-changed. Вместо этого applyConfig() сравнивает новый /config со старым состоянием
   (через wallpaper/layout-diff.js) и патчит только затронутое: тема — CSS-переменные на месте,
   сетка — стили на существующем .nna-grid, блоки — moved двигают style.gridArea, added/removed
   монтируют/размонтируют только свои ячейки, изменённые настройки виджета — перемонтируют только
   его ячейки (page-виджету достаточно нового ctx без пересоздания iframe). */
(function () {
  'use strict';

  function qparam(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  var monitorId = qparam('monitor') || 'main';

  /* Счётчик принятых сообщений /events — виден снаружи (тесты, CDP) как window.NNA_EVENTS. */
  window.NNA_EVENTS = { received: 0, lastType: null };

  /* дефолты (запасной вариант, пока манифест не прочитан или title в нём отсутствует). Настоящие
     заголовки блоков (player/tasks/system/launch/graph/focus/events/weather/eq/photo) приходят из
     title:{ru,en} манифестов виджетов (widgets/<id>/widget.json) — см. refreshLabelsFromManifests
     ниже; idle/tasksSoon/tasksSub — служебные подписи внутри виджетов, тоже читаются из cfg.language,
     но не имеют манифеста (их порождает не виджет, а сама эта карта). */
  var DEFAULT_LABELS = {
    player: 'PLAYER', tasks: 'TASKS', system: 'SYSTEM', launch: 'LAUNCH', graph: 'GRAPH',
    focus: 'FOCUS', events: 'EVENTS', weather: 'WEATHER', eq: 'AUDIO', photo: 'PHOTO',
    idle: 'NOTHING PLAYING', tasksSoon: 'NNA PLANNER', tasksSub: 'SOON'
  };
  /* widget id (папка /widgets/<id>) -> ключ в window.NNA_CONFIG.labels, который читают виджеты
     (L.system, L.tasks, L.photo — см. widget.js каждого виджета); для большинства id ключ
     совпадает с id, три расходятся исторически. */
  var WIDGET_LABEL_KEY = { planner: 'tasks', stats: 'system', photos: 'photo' };
  function labelKeyForWidget(id) {
    return WIDGET_LABEL_KEY.hasOwnProperty(id) ? WIDGET_LABEL_KEY[id] : id;
  }
  function manifestTitle(manifest, language) {
    var t = manifest && manifest.title;
    if (!t) return null;
    return t[language] || t.ru || t.en || null;
  }
  /* Перечитывает заголовки блоков из уже загруженных манифестов (S.manifests) на нужном языке и
     мутирует window.NNA_CONFIG.labels НА МЕСТЕ (не переприсваивает объект) — так виджеты, которые
     захватили L = C.labels один раз при первой загрузке своего widget.js, продолжают видеть
     актуальные значения при следующем рендере (mount/refresh), а applyBlocksAndSettings может сразу
     же перерисовать .nna-label уже смонтированных ячеек без их пересоздания. */
  function refreshLabelsFromManifests(language) {
    var labels = window.NNA_CONFIG.labels || (window.NNA_CONFIG.labels = {});
    Object.keys(S.manifests).forEach(function (id) {
      var manifest = S.manifests[id];
      if (!manifest) return;
      var title = manifestTitle(manifest, language);
      if (title != null) labels[labelKeyForWidget(id)] = title;
    });
    return labels;
  }
  /* Перерисовывает базовый текст .nna-label уже смонтированной ячейки, не трогая strong-часть
     (текущий трек, источник и т.п.) — см. wallpaper/nna-core.js:setLabel про структуру узла. */
  function updateCellLabel(entry, text) {
    var lab = entry.cell.querySelector('.nna-label');
    if (!lab || !lab.firstChild || lab.firstChild.nodeType !== 3) return;
    lab.firstChild.nodeValue = text;
  }
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

  /* приоритет: override (settingsOverride первого блока с этим widget) > файл (config/widgets/<id>.json)
     > default манифеста — как раньше: override общий на все инстансы одного widget id. */
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

  /* С v3-токенов (ui/tokens.css) цвета и шрифты — одна фиксированная брендовая тема, не настройка;
     theme.palette/theme.fonts из app.json больше не применяются страницей (остаются в конфиге как
     задел на будущее/для редактора настроек, см. AppSettings.cs ThemeSettings). Живые из /config —
     геометрия, затемнение, масштаб шрифта и акцент (см. wallpaper/nna-brand.css: --font-scale
     умножает var(--fs-*) у .nna-big/.nna-label/.nna-body-text, --accent красит точку состояния
     и подчёркивание активного пункта у .nna-label b — оба по умолчанию не меняют вид). */
  var ACCENT_MAP = { none: 'transparent', signal: 'var(--signal)', chrome: 'var(--chrome-flat)' };
  function setThemeVars(theme) {
    var root = document.documentElement.style;
    if (theme.radius != null) root.setProperty('--radius', theme.radius + 'px');
    if (theme.gap != null) root.setProperty('--gap', theme.gap + 'px');
    if (theme.pad != null) root.setProperty('--pad', theme.pad + 'px');
    if (theme.blur != null) root.setProperty('--blur', theme.blur + 'px');
    if (theme.dim != null) root.setProperty('--dim', String(theme.dim));
    if (theme.fontScale != null) {
      var scale = Number(theme.fontScale);
      if (!isFinite(scale) || scale <= 0) scale = 100;
      root.setProperty('--font-scale', String(scale / 100));
    }
    if (theme.accent != null) {
      root.setProperty('--accent', ACCENT_MAP.hasOwnProperty(theme.accent) ? ACCENT_MAP[theme.accent] : ACCENT_MAP.none);
    }
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

  /* ---- состояние страницы (переживает config-changed, не пересоздаётся) ------------------- */
  var S = {
    gridEl: null,
    cellsByKey: {},      // key ("widget#n") -> { cell, block, widgetId, kind }
    blocks: [],          // последний применённый monitor.blocks (для diffBlocks)
    manifests: {},        // widget id -> manifest | null (fetch failed / not found)
    scriptsLoaded: {},    // widget id -> Promise (module widget.js, только module-виджеты)
    pageFrames: {},        // widget id -> [iframe, ...] (page-виджеты)
    widgetSettingsById: {},// widget id -> merged settings, применённые сейчас
    currentTheme: {},
    audioConnected: false
  };

  var DIFF = window.NNA_LAYOUT_DIFF;

  function markNotFoundCell(cell, id) {
    var el = document.createElement('div');
    el.className = 'nna-widget-missing';
    el.textContent = 'WIDGET ' + String(id).toUpperCase() + ' NOT FOUND';
    cell.appendChild(el);
  }

  function updateGridGeometry(grid, tb, monitor) {
    var cols = grid.cols || 1, rows = grid.rows || 1;
    if (grid.colWeights && grid.colWeights.length) {
      var cw = [];
      for (var ci = 0; ci < grid.colWeights.length; ci++) cw.push(grid.colWeights[ci] + 'fr');
      S.gridEl.style.gridTemplateColumns = cw.join(' ');
    } else {
      S.gridEl.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
    }
    S.gridEl.style.gridTemplateRows = 'repeat(' + rows + ', minmax(0, 1fr))';
    S.gridEl.style.gap = (grid.gap != null ? grid.gap : 24) + 'px';
    var pad = (grid.pad != null ? grid.pad : 48);
    S.gridEl.style.padding = pad + 'px';
    /* Наш верхний бар (mac-like menu bar) занимает верхнюю кромку: сдвигаем блоки под него. */
    S.gridEl.style.paddingTop = pad + 'px';
    if (tb.enabled) {
      var forThis = String(tb.monitors || 'all').toLowerCase() !== 'primary' || /^main/i.test(String(monitor.name || ''));
      if (forThis) S.gridEl.style.paddingTop = (pad + (tb.height || 30)) + 'px';
    }
  }

  function ensureWidgetScriptLoaded(id) {
    if (!S.scriptsLoaded[id]) S.scriptsLoaded[id] = loadScript('/widgets/' + id + '/widget.js');
    return S.scriptsLoaded[id];
  }

  function pageLog(level, msg) {
    if (window.NNA && window.NNA.log) window.NNA.log(level, msg);
  }

  /* ---- монтирование/размонтирование одной ячейки -------------------------------------------
     Самовосстановление модульных виджетов: каждый монтаж получает 8с на позвать ctx.ready() (см.
     ниже); если не позвал — считаем виджет подвисшим (обычно значит, что первый запрос к
     помощнику завис без ответа/ошибки — см. app.log про ProtocolViolationException на статике,
     из-за которой соединение WebView2 могло держать зависший keep-alive), логируем warn и
     перемонтируем его же ячейку (attempt 1, ещё 15с). Если и это не помогло — logируем error и
     оставляем как есть, дальше виджет живёт как обычно (или так и остаётся пустым/в ошибке). */
  var WIDGET_READY_TIMEOUT_MS = [8000, 15000];

  function mountModuleAtCell(key, cell, block, settings, theme, attempt) {
    attempt = attempt || 0;
    var fn = window.NNA.widgets[block.widget];
    if (typeof fn !== 'function') { markNotFoundCell(cell, block.widget); return; }
    var lifecycle = window.NNA.createLifecycle();
    var settled = false;
    var mountStart = Date.now();
    var readyTimer = null;

    function reportReady() {
      if (settled) return;
      settled = true;
      if (readyTimer) { clearTimeout(readyTimer); readyTimer = null; }
      pageLog('info', 'ready: widget=' + block.widget + ' in ' + (Date.now() - mountStart) + 'ms');
    }
    function reportFail(err) {
      var msg = (err && err.message) ? err.message : String(err == null ? 'unknown' : err);
      pageLog('error', 'widget ' + block.widget + ' failed: ' + msg);
    }

    var ctx = {
      settings: settings, monitor: monitorId, block: block,
      helper: { get: window.NNA.get, post: window.NNA.post }, theme: theme,
      setInterval: lifecycle.setInterval, setTimeout: lifecycle.setTimeout, raf: lifecycle.raf,
      on: lifecycle.on, onDispose: lifecycle.onDispose, dispose: lifecycle.dispose,
      ready: reportReady, fail: reportFail
    };
    try {
      fn(cell, ctx);
    } catch (e) {
      pageLog('error', 'widget ' + block.widget + ' mount threw: ' + ((e && e.message) || e) +
        (e && e.stack ? ' ' + String(e.stack).slice(0, 300) : ''));
      lifecycle.dispose();
      markNotFoundCell(cell, block.widget);
      return;
    }
    cell.__nna = { dispose: lifecycle.dispose, widgetId: block.widget, key: key, readyTimer: null };
    if (S.cellsByKey[key]) S.cellsByKey[key].kind = 'module';

    if (attempt < WIDGET_READY_TIMEOUT_MS.length) {
      readyTimer = setTimeout(function () {
        readyTimer = null;
        if (settled || lifecycle.isDisposed()) return;
        var entry = S.cellsByKey[key];
        if (!entry || entry.cell !== cell) return; // ячейка уже легитимно заменена/удалена
        if (attempt + 1 < WIDGET_READY_TIMEOUT_MS.length) {
          pageLog('warn', 'widget ' + block.widget + ' not ready in ' + Math.round(WIDGET_READY_TIMEOUT_MS[attempt] / 1000) + 's, remounting');
          lifecycle.dispose();
          cell.innerHTML = '';
          mountModuleAtCell(key, cell, block, settings, theme, attempt + 1);
        } else {
          pageLog('error', 'widget ' + block.widget + ' still not ready after remount, giving up');
        }
      }, WIDGET_READY_TIMEOUT_MS[attempt]);
      cell.__nna.readyTimer = readyTimer;
    }
  }

  function mountPageAtCell(key, cell, block, settings, theme) {
    var iframe = document.createElement('iframe');
    iframe.setAttribute('sandbox', 'allow-scripts');
    iframe.src = '/widgets/' + block.widget + '/index.html';
    cell.appendChild(iframe);
    S.pageFrames[block.widget] = S.pageFrames[block.widget] || [];
    S.pageFrames[block.widget].push(iframe);
    iframe.addEventListener('load', function () {
      try {
        iframe.contentWindow.postMessage({
          type: 'ctx',
          ctx: { settings: settings, monitor: monitorId, block: block, theme: theme }
        }, '*');
      } catch (e) { /* noop */ }
    });
    cell.__nna = {
      dispose: function () {
        var arr = S.pageFrames[block.widget] || [];
        var idx = arr.indexOf(iframe);
        if (idx !== -1) arr.splice(idx, 1);
        if (iframe.parentNode) iframe.parentNode.removeChild(iframe);
      },
      widgetId: block.widget, key: key
    };
    if (S.cellsByKey[key]) S.cellsByKey[key].kind = 'page';
  }

  function mountBlockAtKey(key, block, settings, theme) {
    var cell = document.createElement('div');
    cell.className = 'nna-cell';
    cell.setAttribute('data-widget', block.widget);
    cell.style.gridArea = DIFF.gridArea(block);
    S.gridEl.appendChild(cell);
    S.cellsByKey[key] = { cell: cell, block: block, widgetId: block.widget, kind: null };

    var manifest = S.manifests[block.widget];
    if (!manifest) { markNotFoundCell(cell, block.widget); return; }
    var kind = manifest.entry && manifest.entry.kind;
    if (kind === 'module') {
      ensureWidgetScriptLoaded(block.widget).then(function () {
        mountModuleAtCell(key, cell, block, settings, theme);
      }, function (err) {
        pageLog('error', 'widget script load failed: ' + block.widget + ' :: ' + ((err && err.message) || err));
        markNotFoundCell(cell, block.widget);
      });
    } else if (kind === 'page') {
      mountPageAtCell(key, cell, block, settings, theme);
    } else {
      markNotFoundCell(cell, block.widget);
    }
  }

  function unmountCell(key) {
    var entry = S.cellsByKey[key];
    if (!entry) return;
    if (entry.cell.__nna) {
      if (entry.cell.__nna.readyTimer) clearTimeout(entry.cell.__nna.readyTimer);
      if (typeof entry.cell.__nna.dispose === 'function') {
        try { entry.cell.__nna.dispose(); } catch (e) { /* noop */ }
      }
    }
    if (entry.cell.parentNode) entry.cell.parentNode.removeChild(entry.cell);
    delete S.cellsByKey[key];
  }

  /* Настройки виджета изменились: page-инстансам просто шлём новый ctx, module-инстансы
     размонтируем и монтируем заново в той же ячейке (без пересоздания cell -> без сдвига grid). */
  function refreshWidgetInstances(widgetId, settings, theme) {
    var manifest = S.manifests[widgetId];
    var kind = manifest && manifest.entry && manifest.entry.kind;
    var keys = Object.keys(S.cellsByKey).filter(function (k) { return S.cellsByKey[k].widgetId === widgetId; });
    keys.forEach(function (key) {
      var entry = S.cellsByKey[key];
      if (kind === 'page') {
        var arr = S.pageFrames[widgetId] || [];
        for (var j = 0; j < arr.length; j++) {
          try {
            arr[j].contentWindow.postMessage({
              type: 'ctx', ctx: { settings: settings, monitor: monitorId, block: entry.block, theme: theme }
            }, '*');
          } catch (e) { /* noop */ }
        }
      } else {
        if (entry.cell.__nna) {
          if (entry.cell.__nna.readyTimer) clearTimeout(entry.cell.__nna.readyTimer);
          if (typeof entry.cell.__nna.dispose === 'function') {
            try { entry.cell.__nna.dispose(); } catch (e) { /* noop */ }
          }
        }
        entry.cell.innerHTML = '';
        mountModuleAtCell(key, entry.cell, entry.block, settings, theme);
      }
    });
  }

  function collectNeededManifestIds(blocks) {
    var ids = [], seen = {};
    for (var i = 0; i < blocks.length; i++) {
      var id = blocks[i].widget;
      if (!seen[id] && !S.manifests.hasOwnProperty(id)) { seen[id] = true; ids.push(id); }
    }
    return ids;
  }

  function fetchManifests(ids) {
    var chain = Promise.resolve();
    ids.forEach(function (id) {
      chain = chain.then(function () {
        return fetchJson('/widgets/' + id + '/widget.json').then(function (m) {
          S.manifests[id] = m;
        }, function (err) {
          S.manifests[id] = null;
          pageLog('error', 'widget manifest load failed: ' + id + ' :: ' + ((err && err.message) || err));
        });
      });
    });
    return chain;
  }

  /* Ядро живого патча: применяет monitor.blocks к текущему DOM через diffBlocks (moved/added/removed)
     и, если это не превью (isPreview=false — настоящий /config), пересчитывает настройки каждого
     виджета и перемонтирует только те, чьи settings реально изменились (diffWidgets). */
  function applyBlocksAndSettings(nextBlocks, widgetsCfg, theme, isPreview) {
    var neededIds = collectNeededManifestIds(nextBlocks);
    return fetchManifests(neededIds).then(function () {
      refreshLabelsFromManifests(window.NNA_CONFIG.language || 'ru');
      if (!isPreview) {
        var labels = window.NNA_CONFIG.labels || {};
        Object.keys(S.cellsByKey).forEach(function (k) {
          var entry = S.cellsByKey[k];
          var lkey = labelKeyForWidget(entry.widgetId);
          if (labels.hasOwnProperty(lkey)) updateCellLabel(entry, labels[lkey]);
        });
      }
      var nextSettingsById = {};
      var overrideByWidget = {}, idsInOrder = [], i;
      for (i = 0; i < nextBlocks.length; i++) {
        var wid = nextBlocks[i].widget;
        if (!overrideByWidget.hasOwnProperty(wid)) {
          overrideByWidget[wid] = nextBlocks[i].settingsOverride || {};
          idsInOrder.push(wid);
        }
      }
      for (i = 0; i < idsInOrder.length; i++) {
        var id = idsInOrder[i];
        var manifest = S.manifests[id];
        var settings = isPreview
          ? (S.widgetSettingsById[id] || mergeSettings(manifest || {}, {}, overrideByWidget[id]))
          : mergeSettings(manifest || {}, widgetsCfg[id], overrideByWidget[id]);
        nextSettingsById[id] = settings;
        if (!isPreview) {
          window.NNA_CONFIG[id] = settings;
          if (id === 'photos') window.NNA_CONFIG.pins = settings;
          if (id === 'weather') window.NNA_CONFIG.clocks = settings.clocks || DEFAULT_CLOCKS;
        }
      }

      var diff = DIFF.diffBlocks(S.blocks, nextBlocks);
      diff.moved.forEach(function (m) {
        var entry = S.cellsByKey[m.key];
        if (!entry) return;
        entry.cell.style.gridArea = DIFF.gridArea(m.block);
        entry.block = m.block;
      });
      diff.removed.forEach(function (r) { unmountCell(r.key); });
      diff.added.forEach(function (a) {
        mountBlockAtKey(a.key, a.block, nextSettingsById[a.block.widget] || {}, theme);
      });

      S.blocks = nextBlocks.slice();

      if (!isPreview) {
        var changedWidgets = DIFF.diffWidgets(S.widgetSettingsById, nextSettingsById);
        var addedIds = {};
        diff.added.forEach(function (a) { addedIds[a.block.widget] = true; });
        changedWidgets.forEach(function (id2) {
          if (addedIds[id2]) return; // уже смонтирован с актуальными settings выше
          refreshWidgetInstances(id2, nextSettingsById[id2] || {}, theme);
        });
        S.widgetSettingsById = nextSettingsById;
      }

      updateAudioConnection();
    });
  }

  function applyConfig(cfg, opts) {
    opts = opts || {};
    var theme = cfg.theme || {};
    var monitor = cfg.monitor || { grid: { cols: 1, rows: 1 }, blocks: [] };
    var grid = monitor.grid || { cols: 1, rows: 1, gap: 24, pad: 48 };
    var nextBlocks = monitor.blocks || [];
    var widgetsCfg = cfg.widgets || {};
    var tb = cfg.topbar || cfg.topBar || {};

    window.NNA_CONFIG.language = cfg.language || window.NNA_CONFIG.language || 'ru';
    window.NNA_CONFIG.dim = theme.dim;
    setThemeVars(theme);
    updateGridGeometry(grid, tb, monitor);
    S.currentTheme = theme;

    return applyBlocksAndSettings(nextBlocks, widgetsCfg, theme, !!opts.preview);
  }

  function fetchAndApplyConfig() {
    return fetchJson('/config?monitor=' + encodeURIComponent(monitorId)).then(function (cfg) {
      return applyConfig(cfg, {});
    });
  }

  /* ---- мост page <-> хост: разрешаем только пути из needs манифеста --- */
  function findWidgetIdBySource(win) {
    var ids = Object.keys(S.pageFrames);
    for (var i = 0; i < ids.length; i++) {
      var arr = S.pageFrames[ids[i]];
      for (var j = 0; j < arr.length; j++) if (arr[j].contentWindow === win) return ids[i];
    }
    return null;
  }

  function installBridgeListener() {
    window.addEventListener('message', function (e) {
      var d = e.data;
      if (!d || typeof d !== 'object') return;
      var id = findWidgetIdBySource(e.source);
      if (!id) return;
      if (d.type === 'bridge') {
        var manifest = S.manifests[id];
        var needs = (manifest && manifest.needs) || [];
        var cat = pathCategory(d.path);
        if (needs.indexOf(cat) === -1) {
          console.log('nna-bridge forbidden ' + id + ' ' + d.path);
          Object.keys(S.cellsByKey).forEach(function (k) {
            if (S.cellsByKey[k].widgetId === id) S.cellsByKey[k].cell.setAttribute('data-bridge-last', 'forbidden:' + id + ':' + d.path);
          });
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
  }

  /* ---- аудио: WS с автопереподключением, только если кому-то нужно ---- */
  function floatArrayFromBuffer(buf) {
    return Array.prototype.slice.call(new Float32Array(buf));
  }
  function dispatchAudio(arr) {
    window.dispatchEvent(new CustomEvent('weAudio', { detail: arr }));
    Object.keys(S.pageFrames).forEach(function (wid) {
      var manifest = S.manifests[wid];
      var needs = (manifest && manifest.needs) || [];
      if (needs.indexOf('audio') === -1) return;
      var arr2 = S.pageFrames[wid];
      for (var j = 0; j < arr2.length; j++) {
        try { arr2[j].contentWindow.postMessage({ type: 'event', name: 'audio', detail: arr }, '*'); } catch (e) {}
      }
    });
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
  function updateAudioConnection() {
    var any = false;
    Object.keys(S.cellsByKey).forEach(function (k) {
      var manifest = S.manifests[S.cellsByKey[k].widgetId];
      var needs = (manifest && manifest.needs) || [];
      if (needs.indexOf('audio') !== -1) any = true;
    });
    if (any && !S.audioConnected) {
      S.audioConnected = true;
      connectAudio();
    }
  }

  /* ---- live-канал /events: config-changed патчит вместо reload, layout-preview двигает блоки --- */
  function handleConfigChanged() {
    if (!window.NNA) return; // ядро ещё не подключилось; настоящий /config подтянется через initial boot
    fetchAndApplyConfig();
  }

  function connectEvents() {
    var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/events';
    var ws;
    try { ws = new WebSocket(url); } catch (e) { setTimeout(connectEvents, 2000); return; }
    ws.onopen = function () { pageLog('info', 'events connected'); };
    ws.onmessage = function (ev) {
      if (typeof ev.data !== 'string') return;
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      if (!msg || typeof msg !== 'object') return;
      window.NNA_EVENTS.received++;
      window.NNA_EVENTS.lastType = msg.type;
      if (msg.type === 'config-changed') {
        handleConfigChanged();
      } else if (msg.type === 'layout-preview') {
        if (msg.monitorId && String(msg.monitorId) !== monitorId) return;
        applyBlocksAndSettings(msg.blocks || [], {}, S.currentTheme, true);
      }
    };
    ws.onclose = function () { pageLog('info', 'events disconnected'); setTimeout(connectEvents, 2000); };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }

  /* ---- начальная загрузка ------------------------------------------------------------------ */
  function boot(cfg) {
    window.NNA_HELPER = { url: location.origin, token: cfg.token };
    window.NNA_CONFIG = {
      dim: (cfg.theme || {}).dim,
      language: cfg.language || 'ru',
      poll: { stats: 1000, media: 1000, health: 5000 },
      labels: JSON.parse(JSON.stringify(DEFAULT_LABELS)) // копия — мутируем на месте, не трогая шаблон
    };

    /* Контейнер сетки создаётся один раз и переживает все последующие config-changed: только его
       style/children меняются. data-boot проставлен один раз — по нему легко доказать (в CDP или
       вручную), что после live-обновления это тот же DOM-узел, а не результат нового boot(). */
    S.gridEl = document.createElement('div');
    S.gridEl.className = 'nna-grid';
    S.gridEl.setAttribute('data-boot', String(Date.now()) + '-' + Math.random().toString(36).slice(2));
    document.body.appendChild(S.gridEl);

    loadScript('/wallpaper/nna-core.js').then(function () {
      window.NNA.widgets = window.NNA.widgets || {};
      window.NNA.fpsCap = cfg.fpsCap;
      window.NNA.paused = false;
      window.NNA.watchHealth();
      installBridgeListener();
      return loadScript('/wallpaper/i18n.js');
    }).then(function () {
      applyConfig(cfg, {}).then(function () {
        if (!window.NNA_CONFIG.clocks) window.NNA_CONFIG.clocks = DEFAULT_CLOCKS;
        var bootBlocks = (cfg.monitor && cfg.monitor.blocks) || [];
        var bootWidgetIds = bootBlocks.map(function (b) { return b.widget; });
        pageLog('info', 'boot: monitor=' + monitorId + ', blocks=' + bootBlocks.length + ', widgets=[' + bootWidgetIds.join(',') + ']');
        connectEvents();
      });
    }, function (err) {
      document.body.textContent = 'CORE LOAD FAILED: ' + ((err && err.message) || err);
    });

    /* ---- сообщения хоста (WebView2): 'config' теперь патчит как config-changed, не reload ---- */
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (!d || typeof d !== 'object') return;
        if (d.type === 'config') {
          handleConfigChanged();
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

  fetchJson('/config?monitor=' + encodeURIComponent(monitorId))
    .then(boot)
    .catch(function (err) {
      document.body.textContent = 'CONFIG LOAD FAILED: ' + ((err && err.message) || err);
    });
})();
