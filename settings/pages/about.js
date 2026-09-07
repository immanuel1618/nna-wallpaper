// settings/pages/about.js — "About" page (κ): mark, name, live version (GET /health), the
// NNA1618 cluster signature and the repository/releases/licenses/changelog links. New page — the
// old app didn't have one; version/links used to sit at the bottom of general.js's renderEpsilon.

import { el, groupCard } from "../dom.js";

const REPO = "https://github.com/immanuel1618/nna-wallpaper";

export function keywords(lang) {
  return lang === "en"
    ? ["about", "version", "license", "mit", "changelog", "releases", "repository"]
    : ["о программе", "версия", "лицензия", "mit", "журнал изменений", "релизы", "репозиторий"];
}

export async function render(container, ctx) {
  const { t } = ctx;
  const page = el("div", { class: "page-narrow" });
  container.append(page);

  let version = "";
  try { const h = await ctx.api("GET", "/health"); version = h.version || h.app_version || ""; } catch { /* ignore */ }

  const mark = el("div", { class: "about-mark" },
    el("img", { src: "assets/mark-white-64.png", alt: "NNA1618", width: "64", height: "64" }),
    el("div", { class: "t-heading", text: "NNA WALLPAPER" }),
    el("div", { class: "tag", text: (t("version") + " " + version).trim() }),
    el("div", { class: "t-signature about-signature", text: "NNA1618 CLUSTER" }));

  const checkBtn = el("button", {
    class: "ui-btn primary", type: "button", text: t("checkNow"),
    onclick: async () => {
      try { const r = await ctx.api("POST", "/app/check-updates"); ctx.onStatus(JSON.stringify(r), false); }
      catch (err) { ctx.onStatus(t("statusError", err.message), true); }
    },
  });

  const linkRow = (label, href) => el("a", { href, target: "_blank", rel: "noreferrer", class: "row" },
    el("div", { class: "main" },
      el("div", { class: "t", text: label }),
      el("div", { class: "m about-link-addr", text: href })));

  const links = groupCard("links", t("links"),
    el("div", { class: "stack" },
      linkRow(t("repoLink"), REPO),
      linkRow(t("releasesLink"), REPO + "/releases"),
      linkRow(t("licenses"), REPO + "/blob/main/THIRD-PARTY.md"),
      linkRow(t("changelogLink"), REPO + "/blob/main/CHANGELOG.md")));

  const license = groupCard("license", t("license"), el("div", { class: "hint", text: t("mitNote") }));

  page.append(mark, el("div", { class: "rowflex" }, checkBtn), links, license);
}
