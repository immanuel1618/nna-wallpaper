/* NNA1618 — чистые функции сравнения конфигурации обоев (без DOM, без побочных эффектов),
   используются layout.js, чтобы патчить только затронутое вместо reload. Тестируются напрямую
   в node (wallpaper/tests/*), поэтому ES5-стиль и без обращений к window/document на верхнем уровне. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  }
  if (root) {
    root.NNA_LAYOUT_DIFF = api;
  }
})(typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : null), function () {
  'use strict';

  /// <c>row / col / span rowSpan / span colSpan</c> — тот же формат, что раньше был инлайн в layout.js.
  function gridArea(block) {
    return block.row + ' / ' + block.col + ' / span ' + block.rowSpan + ' / span ' + block.colSpan;
  }

  /* Ключ блока: widget + '#' + порядковый номер среди блоков с тем же widget (по порядку в массиве).
     Так два блока одного виджета на одном мониторе различаются стабильно, пока их относительный
     порядок в списке не меняется (обычная ситуация при редактировании раскладки). */
  function keyBlocks(blocks) {
    var counts = {}, keyed = [], i, blk, n;
    for (i = 0; i < blocks.length; i++) {
      blk = blocks[i];
      n = counts.hasOwnProperty(blk.widget) ? counts[blk.widget] + 1 : 0;
      counts[blk.widget] = n;
      keyed.push({ key: blk.widget + '#' + n, block: blk });
    }
    return keyed;
  }

  function sameGeometry(a, b) {
    return a.row === b.row && a.col === b.col && a.rowSpan === b.rowSpan && a.colSpan === b.colSpan;
  }

  /**
   * Сравнивает два списка блоков (монитора) и возвращает { added, removed, moved }.
   * Каждый элемент — { key, block } (для moved — новый block). Порядок: как в nextBlocks.
   */
  function diffBlocks(prevBlocks, nextBlocks) {
    prevBlocks = prevBlocks || [];
    nextBlocks = nextBlocks || [];
    var prevKeyed = keyBlocks(prevBlocks);
    var nextKeyed = keyBlocks(nextBlocks);
    var prevByKey = {}, i;
    for (i = 0; i < prevKeyed.length; i++) prevByKey[prevKeyed[i].key] = prevKeyed[i].block;
    var nextByKey = {};
    for (i = 0; i < nextKeyed.length; i++) nextByKey[nextKeyed[i].key] = nextKeyed[i].block;

    var added = [], removed = [], moved = [];
    for (i = 0; i < nextKeyed.length; i++) {
      var nk = nextKeyed[i];
      if (!prevByKey.hasOwnProperty(nk.key)) {
        added.push(nk);
      } else if (!sameGeometry(prevByKey[nk.key], nk.block)) {
        moved.push(nk);
      }
    }
    for (i = 0; i < prevKeyed.length; i++) {
      var pk = prevKeyed[i];
      if (!nextByKey.hasOwnProperty(pk.key)) removed.push(pk);
    }
    return { added: added, removed: removed, moved: moved };
  }

  /** Список верхнеуровневых ключей theme, чьё значение изменилось (сравнение через JSON.stringify,
   * так что вложенные объекты palette/fonts сравниваются целиком). */
  function diffTheme(prev, next) {
    prev = prev || {};
    next = next || {};
    var keys = {}, k, changed = [];
    for (k in prev) if (prev.hasOwnProperty(k)) keys[k] = true;
    for (k in next) if (next.hasOwnProperty(k)) keys[k] = true;
    for (k in keys) {
      if (!keys.hasOwnProperty(k)) continue;
      if (JSON.stringify(prev[k]) !== JSON.stringify(next[k])) changed.push(k);
    }
    return changed;
  }

  /** Список id виджетов, чьи settings изменились (по значению, глубокое сравнение через JSON.stringify);
   * включает id, появившиеся или пропавшие между prev и next. */
  function diffWidgets(prevSettingsById, nextSettingsById) {
    prevSettingsById = prevSettingsById || {};
    nextSettingsById = nextSettingsById || {};
    var ids = {}, id, changed = [];
    for (id in prevSettingsById) if (prevSettingsById.hasOwnProperty(id)) ids[id] = true;
    for (id in nextSettingsById) if (nextSettingsById.hasOwnProperty(id)) ids[id] = true;
    for (id in ids) {
      if (!ids.hasOwnProperty(id)) continue;
      if (JSON.stringify(prevSettingsById[id]) !== JSON.stringify(nextSettingsById[id])) changed.push(id);
    }
    return changed;
  }

  /** true, если a и b содержат один и тот же набор id мониторов (порядок и дубли не важны). */
  function sameMonitorSet(a, b) {
    a = a || []; b = b || [];
    if (a.length !== b.length) return false;
    var sa = a.slice().sort(), sb = b.slice().sort();
    for (var i = 0; i < sa.length; i++) if (sa[i] !== sb[i]) return false;
    return true;
  }

  return {
    gridArea: gridArea,
    diffBlocks: diffBlocks,
    diffTheme: diffTheme,
    diffWidgets: diffWidgets,
    sameMonitorSet: sameMonitorSet
  };
});
