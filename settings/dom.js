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
