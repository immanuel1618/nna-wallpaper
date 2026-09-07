// settings/pages/planner.js — "Planner" page (θ): login status and what-to-show toggles.
// Carried over from the old app.js's renderDelta near-verbatim (same /planner/status shape,
// same save logic); a full redesign of this page is a later stage, per the owner's brief.

import { el, groupCard, settingRow } from "../dom.js";

export function keywords(lang) {
  return lang === "en"
    ? ["telegram", "login", "logout", "tasks", "meetings", "habits", "money", "briefing"]
    : ["телеграм", "вход", "выход", "задачи", "встречи", "привычки", "финансы", "брифинг"];
}

function switchEl(checked, onToggle) {
  const sw = el("div", { class: "sw" + (checked ? " on" : ""), role: "switch", tabindex: "0" });
  const toggle = () => { const on = !sw.classList.contains("on"); sw.classList.toggle("on", on); onToggle(on); };
  sw.addEventListener("click", toggle);
  sw.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
  return sw;
}

export async function render(container, ctx) {
  const { t } = ctx;
  const page = el("div", { class: "page-narrow" });
  container.append(page);

  let status = null;
  try { status = await ctx.api("GET", "/planner/status"); } catch { status = null; }

  const statusCard = groupCard("login", t("plannerStatus"));
  if (!status) {
    statusCard.append(
      el("div", { class: "empty" }, el("div", { class: "txt", text: t("plannerNotAvailable") })),
      el("div", { class: "rowflex" },
        el("button", { class: "btn primary", type: "button", disabled: true, text: t("loginTelegram") }),
        el("button", { class: "btn", type: "button", disabled: true, text: t("logout") })));
  } else {
    statusCard.append(
      el("div", { class: "card", text: JSON.stringify(status) }),
      el("div", { class: "rowflex" },
        el("button", {
          class: "btn primary", type: "button", text: t("loginTelegram"),
          onclick: async () => { try { await ctx.api("POST", "/app/login"); } catch (err) { ctx.onStatus(t("statusError", err.message), true); } },
        }),
        el("button", {
          class: "btn", type: "button", text: t("logout"),
          onclick: async () => { try { await ctx.api("POST", "/planner/logout"); render(container, ctx); } catch (err) { ctx.onStatus(t("statusError", err.message), true); } },
        })));
  }

  const showCard = groupCard("show", t("plannerShow"));
  const show = new Set(ctx.config.app.planner?.show || []);
  for (const key of ["tasks", "meetings", "habits", "money", "briefing"]) {
    const sw = switchEl(show.has(key), async (on) => {
      if (on) show.add(key); else show.delete(key);
      try {
        await ctx.put({ app: { planner: { ...ctx.config.app.planner, show: [...show] } } });
        ctx.config.app.planner = { ...ctx.config.app.planner, show: [...show] };
        ctx.onStatus(t("statusSaved"), false);
      } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
    });
    showCard.append(settingRow(t("show_" + key), null, sw));
  }

  page.append(statusCard, showCard);
}
