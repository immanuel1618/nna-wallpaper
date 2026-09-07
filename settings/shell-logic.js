// settings/shell-logic.js — pure functions behind the shell (no DOM): legacy ?tab= mapping and
// the sidebar search filter. Covered without a browser by settings/tests/shell.test.mjs.

/**
 * Old and new ?tab= values -> { page, selection? }. Keeps deep links from the WPF host
 * (SettingsWindow's tab constants) and old bookmarks working after the page rewrite.
 * Old greek tab keys (alpha..zeta) are the pre-redesign page order: alpha=layout, beta=blocks,
 * gamma=appearance, delta=planner, epsilon=general, zeta=taskbar.
 */
export function mapTabName(tab) {
  switch (tab) {
    case "launch": return { page: "blocks", selection: "launch" };
    case "events": return { page: "blocks", selection: "events" };
    case "config": case "nna-config": case "general": return { page: "general" };
    case "theme": return { page: "appearance" };
    case "alpha": return { page: "layout" };
    case "beta": return { page: "blocks" };
    case "gamma": return { page: "appearance" };
    case "delta": return { page: "planner" };
    case "epsilon": return { page: "general" };
    case "zeta": return { page: "taskbar" };
    case "layout": case "blocks": case "appearance": case "topbar": case "dock":
    case "taskbar": case "cursor": case "planner": case "about":
      return { page: tab };
    default: return { page: "layout" };
  }
}

function norm(s) {
  return String(s || "").trim().toLowerCase();
}

/** True if `query` appears in `text` (case/whitespace-insensitive). Empty query always matches. */
export function matchesQuery(text, query) {
  const q = norm(query);
  if (!q) return true;
  return norm(text).includes(q);
}

/**
 * items: [{ key, title, keywords?: string[] }] — keywords already localized by the caller
 * (each page module's keywords(lang), see docs/SETTINGS.md).
 * Returns the subset of keys whose title or any keyword contains `query`; all keys if `query`
 * is empty.
 */
export function filterSidebar(items, query) {
  if (!norm(query)) return items.map((i) => i.key);
  return items
    .filter((item) => matchesQuery(item.title, query) || (item.keywords || []).some((k) => matchesQuery(k, query)))
    .map((item) => item.key);
}

/** Key of the first matching item (Enter-to-open in the search box), or null if none match. */
export function firstMatch(items, query) {
  const keys = filterSidebar(items, query);
  return keys.length ? keys[0] : null;
}
