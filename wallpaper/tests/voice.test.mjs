import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

/* NNA1618 — голос интерфейса: без длинных тире, без "!", без эмодзи/пиктограмм-символов в
   строковых литералах поверхностей (topbar/dock/widgets/wallpaper). Разделители — "·", двоеточие
   или "нет данных"; стрелки/статусы — inline SVG (см. widgets/stats/widget.js), не глифы Юникода.
   Комментарии вырезаются перед сканом — правило про голос интерфейса про то, что видит человек
   в UI, а не про комментарии разработчиков в исходниках. */

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../..");

function listJsFiles(dir) {
  const out = [];
  let entries;
  try {
    entries = readdirSync(dir);
  } catch {
    return out;
  }
  for (const name of entries) {
    const full = path.join(dir, name);
    const st = statSync(full);
    if (st.isDirectory()) {
      out.push(...listJsFiles(full));
    } else if (name.endsWith(".js")) {
      out.push(full);
    }
  }
  return out;
}

const targets = ["topbar", "dock", "widgets", "wallpaper"]
  .map((d) => path.join(repoRoot, d))
  .flatMap(listJsFiles);

test("scanned file set is non-empty (sanity check for the glob above)", () => {
  assert.ok(targets.length >= 10, `expected at least 10 js files, found ${targets.length}`);
});

/* Вырезает // и /* *\/ комментарии, извлекает содержимое '...'/"..." строковых литералов.
   Шаблонные литераты (`...`) в этой кодовой базе не используются (ES5-стиль) — в комментариях
   встречаются, в коде нет (проверено вручную), так что не разбираются отдельно. */
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
        if (src[j] === "\n") break; // непарная кавычка — не наш случай, просто останавливаемся
        buf += src[j];
        j++;
      }
      out.push({ text: buf, index: startIndex });
      i = j;
      continue;
    }
    i++;
  }
  return out;
}

// U+2190–21FF стрелки, U+2600–27BF разные символы/дингбаты, U+2B00–2BFF стрелки/звёзды,
// U+1F300–1FAFF эмодзи. "·" (U+00B7), "°" (U+00B0), типографские кавычки — вне этих диапазонов.
const PICTOGRAM_RE = /[←-⇿☀-➿⬀-⯿\u{1F300}-\u{1FAFF}]/u;

for (const file of targets) {
  const rel = path.relative(repoRoot, file);
  test(`voice: ${rel} strings have no em dash / "!" / pictograms`, () => {
    const src = readFileSync(file, "utf8");
    const literals = extractStringLiterals(src);
    const offenders = [];
    for (const lit of literals) {
      const line = src.slice(0, lit.index).split("\n").length;
      if (lit.text.indexOf("—") !== -1) offenders.push(`em dash at line ${line}: ${JSON.stringify(lit.text)}`);
      if (lit.text.indexOf("!") !== -1) offenders.push(`"!" at line ${line}: ${JSON.stringify(lit.text)}`);
      if (PICTOGRAM_RE.test(lit.text)) offenders.push(`pictogram/emoji at line ${line}: ${JSON.stringify(lit.text)}`);
    }
    assert.deepEqual(offenders, [], `voice violations in ${rel}:\n${offenders.join("\n")}`);
  });
}
