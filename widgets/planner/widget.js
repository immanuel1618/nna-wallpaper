/* NNA1618 — NNA Planner block (TASKS): login / logged-in states, today's tasks with done-toggle,
 * briefing, meetings, habits, money, a text-capture field (opens the host InputWindow) and an
 * optional voice button. Layout follows the SYSTEM block: two columns, display numbers, mono labels. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {};
  var helper = window.NNA_HELPER || { url: 'http://127.0.0.1:1618', token: '' };
  var DEFAULT_SHOW = ['briefing', 'tasks', 'meetings', 'habits', 'money'];
  var MONTHS = ['ЯНВ', 'ФЕВ', 'МАР', 'АПР', 'МАЙ', 'ИЮН', 'ИЮЛ', 'АВГ', 'СЕН', 'ОКТ', 'НОЯ', 'ДЕК'];

  function resolveShow() {
    var cfg = window.NNA_CONFIG || {};
    if (Array.isArray(cfg.plannerShow) && cfg.plannerShow.length) return cfg.plannerShow;
    return DEFAULT_SHOW;
  }

  function resolveMonitorId() {
    try { return new URLSearchParams(location.search).get('monitor') || ''; } catch (e) { return ''; }
  }

  function resolveSettings(ctx) {
    var fromCtx = ctx && ctx.settings;
    var fromCfg = window.NNA_CONFIG && window.NNA_CONFIG.widgets && window.NNA_CONFIG.widgets.planner;
    var s = fromCtx || fromCfg || {};
    return {
      refreshSec: (s.refreshSec > 0) ? s.refreshSec : 60,
      voice: s.voice !== false,
      maxTasks: (s.maxTasks > 0) ? s.maxTasks : 6,
    };
  }

  function ensureStyle() {
    if (document.getElementById('nna-planner-style')) return;
    var style = N.el('style', null, null);
    style.id = 'nna-planner-style';
    style.textContent =
      '.nna-tasks{container-type:size}' +
      '.nna-planner{position:absolute;inset:64px 44px 40px;display:flex;flex-direction:column;gap:22px;min-height:0}' +
      '.nna-planner .pl-grid{flex:1;min-height:0;display:grid;grid-template-columns:1.15fr 1fr;gap:44px}' +
      '.nna-planner .pl-col{display:flex;flex-direction:column;gap:20px;min-width:0;min-height:0}' +
      '.nna-planner .pl-sec{display:flex;flex-direction:column;gap:10px;min-height:0}' +
      '.nna-planner .pl-sec.is-grow{flex:1;min-height:0}' +
      '.nna-planner .pl-head{display:flex;align-items:baseline;justify-content:space-between;gap:16px}' +
      '.nna-planner .pl-k{font-size:11px;color:var(--fg-muted);letter-spacing:0.18em;text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-v{font-size:64px}' +
      '.nna-planner .pl-sub{font-size:11px;color:var(--fg-body);letter-spacing:0.16em;text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-list{display:flex;flex-direction:column;gap:16px;overflow:hidden;min-height:0}' +
      '.nna-planner .pl-row{display:flex;align-items:center;gap:14px;cursor:pointer;min-width:0}' +
      '.nna-planner .pl-row:hover .pl-title{color:var(--fg)}' +
      '.nna-planner .pl-check{width:16px;height:16px;border-radius:50%;border:1px solid var(--border);flex:none;position:relative;transition:background 0.2s}' +
      '.nna-planner .pl-row.is-done .pl-check{background:var(--fg);border-color:var(--fg)}' +
      '.nna-planner .pl-row.is-done .pl-title{text-decoration:line-through;color:var(--fg-muted)}' +
      '.nna-planner .pl-title{font-size:15px;color:var(--fg-body);flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;transition:color 0.2s}' +
      '.nna-planner .pl-time{font-size:10px;color:var(--fg-muted);letter-spacing:0.16em;font-family:var(--font-mono);flex:none}' +
      '.nna-planner .pl-time.is-over{color:var(--fg)}' +
      '.nna-planner .pl-more{font-size:10px;color:var(--fg-muted);letter-spacing:0.18em;font-family:var(--font-mono);text-transform:uppercase}' +
      '.nna-planner .pl-empty{font-size:11px;color:var(--fg-muted);letter-spacing:0.18em;text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-brief{font-size:12px;color:var(--fg-body);line-height:1.55;display:-webkit-box;-webkit-line-clamp:4;-webkit-box-orient:vertical;overflow:hidden}' +
      '.nna-planner .pl-meet{display:flex;align-items:baseline;gap:14px;min-width:0}' +
      '.nna-planner .pl-meet-t{font-family:var(--font-display);font-size:22px;color:var(--fg);flex:none;font-variant-numeric:tabular-nums}' +
      '.nna-planner .pl-meet-n{font-size:13px;color:var(--fg-body);flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
      '.nna-planner .pl-chips{display:flex;flex-wrap:wrap;gap:8px}' +
      '.nna-planner .pl-chip{padding:9px 14px;font-size:10px}' +
      '.nna-planner .pl-money-v{font-size:38px}' +
      '.nna-planner .pl-cats{display:flex;flex-direction:column;gap:6px}' +
      '.nna-planner .pl-cat{display:flex;justify-content:space-between;gap:12px;font-size:10px;color:var(--fg-body);letter-spacing:0.14em;text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-add{display:flex;gap:12px;align-items:center;flex:none}' +
      '.nna-planner .pl-add-btn{flex:1;justify-content:flex-start;text-align:left;padding:14px 22px;font-size:11px}' +
      '.nna-planner .pl-mic{width:44px;height:44px;flex:none}' +
      '.nna-planner .pl-mic svg{width:18px;height:18px;fill:currentColor}' +
      '.nna-planner .pl-mic.is-rec{background:var(--fg);color:var(--bg-surface);border-color:var(--fg)}' +
      '.nna-planner .pl-login{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:26px}' +
      '.nna-planner .pl-login .pl-brand{font-size:92px;font-size:16cqh}' +
      '.nna-planner .pl-login .pl-hint{font-size:12px;color:var(--fg-muted);letter-spacing:0.4em;font-family:var(--font-mono);text-transform:uppercase}' +
      '.nna-planner .pl-login .nna-btn{padding:14px 26px;font-size:11px}';
    document.head.appendChild(style);
  }

  function pad2(n) { n = Math.floor(n); return (n < 10 ? '0' : '') + n; }
  function fmtTime(iso) {
    if (!iso) return '';
    var d = new Date(iso); if (isNaN(d.getTime())) return '';
    var now = new Date();
    var same = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
    if (same) return pad2(d.getHours()) + ':' + pad2(d.getMinutes());
    return pad2(d.getDate()) + ' ' + MONTHS[d.getMonth()];
  }
  function fmtMoney(minor) {
    var v = Math.round((minor || 0) / 100);
    var s = String(Math.abs(v)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
    return (v < 0 ? '−' : '') + s + ' ₽';
  }
  function todayLabel() {
    var d = new Date();
    return pad2(d.getDate()) + ' ' + MONTHS[d.getMonth()];
  }

  window.NNA = window.NNA || {};
  window.NNA.widgets = window.NNA.widgets || {};

  window.NNA.widgets.planner = function (mount, ctx) {
    ensureStyle();
    var settings = resolveSettings(ctx);
    var monitorId = resolveMonitorId();
    var b = N.block('tasks', { text: L.tasks || 'TASKS', strong: L.tasksSoon || 'NNA PLANNER' });
    var corner = N.el('div', 'nna-corner', '');
    b.root.appendChild(corner);
    var wrap = N.el('div', 'nna-planner');
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    var loggedIn = null; // tri-state: null = unknown yet
    var profile = null;
    var recorder = null, recTimer = null;
    var lastData = null;

    function text(key, fallback) { return L[key] || fallback; }

    // ---- login state ---------------------------------------------------------------------------
    function renderLogin() {
      wrap.innerHTML = '';
      corner.textContent = '';
      var box = N.el('div', 'pl-login');
      var title = N.el('div', 'pl-brand nna-big', text('plannerBrand', 'NNA PLANNER'));
      var hint = N.el('div', 'pl-hint', text('plannerHint', 'ЗАДАЧИ · ВСТРЕЧИ · ДЕНЬГИ'));
      var btn = N.el('button', 'nna-btn', text('plannerLogin', 'ВОЙТИ ЧЕРЕЗ TELEGRAM'));
      btn.type = 'button';
      btn.addEventListener('click', function () { N.post('/planner/login').catch(function () {}); });
      box.appendChild(title); box.appendChild(hint); box.appendChild(btn);
      wrap.appendChild(box);
    }

    // ---- sections ------------------------------------------------------------------------------
    function section(label, grow) {
      var sec = N.el('div', 'pl-sec' + (grow ? ' is-grow' : ''));
      if (label) sec.appendChild(N.el('div', 'pl-k', label));
      return sec;
    }

    function renderTasks(data, col) {
      var tasks = (data.tasks || []).slice().sort(function (a, b2) {
        if (!!a.overdue !== !!b2.overdue) return a.overdue ? -1 : 1;
        return String(a.due_at || '~').localeCompare(String(b2.due_at || '~'));
      });
      var open = tasks.filter(function (t) { return t.state !== 'done'; });
      var overdue = open.filter(function (t) { return t.overdue; }).length;

      var sec = section(null, true);
      var head = N.el('div', 'pl-head');
      head.appendChild(N.el('div', 'pl-k', text('plannerTasks', 'ЗАДАЧИ · СЕГОДНЯ')));
      head.appendChild(N.el('div', 'pl-v nna-big', String(open.length)));
      sec.appendChild(head);
      sec.appendChild(N.el('div', 'pl-sub', overdue
        ? overdue + ' ' + text('plannerOverdue', 'ПРОСРОЧЕНО')
        : (open.length ? text('plannerOnTrack', 'ВСЁ В СРОК') : text('plannerFree', 'ДЕНЬ СВОБОДЕН'))));

      var list = N.el('div', 'pl-list');
      if (!open.length) {
        list.appendChild(N.el('div', 'pl-empty', text('plannerNoTasks', 'ЗАДАЧ НА СЕГОДНЯ НЕТ')));
      }
      open.slice(0, settings.maxTasks).forEach(function (t) {
        var row = N.el('div', 'pl-row' + (t.state === 'done' ? ' is-done' : ''));
        row.appendChild(N.el('i', 'pl-check'));
        row.appendChild(N.el('span', 'pl-title', t.title || ''));
        var when = t.overdue ? text('plannerOverdueShort', 'ПРОСРОЧЕНО') : fmtTime(t.due_at);
        if (when) row.appendChild(N.el('span', 'pl-time' + (t.overdue ? ' is-over' : ''), when));
        row.title = t.title || '';
        row.addEventListener('click', function () {
          var willBeDone = !row.classList.contains('is-done');
          row.classList.toggle('is-done', willBeDone);
          N.post('/planner/done?id=' + encodeURIComponent(t.id) + '&done=' + (willBeDone ? 1 : 0))
            .then(function () { ctx.setTimeout(tick, 600); })
            .catch(function () { row.classList.toggle('is-done', !willBeDone); N.toast(text('plannerError', 'ОШИБКА')); });
        });
        list.appendChild(row);
      });
      if (open.length > settings.maxTasks) {
        list.appendChild(N.el('div', 'pl-more', '+ ' + (open.length - settings.maxTasks)));
      }
      sec.appendChild(list);
      col.appendChild(sec);
    }

    function renderBriefing(data, col) {
      var sec = section(text('plannerBriefing', 'БРИФИНГ'));
      var p = N.el('div', 'pl-brief', data.briefing || text('plannerNoBriefing', '—'));
      p.title = data.briefing || '';
      sec.appendChild(p);
      col.appendChild(sec);
    }

    function renderMeetings(data, col) {
      var meetings = (data.meetings || []).slice(0, 3);
      var sec = section(text('plannerMeetings', 'ВСТРЕЧИ'));
      if (!meetings.length) sec.appendChild(N.el('div', 'pl-empty', text('plannerNoMeetings', 'НЕТ ВСТРЕЧ')));
      meetings.forEach(function (m) {
        var row = N.el('div', 'pl-meet');
        row.appendChild(N.el('span', 'pl-meet-t', fmtTime(m.starts_at) || '—'));
        var name = N.el('span', 'pl-meet-n', m.title || '');
        name.title = m.title || '';
        row.appendChild(name);
        sec.appendChild(row);
      });
      col.appendChild(sec);
    }

    function renderHabits(data, col) {
      var habits = data.habits || [];
      var sec = section(text('plannerHabits', 'ПРИВЫЧКИ'));
      if (!habits.length) { sec.appendChild(N.el('div', 'pl-empty', text('plannerNoHabits', 'НЕТ ПРИВЫЧЕК'))); col.appendChild(sec); return; }
      var chips = N.el('div', 'pl-chips');
      habits.forEach(function (h) {
        var chip = N.el('button', 'nna-btn pl-chip' + (h.checkedToday ? ' is-on' : ''), h.title || '');
        chip.type = 'button';
        chip.addEventListener('click', function () {
          var next = !chip.classList.contains('is-on');
          chip.classList.toggle('is-on', next);
          N.post('/planner/habit?id=' + encodeURIComponent(h.id) + '&checked=' + (next ? 1 : 0))
            .then(function () { ctx.setTimeout(tick, 600); })
            .catch(function () { chip.classList.toggle('is-on', !next); N.toast(text('plannerError', 'ОШИБКА')); });
        });
        chips.appendChild(chip);
      });
      sec.appendChild(chips);
      col.appendChild(sec);
    }

    function renderMoney(data, col) {
      var money = data.money || {};
      var sec = section(text('plannerMoney', 'ДЕНЬГИ · СЕГОДНЯ'));
      var head = N.el('div', 'pl-head');
      head.appendChild(N.el('div', 'pl-money-v nna-big', fmtMoney(money.spent_minor)));
      if (money.income_minor) head.appendChild(N.el('div', 'pl-sub', '+ ' + fmtMoney(money.income_minor)));
      sec.appendChild(head);
      var cats = money.by_category || {};
      var keys = Object.keys(cats).sort(function (a, b2) { return (cats[b2] || 0) - (cats[a] || 0); }).slice(0, 3);
      if (keys.length) {
        var list = N.el('div', 'pl-cats');
        keys.forEach(function (k) {
          var row = N.el('div', 'pl-cat');
          row.appendChild(N.el('span', null, k));
          row.appendChild(N.el('span', null, fmtMoney(cats[k])));
          list.appendChild(row);
        });
        sec.appendChild(list);
      } else {
        sec.appendChild(N.el('div', 'pl-empty', text('plannerNoSpend', 'ТРАТ НЕТ')));
      }
      col.appendChild(sec);
    }

    // ---- capture: text through the host InputWindow, voice through MediaRecorder ------------------
    function renderAdd() {
      var row = N.el('div', 'pl-add');
      var btn = N.el('button', 'nna-btn pl-add-btn', text('plannerAdd', '+ ЗАДАЧА, ВСТРЕЧА ИЛИ ТРАТА'));
      btn.type = 'button';
      btn.addEventListener('click', function () {
        var r = btn.getBoundingClientRect();
        var px = Math.round(r.left + 24);
        var py = Math.round(r.top - 8);
        N.post('/planner/input?monitor=' + encodeURIComponent(monitorId) +
          '&x=' + px + '&y=' + py + '&placeholder=' + encodeURIComponent(text('plannerPlaceholder', 'Что сделать, когда, сколько?')))
          .catch(function () { N.toast(text('plannerError', 'ОШИБКА')); });
      });
      row.appendChild(btn);
      if (settings.voice) {
        var mic = N.el('button', 'nna-icon-btn pl-mic');
        mic.type = 'button';
        mic.title = text('plannerVoice', 'Голосом');
        mic.appendChild(N.svg('M12 14a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v5a3 3 0 0 0 3 3zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.92V21h2v-3.08A7 7 0 0 0 19 11h-2z'));
        mic.addEventListener('click', function () { toggleRecording(mic); });
        row.appendChild(mic);
      }
      wrap.appendChild(row);
    }

    function toggleRecording(btn) {
      if (recorder && recorder.state === 'recording') { recorder.stop(); return; }
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { N.toast(text('plannerError', 'ОШИБКА')); return; }
      navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
        var chunks = [];
        recorder = new MediaRecorder(stream, { mimeType: 'audio/webm;codecs=opus' });
        recorder.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
        recorder.onstop = function () {
          btn.classList.remove('is-rec');
          clearTimeout(recTimer);
          stream.getTracks().forEach(function (t) { t.stop(); });
          var blob = new Blob(chunks, { type: 'audio/webm' });
          N.toast(text('plannerSending', 'ОТПРАВЛЯЮ'));
          blobToBase64(blob).then(function (b64) { sendVoice(b64); });
        };
        recorder.start();
        btn.classList.add('is-rec');
        N.toast(text('plannerRecording', 'ЗАПИСЬ · НАЖМИ ЕЩЁ РАЗ, ЧТОБЫ ОТПРАВИТЬ'));
        recTimer = ctx.setTimeout(function () { if (recorder && recorder.state === 'recording') recorder.stop(); }, 30000);
      }).catch(function () { N.toast(text('plannerError', 'ОШИБКА МИКРОФОНА')); });
    }

    function blobToBase64(blob) {
      return new Promise(function (resolve) {
        var reader = new FileReader();
        reader.onloadend = function () {
          var s = String(reader.result || '');
          var idx = s.indexOf(',');
          resolve(idx >= 0 ? s.slice(idx + 1) : s);
        };
        reader.readAsDataURL(blob);
      });
    }

    function sendVoice(audioBase64) {
      fetch(helper.url + '/planner/capture?t=' + encodeURIComponent(helper.token), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ audio_base64: audioBase64, mime: 'audio/webm' }),
      }).then(function (r) { return r.json().then(function (j) { return { status: r.status, json: j }; }); })
        .then(function (res) { handleCaptureResult(res.status, res.json); tick(); })
        .catch(function () { N.toast(text('plannerError', 'ОШИБКА')); });
    }

    function handleCaptureResult(status, json) {
      if (status === 429 || (json && json.error === 'limit')) { N.toast(text('plannerLimit', 'ЛИМИТ ИИ НА СЕГОДНЯ')); return; }
      if (json && json.ok) {
        var n = (json.entries || []).length;
        N.toast(n ? text('plannerRecorded', 'ЗАПИСАНО') + ': ' + n : text('plannerNothing', 'НИЧЕГО НЕ РАЗОБРАЛ'));
        return;
      }
      N.toast(json && (json.message || json.error || json.reason) ? String(json.message || json.error || json.reason).toUpperCase() : text('plannerError', 'ОШИБКА'));
    }

    // ---- logged-in layout ----------------------------------------------------------------------
    function renderLoggedIn(data) {
      lastData = data;
      wrap.innerHTML = '';
      corner.textContent = ((profile && profile.display_name) ? String(profile.display_name).toUpperCase() + ' · ' : '') + todayLabel();
      var show = resolveShow();
      var grid = N.el('div', 'pl-grid');
      var left = N.el('div', 'pl-col');
      var right = N.el('div', 'pl-col');
      grid.appendChild(left); grid.appendChild(right);
      wrap.appendChild(grid);

      if (show.indexOf('tasks') !== -1) renderTasks(data, left);
      else renderBriefing(data, left);
      if (show.indexOf('briefing') !== -1 && show.indexOf('tasks') !== -1) renderBriefing(data, right);
      if (show.indexOf('meetings') !== -1) renderMeetings(data, right);
      if (show.indexOf('habits') !== -1) renderHabits(data, right);
      if (show.indexOf('money') !== -1) renderMoney(data, right);
      renderAdd();
    }

    function loadToday() {
      N.get('/planner/today').then(function (data) {
        renderLoggedIn(data);
      }).catch(function () {
        // 401 or transient failure: the next status check decides between login and retry.
      });
    }

    function tick() {
      N.get('/planner/status').then(function (s) {
        loggedIn = !!s.loggedIn;
        profile = s.profile || null;
        if (!loggedIn) { renderLogin(); return; }
        loadToday();
      }).catch(function () {
        // host offline: keep the last render; the page-wide .nna-offline class dims everything.
      });
    }

    function onHostMessage(payload) {
      if (!payload || payload.type !== 'planner') return;
      if (payload.event === 'changed' || payload.event === 'login') { tick(); return; }
      if (payload.event === 'captured') {
        if (payload.ok) {
          var n = (payload.entries || []).length;
          N.toast(n ? text('plannerRecorded', 'ЗАПИСАНО') + ': ' + n : text('plannerNothing', 'НИЧЕГО НЕ РАЗОБРАЛ'));
        } else if (payload.error === 'limit') N.toast(text('plannerLimit', 'ЛИМИТ ИИ НА СЕГОДНЯ'));
        else N.toast(text('plannerError', 'ОШИБКА'));
        tick();
      }
    }

    try {
      if (window.chrome && window.chrome.webview) {
        ctx.on(window.chrome.webview, 'message', function (e) {
          var data = e && e.data;
          if (typeof data === 'string') { try { data = JSON.parse(data); } catch (e2) { return; } }
          onHostMessage(data);
        });
      }
    } catch (e) { /* not inside WebView2 */ }

    ctx.on(window, 'nnaHost', function (e) { onHostMessage(e && e.detail); });

    tick();
    ctx.setInterval(tick, settings.refreshSec * 1000);

    return b;
  };
})();
