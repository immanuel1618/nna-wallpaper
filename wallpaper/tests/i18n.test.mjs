// node --test wallpaper/tests/i18n.test.mjs
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../..");
const require = createRequire(import.meta.url);

/* --- 1. словарь: ru и en несут один и тот же набор ключей ------------------------------- */

const I18N = require(path.join(repoRoot, "wallpaper/i18n.js"));

test("i18n dict: ru and en have the same key set", () => {
  const ruKeys = Object.keys(I18N.dict.ru).sort();
  const enKeys = Object.keys(I18N.dict.en).sort();
  const onlyRu = ruKeys.filter((k) => !enKeys.includes(k));
  const onlyEn = enKeys.filter((k) => !ruKeys.includes(k));
  assert.deepEqual(onlyRu, [], `keys only in ru: ${onlyRu.join(", ")}`);
  assert.deepEqual(onlyEn, [], `keys only in en: ${onlyEn.join(", ")}`);
  assert.ok(ruKeys.length > 20, `expected a grown dictionary, found ${ruKeys.length} keys`);
});

test("i18n dict: weatherCodes table has the same numeric codes in ru and en", () => {
  const ruCodes = Object.keys(I18N.dict.ru.weatherCodes).sort();
  const enCodes = Object.keys(I18N.dict.en.weatherCodes).sort();
  assert.deepEqual(ruCodes, enCodes);
  assert.ok(ruCodes.length >= 20, `expected the full WMO code table, found ${ruCodes.length}`);
});

test("i18n dict: days has 7 entries, months has 12, in both languages", () => {
  for (const l of ["ru", "en"]) {
    assert.equal(I18N.dict[l].days.length, 7, `${l}.days`);
    assert.equal(I18N.dict[l].months.length, 12, `${l}.months`);
  }
});

/* --- 2. widget.js: не осталось служебных английских слов мимо N.t(key, fallback) ---------
   Список слов — те, что реально встречались как хардкод до локализации (см. stage report).
   Обращение и через window.NNA.t(...), и через локальный алиас text(key, fallback) внутри
   widget-функции (см. widgets/focus/widget.js: function text(key, fallback) { return L[key] ||
   N.t(key, fallback); } — L даёт кастомный оверрайд, N.t — словарь) одинаково допустимы: важно,
   чтобы слово само по себе было ключом словаря, а не голым литералом где-то ещё в коде. */

const WIDGETS_DIR = path.join(repoRoot, "widgets");
const widgetFiles = readdirSync(WIDGETS_DIR)
  .filter((name) => name !== "_test-page")
  .map((name) => path.join(WIDGETS_DIR, name, "widget.js"))
  .filter((full) => {
    try {
      readFileSync(full);
      return true;
    } catch {
      return false;
    }
  });

test("scanned widget.js set is non-empty (sanity check for the glob above)", () => {
  assert.ok(widgetFiles.length >= 8, `expected at least 8 widget.js files, found ${widgetFiles.length}`);
});

// та же логика извлечения строковых литералов, что в voice.test.mjs (комментарии вырезаются,
// шаблонные литераты `...` в этой кодовой базе не используются).
function extractStringLiterals(src) {
  const out = [];
  const n = src.length;
  let i = 0;
  while (i < n) {
    const c = src[i], c2 = src[i + 1];
    if (c === "/" && c2 === "/") {
      let end = src.indexOf("\n", i);
      i = end === -1 ? n : end;
      continue;
    }
    if (c === "/" && c2 === "*") {
      let end = src.indexOf("*/", i + 2);
      i = end === -1 ? n : end + 2;
      continue;
    }
    if (c === "'" || c === '"') {
      const quote = c;
      const startIndex = i;
      let j = i + 1;
      let buf = "";
      while (j < n) {
        if (src[j] === "\\") { buf += src[j] + (src[j + 1] || ""); j += 2; continue; }
        if (src[j] === quote) { j++; break; }
        if (src[j] === "\n") break;
        buf += src[j];
        j++;
      }
      out.push({ text: buf, index: startIndex, endIndex: j });
      i = j;
      continue;
    }
    i++;
  }
  return out;
}

// слово из списка засчитывается только если литерал — первый ИЛИ второй (fallback) аргумент
// вызова N.t(key, fallback) / локального алиаса text(key, fallback) (см. widgets/focus/widget.js:
// function text(key, fallback) { return L[key] || N.t(key, fallback); } — L даёт кастомный
// оверрайд, N.t — словарь). Любое другое место — нарушение: слово ушло мимо словаря.
const QUOTED = "(?:'(?:[^'\\\\]|\\\\.)*'|\"(?:[^\"\\\\]|\\\\.)*\")";
const CALL_TAIL_RE = new RegExp("(?:^|[^A-Za-z0-9_$])(?:t|text)\\(\\s*(?:" + QUOTED + "\\s*,\\s*)?$");
const FORBIDDEN = /\b(WORK|CHILL|CYCLES|PRESET|STATS|RESET|DAILY|WAKE|SLEEP|CLEAR|OVERCAST|NODES|EDGES)\b/;

for (const file of widgetFiles) {
  const rel = path.relative(repoRoot, file);
  test(`i18n: ${rel} has no service-label literals outside N.t(...)/text(...)`, () => {
    const src = readFileSync(file, "utf8");
    const literals = extractStringLiterals(src);
    const offenders = [];
    for (const lit of literals) {
      if (!FORBIDDEN.test(lit.text)) continue;
      const before = src.slice(Math.max(0, lit.index - 80), lit.index);
      if (CALL_TAIL_RE.test(before)) continue; // N.t('key', 'WORK') / text('key', 'WORK') — allowed
      const line = src.slice(0, lit.index).split("\n").length;
      offenders.push(`line ${line}: ${JSON.stringify(lit.text)}`);
    }
    assert.deepEqual(offenders, [], `service-label literals outside N.t()/text() in ${rel}:\n${offenders.join("\n")}`);
  });
}
