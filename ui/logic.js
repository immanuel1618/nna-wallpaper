/* NNA UI — чистые функции без DOM, вынесены отдельно для юнит-тестов (ui/tests/components.test.mjs).
   UMD: работает как <script> (window.NNAUILogic) и как импорт в node:test через createRequire. */
(function (root, factory) {
  var mod = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = mod;
  }
  if (typeof root !== "undefined") {
    root.NNAUILogic = mod;
  }
})(typeof window !== "undefined" ? window : this, function () {
  "use strict";

  var POPOVER_GAP = 8;
  var POPOVER_EDGE = 4;

  /**
   * Считает позицию поповера/меню от прямоугольника якоря так, чтобы он не вылезал за окно.
   * anchorRect: {top,left,right,bottom,width,height}
   * popRect: {width,height}
   * viewport: {width,height}
   * placement: 'bottom' | 'top' — предпочитаемая сторона.
   * Возвращает {top, left, placement} — placement может быть развёрнут на противоположный,
   * если места не хватает.
   */
  function placePopover(anchorRect, popRect, viewport, placement) {
    placement = placement === "top" ? "top" : "bottom";
    var top;
    var fitsBelow = anchorRect.bottom + POPOVER_GAP + popRect.height <= viewport.height;
    var fitsAbove = anchorRect.top - POPOVER_GAP - popRect.height >= 0;

    var resolved = placement;
    if (placement === "bottom" && !fitsBelow && fitsAbove) resolved = "top";
    if (placement === "top" && !fitsAbove && fitsBelow) resolved = "bottom";

    if (resolved === "top") {
      top = anchorRect.top - POPOVER_GAP - popRect.height;
    } else {
      top = anchorRect.bottom + POPOVER_GAP;
    }
    if (top < POPOVER_EDGE) top = POPOVER_EDGE;
    if (top + popRect.height > viewport.height - POPOVER_EDGE) {
      top = Math.max(POPOVER_EDGE, viewport.height - popRect.height - POPOVER_EDGE);
    }

    var left = anchorRect.left;
    var maxLeft = viewport.width - popRect.width - POPOVER_EDGE;
    if (left > maxLeft) left = maxLeft;
    if (left < POPOVER_EDGE) left = POPOVER_EDGE;

    return { top: top, left: left, placement: resolved };
  }

  /**
   * Новое значение слайдера. keyDelta — число шагов (1 / -1 стрелки, 10 / -10 PageUp/PageDown),
   * либо Infinity/-Infinity для End/Home.
   */
  function sliderStep(value, min, max, step, keyDelta) {
    if (keyDelta === Infinity) return max;
    if (keyDelta === -Infinity) return min;
    var next = value + keyDelta * step;
    next = Math.round(next / step) * step;
    next = Math.round(next * 1e9) / 1e9; // убрать погрешность float
    if (next < min) next = min;
    if (next > max) next = max;
    return next;
  }

  /**
   * Следующий индекс в listbox/radiogroup по нажатой клавише, пропуская disabled-пункты.
   * list — массив объектов, у которых может быть {disabled:true}.
   */
  function nextIndex(list, current, key) {
    var n = list.length;
    if (!n) return -1;

    function enabled(i) {
      return !(list[i] && list[i].disabled);
    }

    var i = current;
    var dir = 1;
    if (key === "Home") {
      i = 0;
    } else if (key === "End") {
      i = n - 1;
    } else if (key === "ArrowDown" || key === "Down") {
      i = current < 0 ? 0 : current + 1;
      if (i > n - 1) i = 0; // wrap
      dir = 1;
    } else if (key === "ArrowUp" || key === "Up") {
      i = current < 0 ? n - 1 : current - 1;
      if (i < 0) i = n - 1; // wrap
      dir = -1;
    } else {
      return current;
    }

    var tries = 0;
    while (!enabled(i) && tries < n) {
      i += dir;
      if (i < 0) i = n - 1;
      if (i > n - 1) i = 0;
      tries++;
    }
    if (!enabled(i)) {
      for (var j = 0; j < n; j++) {
        if (enabled(j)) return j;
      }
      return current;
    }
    return i;
  }

  /**
   * Переход по первой букве(ам): buffer — накопленная строка ввода, items — [{label}].
   * Возвращает индекс первого совпадения по префиксу (регистронезависимо) или -1.
   */
  function typeahead(items, buffer) {
    if (!buffer) return -1;
    var b = String(buffer).toLowerCase();
    for (var i = 0; i < items.length; i++) {
      var label = String((items[i] && items[i].label) || "").toLowerCase();
      if (label.indexOf(b) === 0) return i;
    }
    return -1;
  }

  return {
    placePopover: placePopover,
    sliderStep: sliderStep,
    nextIndex: nextIndex,
    typeahead: typeahead,
  };
});
