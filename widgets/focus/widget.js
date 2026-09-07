/* NNA1618 — фокус-таймер v3. Модель: WORK -> CHILL -> WORK -> ... -> DONE (widgets/focus/model.js,
   чистая машина состояний, покрыта widgets/focus/tests/model.test.mjs). Переход между фазами
   автоматический, без клика. Клик по кольцу: старт / пауза (в фазе DONE — новая сессия).
   Настройки, статистика и история живут в localStorage обоев (nna-focus-v3; история переносится
   из старого nna-focus-v2 один раз при первом запуске). Оттенок блока меняется по фазе: работа —
   белый, отдых — светло-серый, done — серый. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, F = C.focus || {}, L = C.labels || {};
  var KEY = 'nna-focus-v3';
  var KEY_V2 = 'nna-focus-v2';
  var PRESETS = F.presets || [{ id: 'classic', label: '25/5 x4', work: 25, chill: 5, cycles: 4 }];
  var DAYS = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'];

  // model.js is a separate UMD file (also loaded standalone by the node:test suite), so the
  // wallpaper page fetches it once as a classic <script> the same way nna-core.js is preloaded.
  function loadModel() {
    if (window.NNAFocusModel) return Promise.resolve(window.NNAFocusModel);
    if (!window.__nnaFocusModelPromise) {
      window.__nnaFocusModelPromise = new Promise(function (resolve, reject) {
        var s = document.createElement('script');
        s.src = '/widgets/focus/model.js';
        s.onload = function () { resolve(window.NNAFocusModel); };
        s.onerror = function () { reject(new Error('focus model.js load failed')); };
        document.head.appendChild(s);
      });
    }
    return window.__nnaFocusModelPromise;
  }

  N.focus = function (mount, ctx) {
    var b = N.block('focus', L.focus || 'FOCUS');
    var wrap = N.el('div', 'fo-wrap');
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    var disposed = false;
    ctx.onDispose(function () { disposed = true; });
    loadModel().then(function (Model) {
      if (disposed) return; // unmounted while model.js was loading
      initFocus(Model, b, wrap, ctx);
    }).catch(function (err) {
      if (!disposed) {
        wrap.appendChild(N.el('div', 'pl-empty', 'FOCUS MODEL LOAD FAILED'));
        if (ctx && ctx.fail) ctx.fail(err);
      }
    });

    return b;
  };

  function initFocus(Model, b, wrap, ctx) {
    function text(key, fallback) { return L[key] || fallback; }

    // --- главный вид: кольцо
    var main = N.el('div', 'fo-main');
    var ringWrap = N.el('div', 'fo-ring');
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg'); svg.setAttribute('viewBox', '0 0 120 120');
    var track = document.createElementNS(ns, 'circle'), prog = document.createElementNS(ns, 'circle');
    [track, prog].forEach(function (c) {
      c.setAttribute('cx', '60'); c.setAttribute('cy', '60'); c.setAttribute('r', '54');
      c.setAttribute('fill', 'none'); c.setAttribute('stroke-width', '3');
    });
    track.setAttribute('class', 'fo-track'); prog.setAttribute('class', 'fo-prog');
    var CIRC = 2 * Math.PI * 54;
    prog.setAttribute('stroke-dasharray', CIRC.toFixed(2)); prog.setAttribute('stroke-linecap', 'round');
    svg.appendChild(track); svg.appendChild(prog);
    var inner = N.el('div', 'fo-inner');
    var time = N.el('div', 'fo-time nna-big'), state = N.el('div', 'fo-state nna-mono'), hint = N.el('div', 'fo-hint nna-mono', text('focusStartHint', 'CLICK TO START'));
    inner.appendChild(time); inner.appendChild(state); inner.appendChild(hint);
    ringWrap.appendChild(svg); ringWrap.appendChild(inner);
    main.appendChild(ringWrap);

    // --- панели
    var pSet = N.el('div', 'fo-panel fo-settings is-hidden');
    var pStats = N.el('div', 'fo-panel fo-stats is-hidden');
    var pPre = N.el('div', 'fo-panel fo-presets is-hidden');

    // --- низ: точки циклов + кнопки
    var foot = N.el('div', 'fo-foot');
    var dots = N.el('div', 'nna-dots');
    var bar = N.el('div', 'fo-bar');
    var bPre = tool('PRESET'), bStats = tool('STATS'), bSet = tool('SET'), bReset = tool('RESET');
    bar.appendChild(bPre); bar.appendChild(bStats); bar.appendChild(bSet); bar.appendChild(bReset);
    foot.appendChild(dots); foot.appendChild(bar);

    wrap.appendChild(main); wrap.appendChild(pSet); wrap.appendChild(pStats); wrap.appendChild(pPre); wrap.appendChild(foot);

    function tool(t) { var x = N.el('button', 'nna-btn fo-tool', t); x.type = 'button'; return x; }

    // --- дневные ключи / история (общие для мигратора и статистики)
    function today() { var d = new Date(); return d.getFullYear() + '-' + (d.getMonth() + 1) + '-' + d.getDate(); }
    function dayKey(offset) { var d = new Date(); d.setDate(d.getDate() - offset); return d.getFullYear() + '-' + (d.getMonth() + 1) + '-' + d.getDate(); }
    function hist(key) { return st.history[key] || { sessions: 0, minutes: 0 }; }

    function defaultCfg() {
      return Model.normalizeCfg({ work: F.work || 25, chill: F.chill || 5, cycles: F.cycles || 4 });
    }
    function freshState(history) {
      var s = Model.initial(defaultCfg());
      s.running = false;
      s.remaining = Model.phaseMinutes(s) * 60;
      s.endsAt = 0;
      s.day = today();
      s.preset = (PRESETS[0] && PRESETS[0].id) || 'classic';
      s.history = history || {};
      return s;
    }

    // one-time migration: nna-focus-v2 (work/rest/longRest/per) has no direct WORK/CHILL/CYCLES
    // mapping, so only its history is worth carrying forward — see stage brief.
    function migratedHistory() {
      try {
        var raw = localStorage.getItem(KEY_V2);
        if (!raw) return null;
        var old = JSON.parse(raw);
        return (old && old.history) || null;
      } catch (e) { return null; }
    }

    function load() {
      try {
        var raw = localStorage.getItem(KEY);
        if (raw) {
          var s = JSON.parse(raw);
          if (s && s.cfg && s.phase) {
            if (s.day !== today()) return freshState(s.history || {});
            return s;
          }
        }
      } catch (e) { /* fall through to a fresh (possibly migrated) state */ }
      return freshState(migratedHistory() || {});
    }
    function save() { try { localStorage.setItem(KEY, JSON.stringify(st)); } catch (e) {} }

    // --- состояние
    var st = load(), view = 'main';

    function total() { return Model.phaseMinutes(st) * 60; }
    function remaining() { return st.running ? Math.max(0, (st.endsAt - Date.now()) / 1000) : st.remaining; }

    function toggle() {
      if (st.phase === 'done') { resetTimer(true); return; }
      if (st.running) { st.remaining = remaining(); st.running = false; }
      else { st.endsAt = Date.now() + st.remaining * 1000; st.running = true; }
      save(); render();
    }
    function resetTimer(fullCycle) {
      st.running = false;
      if (fullCycle) {
        var fresh = Model.initial(st.cfg);
        st.phase = fresh.phase; st.completedWork = fresh.completedWork;
      }
      st.remaining = total();
      save(); render();
    }
    function finish() {
      st.running = false;
      var wasWork = st.phase === 'work';
      if (wasWork) {
        var h = hist(today()); h.sessions += 1; h.minutes += st.cfg.work; st.history[today()] = h;
      }
      var next = Model.advance(st);
      st.phase = next.phase; st.completedWork = next.completedWork;
      st.remaining = total();
      if (st.phase === 'done') {
        N.toast(text('focusSessionDone', 'FOCUS SESSION DONE'));
      } else {
        st.endsAt = Date.now() + st.remaining * 1000;
        st.running = true; // automatic transition: no click needed between phases
        N.toast(wasWork ? text('focusBreak', 'FOCUS DONE · CHILL') : text('focusResume', 'CHILL OVER · FOCUS'));
      }
      b.root.classList.add('is-flash');
      ctx.setTimeout(function () { b.root.classList.remove('is-flash'); }, 1500);
      pruneHistory(); save();
    }
    function pruneHistory() {
      var keep = {}; for (var i = 0; i < 30; i++) { var k = dayKey(i); if (st.history[k]) keep[k] = st.history[k]; }
      st.history = keep;
    }
    function applyPreset(p) {
      var cfg = Model.normalizeCfg({ work: p.work, chill: p.chill, cycles: p.cycles });
      var fresh = Model.initial(cfg);
      st.cfg = cfg; st.phase = fresh.phase; st.completedWork = fresh.completedWork;
      st.preset = p.id; st.running = false; st.remaining = total();
      save(); showView('main');
    }

    // --- рендер главного вида
    function render() {
      var rem = remaining(), tot = total();
      if (st.running && rem <= 0) { finish(); rem = remaining(); tot = total(); }
      var label = st.phase === 'work' ? text('focusWork', 'FOCUS') : st.phase === 'chill' ? text('focusChill', 'CHILL') : text('focusDoneState', 'DONE');
      if (st.phase === 'done') {
        time.textContent = '--:--';
        state.textContent = label;
        hint.textContent = text('focusRestartHint', 'CLICK TO RESTART');
      } else {
        var m = Math.floor(rem / 60), s = Math.floor(rem % 60);
        time.textContent = N.pad2(m) + ':' + N.pad2(s);
        state.textContent = st.running ? label : (rem === tot ? text('focusReady', 'READY') + ' · ' + label : text('focusPaused', 'PAUSED') + ' · ' + label);
        hint.textContent = st.running ? text('focusPauseHint', 'CLICK TO PAUSE') : text('focusStartHint', 'CLICK TO START');
      }
      prog.setAttribute('stroke-dashoffset', (CIRC * (tot ? rem / tot : 0)).toFixed(2));
      b.root.classList.toggle('is-running', st.running);
      b.root.classList.toggle('is-work', st.phase === 'work');
      b.root.classList.toggle('is-chill', st.phase === 'chill');
      b.root.classList.toggle('is-done', st.phase === 'done');
      b.setLabel({
        text: L.focus || 'FOCUS',
        strong: st.completedWork + '/' + st.cfg.cycles + (st.phase === 'done' ? ' · ' + text('focusDoneState', 'DONE')
          : (st.preset && st.preset !== 'custom' ? ' · ' + presetLabel() : ' · CUSTOM')),
      });
      var want = st.cfg.cycles;
      while (dots.children.length < want) dots.appendChild(N.el('i'));
      while (dots.children.length > want) dots.removeChild(dots.lastChild);
      var done = Math.min(st.completedWork, want);
      for (var i = 0; i < dots.children.length; i++) dots.children[i].classList.toggle('is-on', i < done);
    }
    function presetLabel() { var p = PRESETS.filter(function (x) { return x.id === st.preset; })[0]; return p ? p.label : 'CUSTOM'; }

    // --- панель настроек: три поля WORK / CHILL / CYCLES, с пояснением под каждым
    function renderSettings() {
      pSet.textContent = '';
      pSet.appendChild(N.el('div', 'fo-panel-k nna-mono', text('focusSettingsTitle', 'SETTINGS · WORK / CHILL / CYCLES')));
      var rows = [
        ['WORK', 'work', 5, 1, 180, text('focusHelpWork', 'ДЛИТЕЛЬНОСТЬ РАБОЧЕЙ ФАЗЫ')],
        ['CHILL', 'chill', 1, 1, 60, text('focusHelpChill', 'ОТДЫХ МЕЖДУ РАБОЧИМИ ФАЗАМИ')],
        ['CYCLES', 'cycles', 1, 1, 12, text('focusHelpCycles', 'РАБОЧИХ ФАЗ ДО DONE')],
      ];
      rows.forEach(function (r) {
        var row = N.el('div', 'fo-row');
        row.appendChild(N.el('span', 'fo-row-k nna-mono', r[0]));
        var minus = N.el('button', 'fo-step', '−'), val = N.el('span', 'fo-row-v nna-big', String(st.cfg[r[1]])), plus = N.el('button', 'fo-step', '+');
        minus.type = plus.type = 'button';
        minus.addEventListener('click', function (e) { e.stopPropagation(); setCfgField(r[1], st.cfg[r[1]] - r[2], r[3], r[4]); val.textContent = String(st.cfg[r[1]]); });
        plus.addEventListener('click', function (e) { e.stopPropagation(); setCfgField(r[1], st.cfg[r[1]] + r[2], r[3], r[4]); val.textContent = String(st.cfg[r[1]]); });
        row.appendChild(minus); row.appendChild(val); row.appendChild(plus);
        pSet.appendChild(row);
        pSet.appendChild(N.el('div', 'fo-row-hint nna-mono', r[5]));
      });
      var done = N.el('button', 'nna-btn fo-done', text('focusDoneBtn', 'DONE')); done.type = 'button';
      done.addEventListener('click', function (e) { e.stopPropagation(); showView('main'); });
      pSet.appendChild(done);
    }
    function setCfgField(key, value, min, max) {
      var next = {}; next[key] = Math.max(min, Math.min(max, Math.round(value)));
      st.cfg = Model.normalizeCfg(Object.assign({}, st.cfg, next));
      st.preset = 'custom';
      if (!st.running) st.remaining = total();
      save(); render();
    }

    // --- статистика
    function renderStats() {
      pStats.textContent = '';
      var t = hist(today());
      pStats.appendChild(N.el('div', 'fo-panel-k nna-mono', text('focusStatsTitle', 'STATS · LAST 7 DAYS')));
      var head = N.el('div', 'fo-stat-head');
      head.appendChild(stat(t.sessions, text('focusStatSessions', 'SESSIONS TODAY')));
      head.appendChild(stat(t.minutes, text('focusStatMinutes', 'MIN TODAY')));
      var week = 0; for (var i = 0; i < 7; i++) week += hist(dayKey(i)).minutes;
      head.appendChild(stat(week, text('focusStatWeek', 'MIN / WEEK')));
      pStats.appendChild(head);
      var bars = N.el('div', 'fo-bars'), max = 1;
      for (var j = 6; j >= 0; j--) max = Math.max(max, hist(dayKey(j)).minutes);
      for (var k = 6; k >= 0; k--) {
        var d = new Date(); d.setDate(d.getDate() - k);
        var col = N.el('div', 'fo-bar-col');
        var v = N.el('div', 'fo-bar-v'); var fill = N.el('i'); fill.style.height = Math.round(hist(dayKey(k)).minutes / max * 100) + '%'; v.appendChild(fill);
        col.appendChild(v); col.appendChild(N.el('span', 'fo-bar-k nna-mono', DAYS[d.getDay()]));
        bars.appendChild(col);
      }
      pStats.appendChild(bars);
      var back = N.el('button', 'nna-btn fo-done', text('focusBack', 'BACK')); back.type = 'button';
      back.addEventListener('click', function (e) { e.stopPropagation(); showView('main'); });
      pStats.appendChild(back);
    }
    function stat(v, k) { var w = N.el('div', 'fo-stat'); w.appendChild(N.el('div', 'fo-stat-v nna-big', String(v))); w.appendChild(N.el('div', 'fo-stat-k nna-mono', k)); return w; }

    // --- пресеты
    function renderPresets() {
      pPre.textContent = '';
      pPre.appendChild(N.el('div', 'fo-panel-k nna-mono', text('focusPresetsTitle', 'PRESETS · WORK / CHILL × CYCLES')));
      var list = N.el('div', 'fo-preset-list');
      PRESETS.forEach(function (p) {
        var btn = N.el('button', 'nna-btn fo-preset' + (st.preset === p.id ? ' is-on' : ''), p.label); btn.type = 'button';
        btn.addEventListener('click', function (e) { e.stopPropagation(); applyPreset(p); });
        list.appendChild(btn);
      });
      pPre.appendChild(list);
      var back = N.el('button', 'nna-btn fo-done', text('focusBack', 'BACK')); back.type = 'button';
      back.addEventListener('click', function (e) { e.stopPropagation(); showView('main'); });
      pPre.appendChild(back);
    }

    function showView(v) {
      view = v;
      main.classList.toggle('is-hidden', v !== 'main');
      pSet.classList.toggle('is-hidden', v !== 'settings');
      pStats.classList.toggle('is-hidden', v !== 'stats');
      pPre.classList.toggle('is-hidden', v !== 'presets');
      if (v === 'settings') renderSettings();
      if (v === 'stats') renderStats();
      if (v === 'presets') renderPresets();
      [bPre, bStats, bSet].forEach(function (x) { x.classList.remove('is-on'); });
      if (v === 'presets') bPre.classList.add('is-on');
      if (v === 'stats') bStats.classList.add('is-on');
      if (v === 'settings') bSet.classList.add('is-on');
      render();
    }

    main.addEventListener('click', function () { toggle(); });
    bPre.addEventListener('click', function (e) { e.stopPropagation(); showView(view === 'presets' ? 'main' : 'presets'); });
    bStats.addEventListener('click', function (e) { e.stopPropagation(); showView(view === 'stats' ? 'main' : 'stats'); });
    bSet.addEventListener('click', function (e) { e.stopPropagation(); showView(view === 'settings' ? 'main' : 'settings'); });
    bReset.addEventListener('click', function (e) { e.stopPropagation(); resetTimer(true); N.toast(text('focusReset', 'FOCUS RESET')); });

    render();
    if (ctx && ctx.ready) ctx.ready(); // кольцо не зависит от сети — готово сразу после первого рендера
    ctx.setInterval(render, 500);
  }
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.focus = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.focus = Object.assign({}, N.config.focus || {}, s);
  var w = N.focus(mount, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
