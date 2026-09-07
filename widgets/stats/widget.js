/* NNA1618 — блок состояния ПК: CPU, GPU, RAM, диски, сеть. Данные из помощника. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {};

  /* Стрелки NET (down/up): маленькие inline-SVG вместо символов ↓/↑ — см. docs/DESIGN-SYSTEM.md
     (никаких пиктограмм-символов в строках интерфейса). Заливки нет, обводка currentColor —
     стиль в .st-net-arrow (wallpaper/nna-blocks.css). */
  var ARROW_DOWN_D = 'M12 4 L12 16 M6 10 L12 16 L18 10';
  var ARROW_UP_D = 'M12 20 L12 8 M6 14 L12 8 L18 14';
  function arrow(d) { var s = N.svg(d); s.setAttribute('class', 'st-net-arrow'); return s; }

  N.stats = function (mount, ctx) {
    var b = N.block('stats', L.system || 'SYSTEM', { needsHelper: true });
    var corner = N.el('div', 'nna-corner');
    b.root.appendChild(corner);

    /* Сетка 2×2 (CPU, GPU, RAM, NET) сверху, диски отдельной секцией снизу — число метрики
       живёт в одной строке со своей меткой (метка слева моно, число справа Roboto Flex 42),
       а не крупным блоком у правого края колонки; см. nna-blocks.css:.nna-stats. */
    var grid = N.el('div', 'st-grid');
    var metrics = N.el('div', 'st-metrics');
    grid.appendChild(metrics);
    b.body.appendChild(grid);

    // --- CPU
    var cpu = row(metrics, 'CPU');
    var cores = N.el('div', 'st-cores');
    cpu.root.appendChild(cores);
    var coreBars = [];

    // --- GPU
    var gpu = row(metrics, 'GPU');
    var vram = barLine(gpu.root, 'VRAM');

    // --- RAM
    var ram = row(metrics, 'RAM');
    var ramBar = barLine(ram.root, 'USED');

    // --- NET
    var net = row(metrics, 'NET');
    var netDown = N.el('div', 'st-net'), netUp = N.el('div', 'st-net');
    net.root.appendChild(netDown); net.root.appendChild(netUp);
    var netDownK = N.el('span', 'st-net-k nna-mono'), netUpK = N.el('span', 'st-net-k nna-mono');
    netDownK.appendChild(arrow(ARROW_DOWN_D)); netDownK.appendChild(document.createTextNode(N.t('netDown', 'DOWN')));
    netUpK.appendChild(arrow(ARROW_UP_D)); netUpK.appendChild(document.createTextNode(N.t('netUp', 'UP')));
    var netDownV = N.el('span', 'st-net-v nna-big'), netUpV = N.el('span', 'st-net-v nna-big');
    netDown.appendChild(netDownK); netDown.appendChild(netDownV);
    netUp.appendChild(netUpK); netUp.appendChild(netUpV);

    // --- DISKS (во всю ширину, под сеткой 2×2 — числа CPU/GPU/RAM больше не наезжают на список)
    var disksWrap = N.el('div', 'st-disks');
    var disksHead = N.el('div', 'st-disks-head');
    var disksK = N.el('span', 'st-k nna-mono', 'DISKS');
    var disksV = N.el('span', 'st-disks-v nna-mono', '');
    disksHead.appendChild(disksK); disksHead.appendChild(disksV);
    var disksList = N.el('div', 'st-disks-list');
    disksWrap.appendChild(disksHead); disksWrap.appendChild(disksList);
    grid.appendChild(disksWrap);
    var diskLines = {};

    mount.appendChild(b.root);

    function row(col, name) {
      var r = N.el('div', 'st-row');
      var head = N.el('div', 'st-head');
      var k = N.el('span', 'st-k nna-mono', name);
      var v = N.el('span', 'st-v nna-big', '·');
      var sub = N.el('div', 'st-sub nna-mono', '');
      head.appendChild(k); head.appendChild(v);
      r.appendChild(head); r.appendChild(sub);
      col.appendChild(r);
      return { root: r, v: v, sub: sub };
    }
    function barLine(parent, name) {
      var w = N.el('div', 'st-barline');
      var k = N.el('span', 'st-bar-k nna-mono', name);
      var bar = N.el('div', 'nna-bar'); var i = N.el('i'); bar.appendChild(i);
      var t = N.el('span', 'st-bar-t nna-mono', '');
      w.appendChild(k); w.appendChild(bar); w.appendChild(t);
      parent.appendChild(w);
      return { fill: i, text: t, bar: bar };
    }
    function pct(x) { return Math.round(x) + '%'; }
    function uptime(s) {
      var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
      return 'UP ' + (d ? d + 'D ' : '') + N.pad2(h) + 'H ' + N.pad2(m) + 'M';
    }

    function render(s) {
      if (!s || !s.cpu) return;
      // CPU
      cpu.v.textContent = pct(s.cpu.percent);
      cpu.sub.textContent = (s.cpu.freq_mhz ? (s.cpu.freq_mhz / 1000).toFixed(1) + ' GHZ · ' : '') + s.cpu.cores + ' ' + N.t('threads', 'THREADS');
      var pc = s.cpu.per_core || [];
      while (coreBars.length < pc.length) {
        var cb = N.el('span', 'st-core'); var ci = N.el('i'); cb.appendChild(ci);
        cores.appendChild(cb); coreBars.push(ci);
      }
      for (var i = 0; i < coreBars.length; i++) {
        var v = pc[i] || 0;
        coreBars[i].style.height = Math.max(4, v) + '%';
        coreBars[i].parentNode.classList.toggle('is-hot', v > 85);
      }
      // GPU
      if (s.gpu && s.gpu.ok) {
        gpu.v.textContent = pct(s.gpu.util);
        gpu.sub.textContent = Math.round(s.gpu.temp) + '°C · ' + Math.round(s.gpu.power_w) + ' W · ' + (s.gpu.name || '').replace(/NVIDIA GeForce /i, '');
        var vp = s.gpu.mem_total_mb ? s.gpu.mem_used_mb / s.gpu.mem_total_mb * 100 : 0;
        vram.fill.style.width = vp.toFixed(1) + '%';
        vram.text.textContent = (s.gpu.mem_used_mb / 1024).toFixed(1) + ' / ' + (s.gpu.mem_total_mb / 1024).toFixed(0) + ' GB';
      } else {
        gpu.v.textContent = '·'; gpu.sub.textContent = N.t('noGpuData', 'NO GPU DATA');
      }
      // RAM
      ram.v.textContent = pct(s.mem.percent);
      ram.sub.textContent = N.fmtGB(s.mem.used) + ' / ' + N.fmtGB(s.mem.total) + ' GB';
      ramBar.fill.style.width = s.mem.percent + '%';
      ramBar.text.textContent = N.fmtGB(s.mem.total - s.mem.used) + ' GB ' + N.t('free', 'FREE');
      // DISKS (секция во всю ширину под сеткой 2×2, см. disksWrap/disksList выше)
      (s.disks || []).forEach(function (d) {
        var line = diskLines[d.mount];
        if (!line) { line = diskLines[d.mount] = barLine(disksList, d.mount); }
        line.fill.style.width = d.percent + '%';
        line.text.textContent = N.fmtGB(d.total - d.used) + ' GB ' + N.t('free', 'FREE');
        line.bar.classList.toggle('is-hot', d.percent > 90);
      });
      disksV.textContent = (s.disks || []).length + ' ' + N.t('vol', 'VOL');
      // NET
      netDownV.textContent = N.fmtBytes(s.net.down_bps, true);
      netUpV.textContent = N.fmtBytes(s.net.up_bps, true);
      net.v.textContent = '';
      corner.textContent = uptime(s.uptime_s || 0);
    }

    var readyReported = false, failReported = false;
    function poll() {
      N.get('/stats').then(function (s) {
        failReported = false;
        render(s);
        if (!readyReported) { readyReported = true; if (ctx && ctx.ready) ctx.ready(); }
      }, function (err) {
        if (!failReported) { failReported = true; if (ctx && ctx.fail) ctx.fail(err); }
      });
    }
    poll();
    ctx.setInterval(poll, (C.poll && C.poll.stats) || 1000);
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.stats = function (mount, ctx) {
  var N = window.NNA;
  var w = N.stats(mount, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
