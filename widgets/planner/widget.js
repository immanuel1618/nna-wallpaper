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
      '.nna-planner .pl-k{font-size:11px;color:var(--fg-muted);letter-spacing:var(--track-label);text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-v{font-size:64px}' +
      '.nna-planner .pl-sub{font-size:11px;color:var(--fg-body);letter-spacing:var(--track-label);text-transform:uppercase;font-family:var(--font-mono)}' +
      // flex:1 + justify-content:center: when the task list is shorter than the section's grown
      // height (few or no tasks today), the rows sit centered in the space below the head/sub
      // (which stay pinned at the top of .pl-sec) instead of leaving one big empty gap underneath.
      '.nna-planner .pl-list{flex:1;display:flex;flex-direction:column;justify-content:center;gap:16px;overflow:hidden;min-height:0}' +
      '.nna-planner .pl-row{display:flex;align-items:center;gap:14px;cursor:pointer;min-width:0}' +
      '.nna-planner .pl-row:hover .pl-title{color:var(--fg)}' +
      '.nna-planner .pl-check{width:16px;height:16px;border-radius:50%;border:1px solid var(--border);flex:none;position:relative;transition:background 0.2s}' +
      '.nna-planner .pl-row.is-done .pl-check{background:var(--fg);border-color:var(--fg)}' +
      '.nna-planner .pl-row.is-done .pl-title{text-decoration:line-through;color:var(--fg-muted)}' +
      '.nna-planner .pl-title{font-size:15px;color:var(--fg-body);flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;transition:color 0.2s}' +
      '.nna-planner .pl-time{font-size:10px;color:var(--fg-muted);letter-spacing:var(--track-number);font-family:var(--font-mono);flex:none}' +
      '.nna-planner .pl-time.is-over{color:var(--fg)}' +
      '.nna-planner .pl-more{font-size:10px;color:var(--fg-muted);letter-spacing:var(--track-label);font-family:var(--font-mono);text-transform:uppercase}' +
      '.nna-planner .pl-empty{font-size:11px;color:var(--fg-muted);letter-spacing:var(--track-label);text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-brief{font-size:12px;color:var(--fg-body);line-height:1.55;display:-webkit-box;-webkit-line-clamp:4;-webkit-box-orient:vertical;overflow:hidden}' +
      '.nna-planner .pl-meet{display:flex;align-items:baseline;gap:14px;min-width:0}' +
      '.nna-planner .pl-meet-t{font-family:var(--font-display);font-size:22px;color:var(--fg);flex:none;font-variant-numeric:tabular-nums}' +
      '.nna-planner .pl-meet-n{font-size:13px;color:var(--fg-body);flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
      '.nna-planner .pl-chips{display:flex;flex-wrap:wrap;gap:8px}' +
      '.nna-planner .pl-chip{padding:9px 14px;font-size:10px}' +
      '.nna-planner .pl-money-v{font-size:38px}' +
      '.nna-planner .pl-cats{display:flex;flex-direction:column;gap:6px}' +
      '.nna-planner .pl-cat{display:flex;justify-content:space-between;gap:12px;font-size:10px;color:var(--fg-body);letter-spacing:var(--track-label);text-transform:uppercase;font-family:var(--font-mono)}' +
      '.nna-planner .pl-add{display:flex;gap:12px;align-items:center;flex:none}' +
      '.nna-planner .pl-add-btn{flex:1;justify-content:flex-start;text-align:left;padding:14px 22px;font-size:11px}' +
      '.nna-planner .pl-mic-wrap{position:relative;width:44px;height:44px;flex:none;display:flex;align-items:center;justify-content:center}' +
      '.nna-planner .pl-mic-ring{position:absolute;inset:0;border-radius:50%;background:var(--fg);opacity:0;transform:scale(1);pointer-events:none;transition:opacity 0.15s}' +
      '.nna-planner .pl-mic-wrap.is-rec .pl-mic-ring{opacity:0.2}' +
      '.nna-planner .pl-mic{width:44px;height:44px;flex:none;position:relative;z-index:1}' +
      '.nna-planner .pl-mic svg{width:18px;height:18px;fill:currentColor}' +
      '.nna-planner .pl-mic.is-rec{background:var(--fg);color:var(--bg-surface);border-color:var(--fg)}' +
      '.nna-planner .pl-voice-timer{font-size:10px;color:var(--fg-muted);letter-spacing:var(--track-number);font-family:var(--font-mono);font-variant-numeric:tabular-nums;flex:none;opacity:0;transition:opacity 0.15s}' +
      '.nna-planner .pl-voice-timer.is-show{opacity:1}' +
      // .is-hidden (shared utility, display:none!important) takes the note/result panel fully out
      // of .nna-planner's gapped flex flow while empty — a short block (e.g. the vertical layout's
      // TASKS slot, ~93px for five stacked sections) would otherwise lose flex height to their
      // gap alone even at zero content height, since flex `gap` applies between rendered items
      // regardless of how small they are.
      '.nna-planner .pl-voice-note{font-size:9px;color:var(--fg-muted);letter-spacing:var(--track-label);text-transform:uppercase;font-family:var(--font-mono);margin-top:8px}' +
      '.nna-planner .pl-voice-result{display:flex;flex-direction:column;gap:10px;margin-top:12px;padding:14px 18px;border-radius:16px;background:var(--btn);border:1px solid rgba(67,67,67,0.5);opacity:0;transition:opacity 0.2s}' +
      '.nna-planner .pl-voice-result.is-show{opacity:1}' +
      '.nna-planner .pl-voice-result-text{font-size:12px;color:var(--fg-body);line-height:1.5}' +
      '.nna-planner .pl-voice-result-row{display:flex;align-items:center;justify-content:flex-end}' +
      '.nna-planner .pl-voice-undo{padding:9px 16px;font-size:10px}' +
      '.nna-planner .pl-login{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:26px}' +
      '.nna-planner .pl-login .pl-brand{font-size:92px;font-size:16cqh}' +
      '.nna-planner .pl-login .pl-hint{font-size:12px;color:var(--fg-muted);letter-spacing:var(--track-label);font-family:var(--font-mono);text-transform:uppercase}' +
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
    var b = N.block('tasks', { text: L.tasks || 'TASKS', strong: N.t('tasksSoon', L.tasksSoon || 'NNA PLANNER') });
    var corner = N.el('div', 'nna-corner', '');
    b.root.appendChild(corner);
    var wrap = N.el('div', 'nna-planner');
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    var loggedIn = null; // tri-state: null = unknown yet
    var profile = null;
    var lastData = null;

    // ---- voice capture state --------------------------------------------------------------------
    var recorder = null, recStream = null, recSend = false, recTimer = null, recTickId = null;
    var recStartAt = 0;
    var audioCtx = null, analyser = null, levelData = null, levelActive = false;
    var micWrap = null, micBtn = null, micRing = null, voiceTimerEl = null, voiceNoteEl = null, voiceResultEl = null;
    var voiceResultTimer = null;
    // true while recording or while the post-capture result/undo panel is shown: suppresses the
    // periodic re-render (loadToday -> renderLoggedIn wipes wrap.innerHTML, which would otherwise
    // yank the mic/result UI out from under an in-progress recording or a just-shown undo button).
    var voiceUiActive = false;

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
      var p = N.el('div', 'pl-brief', data.briefing || text('plannerNoBriefing', 'НЕТ ДАННЫХ'));
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
        row.appendChild(N.el('span', 'pl-meet-t', fmtTime(m.starts_at) || '·'));
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
        micWrap = N.el('div', 'pl-mic-wrap');
        micRing = N.el('div', 'pl-mic-ring');
        micBtn = N.el('button', 'nna-icon-btn pl-mic');
        micBtn.type = 'button';
        micBtn.title = text('plannerVoice', 'Голосом');
        micBtn.appendChild(N.svg('M12 14a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v5a3 3 0 0 0 3 3zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.92V21h2v-3.08A7 7 0 0 0 19 11h-2z'));
        micBtn.addEventListener('click', function () { toggleRecording(); });
        micWrap.appendChild(micRing); micWrap.appendChild(micBtn);
        voiceTimerEl = N.el('div', 'pl-voice-timer', '0:00');
        row.appendChild(micWrap);
        row.appendChild(voiceTimerEl);
      }
      wrap.appendChild(row);
      if (settings.voice) {
        voiceNoteEl = N.el('div', 'pl-voice-note is-hidden', '');
        wrap.appendChild(voiceNoteEl);
      }
      voiceResultEl = N.el('div', 'pl-voice-result is-hidden');
      wrap.appendChild(voiceResultEl);
      // a recording (or an undo panel) in progress when this render happened survives a re-render
      // only via voiceUiActive guarding loadToday(); on first render there is nothing to restore.
    }

    // ---- recording lifecycle --------------------------------------------------------------------
    // is-hidden (display:none) takes the note fully out of the flex flow while empty, not just
    // invisible, so it never costs the block flex height/gap it doesn't need (see ensureStyle()).
    function setVoiceNote(msg) {
      if (!voiceNoteEl) return;
      voiceNoteEl.textContent = msg || '';
      voiceNoteEl.classList.toggle('is-hidden', !msg);
    }

    function resolveCaptureDeviceId() {
      if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return Promise.resolve(null);
      return N.get('/audio/capture-device').catch(function () { return { name: null }; }).then(function (pref) {
        var wantName = pref && pref.name;
        if (!wantName) return null;
        return navigator.mediaDevices.enumerateDevices().then(function (list) {
          var found = list.filter(function (d) { return d.kind === 'audioinput' && d.label === wantName; })[0];
          return found ? found.deviceId : null; // no label match (no permission yet, or renamed device): fall back to default
        }).catch(function () { return null; });
      });
    }

    function toggleRecording() {
      if (recorder && recorder.state === 'recording') { finishRecording(true); return; }
      startRecording();
    }

    function startRecording() {
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        setVoiceNote(text('plannerNoMic', 'НЕТ МИКРОФОНА'));
        N.toast(text('plannerNoMic', 'НЕТ МИКРОФОНА'));
        return;
      }
      resolveCaptureDeviceId().then(function (deviceId) {
        var constraints = { audio: deviceId ? { deviceId: { exact: deviceId } } : true };
        return navigator.mediaDevices.getUserMedia(constraints);
      }).then(function (stream) {
        var chunks = [];
        try {
          recorder = new MediaRecorder(stream, { mimeType: 'audio/webm;codecs=opus' });
        } catch (e) {
          stream.getTracks().forEach(function (t) { t.stop(); });
          setVoiceNote(text('plannerMicError', 'ОШИБКА МИКРОФОНА'));
          N.toast(text('plannerMicError', 'ОШИБКА МИКРОФОНА'));
          return;
        }
        recStream = stream;
        recSend = false;
        recorder.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
        recorder.onstop = function () {
          stopLevelMeter();
          if (recTickId != null) { clearInterval(recTickId); recTickId = null; }
          micUiReset();
          stream.getTracks().forEach(function (t) { t.stop(); });
          var send = recSend;
          recorder = null; recStream = null;
          if (!send) { voiceUiActive = false; return; }
          var blob = new Blob(chunks, { type: 'audio/webm' });
          N.toast(text('plannerSending', 'ОТПРАВЛЯЮ'));
          blobToBase64(blob).then(function (b64) { sendVoice(b64); });
        };
        recorder.start();
        recStartAt = Date.now();
        voiceUiActive = true;
        setVoiceNote('');
        if (micWrap) micWrap.classList.add('is-rec');
        if (micBtn) micBtn.classList.add('is-rec');
        if (voiceTimerEl) { voiceTimerEl.textContent = '0:00'; voiceTimerEl.classList.add('is-show'); }
        startLevelMeter(stream);
        recTickId = ctx.setInterval(updateVoiceTimer, 1000);
        N.toast(text('plannerRecording', 'ЗАПИСЬ · НАЖМИ ЕЩЁ РАЗ, ЧТОБЫ ОТПРАВИТЬ'));
        recTimer = ctx.setTimeout(function () { finishRecording(true); }, 30000);
      }).catch(function (err) {
        var denied = err && (err.name === 'NotAllowedError' || err.name === 'SecurityError');
        var msg = denied ? text('plannerMicDenied', 'НЕТ ДОСТУПА К МИКРОФОНУ') : text('plannerNoMic', 'НЕТ МИКРОФОНА');
        setVoiceNote(msg);
        N.toast(msg);
      });
    }

    function finishRecording(send) {
      if (!recorder || recorder.state !== 'recording') return;
      recSend = send;
      recorder.stop();
    }

    function micUiReset() {
      clearTimeout(recTimer);
      if (micWrap) micWrap.classList.remove('is-rec');
      if (micBtn) micBtn.classList.remove('is-rec');
      if (micRing) micRing.style.transform = 'scale(1)';
      if (voiceTimerEl) voiceTimerEl.classList.remove('is-show');
    }

    function updateVoiceTimer() {
      if (!voiceTimerEl) return;
      var s = Math.max(0, Math.floor((Date.now() - recStartAt) / 1000));
      voiceTimerEl.textContent = Math.floor(s / 60) + ':' + N.pad2(s % 60);
    }

    // clicking anywhere outside the mic button while recording cancels without sending
    ctx.on(document, 'click', function (e) {
      if (!recorder || recorder.state !== 'recording') return;
      if (micWrap && micWrap.contains(e.target)) return; // the mic button's own handler sends instead
      finishRecording(false);
    }, true);

    // ---- input level -> pulsing ring (scale 1.0-1.6, opacity via .is-rec) ------------------------
    function startLevelMeter(stream) {
      try {
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return;
        audioCtx = new Ctx();
        var source = audioCtx.createMediaStreamSource(stream);
        analyser = audioCtx.createAnalyser();
        analyser.fftSize = 256;
        analyser.smoothingTimeConstant = 0.6;
        levelData = new Uint8Array(analyser.frequencyBinCount);
        source.connect(analyser);
        levelActive = true;
        ctx.raf(levelLoop);
      } catch (e) { /* no AudioContext (or blocked): ring just stays static while recording */ }
    }
    function levelLoop() {
      if (!levelActive || !analyser) return;
      analyser.getByteFrequencyData(levelData);
      var sum = 0, i;
      for (i = 0; i < levelData.length; i++) sum += levelData[i];
      var avg = sum / levelData.length / 255;
      var scale = 1 + Math.min(1, avg * 1.6) * 0.6;
      if (micRing) micRing.style.transform = 'scale(' + scale.toFixed(3) + ')';
      ctx.raf(levelLoop);
    }
    function stopLevelMeter() {
      levelActive = false;
      analyser = null;
      levelData = null;
      if (audioCtx) { try { audioCtx.close(); } catch (e) {} audioCtx = null; }
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
      voiceUiActive = true;
      fetch(helper.url + '/planner/capture?t=' + encodeURIComponent(helper.token), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ audio_base64: audioBase64, mime: 'audio/webm' }),
      }).then(function (r) { return r.json().then(function (j) { return { status: r.status, json: j }; }); })
        .then(function (res) { handleCaptureResult(res.status, res.json); })
        .catch(function () { voiceUiActive = false; N.toast(text('plannerError', 'ОШИБКА')); tick(); });
    }

    // ---- shared result handling: both mic capture (sendVoice) and text capture via InputWindow
    // (onHostMessage 'captured') land here, so both get the recognized-text + undo panel. ----------
    function handleCaptureResult(status, json) {
      if (status === 429 || (json && json.error === 'limit')) {
        voiceUiActive = false;
        N.toast(text('plannerLimit', 'ЛИМИТ ИИ НА СЕГОДНЯ'));
        tick();
        return;
      }
      if (json && json.ok) {
        showVoiceResult(json);
        return;
      }
      voiceUiActive = false;
      N.toast(json && (json.message || json.error || json.reason) ? String(json.message || json.error || json.reason).toUpperCase() : text('plannerError', 'ОШИБКА'));
      tick();
    }

    function showVoiceResult(json) {
      var entries = json.entries || [];
      var recognized = entries.length
        ? entries.map(function (e) { return e.title || ''; }).filter(Boolean).join(' · ')
        : text('plannerNothing', 'НИЧЕГО НЕ РАЗОБРАЛ');
      N.toast(entries.length ? text('plannerRecorded', 'ЗАПИСАНО') + ': ' + entries.length : text('plannerNothing', 'НИЧЕГО НЕ РАЗОБРАЛ'));

      voiceUiActive = true;
      if (voiceResultEl) {
        voiceResultEl.textContent = '';
        voiceResultEl.appendChild(N.el('div', 'pl-voice-result-text', recognized || text('plannerNothing', 'НИЧЕГО НЕ РАЗОБРАЛ')));
        var batchId = json.batch_id;
        if (batchId && entries.length) {
          var row = N.el('div', 'pl-voice-result-row');
          var undo = N.el('button', 'nna-btn pl-voice-undo', text('plannerUndo', 'ОТМЕНИТЬ'));
          undo.type = 'button';
          undo.addEventListener('click', function () {
            clearTimeout(voiceResultTimer);
            N.post('/planner/undo?batch=' + encodeURIComponent(batchId)).then(function () {
              N.toast(text('plannerUndone', 'ОТМЕНЕНО'));
              hideVoiceResult();
            }).catch(function () { N.toast(text('plannerError', 'ОШИБКА')); });
          });
          row.appendChild(undo);
          voiceResultEl.appendChild(row);
        }
        voiceResultEl.classList.remove('is-hidden');
        voiceResultEl.classList.add('is-show');
      }
      clearTimeout(voiceResultTimer);
      voiceResultTimer = ctx.setTimeout(hideVoiceResult, 5000);
    }

    function hideVoiceResult() {
      if (voiceResultEl) { voiceResultEl.classList.remove('is-show'); voiceResultEl.classList.add('is-hidden'); }
      voiceUiActive = false;
      tick();
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
      // a re-render wipes wrap.innerHTML, which would yank the mic/result UI out from under an
      // in-progress recording or a just-shown undo panel; hideVoiceResult()/the cancel path call
      // tick() again once voiceUiActive clears, so nothing here is missed, just deferred a few seconds.
      if (voiceUiActive) return;
      N.get('/planner/today').then(function (data) {
        renderLoggedIn(data);
      }).catch(function () {
        // 401 or transient failure: the next status check decides between login and retry.
      });
    }

    // ---- readiness / self-heal ------------------------------------------------------------------
    // A /planner/status answer counts as "data" the moment it arrives, whether or not the user is
    // logged in — the empty-LAUNCH/stuck-TASKS incident this guards against showed a status of
    // false/never-arriving is exactly the failure mode layout.js's remount watchdog needs to know
    // about promptly, not 8s of silent guessing.
    var reportedReady = false;
    function reportReady() {
      if (reportedReady) return;
      reportedReady = true;
      if (ctx && ctx.ready) ctx.ready();
    }
    var MAX_STATUS_RETRIES = 5;
    var statusRetryCount = 0;
    var loggedOutPollId = null;

    function clearLoggedOutPoll() {
      if (loggedOutPollId != null) { clearTimeout(loggedOutPollId); loggedOutPollId = null; }
    }

    function tick() {
      N.get('/planner/status').then(function (s) {
        statusRetryCount = 0;
        reportReady();
        loggedIn = !!s.loggedIn;
        profile = s.profile || null;
        clearLoggedOutPoll();
        if (!loggedIn) {
          renderLogin();
          // refreshSec (default 60s) is meant for the logged-in data cadence; a stuck "sign in"
          // screen while the host is actually already logged in (session restored a few seconds
          // after this page booted) must not wait a full minute to notice — recheck every 30s too.
          loggedOutPollId = ctx.setTimeout(tick, 30000);
          return;
        }
        loadToday();
      }).catch(function (err) {
        // Network error / host offline: retry sooner than the refreshSec cadence, up to a point,
        // then fall back to it (and let the layout-level remount watchdog take over if this widget
        // still never became ready).
        if (statusRetryCount < MAX_STATUS_RETRIES) {
          statusRetryCount++;
          ctx.setTimeout(tick, 3000);
        } else {
          statusRetryCount = 0;
          if (ctx && ctx.fail) ctx.fail(err);
        }
      });
    }

    function onHostMessage(payload) {
      if (!payload || payload.type !== 'planner') return;
      if (payload.event === 'changed' || payload.event === 'login') { tick(); return; }
      if (payload.event === 'captured') {
        // same shape as the direct /planner/capture response (ok, entries, batch_id, error/reason);
        // shown through the same recognized-text + undo panel as a mic capture (handleCaptureResult).
        handleCaptureResult(payload.error === 'limit' ? 429 : 200, payload);
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
