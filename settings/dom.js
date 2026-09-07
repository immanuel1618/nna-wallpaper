// settings/dom.js — tiny DOM builder plus the macOS-style "group card / setting row" layout
// helpers shared by every page under settings/pages/. Same `el()` used to live copy-pasted in
// app.js, forms.js and taskbar-tab.js; kept those two alone (they work) and centralised it here
// only for the new pages.

export function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") node.className = v;
      else if (k === "text") node.textContent = v;
      else if (k.startsWith("on") && typeof v === "function") node.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) node.setAttribute(k, v === true ? "" : v);
    }
  }
  for (const c of children) {
    if (c === null || c === undefined) continue;
    node.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return node;
}

/** One macOS System Settings-style card: a mono uppercase title, then rows. `id` is used by
 * shell.js's search to find and highlight matching cards on the open page. */
export function groupCard(id, titleText, ...children) {
  return el("div", { class: "group", "data-group": id },
    el("div", { class: "group-title", text: titleText }),
    ...children);
}

/**
 * One row inside a group: label (+ optional help line) on the left, one control on the right —
 * mirrors the existing `.setrow` used across the old tabs (β/δ/ζ), reused as-is. The control is
 * wrapped in `.row-control` (a bounded flex-basis column, see design-system.css): NNAUI's
 * `select` and `slider` both set `width:100%` on themselves, which — mounted directly as an
 * unconstrained flex item — computes a huge flex-basis from that 100% and squeezes the label
 * next to it almost to nothing. Giving the control column a capped width first, then letting the
 * 100% resolve against *that*, is the fix; `toggle`/`segmented` are self-sized and unaffected
 * either way.
 */
export function settingRow(titleText, helpText, control) {
  return el("div", { class: "setrow" },
    el("div", { class: "main" }, el("div", { class: "t", text: titleText }), helpText ? el("div", { class: "s", text: helpText }) : null),
    el("div", { class: "row-control" }, control));
}

/** A `groupCard` whose body starts collapsed, toggled by a chevron button next to the title (see
 * taskbar-tab.js's "Advanced" JSON import/export section). Returns the card element; nothing else
 * needs to reach into it, the toggle is entirely self-contained. */
export function collapsibleCard(id, titleText, ...children) {
  const body = el("div", { class: "stack", hidden: true });
  body.append(...children);
  const chevron = el("span", { class: "nav-icon" });
  chevron.innerHTML = ICON_CHEVRON_DOWN;
  const toggleBtn = el("button", {
    class: "ui-icon-btn", type: "button", "aria-expanded": "false",
    onclick: () => {
      const open = body.hidden; // about to open
      body.hidden = !open;
      toggleBtn.setAttribute("aria-expanded", String(open));
      chevron.style.transform = open ? "rotate(180deg)" : "";
    },
  }, chevron);
  const head = el("div", { class: "group-title group-title-row" }, el("span", { text: titleText }), toggleBtn);
  return el("div", { class: "group", "data-group": id }, head, body);
}
// Inlined rather than imported from icons.js to avoid a circular import (icons.js has no
// dependency on dom.js today, but keeping this module import-free of icons.js keeps it that way).
const ICON_CHEVRON_DOWN = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6 L8 10 L12 6"/></svg>';

/** Resolves a widget's display name for the current language: prefers manifest `title:{ru,en}`
 * (docs/SETTINGS.md "Русские названия блоков"), falls back to the old single-language `name`,
 * then the widget id. Shared by blocks.js (cards + block page), layout-canvas.js (palette + grid
 * block labels) so every place a block's name shows up agrees. */
export function widgetLabel(widget, lang) {
  if (!widget) return "";
  const title = widget.title;
  if (title && typeof title === "object") {
    const resolved = title[lang] || title.ru || title.en;
    if (resolved) return resolved;
  }
  return widget.name || widget.id || "";
}

// Base/Surface/Slate/Steel — the four neutral palette tones a surface color picker may choose
// from (docs/DESIGN-SYSTEM.md "Палитра"); Signal/Blood/Chrome are accents, not surface fills, so
// they are deliberately not offered here.
const PALETTE_SWATCHES = [
  { value: "#0B0B0B", labelKey: "swatchBase" },
  { value: "#161616", labelKey: "swatchSurface" },
  { value: "#434343", labelKey: "swatchSlate" },
  { value: "#808080", labelKey: "swatchSteel" },
];

/**
 * Mounts a `NNAUI.segmented` restricted to the four neutral palette tones, each item prefixed
 * with a small color swatch — replaces a native `<input type="color">` + hex field (owner
 * decision: no color pickers, pick from the palette instead). The stored value is still a plain
 * hex string, unchanged by callers. Returns the same handle `NNAUI.segmented` returns.
 */
export function paletteSwatchField(mount, t, value, onChange) {
  const items = PALETTE_SWATCHES.map((p) => ({ value: p.value, label: t(p.labelKey) }));
  const ctl = window.NNAUI.segmented(mount, { value, items, onChange });
  const buttons = mount.querySelectorAll(".ui-segmented-item");
  PALETTE_SWATCHES.forEach((p, i) => {
    const btn = buttons[i];
    if (!btn) return;
    const swatch = el("span", {
      "aria-hidden": "true",
      style: `display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:5px;vertical-align:middle;background:${p.value};border:1px solid var(--slate)`,
    });
    btn.prepend(swatch);
  });
  return ctl;
}
