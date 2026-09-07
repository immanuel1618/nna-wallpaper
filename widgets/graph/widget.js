/* NNA1618 — граф graphify на canvas, статичный. Раскладка считается один раз при загрузке
   (силовая, с подгонкой под весь блок) и замирает. Наведение подсвечивает соседей,
   клик по узлу открывает файл через помощника, клик по заголовку переключает H: / E:. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, G = C.graph || {}, L = C.labels || {};
  var TITLE = { h: 'H:', e: 'E:' };
  try { var _t = typeof G.titles === 'string' ? JSON.parse(G.titles || '{}') : G.titles; if (_t && typeof _t === 'object') TITLE = Object.assign(TITLE, _t); } catch (e) {}

  N.graph = function (mount) {
    var b = N.block('graph', L.graph || 'GRAPH', { needsHelper: true });
    b.label.classList.add('is-clickable');
    var corner = N.el('div', 'nna-corner');
    var canvas = N.el('canvas', 'gr-canvas');
    var tip = N.el('div', 'gr-tip nna-mono');
    b.root.appendChild(corner);
    b.body.appendChild(canvas); b.body.appendChild(tip);
    mount.appendChild(b.root);

    var ctx = canvas.getContext('2d');
    var src = G.start === 'e' ? 'e' : 'h';
    var nodes = [], links = [], adj = {}, root = '', total = 0, lastG = null;
    var W = 0, H = 0, dpr = 1, PAD = 36, TOP = 54;
    var hover = null, P = {}, loading = false;

    function resize() {
      var r = b.body.getBoundingClientRect();
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      var oW = W, oH = H;
      W = Math.max(1, r.width); H = Math.max(1, r.height);
      canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
      canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      if (lastG && (Math.abs(W - oW) > oW * 0.1 || Math.abs(H - oH) > oH * 0.1)) build(lastG);
      else draw();
    }

    function load() {
      if (loading) return;
      loading = true;
      b.setLabel({ text: L.graph || 'GRAPH', strong: TITLE[src] });
      corner.textContent = 'LOADING';
      N.get('/graph?src=' + src).then(function (g) { loading = false; build(g); },
        function () { loading = false; corner.textContent = 'NO DATA'; });
    }

    function tune() {
      var n = Math.max(nodes.length, 1);
      var area = Math.max(1, (W - PAD * 2) * (H - TOP - PAD));
      var spacing = Math.sqrt(area / n);
      P = { rep: spacing * spacing * 0.55, rest: spacing * 0.95, spring: 0.028, gravity: 0.004,
        damp: 0.82, vmax: spacing * 0.22, cutoff: spacing * 3.2 };
    }

    function build(g) {
      lastG = g;
      if (W < 60 || H < 60) return;
      root = g.root || ''; total = g.total_nodes || g.nodes.length;
      nodes = []; links = []; adj = {};
      var byId = {}, n = g.nodes.length, seed = 1;
      function rnd() { seed = (seed * 16807) % 2147483647; return seed / 2147483647; }   // детерминированно: раскладка одна и та же
      var cx = W / 2, cy = (H + TOP) / 2, R = Math.min(W - PAD * 2, H - TOP - PAD) * 0.42;
      g.nodes.forEach(function (d, i) {
        var a = i / n * Math.PI * 2 + rnd() * 0.2, rr = R * (0.25 + 0.75 * Math.sqrt(rnd()));
        var node = { id: d.id, label: d.label || d.id, file: d.file, type: d.type, degree: d.degree || 0,
          x: cx + Math.cos(a) * rr, y: cy + Math.sin(a) * rr, vx: 0, vy: 0 };
        node.r = 2.4 + Math.min(9, Math.log(1 + node.degree) * 1.8);
        byId[node.id] = node; nodes.push(node); adj[node.id] = {};
      });
      g.links.forEach(function (l) {
        var s = byId[l.source], t = byId[l.target];
        if (!s || !t || s === t) return;
        links.push({ s: s, t: t });
        adj[s.id][t.id] = 1; adj[t.id][s.id] = 1;
      });
      var maxDeg = nodes.reduce(function (m, d) { return Math.max(m, d.degree); }, 1);
      nodes.forEach(function (d) { d.shade = 0.35 + 0.65 * Math.pow(d.degree / maxDeg, 0.5); d.showLabel = false; });
      nodes.sort(function (a, c) { return a.degree - c.degree; });
      nodes.slice(-Math.min(16, nodes.length)).forEach(function (d) { d.showLabel = true; });
      tune();
      for (var k = 0; k < 700; k++) step(k < 500 ? 0.5 : 0.1, rnd);   // раскладываем до покоя и замираем
      corner.textContent = nodes.length + ' / ' + total + ' NODES';
      hover = null;
      draw();
    }

    function step(fitEase, rnd) {
      var n = nodes.length, i, j, a, c, dx, dy, d2, d, f, cut2 = P.cutoff * P.cutoff;
      for (i = 0; i < n; i++) {
        a = nodes[i];
        for (j = i + 1; j < n; j++) {
          c = nodes[j];
          dx = c.x - a.x; dy = c.y - a.y; d2 = dx * dx + dy * dy;
          if (d2 > cut2) continue;
          if (d2 < 0.01) { dx = rnd() - 0.5; dy = rnd() - 0.5; d2 = 0.5; }
          f = P.rep / (d2 + 1) * 0.5;
          d = Math.sqrt(d2); dx /= d; dy /= d;
          a.vx -= dx * f; a.vy -= dy * f; c.vx += dx * f; c.vy += dy * f;
        }
      }
      for (i = 0; i < links.length; i++) {
        var l = links[i];
        dx = l.t.x - l.s.x; dy = l.t.y - l.s.y; d = Math.sqrt(dx * dx + dy * dy) || 1;
        f = (d - P.rest) * P.spring;
        dx /= d; dy /= d;
        l.s.vx += dx * f; l.s.vy += dy * f; l.t.vx -= dx * f; l.t.vy -= dy * f;
      }
      var cx = W / 2, cy = (H + TOP) / 2;
      var minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
      for (i = 0; i < n; i++) {
        a = nodes[i];
        a.vx += (cx - a.x) * P.gravity; a.vy += (cy - a.y) * P.gravity;
        a.vx *= P.damp; a.vy *= P.damp;
        var sp = Math.sqrt(a.vx * a.vx + a.vy * a.vy);
        if (sp > P.vmax) { a.vx *= P.vmax / sp; a.vy *= P.vmax / sp; }
        a.x += a.vx; a.y += a.vy;
        if (a.x < minX) minX = a.x; if (a.x > maxX) maxX = a.x; if (a.y < minY) minY = a.y; if (a.y > maxY) maxY = a.y;
      }
      if (n > 2) {                                   // подгонка под блок: не комок и не за край
        var bw = Math.max(1, maxX - minX), bh = Math.max(1, maxY - minY);
        var kx = 1 + (Math.min(Math.max((W - PAD * 2) / bw, 0.9), 1.1) - 1) * fitEase;
        var ky = 1 + (Math.min(Math.max((H - TOP - PAD) / bh, 0.9), 1.1) - 1) * fitEase;
        var mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
        for (i = 0; i < n; i++) {
          a = nodes[i];
          a.x = cx + (a.x - mx) * kx; a.y = cy + (a.y - my) * ky;
          if (a.x < PAD) a.x = PAD; else if (a.x > W - PAD) a.x = W - PAD;
          if (a.y < TOP) a.y = TOP; else if (a.y > H - PAD) a.y = H - PAD;
        }
      }
    }

    function draw() {
      ctx.clearRect(0, 0, W, H);
      var i, hid = hover ? hover.id : null;
      ctx.lineWidth = 1;
      for (i = 0; i < links.length; i++) {
        var l = links[i], hot = hid && (l.s.id === hid || l.t.id === hid);
        ctx.strokeStyle = hot ? 'rgba(255,255,255,0.85)' : 'rgba(128,128,128,0.22)';
        ctx.beginPath(); ctx.moveTo(l.s.x, l.s.y); ctx.lineTo(l.t.x, l.t.y); ctx.stroke();
      }
      for (i = 0; i < nodes.length; i++) {
        var d = nodes[i], isHot = hid && (d.id === hid || adj[hid][d.id]);
        var g = Math.round(103 + (255 - 103) * d.shade);
        ctx.fillStyle = isHot ? '#FFFFFF' : 'rgb(' + g + ',' + g + ',' + g + ')';
        ctx.beginPath(); ctx.arc(d.x, d.y, d.r + (d.id === hid ? 2 : 0), 0, Math.PI * 2); ctx.fill();
        if (d.type === 'concept' || d.type === 'document') {
          ctx.strokeStyle = 'rgba(5,5,5,0.9)'; ctx.lineWidth = 1.5;
          ctx.beginPath(); ctx.arc(d.x, d.y, Math.max(1.2, d.r - 2.2), 0, Math.PI * 2); ctx.stroke(); ctx.lineWidth = 1;
        }
      }
      ctx.font = '500 10px "DM Mono", monospace'; ctx.textBaseline = 'middle';
      for (i = 0; i < nodes.length; i++) {
        var nd = nodes[i];
        if (!nd.showLabel || nd.id === hid) continue;
        ctx.fillStyle = 'rgba(128,128,128,0.9)';
        ctx.fillText(short(nd.label), nd.x + nd.r + 6, nd.y);
      }
    }
    function short(s) { var m = G.maxLabelLen || 26; s = String(s || ''); return s.length > m ? s.slice(0, m - 1) + '…' : s; }
    function nearest(x, y) {
      var best = null, bd = 16 * 16;
      for (var i = 0; i < nodes.length; i++) {
        var d = nodes[i], dx = d.x - x, dy = d.y - y, d2 = dx * dx + dy * dy;
        if (d2 < bd + d.r * d.r) { bd = d2; best = d; }
      }
      return best;
    }

    canvas.addEventListener('mousemove', function (e) {
      var r = canvas.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
      var h = nearest(x, y);
      if (h) {
        tip.textContent = h.label + (h.file ? '  ·  ' + h.file : '');
        tip.style.left = Math.min(W - 24, x + 14) + 'px';
        tip.style.top = Math.max(8, y - 26) + 'px';
        tip.classList.add('is-show'); canvas.style.cursor = 'pointer';
      } else { tip.classList.remove('is-show'); canvas.style.cursor = 'default'; }
      if (h !== hover) { hover = h; draw(); }
    });
    canvas.addEventListener('mouseleave', function () { if (hover) { hover = null; draw(); } tip.classList.remove('is-show'); });
    canvas.addEventListener('click', function () { if (hover) openNode(hover); });
    b.label.addEventListener('click', function () { src = src === 'h' ? 'e' : 'h'; load(); });

    function openNode(d) {
      if (!G.openOnClick || !d.file) return;
      var file = String(d.file).replace(/\//g, '\\');
      var r = String(root || '').replace(/\//g, '\\').replace(/\\$/, '');
      var cands = [];
      if (r) { cands.push(r + '\\' + file); cands.push(r.slice(0, 2) + '\\' + file); }
      tryOpen(cands, 0, d);
    }
    function tryOpen(c, i, d) {
      if (i >= c.length) { N.toast('NOT FOUND · ' + short(d.label)); return; }
      N.post('/open?path=' + encodeURIComponent(c[i])).then(function (r) {
        if (r && r.ok) N.toast('OPEN · ' + short(d.label)); else tryOpen(c, i + 1, d);
      }, function () { N.toast('HELPER OFFLINE'); });
    }

    resize();
    setInterval(function () { var r = b.body.getBoundingClientRect(); if (Math.abs(r.width - W) > 2 || Math.abs(r.height - H) > 2) resize(); }, 500);
    load();
    setInterval(load, 10 * 60 * 1000);   // граф на диске мог пересобраться
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.graph = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.graph = Object.assign({}, N.config.graph || {}, s);
  var w = N.graph(mount);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
