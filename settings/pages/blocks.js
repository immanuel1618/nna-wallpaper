// settings/pages/blocks.js — "Blocks" page (β): every installed widget plus the two hand-rolled
// launch/events editors, as a macOS-style row list (icon, name, enabled toggle) on the left and
// the selected item's form (forms.js) on the right. Carries over renderBeta's save logic as-is.
// Full preview cards for each block are a later stage (see the owner's brief) — these rows are
// deliberately plain.
//
// The enabled toggle is a small addition over the old tab: it flips `widgetSettings[id].enabled`
// (default true when absent). Nothing in the widget runtime reads that flag yet — it's a generic,
// forward-compatible on/off a widget script can check for itself; it does not touch the monitor
// layout (a widget can be "enabled" here and still not be placed on any monitor, or vice versa —
// wiring the two together is layout-editor.js's job, out of scope for this stage).

import { renderWidgetForm, renderLaunchForm, renderEventsForm } from "../forms.js";
import { el } from "../dom.js";
import { iconEl } from "../icons.js";

export function keywords(lang) {
  return lang === "en"
    ? ["widgets", "launch", "events", "groups", "items"]
    : ["виджеты", "запуск", "события", "группы", "пункты"];
}

function switchEl(checked, onToggle) {
  const sw = el("div", { class: "sw" + (checked ? " on" : ""), role: "switch", tabindex: "0" });
  const toggle = () => { const on = !sw.classList.contains("on"); sw.classList.toggle("on", on); onToggle(on); };
  sw.addEventListener("click", (e) => { e.stopPropagation(); toggle(); });
  sw.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
  return sw;
}

export function render(container, ctx) {
  const { t } = ctx;
  const selection = ctx.selection && (ctx.selection === "launch" || ctx.selection === "events" || ctx.widgets.some((w) => w.id === ctx.selection))
    ? ctx.selection
    : "launch";
  ctx.setSelection(selection);

  const page = el("div", { class: "widgets-page" });
  const list = el("div", { class: "widget-list list" });
  const formBox = el("div", { class: "widget-form" });

  const entries = [
    { id: "launch", label: t("launchTitle") },
    { id: "events", label: t("eventsTitle") },
    ...ctx.widgets.map((w) => ({ id: w.id, label: (w.name || w.id) + "  " + (w.version || ""), source: w.source, isWidget: true })),
  ];
  for (const entry of entries) {
    const row = el("div", { class: "row" + (entry.id === selection ? " on" : ""), onclick: () => { ctx.setSelection(entry.id); render(container, ctx); } },
      iconEl("blocks"),
      el("div", { class: "main" }, el("div", { class: "t", text: entry.label }), entry.source ? el("div", { class: "m", text: entry.source }) : null),
      entry.isWidget ? switchEl(widgetEnabled(ctx, entry.id), (on) => setWidgetEnabled(ctx, entry.id, on)) : null);
    list.append(row);
  }
  if (ctx.widgets.length === 0) {
    list.append(el("div", { class: "empty" }, el("div", { class: "txt", text: t("emptyWidgets") })));
  }

  let getValue = null;
  let saveHandler = null;

  if (selection === "launch") {
    const form = renderLaunchForm(formBox, ctx.config.launch || {}, t);
    getValue = form.getValue;
    saveHandler = () => ctx.put({ launch: getValue() });
  } else if (selection === "events") {
    const form = renderEventsForm(formBox, ctx.config.events || {}, t);
    getValue = form.getValue;
    saveHandler = () => ctx.put({ events: getValue() });
  } else {
    const widget = ctx.widgets.find((w) => w.id === selection);
    if (widget) {
      const values = ctx.config.widgetSettings?.[widget.id] || {};
      const form = renderWidgetForm(formBox, widget.settings || [], values, t);
      getValue = form.getValue;
      saveHandler = () => ctx.put({ widgetSettings: { [widget.id]: getValue() } });
    } else {
      formBox.append(el("div", { class: "empty" }, el("div", { class: "txt", text: t("noWidgetSelected") })));
    }
  }

  const footer = saveHandler
    ? el("div", { class: "rowflex" }, el("div", { class: "sp" }), el("button", {
      class: "btn primary", type: "button", text: t("save"),
      onclick: async () => {
        ctx.onStatus(t("statusSaving"), false);
        try {
          await saveHandler();
          if (selection === "launch") ctx.config.launch = getValue();
          else if (selection === "events") ctx.config.events = getValue();
          else ctx.config.widgetSettings[selection] = getValue();
          ctx.onStatus(t("statusSaved"), false);
        } catch (err) {
          ctx.onStatus(t("statusError", err.message), true);
        }
      },
    }))
    : null;

  page.append(list, el("div", { class: "widget-form-wrap stack" }, formBox, footer));
  container.append(page);
}

function widgetEnabled(ctx, id) {
  const v = ctx.config.widgetSettings?.[id]?.enabled;
  return v !== false;
}

async function setWidgetEnabled(ctx, id, on) {
  ctx.config.widgetSettings ||= {};
  ctx.config.widgetSettings[id] = { ...(ctx.config.widgetSettings[id] || {}), enabled: on };
  try {
    await ctx.put({ widgetSettings: { [id]: ctx.config.widgetSettings[id] } });
    ctx.onStatus(ctx.t("statusSaved"), false);
  } catch (err) {
    ctx.onStatus(ctx.t("statusError", err.message), true);
  }
}
