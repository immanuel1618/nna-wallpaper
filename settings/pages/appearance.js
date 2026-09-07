// settings/pages/appearance.js — "Appearance" page (γ): the geometry/perf knobs the wallpaper
// page still reads live (dim, radius, gap, pad, blur, fps cap, pause-under-fullscreen). One fixed
// brand theme (see ThemeSettings in AppSettings.cs) — no palette editor or preset picker here,
// same scope as the old renderGamma, rebuilt on NNAUI.slider/toggle per the owner's brief.

import { el, groupCard, settingRow } from "../dom.js";
import { makeScheduler } from "../api.js";

export function keywords(lang) {
  return lang === "en"
    ? ["dim", "radius", "gap", "padding", "blur", "fps", "fullscreen", "pause", "font size", "accent", "signal", "chrome"]
    : ["затемнение", "скругление", "отступ", "поля", "размытие", "fps", "полноэкранный", "пауза", "размер шрифта", "акцент", "signal", "chrome"];
}

export function render(container, ctx) {
  const { t } = ctx;
  const theme = { ...ctx.config.app.theme };
  let fpsCap = ctx.config.app.fpsCap;
  let pauseOnFullscreen = ctx.config.app.pauseOnFullscreen;

  const scheduleSave = makeScheduler(async () => {
    ctx.onStatus(t("statusSaving"), false);
    try {
      await ctx.put({ app: { theme, fpsCap, pauseOnFullscreen } });
      ctx.config.app.theme = theme; ctx.config.app.fpsCap = fpsCap; ctx.config.app.pauseOnFullscreen = pauseOnFullscreen;
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  }, 300);

  const sliderRow = (labelKey, value, min, max, step, onInput) => {
    const mount = el("div", { class: "ui-slider-mount" });
    const row = settingRow(t(labelKey), null, mount);
    window.NNAUI.slider(mount, {
      min, max, step, value,
      format: (v) => String(step < 1 ? v.toFixed(2) : Math.round(v)),
      onInput: (v) => { onInput(v); scheduleSave(); },
    });
    return row;
  };

  const geometry = groupCard("geometry", t("themeGeometry"),
    sliderRow("radius", theme.radius, 0, 55, 1, (v) => { theme.radius = v; }),
    sliderRow("gridGap", theme.gap, 0, 55, 1, (v) => { theme.gap = v; }),
    sliderRow("gridPad", theme.pad, 0, 89, 1, (v) => { theme.pad = v; }),
    sliderRow("blur", theme.blur, 0, 34, 1, (v) => { theme.blur = v; }));

  const pauseMount = el("div");
  const performance = groupCard("performance", t("performanceSection"),
    sliderRow("dim", theme.dim, 0, 1, 0.05, (v) => { theme.dim = v; }),
    sliderRow("fpsCap", fpsCap, 1, 240, 1, (v) => { fpsCap = v; }),
    settingRow(t("pauseOnFullscreen"), null, pauseMount));

  window.NNAUI.toggle(pauseMount, { checked: !!pauseOnFullscreen, onChange: (on) => { pauseOnFullscreen = on; scheduleSave(); } });

  // Owner decision D6: one theme, a few dials — font scale and accent join dim/radius/etc. as
  // knobs on the fixed brand theme rather than a palette editor. The wallpaper page (edited by
  // another agent this round) reads app.theme.fontScale/accent straight off this same object.
  const fontScaleMount = el("div");
  const accentMount = el("div");
  const knobs = groupCard("themeKnobs", t("themeKnobsSection"),
    settingRow(t("fontScale"), null, fontScaleMount),
    settingRow(t("accent"), null, accentMount));

  window.NNAUI.segmented(fontScaleMount, {
    value: Number(theme.fontScale) || 100,
    items: [{ value: 90, label: "90%" }, { value: 100, label: "100%" }, { value: 110, label: "110%" }],
    onChange: (v) => { theme.fontScale = v; scheduleSave(); },
  });
  window.NNAUI.segmented(accentMount, {
    value: theme.accent || "none",
    items: [
      { value: "none", label: t("accent_none") },
      { value: "signal", label: t("accent_signal") },
      { value: "chrome", label: t("accent_chrome") },
    ],
    onChange: (v) => { theme.accent = v; scheduleSave(); },
  });

  container.append(el("div", { class: "page-wide" }, geometry, knobs, performance));
}
