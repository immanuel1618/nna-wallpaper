/* NNA UI — библиотека компонентов поверх токенов v3 (ui/tokens.css, ui/components.css).
   UMD: обычный <script> ставит window.NNAUI; в CommonJS/Node доступен через module.exports.
   Порядок загрузки в браузере: tokens.css, components.css, logic.js, components.js, затем app.js.
   Без готовых иконочных наборов, без эмодзи, без transition: all — см. docs/DESIGN-SYSTEM.md. */
(function (root, factory) {
  var Logic =
    typeof module === "object" && module.exports
      ? require("./logic.js")
      : root.NNAUILogic;
  var mod = factory(Logic);
  if (typeof module === "object" && module.exports) {
    module.exports = mod;
  }
  if (typeof root !== "undefined") {
    root.NNAUI = mod;
  }
})(typeof window !== "undefined" ? window : this, function (Logic) {
  "use strict";

  var hasDom = typeof document !== "undefined";
  var uidCounter = 0;
  function uid(prefix) {
    uidCounter += 1;
    return (prefix || "nnaui") + "-" + uidCounter;
  }

  function viewport() {
    return { width: window.innerWidth, height: window.innerHeight };
  }

  function rectOf(el) {
    var r = el.getBoundingClientRect();
    return { top: r.top, left: r.left, right: r.right, bottom: r.bottom, width: r.width, height: r.height };
  }

  function placeFixed(el, anchorEl, placement) {
    var ar = rectOf(anchorEl);
    var pr = { width: el.offsetWidth, height: el.offsetHeight };
    var pos = Logic.placePopover(ar, pr, viewport(), placement || "bottom");
    el.style.top = pos.top + "px";
    el.style.left = pos.left + "px";
    return pos;
  }

  function onOutside(els, cb) {
    function handler(e) {
      for (var i = 0; i < els.length; i++) {
        if (els[i] && (els[i] === e.target || els[i].contains(e.target))) return;
      }
      cb(e);
    }
    document.addEventListener("pointerdown", handler, true);
    return function () {
      document.removeEventListener("pointerdown", handler, true);
    };
  }

  function onEscape(cb) {
    function handler(e) {
      if (e.key === "Escape") cb(e);
    }
    document.addEventListener("keydown", handler, true);
    return function () {
      document.removeEventListener("keydown", handler, true);
    };
  }

  /* ── tooltip ─────────────────────────────────────────────────────────── */
  function tooltip(el, text) {
    var box = null;
    var timer = null;
    function show() {
      box = document.createElement("div");
      box.className = "ui-tooltip";
      box.textContent = text;
      document.body.appendChild(box);
      placeFixed(box, el, "top");
      // force reflow then show
      requestAnimationFrame(function () {
        if (box) box.classList.add("is-show");
      });
    }
    function hide() {
      if (timer) { clearTimeout(timer); timer = null; }
      if (box && box.parentNode) box.parentNode.removeChild(box);
      box = null;
    }
    function schedule() {
      hide();
      timer = setTimeout(show, 400);
    }
    el.addEventListener("mouseenter", schedule);
    el.addEventListener("mouseleave", hide);
    el.addEventListener("focus", schedule);
    el.addEventListener("blur", hide);
    return { hide: hide };
  }

  /* ── popover ─────────────────────────────────────────────────────────── */
  function popover(anchorEl, opts) {
    opts = opts || {};
    var el = document.createElement("div");
    el.className = "ui-popover";
    el.setAttribute("role", "dialog");
    if (opts.content instanceof Node) {
      el.appendChild(opts.content);
    } else if (typeof opts.content === "string") {
      el.innerHTML = opts.content;
    }
    document.body.appendChild(el);
    placeFixed(el, anchorEl, opts.placement || "bottom");

    var closed = false;
    var offOutside = onOutside([el, anchorEl], function () { close(); });
    var offEsc = onEscape(function () { close(); });

    function close() {
      if (closed) return;
      closed = true;
      offOutside();
      offEsc();
      if (el.parentNode) el.parentNode.removeChild(el);
      if (typeof opts.onClose === "function") opts.onClose();
    }

    return { close: close, el: el };
  }

  /* ── menu ────────────────────────────────────────────────────────────── */
  function menu(anchorEl, opts) {
    opts = opts || {};
    var items = opts.items || [];
    var el = document.createElement("div");
    el.className = "ui-menu";
    el.setAttribute("role", "menu");
    el.tabIndex = -1;

    var rows = [];
    items.forEach(function (item, i) {
      if (item.separator) {
        var sep = document.createElement("div");
        sep.className = "ui-menu-sep";
        el.appendChild(sep);
        return;
      }
      var row = document.createElement("div");
      row.className = "ui-menu-item";
      row.setAttribute("role", "menuitem");
      row.id = uid("menu-item");
      if (item.disabled) row.setAttribute("aria-disabled", "true");
      var label = document.createElement("span");
      label.textContent = item.label || "";
      row.appendChild(label);
      if (item.hint) {
        var hint = document.createElement("span");
        hint.className = "hint";
        hint.textContent = item.hint;
        row.appendChild(hint);
      }
      row.addEventListener("click", function () {
        if (item.disabled) return;
        if (typeof item.onClick === "function") item.onClick(item);
        close();
      });
      row.addEventListener("mouseenter", function () {
        setActive(rows.indexOf(row));
      });
      el.appendChild(row);
      rows.push(row);
    });

    document.body.appendChild(el);
    placeFixed(el, anchorEl, opts.placement || "bottom");

    var activeIndex = -1;
    function setActive(i) {
      if (activeIndex >= 0 && rows[activeIndex]) rows[activeIndex].classList.remove("is-active");
      activeIndex = i;
      if (activeIndex >= 0 && rows[activeIndex]) {
        rows[activeIndex].classList.add("is-active");
        el.setAttribute("aria-activedescendant", rows[activeIndex].id);
      }
    }

    var enabledList = items.filter(function (it) { return !it.separator; });
    function rowIndexFor(logicIndex) {
      // rows[] excludes separators already (1:1 with enabledList), so it matches directly.
      return logicIndex;
    }

    function onKey(e) {
      if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Home" || e.key === "End") {
        e.preventDefault();
        var next = Logic.nextIndex(enabledList, activeIndex, e.key);
        setActive(rowIndexFor(next));
      } else if (e.key === "Enter") {
        e.preventDefault();
        if (activeIndex >= 0) rows[activeIndex].click();
      } else if (e.key === "Escape") {
        close();
      }
    }

    var closed = false;
    var offOutside = onOutside([el, anchorEl], function () { close(); });
    document.addEventListener("keydown", onKey, true);
    el.focus();

    function close() {
      if (closed) return;
      closed = true;
      offOutside();
      document.removeEventListener("keydown", onKey, true);
      if (el.parentNode) el.parentNode.removeChild(el);
      if (typeof opts.onClose === "function") opts.onClose();
    }

    return { close: close, el: el };
  }

  /* ── dialog ──────────────────────────────────────────────────────────── */
  function dialog(opts) {
    opts = opts || {};
    var overlay = document.createElement("div");
    overlay.className = "ui-dialog-overlay";
    var box = document.createElement("div");
    box.className = "ui-dialog";
    box.setAttribute("role", "dialog");
    box.setAttribute("aria-modal", "true");

    if (opts.title) {
      var title = document.createElement("div");
      title.className = "ui-dialog-title";
      title.textContent = opts.title;
      box.appendChild(title);
    }

    var body = document.createElement("div");
    body.className = "ui-dialog-body";
    if (opts.body instanceof Node) body.appendChild(opts.body);
    else if (typeof opts.body === "string") body.textContent = opts.body;
    box.appendChild(body);

    var actionsWrap = document.createElement("div");
    actionsWrap.className = "ui-dialog-actions";
    var buttons = [];
    (opts.actions || []).forEach(function (action) {
      var btn = document.createElement("button");
      btn.className = "ui-btn" + (action.primary ? " primary" : " ghost");
      btn.textContent = action.label || "";
      btn.addEventListener("click", function () {
        if (typeof action.onClick === "function") action.onClick();
        close();
      });
      actionsWrap.appendChild(btn);
      buttons.push(btn);
    });
    box.appendChild(actionsWrap);

    overlay.appendChild(box);
    document.body.appendChild(overlay);

    var focusables = buttons.length ? buttons : [box];
    focusables[focusables.length - 1].focus();

    function trapTab(e) {
      if (e.key !== "Tab") return;
      var idx = focusables.indexOf(document.activeElement);
      e.preventDefault();
      var next;
      if (e.shiftKey) next = idx <= 0 ? focusables.length - 1 : idx - 1;
      else next = idx === focusables.length - 1 ? 0 : idx + 1;
      focusables[next].focus();
    }
    function onKey(e) {
      if (e.key === "Escape") close();
      else trapTab(e);
    }
    document.addEventListener("keydown", onKey, true);

    var closed = false;
    function close() {
      if (closed) return;
      closed = true;
      document.removeEventListener("keydown", onKey, true);
      if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
    }

    return { close: close, el: overlay };
  }

  /* ── toggle ──────────────────────────────────────────────────────────── */
  function toggle(el, opts) {
    opts = opts || {};
    var checked = !!opts.checked;
    el.className = (el.className ? el.className + " " : "") + "ui-toggle";
    el.setAttribute("role", "switch");
    el.tabIndex = 0;
    el.setAttribute("aria-checked", String(checked));
    el.innerHTML = "";
    var track = document.createElement("span");
    track.className = "ui-toggle-track";
    el.appendChild(track);
    if (opts.label) {
      var label = document.createElement("span");
      label.textContent = opts.label;
      el.appendChild(label);
    }

    function set(next, fromUser) {
      checked = !!next;
      el.setAttribute("aria-checked", String(checked));
      if (fromUser && typeof opts.onChange === "function") opts.onChange(checked);
    }

    el.addEventListener("click", function () { set(!checked, true); });
    el.addEventListener("keydown", function (e) {
      if (e.key === " " || e.key === "Enter") {
        e.preventDefault();
        set(!checked, true);
      }
    });

    return {
      el: el,
      get checked() { return checked; },
      set: function (v) { set(v, false); },
    };
  }

  /* ── slider ──────────────────────────────────────────────────────────── */
  function slider(el, opts) {
    opts = opts || {};
    var min = opts.min != null ? opts.min : 0;
    var max = opts.max != null ? opts.max : 100;
    var step = opts.step != null ? opts.step : 1;
    var value = opts.value != null ? opts.value : min;
    var format = opts.format || function (v) { return String(v); };

    el.className = (el.className ? el.className + " " : "") + "ui-slider";
    el.innerHTML = "";
    var track = document.createElement("div");
    track.className = "ui-slider-track";
    var fill = document.createElement("div");
    fill.className = "ui-slider-fill";
    var thumb = document.createElement("div");
    thumb.className = "ui-slider-thumb";
    thumb.tabIndex = 0;
    thumb.setAttribute("role", "slider");
    thumb.setAttribute("aria-valuemin", String(min));
    thumb.setAttribute("aria-valuemax", String(max));
    track.appendChild(fill);
    track.appendChild(thumb);
    el.appendChild(track);
    var valueEl = document.createElement("span");
    valueEl.className = "ui-slider-value";
    el.appendChild(valueEl);

    function render() {
      var pct = (value - min) / (max - min || 1);
      pct = Math.max(0, Math.min(1, pct));
      fill.style.width = (pct * 100) + "%";
      thumb.style.left = (pct * 100) + "%";
      thumb.setAttribute("aria-valuenow", String(value));
      valueEl.textContent = format(value);
    }

    function set(next, fromUser) {
      value = Math.max(min, Math.min(max, next));
      render();
      if (fromUser && typeof opts.onInput === "function") opts.onInput(value);
    }
    function commit() {
      if (typeof opts.onChange === "function") opts.onChange(value);
    }

    function valueFromClientX(clientX) {
      var r = track.getBoundingClientRect();
      var pct = r.width ? (clientX - r.left) / r.width : 0;
      pct = Math.max(0, Math.min(1, pct));
      var raw = min + pct * (max - min);
      return Logic.sliderStep(min, min, max, step, Math.round((raw - min) / step));
    }

    var dragging = false;
    function onDown(e) {
      dragging = true;
      thumb.focus();
      set(valueFromClientX(e.clientX), true);
      e.preventDefault();
    }
    function onMove(e) {
      if (!dragging) return;
      set(valueFromClientX(e.clientX), true);
    }
    function onUp() {
      if (!dragging) return;
      dragging = false;
      commit();
    }
    track.addEventListener("pointerdown", onDown);
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);

    thumb.addEventListener("keydown", function (e) {
      var delta = null;
      if (e.key === "ArrowRight" || e.key === "ArrowUp") delta = 1;
      else if (e.key === "ArrowLeft" || e.key === "ArrowDown") delta = -1;
      else if (e.key === "PageUp") delta = 10;
      else if (e.key === "PageDown") delta = -10;
      else if (e.key === "Home") delta = -Infinity;
      else if (e.key === "End") delta = Infinity;
      if (delta === null) return;
      e.preventDefault();
      set(Logic.sliderStep(value, min, max, step, delta), true);
      commit();
    });

    render();

    return {
      el: el,
      get value() { return value; },
      set: function (v) { set(v, false); },
    };
  }

  /* ── segmented ───────────────────────────────────────────────────────── */
  function segmented(el, opts) {
    opts = opts || {};
    var items = opts.items || [];
    var value = opts.value;
    el.className = (el.className ? el.className + " " : "") + "ui-segmented";
    el.setAttribute("role", "radiogroup");
    el.innerHTML = "";

    var buttons = items.map(function (item) {
      var btn = document.createElement("div");
      btn.className = "ui-segmented-item";
      btn.setAttribute("role", "radio");
      btn.tabIndex = item.value === value ? 0 : -1;
      btn.textContent = item.label;
      btn.setAttribute("aria-checked", String(item.value === value));
      btn.addEventListener("click", function () { select(item.value, true); });
      el.appendChild(btn);
      return btn;
    });

    function select(v, fromUser) {
      value = v;
      items.forEach(function (item, i) {
        var on = item.value === value;
        buttons[i].setAttribute("aria-checked", String(on));
        buttons[i].tabIndex = on ? 0 : -1;
      });
      if (fromUser && typeof opts.onChange === "function") opts.onChange(value);
    }

    el.addEventListener("keydown", function (e) {
      if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
      var idx = items.findIndex(function (it) { return it.value === value; });
      var key = e.key === "ArrowRight" ? "ArrowDown" : "ArrowUp";
      var next = Logic.nextIndex(items, idx, key);
      select(items[next].value, true);
      buttons[next].focus();
      e.preventDefault();
    });

    return {
      el: el,
      get value() { return value; },
      set: function (v) { select(v, false); },
    };
  }

  /* ── select ──────────────────────────────────────────────────────────── */
  function select(el, opts) {
    opts = opts || {};
    var options = opts.options || [];
    var value = opts.value;
    var searchable = !!opts.searchable;

    var host = el;
    var nativeSelect = null;
    if (el.tagName === "SELECT") {
      nativeSelect = el;
      host = document.createElement("div");
      el.parentNode.insertBefore(host, el);
      el.style.display = "none";
    }
    host.className = (host.className ? host.className + " " : "") + "ui-select";

    var trigger = document.createElement("div");
    trigger.className = "ui-select-trigger";
    trigger.tabIndex = 0;
    trigger.setAttribute("role", "combobox");
    trigger.setAttribute("aria-haspopup", "listbox");
    trigger.setAttribute("aria-expanded", "false");
    var valueSpan = document.createElement("span");
    valueSpan.className = "v";
    var arrow = document.createElement("span");
    arrow.className = "arrow";
    trigger.appendChild(valueSpan);
    trigger.appendChild(arrow);
    host.innerHTML = "";
    host.appendChild(trigger);

    function currentLabel() {
      var found = options.filter(function (o) { return o.value === value; })[0];
      return found ? found.label : "";
    }
    function render() {
      valueSpan.textContent = currentLabel();
    }
    render();

    var listboxEl = null;
    var optionRows = [];
    var activeIndex = -1;
    var typeaheadBuffer = "";
    var typeaheadTimer = null;

    function open() {
      if (listboxEl) return;
      host.classList.add("is-open");
      trigger.setAttribute("aria-expanded", "true");
      listboxEl = document.createElement("div");
      listboxEl.className = "ui-listbox";
      listboxEl.setAttribute("role", "listbox");
      listboxEl.id = uid("listbox");

      var searchInput = null;
      if (searchable) {
        searchInput = document.createElement("input");
        searchInput.className = "ui-select-search";
        searchInput.placeholder = "";
        listboxEl.appendChild(searchInput);
      }

      var listWrap = document.createElement("div");
      listboxEl.appendChild(listWrap);

      function buildRows(filter) {
        listWrap.innerHTML = "";
        optionRows = [];
        var visible = options.filter(function (o) {
          return !filter || String(o.label).toLowerCase().indexOf(filter.toLowerCase()) !== -1;
        });
        if (!visible.length) {
          var empty = document.createElement("div");
          empty.className = "ui-option-empty";
          empty.textContent = "—";
          listWrap.appendChild(empty);
          return;
        }
        visible.forEach(function (opt) {
          var row = document.createElement("div");
          row.className = "ui-option";
          row.id = uid("option");
          row.setAttribute("role", "option");
          if (opt.disabled) row.setAttribute("aria-disabled", "true");
          if (opt.value === value) row.classList.add("is-selected");
          var label = document.createElement("div");
          label.textContent = opt.label;
          row.appendChild(label);
          if (opt.hint) {
            var hint = document.createElement("div");
            hint.className = "hint";
            hint.textContent = opt.hint;
            row.appendChild(hint);
          }
          row.addEventListener("click", function () {
            if (opt.disabled) return;
            choose(opt.value);
          });
          row.addEventListener("mouseenter", function () {
            setActive(optionRows.indexOf(row));
          });
          listWrap.appendChild(row);
          optionRows.push(row);
        });
      }
      buildRows("");

      document.body.appendChild(listboxEl);
      placeFixed(listboxEl, trigger, opts.placement || "bottom");
      trigger.setAttribute("aria-controls", listboxEl.id);

      activeIndex = options.findIndex(function (o) { return o.value === value; });
      setActive(activeIndex);

      if (searchInput) {
        searchInput.focus();
        searchInput.addEventListener("input", function () {
          buildRows(searchInput.value);
          activeIndex = optionRows.length ? 0 : -1;
          setActive(activeIndex);
        });
        searchInput.addEventListener("keydown", handleKey);
      } else {
        trigger.focus();
      }

      var offOutside = onOutside([listboxEl, trigger], function () { close(); });
      document.addEventListener("keydown", handleKey, true);

      function setActive(i) {
        if (activeIndex >= 0 && optionRows[activeIndex]) optionRows[activeIndex].classList.remove("is-active");
        activeIndex = i;
        if (activeIndex >= 0 && optionRows[activeIndex]) {
          optionRows[activeIndex].classList.add("is-active");
          listboxEl.setAttribute("aria-activedescendant", optionRows[activeIndex].id);
          optionRows[activeIndex].scrollIntoView({ block: "nearest" });
        }
      }
      listboxEl._setActive = setActive;

      function handleKey(e) {
        if (e.key === "Escape") {
          e.preventDefault();
          close();
          trigger.focus();
        } else if (e.key === "Enter") {
          e.preventDefault();
          if (activeIndex >= 0 && optionRows[activeIndex]) optionRows[activeIndex].click();
        } else if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Home" || e.key === "End") {
          e.preventDefault();
          var enabledOptions = options.filter(function (o) { return !o.disabled; });
          var next = Logic.nextIndex(enabledOptions, activeIndex, e.key);
          setActive(next);
        } else if (!searchInput && e.key.length === 1) {
          typeaheadBuffer += e.key;
          clearTimeout(typeaheadTimer);
          typeaheadTimer = setTimeout(function () { typeaheadBuffer = ""; }, 600);
          var idx = Logic.typeahead(options, typeaheadBuffer);
          if (idx !== -1) setActive(idx);
        }
      }

      listboxEl._close = function () {
        offOutside();
        document.removeEventListener("keydown", handleKey, true);
      };
    }

    function close() {
      if (!listboxEl) return;
      host.classList.remove("is-open");
      trigger.setAttribute("aria-expanded", "false");
      if (listboxEl._close) listboxEl._close();
      if (listboxEl.parentNode) listboxEl.parentNode.removeChild(listboxEl);
      listboxEl = null;
    }

    function choose(v) {
      value = v;
      render();
      if (nativeSelect) nativeSelect.value = v;
      close();
      trigger.focus();
      if (typeof opts.onChange === "function") opts.onChange(value);
    }

    trigger.addEventListener("click", function () {
      if (listboxEl) close();
      else open();
    });
    trigger.addEventListener("keydown", function (e) {
      if (!listboxEl && (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Enter" || e.key === " ")) {
        e.preventDefault();
        open();
      }
    });

    return {
      el: host,
      get value() { return value; },
      set: function (v) { value = v; render(); },
      setOptions: function (opts2) { options = opts2; render(); },
      open: open,
      close: close,
    };
  }

  return {
    select: select,
    toggle: toggle,
    slider: slider,
    segmented: segmented,
    popover: popover,
    tooltip: tooltip,
    dialog: dialog,
    menu: menu,
    _internal: hasDom ? { placeFixed: placeFixed, viewport: viewport, uid: uid } : null,
  };
});
