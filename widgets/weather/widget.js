/* NNA1618 — погода (через помощника, Open-Meteo) и часы в нескольких городах. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {}, CLOCKS = C.clocks || [];
  var DAYS_EN = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];

  N.weather = function (mount, ctx) {
    var b = N.block('weather', L.weather || 'WEATHER', { needsHelper: true });
    var wrap = N.el('div', 'we-wrap');
    var city = N.el('div', 'we-city nna-mono', N.t('noData', 'NO DATA'));
    var temp = N.el('div', 'we-temp nna-big', '·');
    var cond = N.el('div', 'we-cond nna-mono', '');
    var meta = N.el('div', 'we-meta nna-mono', '');
    var days = N.el('div', 'we-days');
    var clocks = N.el('div', 'we-clocks');
    wrap.appendChild(city); wrap.appendChild(temp); wrap.appendChild(cond); wrap.appendChild(meta);
    wrap.appendChild(days); wrap.appendChild(clocks);
    b.body.appendChild(wrap);
    mount.appendChild(b.root);

    var clockEls = CLOCKS.map(function (c) {
      var row = N.el('div', 'we-clock');
      var k = N.el('span', 'we-clock-k nna-mono', c.label);
      var v = N.el('span', 'we-clock-v nna-big', '--:--');
      var d = N.el('span', 'we-clock-d nna-mono', '');
      row.appendChild(k); row.appendChild(v); row.appendChild(d);
      clocks.appendChild(row);
      var fmtT = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: c.tz });
      var fmtD = new Intl.DateTimeFormat('en-US', { weekday: 'short', timeZone: c.tz });
      return { v: v, d: d, fmtT: fmtT, fmtD: fmtD };
    });

    function deg(x) { return x == null ? '·' : Math.round(x) + '°'; }
    // helper.py/WeatherService.cs присылают code (числовой WMO weathercode Open-Meteo) и text
    // (тот же перевод, но всегда по-английски) — на ru используем свою таблицу weatherCodes из
    // wallpaper/i18n.js по коду, английский текст помощника остаётся запасным вариантом.
    function condText(code, fallbackText) {
      var map = N.t('weatherCodes', null);
      if (map && code != null && Object.prototype.hasOwnProperty.call(map, code)) return map[code];
      return fallbackText || '';
    }
    function renderWeather(w) {
      city.textContent = w.name || N.t('noData', 'NO DATA');
      if (!w.ok) { temp.textContent = '·'; cond.textContent = N.t('noData', 'NO DATA'); meta.textContent = ''; return; }
      temp.textContent = deg(w.temp);
      cond.textContent = condText(w.code, w.text);
      meta.textContent = N.t('weatherFeels', 'FEELS') + ' ' + deg(w.feels) + ' · ' + N.t('weatherWind', 'WIND') + ' ' +
        Math.round(w.wind_ms || 0) + ' ' + N.t('weatherMs', 'M/S') + ' · ' + N.t('weatherHum', 'HUM') + ' ' + Math.round(w.humidity || 0) + '%';
      days.textContent = '';
      var daysShort = N.t('days', DAYS_EN);
      (w.days || []).slice(1, 4).forEach(function (d) {
        var el = N.el('div', 'we-day');
        var dt = new Date(d.date + 'T12:00:00');
        el.appendChild(N.el('span', 'we-day-k nna-mono', daysShort[dt.getDay()]));
        el.appendChild(N.el('span', 'we-day-v nna-big', deg(d.max) + ' / ' + deg(d.min)));
        el.appendChild(N.el('span', 'we-day-t nna-mono', condText(d.code, d.text)));
        days.appendChild(el);
      });
    }
    function tickClocks() {
      var now = new Date(), localIdx = now.getDay(), daysShort = N.t('days', DAYS_EN);
      clockEls.forEach(function (c) {
        c.v.textContent = c.fmtT.format(now);
        var wd = c.fmtD.format(now).toLowerCase().slice(0, 3);
        var idx = DAYS_EN.indexOf(wd);
        c.d.textContent = (idx === -1 || idx === localIdx) ? '' : daysShort[idx];
      });
    }
    var readyReported = false, failReported = false;
    function reportReady() {
      if (readyReported) return;
      readyReported = true;
      if (ctx && ctx.ready) ctx.ready();
    }
    function poll() {
      N.get('/weather').then(function (w) {
        failReported = false;
        renderWeather(w);
        reportReady();
      }, function (err) {
        if (!failReported) { failReported = true; if (ctx && ctx.fail) ctx.fail(err); }
      });
    }

    // часы не зависят от сети — если погода недоступна, готовность всё равно наступает по ним
    poll(); tickClocks(); reportReady();
    ctx.setInterval(tickClocks, 1000);
    ctx.setInterval(poll, ((C.weather && C.weather.refreshMin) || 10) * 60 * 1000);
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.weather = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.weather = Object.assign({}, N.config.weather || {}, { refreshMin: s.refreshMin });
  N.config.clocks = s.clocks || N.config.clocks || [
    { label: 'MOSCOW', tz: 'Europe/Moscow' },
    { label: 'LOS ANGELES', tz: 'America/Los_Angeles' },
    { label: 'VLADIVOSTOK', tz: 'Asia/Vladivostok' }
  ];
  var w = N.weather(mount, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
