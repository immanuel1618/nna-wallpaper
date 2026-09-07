// settings/pages/cursor.js — "Cursor" page (η): the three brand cursor schemes (GET/POST
// /cursor/*, see CursorService.cs and docs/CURSORS.md). Each card shows the real arrow/hand SVGs
// copied to settings/assets/cursors/<variant>/ at build time (brand/cursors/<variant>/*.svg is
// the design source; presets/cursors/<variant>/*.cur is what actually gets installed — SVGs are
// only used here as a preview, .cur files can't be shown in an <img>).

import { el, groupCard, settingRow } from "../dom.js";

const VARIANTS = ["mark", "line", "mono"];
const SIZES = [32, 48, 64];

export function keywords(lang) {
  return lang === "en"
    ? ["cursor", "pointer", "variant", "apply", "reset", "backup", "size"]
    : ["курсор", "указатель", "вариант", "применить", "сброс", "бэкап", "размер"];
}

function localize(value, lang) {
  if (!value) return "";
  if (typeof value === "string") return value;
  return value[lang] || value.ru || value.en || "";
}

export async function render(container, ctx) {
  const { t, lang } = ctx;
  const page = el("div", { class: "page-wide" });
  container.append(page);

  let status = null;
  try { status = await ctx.api("GET", "/cursor/status"); } catch { status = null; }

  if (!status) {
    page.append(el("div", { class: "empty" }, el("div", { class: "txt", text: t("cursorUnavailable") })));
    return;
  }

  let selected = status.active || "mark";
  let selectedSize = SIZES.includes(status.size) ? status.size : 32;

  const cardsBox = el("div", { class: "cursor-cards" });
  const variantsGroup = groupCard("variants", t("cursorVariantsSection"), cardsBox);

  function drawCards() {
    cardsBox.innerHTML = "";
    for (const id of VARIANTS) {
      const meta = (status.variants || []).find((v) => v.id === id) || { id };
      const isActive = status.active === id;
      const card = el("div", { class: "cursor-card" + (selected === id ? " sel" : "") + (isActive ? " active" : ""), onclick: () => { selected = id; drawCards(); } },
        el("div", { class: "cursor-preview" },
          el("img", { src: `assets/cursors/${id}/arrow.svg`, alt: id, width: "32", height: "32" }),
          el("img", { src: `assets/cursors/${id}/hand.svg`, alt: id, width: "32", height: "32" })),
        el("div", { class: "t", text: localize(meta.name, lang) || id }),
        meta.description ? el("div", { class: "m", text: meta.description }) : null,
        isActive ? el("div", { class: "tag", text: t("cursorActiveTag") }) : null);
      cardsBox.append(card);
    }
  }
  drawCards();

  const sizeMount = el("div");
  const applyBtn = el("button", { class: "ui-btn primary", type: "button", text: t("cursorApply") });
  const resetBtn = el("button", { class: "ui-btn", type: "button", text: t("cursorReset"), disabled: !status.backup });
  const backupNote = el("div", { class: "hint", text: status.backup ? t("cursorBackupPresent") : t("cursorBackupAbsent") });

  const controls = groupCard("apply", t("cursorSizeSection"),
    settingRow(t("cursorSize"), null, sizeMount),
    el("div", { class: "rowflex" }, applyBtn, resetBtn),
    backupNote);

  window.NNAUI.segmented(sizeMount, {
    value: String(selectedSize),
    items: SIZES.map((s) => ({ value: String(s), label: String(s) })),
    onChange: (v) => { selectedSize = Number(v); },
  });

  applyBtn.addEventListener("click", async () => {
    ctx.onStatus(t("statusSaving"), false);
    try {
      const r = await ctx.api("POST", "/cursor/apply", { variant: selected, size: selectedSize });
      if (r.ok === false) throw new Error(r.error || "apply failed");
      status.active = selected;
      status.size = selectedSize;
      status.backup = true;
      resetBtn.disabled = false;
      backupNote.textContent = t("cursorBackupPresent");
      drawCards();
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  });

  resetBtn.addEventListener("click", async () => {
    ctx.onStatus(t("statusSaving"), false);
    try {
      const r = await ctx.api("POST", "/cursor/reset");
      if (r.ok === false) throw new Error(r.error || "reset failed");
      status.active = null;
      status.backup = false;
      resetBtn.disabled = true;
      backupNote.textContent = t("cursorBackupAbsent");
      drawCards();
      ctx.onStatus(t("statusSaved"), false);
    } catch (err) { ctx.onStatus(t("statusError", err.message), true); }
  });

  page.append(variantsGroup, controls);
}
