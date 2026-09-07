/* NNA1618 — NNA Planner block: login/offline states, briefing, tasks, meetings, habits, money,
 * a text-capture field (opens InputWindow through the host) and an optional voice button. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {};
  var helper = window.NNA_HELPER || { url: 'http://127.0.0.1:1618', token: '' };
  var DEFAULT_SHOW = ['briefing', 'tasks', 'meetings', 'habits', 'money'];

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
    };
  }

  function ensureStyle() {
    if (document.getElementById('nna-planner-style')) return;
    var style = N.el('style', null, null);
    style.id = 'nna-planner-style';
    style.textContent =
      '.nna-planner{position:absolute;inset:0;display:flex;flex-direction:column;gap:14px;padding:0 8px;overflow:hidden}' +
      '.nna-planner .pl-brief{font-size:12px;color:var(--fg-body);line-height:1.5}' +
      '.nna-planner .pl-section{display:flex;flex-direction:column;gap:8px;min-height:0}' +
      '.nna-planner .pl-h{font-size:9px;color:var(--fg-muted);letter-spacing:0.28em}' +
      '.nna-planner .pl-list{display:flex;flex-direction:column;gap:8px;overflow:hidden}' +
      '.nna-planner .pl-row{display:flex;align-items:center;gap:10px;cursor:pointer}' +
      '.nna-planner .pl-check{width:14px;height:14px;border-radius:50%;border:1px solid var(--border);flex:none}' +
      '.nna-planner .pl-row.is-done .pl-check{background:var(--fg)}' +
      '.nna-planner .pl-row.is-done .pl-title{text-decoration:line-through;color:var(--fg-muted)}' +
      '.nna-planner .pl-title{font-size:12px;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
      '.nna-planner .pl-time{font-size:10px;color:var(--fg-muted);font-family:var(--font-mono)}' +
      '.nna-planner .pl-tag{font-size:8px;letter-spacing:0.14em;color:#fff;background:rgba(200,40,40,0.6);padding:2px 6px;border-radius:6px;flex:none}' +
      '.nna-planner .pl-empty{font-size:11px;color:var(--fg-muted)}' +
      '.nna-planner .pl-money{font-size:12px}' +
      '.nna-planner .pl-money .pl-cat{display:flex;justify-content:space-between;font-size:10px;color:var(--fg-body);margin-top:4px}' +
      '.nna-planner .pl-add{margin-top:auto;display:flex;gap:8px;align-items:center}' +
      '.nna-planner .pl-add-field{flex:1;font-size:10px;letter-spacing:0.14em;color:var(--fg-muted);border:1px solid rgba(67,67,67,0.7);border-radius:10px;padding:10px 12px;cursor:text}' +
      '.nna-planner .pl-mic{width:34px;height:34px;border-radius:50%;border:1px solid rgba(67,67,67,0.7);background:transparent;color:var(--fg);flex:none;cursor:pointer}' +
      '.nna-planner .pl-mic.is-rec{background:rgba(200,40,40,0.5);border-color:transparent}' +
      '.nna-planner .pl-login{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px;height:100%}' +
      '.nna-planner .pl-login-btn{padding:11px 20px;font-size:10px;letter-spacing:0.2em;border:1px solid rgba(67,67,67,0.8);border-radius:12px;background:transparent;color:var(--fg);cursor:pointer}' +
      '.nna-planner .pl-login-btn:hover{background:var(--btn-hover,rgba(255,255,255,0.06))}';
    document.head.appendChild(style);
  }

  window.NNA = window.NNA || {};
  window.NNA.widgets = window.NNA.widgets || {};

  window.NNA.widgets.planner = function (mount, ctx) {
    ensureStyle();
    var settings = resolveSettings(ctx);
    var monitorId = resolveMonitorId();
    var b = N.block('tasks', { text: L.tasks || 'TASKS', strong: 'NNA PLANNER' });
    var wrap = N.el('div', 'nna-planner');
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    var loggedIn = null; // tri-state: null = unknown yet
    var recorder = null, recTimer = null;

    function text(key, fallback) { return L[key] || fallback; }

    function renderLogin() {
      wrap.innerHTML = '';
      var box = N.el('div', 'pl-login');
      var title = N.el('div', 'ta-title nna-big', text('plannerBrand', 'NNA PLANNER'));
      var btn = N.el('button', 'pl-login-btn', text('plannerLogin', 'ВОЙТИ'));
      btn.addEventListener('click', function () { N.post('/planner/login').catch(function () {}); });
      box.appendChild(title); box.appendChild(btn);
      wrap.appendChild(box);
    }

    function renderBriefing(data) {
      var sec = N.el('div', 'pl-section');
      sec.appendChild(N.el('div', 'pl-brief', data.briefing || ''));
      wrap.appendChild(sec);
    }

    function renderTasks(data) {
      var sec = N.el('div', 'pl-section');
      sec.appendChild(N.el('div', 'pl-h', text('plannerTasks', 'ЗАДАЧИ')));
      var list = N.el('div', 'pl-list');
      var tasks = (data.tasks || []).slice().sort(function (a, b2) {
        if (a.overdue !== b2.overdue) return a.overdue ? -1 : 1;
        return 0;
      });
      if (!tasks.length) list.appendChild(N.el('div', 'pl-empty', text('plannerNoTasks', 'Нет задач')));
      tasks.forEach(function (t) {
        var row = N.el('div', 'pl-row' + (t.state === 'done' ? ' is-done' : ''));
        row.appendChild(N.el('i', 'pl-check'));
        row.appendChild(N.el('span', 'pl-title', t.title));
        if (t.overdue) row.appendChild(N.el('span', 'pl-tag', text('plannerOverdue', 'ПРОСРОЧЕНО')));
        row.addEventListener('click', function () {
          var willBeDone = !row.classList.contains('is-done');
          row.classList.toggle('is-done', willBeDone);
          N.post('/planner/done?id=' + encodeURIComponent(t.id) + '&done=' + (willBeDone ? '1' : '0'))
            .then(function () { tick(); })
            .catch(function () { row.classList.toggle('is-done', !willBeDone); N.toast(text('plannerError', 'Ошибка')); });
        });
        list.appendChild(row);
      });
      sec.appendChild(list);
      wrap.appendChild(sec);
    }

    function renderMeetings(data) {
      var meetings = data.meetings || [];
      if (!meetings.length) return;
      var sec = N.el('div', 'pl-section');
      sec.appendChild(N.el('div', 'pl-h', text('plannerMeetings', 'ВСТРЕЧИ')));
      var list = N.el('div', 'pl-list');
      meetings.forEach(function (m) {
        var row = N.el('div', 'pl-row');
        var d = new Date(m.starts_at);
        row.appendChild(N.el('span', 'pl-time', N.pad2(d.getHours()) + ':' + N.pad2(d.getMinutes())));
        row.appendChild(N.el('span', 'pl-title', m.title));
        list.appendChild(row);
      });
      sec.appendChild(list);
      wrap.appendChild(sec);
    }

    function renderHabits(data) {
      var habits = data.habits || [];
      if (!habits.length) return;
      var sec = N.el('div', 'pl-section');
      sec.appendChild(N.el('div', 'pl-h', text('plannerHabits', 'ПРИВЫЧКИ')));
      var list = N.el('div', 'pl-list');
      habits.forEach(function (h) {
        var row = N.el('div', 'pl-row' + (h.checkedToday ? ' is-done' : ''));
        row.appendChild(N.el('i', 'pl-check'));
        row.appendChild(N.el('span', 'pl-title', h.title));
        row.addEventListener('click', function () {
          var next = !row.classList.contains('is-done');
          row.classList.toggle('is-done', next);
          N.post('/planner/habit?id=' + encodeURIComponent(h.id) + '&checked=' + (next ? '1' : '0'))
            .then(function () { tick(); })
            .catch(function () { row.classList.toggle('is-done', !next); N.toast(text('plannerError', 'Ошибка')); });
        });
        list.appendChild(row);
      });
      sec.appendChild(list);
      wrap.appendChild(sec);
    }

    function renderMoney(data) {
      var money = data.money || { spent_minor: 0, by_category: {} };
      var sec = N.el('div', 'pl-section');
      sec.appendChild(N.el('div', 'pl-h', text('plannerMoney', 'ТРАТЫ')));
      var box = N.el('div', 'pl-money');
      var rub = Math.round((money.spent_minor || 0) / 100);
      box.appendChild(N.el('div', null, rub.toLocaleString('ru-RU') + ' ₽'));
      var cats = money.by_category || {};
      Object.keys(cats).forEach(function (k) {
        var line = N.el('div', 'pl-cat');
        line.appendChild(N.el('span', null, k));
        line.appendChild(N.el('span', null, Math.round(cats[k] / 100).toLocaleString('ru-RU') + ' ₽'));
        box.appendChild(line);
      });
      sec.appendChild(box);
      wrap.appendChild(sec);
    }

    function renderAdd() {
      var row = N.el('div', 'pl-add');
      var field = N.el('div', 'pl-add-field', text('plannerAdd', '+ ДОБАВИТЬ'));
      row.appendChild(field);
      field.addEventListener('click', function () {
        var r = field.getBoundingClientRect();
        var px = Math.round(r.left + r.width / 2);
        var py = Math.round(r.top + r.height / 2);
        N.post('/planner/input?monitor=' + encodeURIComponent(monitorId) +
          '&x=' + px + '&y=' + py + '&placeholder=' + encodeURIComponent(text('plannerPlaceholder', 'Что сделать?')))
          .catch(function () {});
      });
      if (settings.voice) {
        var mic = N.el('button', 'pl-mic');
        mic.appendChild(N.svg('M12 14a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v5a3 3 0 0 0 3 3zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.92V21h2v-3.08A7 7 0 0 0 19 11h-2z'));
        mic.addEventListener('click', function () { toggleRecording(mic); });
        row.appendChild(mic);
      }
      wrap.appendChild(row);
    }

    function toggleRecording(btn) {
      if (recorder && recorder.state === 'recording') { recorder.stop(); return; }
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { N.toast(text('plannerError', 'Ошибка')); return; }
      navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
        var chunks = [];
        recorder = new MediaRecorder(stream, { mimeType: 'audio/webm;codecs=opus' });
        recorder.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
        recorder.onstop = function () {
          btn.classList.remove('is-rec');
          clearTimeout(recTimer);
          stream.getTracks().forEach(function (t) { t.stop(); });
          var blob = new Blob(chunks, { type: 'audio/webm' });
          blobToBase64(blob).then(function (b64) { sendVoice(b64); });
        };
        recorder.start();
        btn.classList.add('is-rec');
        recTimer = setTimeout(function () { if (recorder && recorder.state === 'recording') recorder.stop(); }, 30000);
      }).catch(function () { N.toast(text('plannerError', 'Ошибка')); });
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
        .catch(function () { N.toast(text('plannerError', 'Ошибка')); });
    }

    function handleCaptureResult(status, json) {
      if (status === 429 || (json && json.error === 'limit')) { N.toast(text('plannerLimit', 'Лимит ИИ на сегодня')); return; }
      if (json && json.ok) { N.toast((text('plannerRecorded', 'Записано') || 'Записано') + ': ' + ((json.entries || []).length)); return; }
      N.toast(json && (json.message || json.error) ? String(json.message || json.error) : text('plannerError', 'Ошибка'));
    }

    function renderLoggedIn(data) {
      wrap.innerHTML = '';
      var show = resolveShow();
      show.forEach(function (id) {
        if (id === 'briefing') renderBriefing(data);
        else if (id === 'tasks') renderTasks(data);
        else if (id === 'meetings') renderMeetings(data);
        else if (id === 'habits') renderHabits(data);
        else if (id === 'money') renderMoney(data);
      });
      renderAdd();
    }

    function loadToday() {
      N.get('/planner/today').then(function (data) {
        renderLoggedIn(data);
      }).catch(function () {
        // 401 or transient failure: fall back to the login view on the next status check.
      });
    }

    function tick() {
      N.get('/planner/status').then(function (s) {
        var was = loggedIn;
        loggedIn = !!s.loggedIn;
        if (!loggedIn) { renderLogin(); return; }
        if (was !== true) { loadToday(); return; }
        loadToday();
      }).catch(function () {
        // helper offline: keep last render, global .nna-offline dims the page.
      });
    }

    function onHostMessage(payload) {
      if (!payload || payload.type !== 'planner') return;
      if (payload.event === 'changed' || payload.event === 'login') { tick(); return; }
      if (payload.event === 'captured') {
        if (payload.ok) N.toast((text('plannerRecorded', 'Записано')) + ': ' + ((payload.entries || []).length));
        else if (payload.error === 'limit') N.toast(text('plannerLimit', 'Лимит ИИ на сегодня'));
        else N.toast(text('plannerError', 'Ошибка'));
        tick();
      }
    }

    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
          var data = e && e.data;
          if (typeof data === 'string') { try { data = JSON.parse(data); } catch (e2) { return; } }
          onHostMessage(data);
        });
      }
    } catch (e) { /* not inside WebView2 */ }

    window.addEventListener('nnaHost', function (e) { onHostMessage(e && e.detail); });

    tick();
    setInterval(tick, settings.refreshSec * 1000);

    return b;
  };
})();
