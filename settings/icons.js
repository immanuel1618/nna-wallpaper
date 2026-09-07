// settings/icons.js — small inline-SVG line icons used by the sidebar and a few page rows.
// Rule (docs/DESIGN-SYSTEM.md): no icon sets, no emoji — simple 1.5px line glyphs, 16x16 viewBox,
// stroke="currentColor" so they inherit the row's text color (muted -> fg on hover/active).

function svg(inner, viewBox) {
  return `<svg viewBox="${viewBox || "0 0 16 16"}" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${inner}</svg>`;
}

// Sidebar page icons — one per page, in sidebar order.
export const ICONS = {
  // Раскладка: 3-column grid
  layout: svg('<rect x="2" y="2.5" width="12" height="11" rx="1"/><line x1="6.3" y1="2.5" x2="6.3" y2="13.5"/><line x1="10.6" y1="2.5" x2="10.6" y2="13.5"/>'),
  // Блоки: four small tiles
  blocks: svg('<rect x="2" y="2" width="5" height="5" rx="0.8"/><rect x="9" y="2" width="5" height="5" rx="0.8"/><rect x="2" y="9" width="5" height="5" rx="0.8"/><rect x="9" y="9" width="5" height="5" rx="0.8"/>'),
  // Внешний вид: aperture / dim dial
  appearance: svg('<circle cx="8" cy="8" r="5.5"/><circle cx="8" cy="8" r="2"/><line x1="8" y1="2.5" x2="8" y2="4.3"/>'),
  // Верхняя строка: rectangle with a filled top strip
  topbar: svg('<rect x="2" y="3" width="12" height="10" rx="1"/><line x1="2" y1="6" x2="14" y2="6"/>'),
  // Док: rectangle with a filled bottom strip and two ticks (running app dots)
  dock: svg('<rect x="2" y="3" width="12" height="10" rx="1"/><line x1="2" y1="10" x2="14" y2="10"/><line x1="5.5" y1="12" x2="5.5" y2="12"/><line x1="8" y1="12" x2="8" y2="12"/>'),
  // Панель задач: thick bar along the bottom
  taskbar: svg('<rect x="2" y="2.5" width="12" height="11" rx="1"/><line x1="2" y1="10.5" x2="14" y2="10.5"/>'),
  // Курсор: arrow pointer outline
  cursor: svg('<path d="M4 2.2 L4 13 L6.7 10.6 L8.7 13.8 L10.2 13 L8.2 9.8 L11.4 9.4 Z"/>'),
  // Планировщик: checklist
  planner: svg('<rect x="2.5" y="2.5" width="11" height="11" rx="1"/><path d="M5 8 L7 10 L11 6"/>'),
  // Общие: gear
  general: svg('<circle cx="8" cy="8" r="2.3"/><path d="M8 2.5 V4.3 M8 11.7 V13.5 M2.5 8 H4.3 M11.7 8 H13.5 M4.1 4.1 L5.4 5.4 M10.6 10.6 L11.9 11.9 M4.1 11.9 L5.4 10.6 M10.6 5.4 L11.9 4.1"/>'),
  // О программе: info circle
  about: svg('<circle cx="8" cy="8" r="5.5"/><line x1="8" y1="7.2" x2="8" y2="11"/><line x1="8" y1="5" x2="8" y2="5"/>'),
};

// Small utility glyphs used inline in a few pages/the sidebar search box.
export const UTIL = {
  search: svg('<circle cx="6.8" cy="6.8" r="4.3"/><line x1="10" y1="10" x2="13.5" y2="13.5"/>'),
  plus: svg('<line x1="8" y1="3" x2="8" y2="13"/><line x1="3" y1="8" x2="13" y2="8"/>'),
  minus: svg('<line x1="3" y1="8" x2="13" y2="8"/>'),
  trash: svg('<path d="M3 4.5 H13 M6 4.5 V3 a1 1 0 0 1 1 -1 h2 a1 1 0 0 1 1 1 v1.5 M4.5 4.5 L5 13 a1 1 0 0 0 1 1 h4 a1 1 0 0 0 1 -1 l0.5 -8.5"/>'),
  drag: svg('<circle cx="5.5" cy="4" r="0.9"/><circle cx="10.5" cy="4" r="0.9"/><circle cx="5.5" cy="8" r="0.9"/><circle cx="10.5" cy="8" r="0.9"/><circle cx="5.5" cy="12" r="0.9"/><circle cx="10.5" cy="12" r="0.9"/>'),
  folder: svg('<path d="M2 4.5 a1 1 0 0 1 1 -1 h3.2 l1.2 1.5 H13 a1 1 0 0 1 1 1 V12 a1 1 0 0 1 -1 1 H3 a1 1 0 0 1 -1 -1 Z"/>'),
  check: svg('<path d="M3.5 8.3 L6.5 11.3 L12.5 4.7"/>'),
};

export function iconEl(name, group) {
  const span = document.createElement("span");
  span.className = "nav-icon";
  span.innerHTML = (group === "util" ? UTIL : ICONS)[name] || "";
  return span;
}
