/* NNA1618 — события и ежедневные отсчёты. Данные в helper\events.json:
   { "events": [{"label":"NEW YEAR","date":"2027-01-01"}], "daily": [{"label":"SLEEP","time":"23:30"}] }
   Показывает ближайшие, по три на страницу, стрелки листают, кнопка EDIT открывает файл в редакторе. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {}, EV = C.events || {};
  var PER = EV.perPage || 3;
  var MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];

  N.events = function (mount, ctx) {
    var b = N.block('events', L.events || 'EVENTS', { needsHelper: true });
    var tools = N.el('div', 'ev-tools');
    var prev = N.el('button', 'ev-nav', '‹'), next = N.el('button', 'ev-nav', '›');
    var pageEl = N.el('span', 'ev-page nna-mono', '');
    var edit = N.el('button', 'nna-btn ev-edit', 'EDIT');
    prev.type = next.type = edit.type = 'button';
    tools.appendChild(prev); tools.appendChild(pageEl); tools.appendChild(next); tools.appendChild(edit);
    var list = N.el('div', 'ev-list');
    b.root.appendChild(tools);
    b.body.appendChild(list);
    mount.appendChild(b.root);

    var data = { events: [], daily: [] }, items = [], page = 0, cards = [];

    function parseDate(s) {
      if (!s) return null;
      var d = new Date(/T/.test(s) ? s : s + 'T00:00:00');
      return isNaN(d) ? null : d;
    }
    function nextDaily(time) {
      var m = /^(\d{1,2}):(\d{2})$/.exec(String(time || '').trim());
      if (!m) return null;
      var now = new Date(), d = new Date(now.getFullYear(), now.getMonth(), now.getDate(), +m[1], +m[2], 0, 0);
      if (d <= now) d.setDate(d.getDate() + 1);
      return d;
    }
    function rebuild() {
      var now = Date.now(), out = [];
      (data.events || []).forEach(function (e) {
        var d = parseDate(e.date); if (!d) return;
        var past = d.getTime() < now - 86400000;
        if (past) return;
        out.push({ kind: 'event', label: e.label || 'EVENT', at: d });
      });
      (data.daily || []).forEach(function (e) {
        var d = nextDaily(e.time); if (!d) return;
        out.push({ kind: 'daily', label: e.label || 'DAILY', at: d, time: e.time });
      });
      out.sort(function (a, c) { return a.at - c.at; });
      items = out;
      var pages = Math.max(1, Math.ceil(items.length / PER));
      if (page >= pages) page = pages - 1;
      renderPage();
    }
    function fmtDate(d) { return N.pad2(d.getDate()) + ' ' + MONTHS[d.getMonth()] + ' ' + d.getFullYear(); }
    function renderPage() {
      list.textContent = ''; cards = [];
      var pages = Math.max(1, Math.ceil(items.length / PER));
      pageEl.textContent = items.length ? (page + 1) + ' / ' + pages : '';
      prev.classList.toggle('is-hidden', pages <= 1); next.classList.toggle('is-hidden', pages <= 1);
      var slice = items.slice(page * PER, page * PER + PER);
      if (!slice.length) {
        var empty = N.el('div', 'ev-empty nna-mono', 'NO EVENTS · PRESS EDIT');
        list.appendChild(empty); return;
      }
      slice.forEach(function (it) {
        var card = N.el('div', 'ev-card' + (it.kind === 'daily' ? ' is-daily' : ''));
        var k = N.el('div', 'ev-k nna-mono', it.label);
        var v = N.el('div', 'ev-v nna-big', '');
        var u = N.el('div', 'ev-u nna-mono', '');
        var s = N.el('div', 'ev-s nna-mono', it.kind === 'daily' ? 'DAILY · ' + it.time : fmtDate(it.at));
        card.appendChild(k); card.appendChild(v); card.appendChild(u); card.appendChild(s);
        list.appendChild(card);
        cards.push({ it: it, v: v, u: u });
      });
      tick();
    }
    function tick() {
      var now = Date.now();
      cards.forEach(function (c) {
        var diff = c.it.at - now;
        if (c.it.kind === 'daily' && diff < 0) { rebuild(); return; }
        var s = Math.max(0, Math.floor(diff / 1000));
        var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
        if (c.it.kind === 'event') {
          if (d >= 1) { c.v.textContent = d < 100 ? N.pad2(d) : String(d); c.u.textContent = d === 1 ? 'DAY' : 'DAYS'; }
          else { c.v.textContent = N.pad2(h) + ':' + N.pad2(m); c.u.textContent = diff <= 0 ? 'TODAY' : 'HOURS LEFT'; }
        } else {
          c.v.textContent = h > 0 ? N.pad2(h) + ':' + N.pad2(m) : N.pad2(m) + ':' + N.pad2(r);
          c.u.textContent = h > 0 ? 'H : M' : 'M : S';
        }
      });
    }
    var readyReported = false, failReported = false;
    function load() {
      N.get('/events').then(function (d) {
        failReported = false;
        data = d; rebuild();
        if (!readyReported) { readyReported = true; if (ctx && ctx.ready) ctx.ready(); }
      }, function (err) {
        if (!failReported) { failReported = true; if (ctx && ctx.fail) ctx.fail(err); }
      });
    }

    prev.addEventListener('click', function () { if (page > 0) { page--; renderPage(); } });
    next.addEventListener('click', function () { if ((page + 1) * PER < items.length) { page++; renderPage(); } });
    edit.addEventListener('click', function () {
      N.post('/edit?what=events').then(function (r) { N.toast(r && r.ok ? 'OPENING EVENTS.JSON' : (r && r.error) || 'FAILED'); },
        function () { N.toast('HELPER OFFLINE'); });
    });

    load();
    ctx.setInterval(tick, 1000);
    ctx.setInterval(load, (EV.refreshSec || 30) * 1000);
    ctx.setInterval(rebuild, 60000);
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.events = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.events = Object.assign({}, N.config.events || {}, s);
  var w = N.events(mount, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
