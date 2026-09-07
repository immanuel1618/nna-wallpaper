/* NNA1618 — ядро: клиент помощника, утилиты, каркас блока, тост, часы. */
(function () {
  'use strict';
  var cfgHelper = window.NNA_HELPER || { url: 'http://127.0.0.1:1618', token: '' };
  var C = window.NNA_CONFIG || {};
  var online = null;
  var toastEl = null, toastTimer = null;

  function get(path) {
    return fetch(cfgHelper.url + path, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  }
  function post(path) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return fetch(cfgHelper.url + path + sep + 't=' + encodeURIComponent(cfgHelper.token), { method: 'POST' })
      .then(function (r) { return r.json(); });
  }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function pad2(n) { n = Math.floor(n); return (n < 10 ? '0' : '') + n; }
  function fmtTime(s) {
    s = Math.max(0, Math.floor(s || 0));
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    return (h ? h + ':' + pad2(m) : m) + ':' + pad2(r);
  }
  function fmtBytes(b, perSec) {
    var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0; b = b || 0;
    while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return (b >= 100 ? Math.round(b) : b >= 10 ? b.toFixed(1) : b.toFixed(2)) + ' ' + u[i] + (perSec ? '/S' : '');
  }
  function fmtGB(b) { return (b / 1073741824).toFixed(b >= 107374182400 ? 0 : 1); }
  var DAYS = ['SUNDAY', 'MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY'];
  var MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
  function fmtDate(d) { return DAYS[d.getDay()] + ' · ' + pad2(d.getDate()) + ' ' + MONTHS[d.getMonth()] + ' ' + d.getFullYear(); }
  function fmtClock(d) { return pad2(d.getHours()) + ':' + pad2(d.getMinutes()); }

  /* каркас блока: <section class="nna-block"><div class="nna-label">…</div><div class="nna-body"/></section> */
  function block(id, label, opts) {
    opts = opts || {};
    var root = el('section', 'nna-block nna-' + id + (opts.needsHelper ? ' nna-needs-helper' : ''));
    root.dataset.block = id;
    var lab = el('div', 'nna-label');
    setLabel(lab, label);
    var body = el('div', 'nna-body');
    root.appendChild(lab); root.appendChild(body);
    return { root: root, body: body, label: lab, setLabel: function (t) { setLabel(lab, t); } };
  }
  function setLabel(lab, label) {
    lab.textContent = '';
    if (typeof label === 'string') { lab.textContent = label; return; }
    lab.appendChild(document.createTextNode(label.text || ''));
    if (label.strong != null) {
      lab.appendChild(document.createTextNode(label.text ? ' · ' : ''));
      lab.appendChild(el('b', null, label.strong));
    }
  }

  function toast(msg) {
    if (!toastEl) { toastEl = el('div', 'nna-toast'); document.body.appendChild(toastEl); }
    toastEl.textContent = msg;
    toastEl.classList.add('is-show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('is-show'); }, 1800);
  }

  function svg(pathD, viewBox) {
    var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('viewBox', viewBox || '0 0 24 24');
    var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', pathD);
    s.appendChild(p);
    return s;
  }

  /* здоровье помощника: раз в N секунд, класс nna-offline на <html> */
  function watchHealth() {
    function tick() {
      get('/health').then(function () { setOnline(true); }, function () { setOnline(false); });
    }
    tick();
    setInterval(tick, (C.poll && C.poll.health) || 5000);
  }
  function setOnline(v) {
    if (v === online) return;
    online = v;
    document.documentElement.classList.toggle('nna-offline', !v);
  }

  function ready(fn) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn);
    else fn();
  }

  /* ---- жизненный цикл виджета -------------------------------------------
     Один объект на монтирование (модульный виджет или ячейка page-виджета). Виджет использует
     ctx.setInterval/setTimeout/raf/on вместо голых глобальных вызовов; layout.js вызывает
     ctx.dispose() при размонтировании ячейки (смена настроек, удаление блока, смена монитора),
     что снимает все таймеры и слушатели разом — без этого виджет продолжал бы тикать в фоне
     после того, как его DOM уже удалён. */
  function createLifecycle() {
    var pending = [];   // { clear } записи для живых setInterval/setTimeout/raf, снятые по срабатыванию
    var listeners = []; // { target, event, fn, opts }
    var disposers = [];
    var disposed = false;

    function untrack(rec) {
      var i = pending.indexOf(rec);
      if (i !== -1) pending.splice(i, 1);
    }

    function lcSetInterval(fn, ms) {
      var id = setInterval(fn, ms);
      pending.push({ clear: function () { clearInterval(id); } });
      return id;
    }
    function lcSetTimeout(fn, ms) {
      var rec;
      var id = setTimeout(function () { untrack(rec); fn(); }, ms);
      rec = { clear: function () { clearTimeout(id); } };
      pending.push(rec);
      return id;
    }
    function lcRaf(fn) {
      var rec;
      var id = requestAnimationFrame(function (ts) { untrack(rec); fn(ts); });
      rec = { clear: function () { cancelAnimationFrame(id); } };
      pending.push(rec);
      return id;
    }
    function lcOn(target, event, fn, opts) {
      if (!target || typeof target.addEventListener !== 'function') return;
      target.addEventListener(event, fn, opts);
      listeners.push({ target: target, event: event, fn: fn, opts: opts });
    }
    function lcOnDispose(fn) {
      if (typeof fn === 'function') disposers.push(fn);
    }
    function dispose() {
      if (disposed) return;
      disposed = true;
      var i;
      for (i = 0; i < pending.length; i++) { try { pending[i].clear(); } catch (e) { /* noop */ } }
      pending.length = 0;
      for (i = 0; i < listeners.length; i++) {
        var l = listeners[i];
        try { l.target.removeEventListener(l.event, l.fn, l.opts); } catch (e) { /* noop */ }
      }
      listeners.length = 0;
      for (i = 0; i < disposers.length; i++) { try { disposers[i](); } catch (e) { /* noop */ } }
      disposers.length = 0;
    }

    return {
      setInterval: lcSetInterval, setTimeout: lcSetTimeout, raf: lcRaf,
      on: lcOn, onDispose: lcOnDispose, dispose: dispose,
      isDisposed: function () { return disposed; }
    };
  }

  /* общее затемнение: чёрный слой поверх всего, клики сквозь него проходят */
  ready(function () {
    var dim = Math.min(1, Math.max(0, +C.dim || 0));
    if (!dim) return;
    var d = el('div', 'nna-dim');
    d.style.opacity = String(dim);
    document.body.appendChild(d);
  });

  window.NNA = {
    get: get, post: post, el: el, pad2: pad2, fmtTime: fmtTime, fmtBytes: fmtBytes, fmtGB: fmtGB,
    fmtDate: fmtDate, fmtClock: fmtClock, block: block, toast: toast, svg: svg, ready: ready,
    watchHealth: watchHealth, isOnline: function () { return online !== false; }, config: C,
    createLifecycle: createLifecycle,
    icons: {
      play: 'M8 5v14l11-7z',
      pause: 'M6 5h4v14H6zm8 0h4v14h-4z',
      next: 'M6 6l8.5 6L6 18zM16 6h2v12h-2z',
      prev: 'M18 6l-8.5 6L18 18zM6 6h2v12H6z',
      reset: 'M12 5V2L7 7l5 5V8c3.3 0 6 2.7 6 6s-2.7 6-6 6-6-2.7-6-6H4c0 4.4 3.6 8 8 8s8-3.6 8-8-3.6-8-8-8z'
    }
  };
})();
