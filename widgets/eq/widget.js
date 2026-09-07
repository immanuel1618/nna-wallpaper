/* NNA1618 — эквалайзер. Слушает событие weAudio, которое рассылает index.html базы
   (Wallpaper Engine отдаёт 128 значений: 64 левый канал + 64 правый). Плавный, во всю высоту. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, E = C.eq || {}, L = C.labels || {};

  N.eq = function (mount, ctx) {
    var b = N.block('eq', L.eq || 'AUDIO');
    var corner = N.el('div', 'nna-corner', 'SILENT');
    var canvas = N.el('canvas', 'eq-canvas');
    b.root.appendChild(corner);
    b.body.appendChild(canvas);
    mount.appendChild(b.root);

    var c2d = canvas.getContext('2d');
    var BARS = E.bars || 56, ATT = E.attack || 0.55, REL = E.release || 0.075, GAIN = E.gain || 1.7, MINH = E.minHeight || 0.02;
    var W = 0, H = 0, dpr = 1;
    var raw = null, lastAudio = 0, level = new Float32Array(BARS), peak = new Float32Array(BARS);
    var agc = 0.4;            // автоусиление: следит за громкостью, чтобы полосы не были ни плоскими, ни в упор
    var t0 = Date.now();

    function resize() {
      var r = b.body.getBoundingClientRect();
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      W = Math.max(1, r.width); H = Math.max(1, r.height);
      canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
      canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
      c2d.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    ctx.on(window, 'weAudio', function (e) {
      raw = e.detail; lastAudio = Date.now();
    });

    // 128 значений WE -> BARS полос: низкие частоты слева, стерео усредняем
    function bands() {
      var out = new Float32Array(BARS);
      if (!raw || !raw.length) return out;
      var half = raw.length >> 1, sum = 0;
      for (var i = 0; i < BARS; i++) {
        var a = Math.floor(i / BARS * half), z = Math.max(a + 1, Math.floor((i + 1) / BARS * half));
        var acc = 0, n = 0;
        for (var k = a; k < z; k++) { acc += (raw[k] || 0) + (raw[k + half] || 0); n += 2; }
        out[i] = n ? acc / n : 0;
        sum += out[i];
      }
      var mean = sum / BARS;
      agc += (Math.max(mean * 4, 0.12) - agc) * 0.02;     // медленно подстраиваемся под громкость
      return out;
    }

    function step() {
      var active = Date.now() - lastAudio < 1500;
      var tgt = active ? bands() : null;
      var tt = (Date.now() - t0) / 1000;
      for (var i = 0; i < BARS; i++) {
        var v = tgt ? Math.min(1, Math.pow(tgt[i] / agc * GAIN * 0.35, 0.75)) : 0;
        if (!tgt) v = MINH + 0.09 * (0.5 + 0.5 * Math.sin(tt * 0.9 + i * 0.28)) * (0.6 + 0.4 * Math.sin(tt * 0.31 + i * 0.05));   // тихо: медленно дышит
        if (v > level[i]) level[i] += (v - level[i]) * ATT; else level[i] += (v - level[i]) * REL;
        if (level[i] > peak[i]) peak[i] = level[i]; else peak[i] -= 0.004;
        if (peak[i] < level[i]) peak[i] = level[i];
      }
      corner.textContent = active ? 'LIVE' : 'SILENT';
    }

    function draw() {
      c2d.clearRect(0, 0, W, H);
      var padX = 30, padTop = 56, padBot = 26;
      var innerW = W - padX * 2, innerH = H - padTop - padBot;
      var gap = Math.max(2, innerW / BARS * 0.28), bw = (innerW - gap * (BARS - 1)) / BARS;
      var base = padTop + innerH;
      for (var i = 0; i < BARS; i++) {
        var h = Math.max(2, level[i] * innerH), x = padX + i * (bw + gap);
        var g = c2d.createLinearGradient(0, base - h, 0, base);
        g.addColorStop(0, 'rgba(255,255,255,' + (0.55 + 0.45 * level[i]).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(200,200,200,0.35)');
        c2d.fillStyle = g;
        roundRect(x, base - h, bw, h, Math.min(4, bw / 2));
        c2d.fill();
        var ph = base - Math.max(2, peak[i] * innerH) - 3;      // пик: тонкая планка над полосой
        c2d.fillStyle = 'rgba(200,200,200,0.9)';
        c2d.fillRect(x, ph, bw, 2);
      }
      c2d.fillStyle = 'rgba(67,67,67,0.6)';
      c2d.fillRect(padX, base + 6, innerW, 1);
    }
    function roundRect(x, y, w, h, r) {
      c2d.beginPath();
      c2d.moveTo(x + r, y); c2d.lineTo(x + w - r, y); c2d.quadraticCurveTo(x + w, y, x + w, y + r);
      c2d.lineTo(x + w, y + h); c2d.lineTo(x, y + h); c2d.lineTo(x, y + r); c2d.quadraticCurveTo(x, y, x + r, y);
      c2d.closePath();
    }
    function loop() { step(); draw(); ctx.raf(loop); }

    resize();
    ctx.setInterval(function () { var r = b.body.getBoundingClientRect(); if (Math.abs(r.width - W) > 2 || Math.abs(r.height - H) > 2) resize(); }, 500);
    loop();
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.eq = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.eq = Object.assign({}, N.config.eq || {}, s);
  var w = N.eq(mount, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
