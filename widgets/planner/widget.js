/* NNA1618 — блок задач. Пока заглушка: NNA PLANNER SOON и три пустые строки.
   Перенесено как есть из nna-tasks.js; этап 7 заменит содержимое. */
(function () {
  'use strict';
  var N = window.NNA, C = N.config, L = C.labels || {};

  N.tasks = function (mount) {
    var b = N.block('tasks', L.tasks || 'TASKS');
    var wrap = N.el('div', 'ta-wrap');
    var title = N.el('div', 'ta-title nna-big', L.tasksSoon || 'NNA PLANNER');
    var sub = N.el('div', 'ta-sub nna-mono', L.tasksSub || 'SOON');
    var list = N.el('div', 'ta-list');
    for (var i = 0; i < 3; i++) {
      var row = N.el('div', 'ta-row');
      row.appendChild(N.el('i', 'ta-check'));
      row.appendChild(N.el('span', 'ta-line'));
      list.appendChild(row);
    }
    wrap.appendChild(title); wrap.appendChild(sub); wrap.appendChild(list);
    b.body.appendChild(wrap);
    mount.appendChild(b.root);
    return b;
  };
})();

/* ---- обёртка под контракт kind=module (WIDGET-SDK.md) --------------------- */
(function () {
  'use strict';
  window.NNA.widgets = window.NNA.widgets || {};
  window.NNA.widgets.planner = function (mount, ctx) {
    var b = window.NNA.tasks(mount);
    return {
      root: b.root,
      destroy: function () {
        if (b.root && b.root.parentNode) b.root.parentNode.removeChild(b.root);
      }
    };
  };
})();
