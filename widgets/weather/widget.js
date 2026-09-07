/* NNA1618 — погода (через помощника, Open-Meteo) и часы в нескольких городах. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {}, CLOCKS = C.clocks || [];
  var DAYS = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];

  N.weather = function (mount) {
    var b = N.block('weather', L.weather || 'WEATHER', { needsHelper: true });
    var wrap = N.el('div', 'we-wrap');
    var city = N.el('div', 'we-city nna-mono', '—');
    var temp = N.el('div', 'we-temp nna-big', '—');
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

    function deg(x) { return x == null ? '—' : Math.round(x) + '°'; }
    function renderWeather(w) {
      city.textContent = w.name || '—';
      if (!w.ok) { temp.textContent = '—'; cond.textContent = 'NO DATA'; meta.textContent = ''; return; }
      temp.textContent = deg(w.temp);
      cond.textContent = w.text || '';
      meta.textContent = 'FEELS ' + deg(w.feels) + ' · WIND ' + Math.round(w.wind_ms || 0) + ' M/S · HUM ' + Math.round(w.humidity || 0) + '%';
      days.textContent = '';
      (w.days || []).slice(1, 4).forEach(function (d) {
        var el = N.el('div', 'we-day');
        var dt = new Date(d.date + 'T12:00:00');
        el.appendChild(N.el('span', 'we-day-k nna-mono', DAYS[dt.getDay()]));
        el.appendChild(N.el('span', 'we-day-v nna-big', deg(d.max) + ' / ' + deg(d.min)));
        el.appendChild(N.el('span', 'we-day-t nna-mono', d.text));
        days.appendChild(el);
      });
    }
    function tickClocks() {
      var now = new Date(), local = DAYS[now.getDay()];
      clockEls.forEach(function (c) {
        c.v.textContent = c.fmtT.format(now);
        var wd = c.fmtD.format(now).toUpperCase().slice(0, 3);
        c.d.textContent = wd === local ? '' : wd;
      });
    }
    function poll() { N.get('/weather').then(renderWeather, function () {}); }

    poll(); tickClocks();
    setInterval(tickClocks, 1000);
    setInterval(poll, ((C.weather && C.weather.refreshMin) || 10) * 60 * 1000);
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
  var w = N.weather(mount);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
