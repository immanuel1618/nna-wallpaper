/* NNA1618 — блок плеера: обложка на весь блок, управление через помощника,
   большие часы, когда ничего не играет. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {};

  function sourceName(aumid) {
    var s = String(aumid || '');
    if (/yandex\.music/i.test(s)) return 'YANDEX MUSIC';
    if (/spotify/i.test(s)) return 'SPOTIFY';
    if (/msedge|chrome|firefox|brave|opera/i.test(s)) return 'BROWSER';
    if (/vlc/i.test(s)) return 'VLC';
    if (/telegram/i.test(s)) return 'TELEGRAM';
    var last = s.split(/[\\/!.]/).filter(Boolean).pop() || 'MEDIA';
    return last.replace(/[_-]+/g, ' ').toUpperCase().slice(0, 18);
  }

  N.player = function (mount) {
    var b = N.block('player', L.player || 'PLAYER', { needsHelper: true });
    var body = b.body;

    // слои обложки (кроссфейд)
    var cover = N.el('div', 'np-cover');
    var imgA = N.el('img', 'np-img'), imgB = N.el('img', 'np-img');
    imgA.alt = ''; imgB.alt = '';
    cover.appendChild(imgA); cover.appendChild(imgB);
    var shade = N.el('div', 'np-shade');

    // режим ожидания: часы
    var idle = N.el('div', 'np-idle');
    var idleTime = N.el('div', 'np-time nna-big');
    var idleDate = N.el('div', 'np-date nna-mono');
    var idleHint = N.el('div', 'np-hint nna-mono', L.idle || 'NOTHING PLAYING');
    idle.appendChild(idleTime); idle.appendChild(idleDate); idle.appendChild(idleHint);

    // трек
    var meta = N.el('div', 'np-meta');
    var title = N.el('div', 'np-title nna-big');
    var artist = N.el('div', 'np-artist nna-mono');
    meta.appendChild(title); meta.appendChild(artist);

    // управление
    var controls = N.el('div', 'np-controls');
    var bPrev = iconBtn('prev'), bToggle = iconBtn('play', true), bNext = iconBtn('next');
    controls.appendChild(bPrev); controls.appendChild(bToggle); controls.appendChild(bNext);

    // прогресс
    var prog = N.el('div', 'np-progress');
    var bar = N.el('div', 'nna-bar'); var fill = N.el('i'); bar.appendChild(fill);
    var times = N.el('div', 'np-times nna-mono');
    prog.appendChild(bar); prog.appendChild(times);

    body.appendChild(cover); body.appendChild(shade); body.appendChild(idle);
    body.appendChild(meta); body.appendChild(controls); body.appendChild(prog);
    mount.appendChild(b.root);

    var state = null, front = imgA, lastThumbKey = null, lastPoll = 0, busy = false;

    function iconBtn(name, primary) {
      var btn = N.el('button', 'nna-icon-btn' + (primary ? ' is-primary' : ''));
      btn.type = 'button';
      btn.appendChild(N.svg(N.icons[name]));
      btn.dataset.icon = name;
      return btn;
    }
    function setIcon(btn, name) {
      if (btn.dataset.icon === name) return;
      btn.dataset.icon = name;
      btn.textContent = '';
      btn.appendChild(N.svg(N.icons[name]));
    }
    function act(action, extra) {
      if (busy) return;
      busy = true; bToggle.classList.add('is-busy');
      N.post('/media/' + action + (extra || '')).then(function (r) {
        if (r && r.ok === false) N.toast(r.error || 'FAILED');
      }, function () { N.toast('HELPER OFFLINE'); }).then(function () {
        busy = false; bToggle.classList.remove('is-busy');
        setTimeout(poll, 250);
      });
    }
    bPrev.addEventListener('click', function () { act('prev'); });
    bNext.addEventListener('click', function () { act('next'); });
    bToggle.addEventListener('click', function () { act('toggle'); });
    bar.addEventListener('click', function (e) {
      if (!state || !state.can_seek || !state.duration_s) return;
      var r = bar.getBoundingClientRect();
      var frac = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
      act('seek', '?pos=' + (frac * state.duration_s).toFixed(1));
    });

    function swapCover(dataUrl, key) {
      if (key === lastThumbKey) return;
      lastThumbKey = key;
      var back = front === imgA ? imgB : imgA;
      if (!dataUrl) { front.classList.remove('is-front'); back.classList.remove('is-front'); return; }
      back.onload = function () {
        back.classList.add('is-front');
        front.classList.remove('is-front');
        front = back;
      };
      back.src = dataUrl;
    }

    function render() {
      var s = state, hasTrack = !!(s && s.active && (s.title || s.artist));
      b.root.classList.toggle('is-idle', !hasTrack);
      b.root.classList.toggle('is-playing', !!(hasTrack && s.playing));
      if (!hasTrack) {
        b.setLabel({ text: L.player || 'PLAYER', strong: 'IDLE' });
        swapCover(null, null);
        return;
      }
      b.setLabel({ text: L.player || 'PLAYER', strong: sourceName(s.source) });
      title.textContent = s.title || '';
      artist.textContent = [s.artist, s.album && s.album !== s.title ? s.album : ''].filter(Boolean).join(' · ');
      setIcon(bToggle, s.playing ? 'pause' : 'play');
      swapCover(s.thumbnail, (s.title || '') + '|' + (s.artist || '') + '|' + (s.thumbnail ? s.thumbnail.length : 0));
      var dur = s.duration_s || 0;
      prog.classList.toggle('is-hidden', !dur);
      if (dur) {
        var pos = s.position_s || 0;
        if (s.playing && lastPoll) pos += (Date.now() - lastPoll) / 1000;
        pos = Math.min(pos, dur);
        fill.style.width = (pos / dur * 100).toFixed(2) + '%';
        times.textContent = N.fmtTime(pos) + ' / ' + N.fmtTime(dur);
      }
    }

    function poll() {
      N.get('/media').then(function (s) {
        state = s; lastPoll = Date.now(); render();
      }, function () { /* офлайн покажет каркас */ });
    }
    function tickClock() {
      var d = new Date();
      idleTime.textContent = N.fmtClock(d);
      idleDate.textContent = N.fmtDate(d);
    }
    tickClock(); poll();
    setInterval(tickClock, 1000);
    setInterval(poll, (C.poll && C.poll.media) || 1000);
    setInterval(function () { if (state && state.playing) render(); }, 500);
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.player = function (mount, ctx) {
  var N = window.NNA;
  var w = N.player(mount);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
