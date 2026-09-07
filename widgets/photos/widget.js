/* NNA1618 — виджет photos (обёртка module): ротация фото из папки, настроенной в widget.json.
   Новая реализация без DOM базы Nothing OS: свой слой из двух <img> (см. pin-rotator.js —
   математика затухания/зума перенесена как есть), список берётся у хоста через ctx.helper.get.
   Переиспользует готовые классы .pin-layer/.pin-img/.pin-zoom из nna-blocks.css (копия, без правок). */
window.NNA = window.NNA || {};
window.NNA.widgets = window.NNA.widgets || {};

(function () {
  'use strict';

  function isImageName(n) { return /\.(jpe?g|png|webp|gif|avif|bmp)$/i.test(String(n)); }
  function natural(a, b) { return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' }); }

  window.NNA.widgets.photos = function (mount, ctx) {
    var N = window.NNA;
    var settings = ctx.settings || {};
    var intervalMin = settings.intervalMin || 10;
    var order = settings.order || 'sequential';
    var fadeMs = settings.fadeMs || 1400;
    var zoom = settings.zoom !== false;
    var zoomScale = settings.zoomScale || 1.08;
    var INTERVAL_MS = Math.max(1, intervalMin) * 60 * 1000;
    var REFRESH_MS = 10 * 60 * 1000;

    var labels = (N.config && N.config.labels) || {};
    var b = N.block('photo', labels.photo || 'PHOTO');
    b.root.style.cursor = 'pointer';
    mount.appendChild(b.root);

    var layer = document.createElement('div');
    layer.className = 'pin-layer';
    var imgA = document.createElement('img'); imgA.className = 'pin-img'; imgA.alt = '';
    var imgB = document.createElement('img'); imgB.className = 'pin-img'; imgB.alt = '';
    layer.appendChild(imgA);
    layer.appendChild(imgB);
    b.body.appendChild(layer);

    var empty = document.createElement('div');
    empty.className = 'nna-muted nna-mono';
    empty.style.cssText = 'position:absolute;inset:0;display:none;align-items:center;justify-content:center;' +
      'text-align:center;font-size:10px;letter-spacing:0.22em;padding:16px 22px;';
    empty.textContent = 'NO PHOTOS · SET FOLDER IN SETTINGS';
    b.body.appendChild(empty);

    var urls = [], currentUrl = null, front = null;
    var nextTimer = null, refreshTimer = null, scanning = false, destroyed = false;

    function showEmpty(show) {
      empty.style.display = show ? 'flex' : 'none';
      layer.style.display = show ? 'none' : 'block';
    }

    function pickNext() {
      if (!urls.length) return null;
      if (urls.length === 1) return urls[0];
      if (order === 'random') {
        var n, guard = 0;
        do { n = urls[Math.floor(Math.random() * urls.length)]; guard++; } while (n === currentUrl && guard < 25);
        return n;
      }
      var i = urls.indexOf(currentUrl);
      return urls[(i + 1) % urls.length];
    }

    function show(url) {
      if (!url || destroyed) return;
      var back = front === imgA ? imgB : imgA;
      var pre = new Image();
      pre.onload = function () {
        if (destroyed) return;
        currentUrl = url;
        back.style.transitionDuration = fadeMs + 'ms';
        back.src = url;
        if (zoom) {
          back.style.animation = 'none';
          void back.offsetWidth;   /* перезапуск анимации */
          back.style.animation = 'pin-zoom ' + (INTERVAL_MS + fadeMs) + 'ms linear forwards';
          back.style.setProperty('--pin-zoom', zoomScale);
        } else {
          back.style.animation = 'none';
        }
        back.classList.add('is-front');
        if (front) front.classList.remove('is-front');
        front = back;
      };
      pre.onerror = function () { /* пропускаем битую картинку */ };
      pre.src = url;
    }

    function next() {
      if (!urls.length) { refresh(); return; }
      show(pickNext());
    }

    function scheduleNext() {
      clearTimeout(nextTimer);
      nextTimer = ctx.setTimeout(function () { next(); scheduleNext(); }, INTERVAL_MS);
    }

    function refresh() {
      if (scanning || destroyed) return;
      scanning = true;
      ctx.helper.get('/pins?d=photos').then(function (j) {
        scanning = false;
        if (destroyed) return;
        var list = (j && j.urls) || [];
        list = list.filter(isImageName);
        if (order === 'sequential') list = list.slice().sort(natural);
        urls = list;
        if (!urls.length) { showEmpty(true); return; }
        showEmpty(false);
        if (!currentUrl || urls.indexOf(currentUrl) === -1) show(pickNext());
      }, function () {
        scanning = false;
        if (destroyed) return;
        urls = [];
        showEmpty(true);
      });
    }

    function onClick(e) {
      if (e.target && e.target.closest && e.target.closest('a, button')) return;
      next();
    }
    ctx.on(b.root, 'click', onClick);

    showEmpty(true);
    refresh();
    scheduleNext();
    refreshTimer = ctx.setInterval(refresh, REFRESH_MS);

    ctx.onDispose(function () {
      destroyed = true;
      if (b.root.parentNode) b.root.parentNode.removeChild(b.root);
    });

    return { root: b.root };
  };
})();
