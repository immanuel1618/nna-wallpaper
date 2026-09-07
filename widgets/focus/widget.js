/* NNA1618 — фокус-таймер v2. Клик по кольцу: старт / пауза. Внизу кнопки: PRESET, STATS, SET, RESET.
   Настройки, статистика и история живут в localStorage обоев. Оттенок блока меняется по фазе:
   работа — белый, отдых — светло-серый, длинный отдых — серый. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, F = C.focus || {}, L = C.labels || {};
  var KEY = 'nna-focus-v2';
  var PRESETS = F.presets || [{ id: 'classic', label: '25 / 5 ×4', work: 25, rest: 5, longRest: 15, per: 4 }];
  var DAYS = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'];

  N.focus = function (mount) {
    var b = N.block('focus', L.focus || 'FOCUS');
    var wrap = N.el('div', 'fo-wrap');

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
    var time = N.el('div', 'fo-time nna-big'), state = N.el('div', 'fo-state nna-mono'), hint = N.el('div', 'fo-hint nna-mono', 'CLICK TO START');
    inner.appendChild(time); inner.appendChild(state); inner.appendChild(hint);
    ringWrap.appendChild(svg); ringWrap.appendChild(inner);
    main.appendChild(ringWrap);

    // --- панели
    var pSet = N.el('div', 'fo-panel fo-settings is-hidden');
    var pStats = N.el('div', 'fo-panel fo-stats is-hidden');
    var pPre = N.el('div', 'fo-panel fo-presets is-hidden');

    // --- низ: точки сессий + кнопки
    var foot = N.el('div', 'fo-foot');
    var dots = N.el('div', 'nna-dots');
    var bar = N.el('div', 'fo-bar');
    var bPre = tool('PRESET'), bStats = tool('STATS'), bSet = tool('SET'), bReset = tool('RESET');
    bar.appendChild(bPre); bar.appendChild(bStats); bar.appendChild(bSet); bar.appendChild(bReset);
    foot.appendChild(dots); foot.appendChild(bar);

    wrap.appendChild(main); wrap.appendChild(pSet); wrap.appendChild(pStats); wrap.appendChild(pPre); wrap.appendChild(foot);
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    function tool(t) { var x = N.el('button', 'nna-btn fo-tool', t); x.type = 'button'; return x; }

    // --- состояние
    var st = load(), view = 'main';
    function today() { var d = new Date(); return d.getFullYear() + '-' + (d.getMonth() + 1) + '-' + d.getDate(); }
    function dayKey(offset) { var d = new Date(); d.setDate(d.getDate() - offset); return d.getFullYear() + '-' + (d.getMonth() + 1) + '-' + d.getDate(); }
    function defaults() {
      return { work: F.work || 25, rest: F.rest || 5, longRest: F.longRest || 15, per: F.sessionsBeforeLong || 4, autoNext: !!F.autoNext };
    }
    function fresh(prev) {
      var cfg = (prev && prev.cfg) || defaults();
      return { phase: 'work', running: false, remaining: cfg.work * 60, endsAt: 0, sessions: 0, day: today(),
        cfg: cfg, preset: (prev && prev.preset) || 'classic', history: (prev && prev.history) || {} };
    }
    function load() {
      try {
        var s = JSON.parse(localStorage.getItem(KEY) || 'null');
        if (!s || !s.cfg) return fresh(s);
        if (s.day !== today()) { var f = fresh(s); f.history = s.history || {}; return f; }
        return s;
      } catch (e) { return fresh(null); }
    }
    function save() { try { localStorage.setItem(KEY, JSON.stringify(st)); } catch (e) {} }
    function total() { return (st.phase === 'work' ? st.cfg.work : st.phase === 'long' ? st.cfg.longRest : st.cfg.rest) * 60; }
    function remaining() { return st.running ? Math.max(0, (st.endsAt - Date.now()) / 1000) : st.remaining; }
    function hist(key) { return st.history[key] || { sessions: 0, minutes: 0 }; }

    function toggle() {
      if (st.running) { st.remaining = remaining(); st.running = false; }
      else { st.endsAt = Date.now() + st.remaining * 1000; st.running = true; }
      save(); render();
    }
    function resetTimer(fullCycle) {
      st.running = false;
      if (fullCycle) { st.phase = 'work'; st.sessions = 0; }
      st.remaining = total();
      save(); render();
    }
    function finish() {
      st.running = false;
      if (st.phase === 'work') {
        st.sessions += 1;
        var h = hist(today()); h.sessions += 1; h.minutes += st.cfg.work; st.history[today()] = h;
        st.phase = (st.sessions % st.cfg.per === 0) ? 'long' : 'rest';
        N.toast('FOCUS DONE · ' + st.sessions + ' TODAY');
      } else {
        st.phase = 'work';
        N.toast('BREAK OVER · FOCUS');
      }
      st.remaining = total();
      if (st.cfg.autoNext) { st.endsAt = Date.now() + st.remaining * 1000; st.running = true; }
      b.root.classList.add('is-flash');
      setTimeout(function () { b.root.classList.remove('is-flash'); }, 1500);
      pruneHistory(); save();
    }
    function pruneHistory() {
      var keep = {}; for (var i = 0; i < 30; i++) { var k = dayKey(i); if (st.history[k]) keep[k] = st.history[k]; }
      st.history = keep;
    }
    function applyPreset(p) {
      st.cfg = { work: p.work, rest: p.rest, longRest: p.longRest, per: p.per, autoNext: st.cfg.autoNext };
      st.preset = p.id; st.phase = 'work'; st.running = false; st.remaining = st.cfg.work * 60; st.sessions = 0;
      save(); showView('main');
    }

    // --- рендер главного вида
    function render() {
      var rem = remaining(), tot = total();
      if (st.running && rem <= 0) { finish(); rem = remaining(); tot = total(); }
      var m = Math.floor(rem / 60), s = Math.floor(rem % 60);
      time.textContent = N.pad2(m) + ':' + N.pad2(s);
      var label = st.phase === 'work' ? 'FOCUS' : st.phase === 'long' ? 'LONG REST' : 'REST';
      state.textContent = st.running ? label : (rem === tot ? 'READY · ' + label : 'PAUSED · ' + label);
      hint.textContent = st.running ? 'CLICK TO PAUSE' : 'CLICK TO START';
      prog.setAttribute('stroke-dashoffset', (CIRC * (rem / (tot || 1))).toFixed(2));
      b.root.classList.toggle('is-running', st.running);
      b.root.classList.toggle('is-work', st.phase === 'work');
      b.root.classList.toggle('is-rest', st.phase === 'rest');
      b.root.classList.toggle('is-long', st.phase === 'long');
      b.setLabel({ text: L.focus || 'FOCUS', strong: (st.sessions % st.cfg.per) + '/' + st.cfg.per + (st.preset && st.preset !== 'custom' ? ' · ' + presetLabel() : ' · CUSTOM') });
      var want = st.cfg.per;
      while (dots.children.length < want) dots.appendChild(N.el('i'));
      while (dots.children.length > want) dots.removeChild(dots.lastChild);
      var done = st.sessions % want; if (done === 0 && st.sessions > 0 && st.phase === 'long') done = want;
      for (var i = 0; i < dots.children.length; i++) dots.children[i].classList.toggle('is-on', i < done);
    }
    function presetLabel() { var p = PRESETS.filter(function (x) { return x.id === st.preset; })[0]; return p ? p.label : 'CUSTOM'; }

    // --- панель настроек
    function renderSettings() {
      pSet.textContent = '';
      pSet.appendChild(N.el('div', 'fo-panel-k nna-mono', 'SETTINGS · MINUTES'));
      [['WORK', 'work', 5, 5, 180], ['REST', 'rest', 1, 1, 60], ['LONG REST', 'longRest', 5, 5, 90], ['CYCLE', 'per', 1, 1, 8]].forEach(function (r) {
        var row = N.el('div', 'fo-row');
        row.appendChild(N.el('span', 'fo-row-k nna-mono', r[0]));
        var minus = N.el('button', 'fo-step', '−'), val = N.el('span', 'fo-row-v nna-big', String(st.cfg[r[1]])), plus = N.el('button', 'fo-step', '+');
        minus.type = plus.type = 'button';
        minus.addEventListener('click', function (e) { e.stopPropagation(); st.cfg[r[1]] = Math.max(r[3], st.cfg[r[1]] - r[2]); st.preset = 'custom'; afterCfg(); val.textContent = String(st.cfg[r[1]]); });
        plus.addEventListener('click', function (e) { e.stopPropagation(); st.cfg[r[1]] = Math.min(r[4], st.cfg[r[1]] + r[2]); st.preset = 'custom'; afterCfg(); val.textContent = String(st.cfg[r[1]]); });
        row.appendChild(minus); row.appendChild(val); row.appendChild(plus);
        pSet.appendChild(row);
      });
      var auto = N.el('div', 'fo-row');
      auto.appendChild(N.el('span', 'fo-row-k nna-mono', 'AUTO NEXT'));
      var tg = N.el('button', 'nna-btn fo-toggle' + (st.cfg.autoNext ? ' is-on' : ''), st.cfg.autoNext ? 'ON' : 'OFF');
      tg.type = 'button';
      tg.addEventListener('click', function (e) { e.stopPropagation(); st.cfg.autoNext = !st.cfg.autoNext; tg.textContent = st.cfg.autoNext ? 'ON' : 'OFF'; tg.classList.toggle('is-on', st.cfg.autoNext); save(); });
      auto.appendChild(tg);
      pSet.appendChild(auto);
      var done = N.el('button', 'nna-btn fo-done', 'DONE'); done.type = 'button';
      done.addEventListener('click', function (e) { e.stopPropagation(); showView('main'); });
      pSet.appendChild(done);
    }
    function afterCfg() {
      if (!st.running) st.remaining = total();
      save();
    }

    // --- статистика
    function renderStats() {
      pStats.textContent = '';
      var t = hist(today());
      pStats.appendChild(N.el('div', 'fo-panel-k nna-mono', 'STATS · LAST 7 DAYS'));
      var head = N.el('div', 'fo-stat-head');
      head.appendChild(stat(t.sessions, 'SESSIONS TODAY'));
      head.appendChild(stat(t.minutes, 'MIN TODAY'));
      var week = 0; for (var i = 0; i < 7; i++) week += hist(dayKey(i)).minutes;
      head.appendChild(stat(week, 'MIN / WEEK'));
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
      var back = N.el('button', 'nna-btn fo-done', 'BACK'); back.type = 'button';
      back.addEventListener('click', function (e) { e.stopPropagation(); showView('main'); });
      pStats.appendChild(back);
    }
    function stat(v, k) { var w = N.el('div', 'fo-stat'); w.appendChild(N.el('div', 'fo-stat-v nna-big', String(v))); w.appendChild(N.el('div', 'fo-stat-k nna-mono', k)); return w; }

    // --- пресеты
    function renderPresets() {
      pPre.textContent = '';
      pPre.appendChild(N.el('div', 'fo-panel-k nna-mono', 'PRESETS · WORK / REST × CYCLE'));
      var list = N.el('div', 'fo-preset-list');
      PRESETS.forEach(function (p) {
        var btn = N.el('button', 'nna-btn fo-preset' + (st.preset === p.id ? ' is-on' : ''), p.label); btn.type = 'button';
        btn.addEventListener('click', function (e) { e.stopPropagation(); applyPreset(p); });
        list.appendChild(btn);
      });
      pPre.appendChild(list);
      var back = N.el('button', 'nna-btn fo-done', 'BACK'); back.type = 'button';
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
    bReset.addEventListener('click', function (e) { e.stopPropagation(); resetTimer(true); N.toast('FOCUS RESET'); });

    render();
    setInterval(render, 500);
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.focus = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.focus = Object.assign({}, N.config.focus || {}, s);
  var w = N.focus(mount);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
