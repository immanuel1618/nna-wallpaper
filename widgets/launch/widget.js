/* NNA1618 — быстрый запуск с иконками. launch.json у помощника, иконки он извлекает сам.
   Обычный вид (вертикальный): ряд кнопок групп, ниже строки «группа: иконки с подписями».
   Компактный вид (горизонтальный): колонки по группам, заголовок колонки запускает всю группу,
   иконки без подписей, подпись всплывает при наведении. Папки всегда с подписью. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {}, LC = C.launch || {};
  var HELPER = (window.NNA_HELPER || {}).url || 'http://127.0.0.1:1618';
  var FOLDER = 'M3 6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z';

  N.launch = function (mount, opts, ctx) {
    opts = opts || {};
    var compact = !!opts.compact;
    var b = N.block('launch', L.launch || 'LAUNCH', { needsHelper: true });
    if (compact) b.root.classList.add('is-compact');
    if (LC.grayIcons !== false) b.root.classList.add('is-gray');
    var edit = N.el('button', 'nna-btn nna-corner-btn', 'EDIT');
    edit.type = 'button';
    b.root.appendChild(edit);
    var groups = N.el('div', 'la-groups');
    var items = N.el('div', 'la-items');
    if (!compact) b.body.appendChild(groups);
    b.body.appendChild(items);
    mount.appendChild(b.root);

    var loaded = false, lastSig = '';

    function flash(btn, ok) {
      btn.classList.add(ok ? 'is-flash' : 'is-busy');
      ctx.setTimeout(function () { btn.classList.remove('is-flash', 'is-busy'); }, ok ? 700 : 1200);
    }
    function run(kind, id, btn, label) {
      btn.classList.add('is-busy');
      N.post('/launch/' + kind + '?id=' + encodeURIComponent(id)).then(function (r) {
        btn.classList.remove('is-busy');
        var ok = !!(r && r.ok);
        flash(btn, ok);
        if (ok) N.toast((kind === 'group' ? 'GROUP ' : '') + label + ' · LAUNCHED');
        else N.toast(label + ' · ' + ((r && r.failed && r.failed.length) ? 'FAILED: ' + r.failed.join(', ').toUpperCase() : (r && r.error) || 'FAILED'));
      }, function () {
        btn.classList.remove('is-busy');
        N.toast('HELPER OFFLINE');
      });
    }
    function initials(label) {
      var w = String(label || '?').replace(/[^A-Za-zА-Яа-я0-9 ]/g, ' ').trim().split(/\s+/);
      return (w.length > 1 ? w[0][0] + w[1][0] : w[0].slice(0, 2)).toUpperCase();
    }
    function folderSvg() {
      var s = N.svg(FOLDER);
      s.setAttribute('class', 'la-folder');
      return s;
    }
    function chip(id, it) {
      var label = it.label || id, isDir = it.kind === 'dir';
      var btn = N.el('button', 'nna-btn la-item ' + (isDir ? 'is-dir' : 'is-app'));
      btn.type = 'button';
      btn.dataset.label = label;
      if (isDir) {
        btn.appendChild(folderSvg());
        btn.appendChild(N.el('span', 'la-item-t', label));
      } else {
        if (it.icon) {
          var img = N.el('img', 'la-ico');
          img.alt = ''; img.draggable = false;
          img.src = HELPER + it.icon;
          img.addEventListener('error', function () { img.replaceWith(N.el('span', 'la-ini', initials(label))); });
          btn.appendChild(img);
        } else {
          btn.appendChild(N.el('span', 'la-ini', initials(label)));
        }
        if (!compact) btn.appendChild(N.el('span', 'la-item-t', label));
        if (it.badge) btn.appendChild(N.el('i', 'la-badge', it.badge));
      }
      btn.addEventListener('click', function () { run('item', id, btn, label); });
      return btn;
    }
    function groupBtn(g, cls) {
      var btn = N.el('button', 'nna-btn ' + cls, g.label || g.id);
      btn.type = 'button';
      btn.addEventListener('click', function () { run('group', g.id, btn, g.label || g.id); });
      return btn;
    }

    function render(cfg) {
      var sig = JSON.stringify(cfg);
      if (sig === lastSig) return;      // ничего не поменялось — не перерисовываем, чтобы не мигало
      lastSig = sig;
      groups.textContent = ''; items.textContent = '';
      var seen = {};
      (cfg.groups || []).forEach(function (g) {
        if (!compact) groups.appendChild(groupBtn(g, 'la-group'));
        var set = N.el('div', 'la-set');
        set.appendChild(compact ? groupBtn(g, 'la-set-h') : N.el('span', 'la-set-k nna-mono', g.label || g.id));
        var list = N.el('div', 'la-set-items');
        (g.items || []).forEach(function (id) {
          var it = cfg.items[id]; if (!it) return;
          seen[id] = 1;
          list.appendChild(chip(id, it));
        });
        set.appendChild(list);
        items.appendChild(set);
      });
      var rest = Object.keys(cfg.items || {}).filter(function (id) { return !seen[id]; });
      if (rest.length) {
        var set = N.el('div', 'la-set');
        set.appendChild(N.el('span', 'la-set-k nna-mono', 'OTHER'));
        var list = N.el('div', 'la-set-items');
        rest.forEach(function (id) { list.appendChild(chip(id, cfg.items[id])); });
        set.appendChild(list);
        items.appendChild(set);
      }
      loaded = true;
    }
    function load() {
      N.get('/launch/list').then(render, function () { if (!loaded) ctx.setTimeout(load, 4000); });
    }
    edit.addEventListener('click', function () {
      N.post('/edit?what=launch').then(function (r) { N.toast(r && r.ok ? 'OPENING LAUNCH.JSON' : (r && r.error) || 'FAILED'); },
        function () { N.toast('HELPER OFFLINE'); });
    });

    load();
    ctx.setInterval(load, 20000);   // иконки дотягиваются в фоне, список перечитывается
    return b;
  };
})();

/* NNA Wallpaper widget wrapper */
window.NNA.widgets = window.NNA.widgets || {};
window.NNA.widgets.launch = function (mount, ctx) {
  var N = window.NNA, s = (ctx && ctx.settings) || {};
  N.config.launch = Object.assign({}, N.config.launch || {}, s);
  var w = N.launch(mount, { compact: !!s.compact }, ctx);
  return { root: w && w.root, destroy: (w && w.destroy) || null };
};
