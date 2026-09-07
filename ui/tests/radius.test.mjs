import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../..");

// NNA1618 — единственные допустимые пиксельные значения border-radius (ряд 5·8·13·21·111,
// см. ui/tokens.css --r-card/--r-media/--r-pill и docs/DESIGN-SYSTEM.md). 0 (без скругления)
// и 50% (круг: аватары, точки, ручки) — тоже допустимы, это не часть шкалы, а "нет скругления"/
// "полный круг". var(--radius) — единственное исключение: единственный ЖИВОЙ пользовательский
// параметр (theme.radius из настроек обоев, wallpaper/layout.js setThemeVars), не часть шкалы
// дизайн-системы. Любой var(--r-*) или var(--sp-*) допустим без проверки числового значения —
// это уже токены той же шкалы.
const ALLOWED_PX = new Set([0, 5, 8, 13, 21, 111]);
const ALLOWED_VAR_RE = /^var\(--(r-[a-z0-9-]+|sp-\d+|radius)\)$/;

function listCssFiles(dir) {
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
      out.push(...listCssFiles(full));
    } else if (name.endsWith(".css")) {
      out.push(full);
    }
  }
  return out;
}

const targets = new Set([
  ...listCssFiles(path.join(repoRoot, "ui")),
  ...listCssFiles(path.join(repoRoot, "settings")),
  ...listCssFiles(path.join(repoRoot, "topbar")),
  ...listCssFiles(path.join(repoRoot, "dock")),
  ...listCssFiles(path.join(repoRoot, "wallpaper")),
]);

test("scanned file set is non-empty (sanity check for the glob above)", () => {
  assert.ok(targets.size >= 6, `expected at least 6 css files, found ${targets.size}`);
});

// Разбирает значение border-radius (может быть шорткатом с несколькими сегментами: "0 0 5px 0")
// на отдельные "слова" — числа/px-значения, var(...), проценты, ключевые слова.
function tokenizeRadiusValue(raw) {
  const value = raw.trim();
  // var(...) может содержать запятую-fallback — считаем его одним токеном целиком.
  const tokens = [];
  let rest = value;
  while (rest.length) {
    rest = rest.trim();
    if (!rest) break;
    if (rest.startsWith("var(")) {
      const close = rest.indexOf(")");
      tokens.push(rest.slice(0, close + 1));
      rest = rest.slice(close + 1);
    } else {
      const m = /^\S+/.exec(rest);
      if (!m) break;
      tokens.push(m[0]);
      rest = rest.slice(m[0].length);
    }
  }
  return tokens;
}

function isAllowedToken(tok) {
  if (tok === "0" || tok === "50%" || tok === "inherit") return true;
  if (ALLOWED_VAR_RE.test(tok)) return true;
  const m = /^(-?\d+(?:\.\d+)?)px$/.exec(tok);
  if (m) return ALLOWED_PX.has(Number(m[1]));
  return false;
}

const RADIUS_RE = /border-radius\s*:\s*([^;{}]+);/g;

for (const file of targets) {
  const rel = path.relative(repoRoot, file);
  test(`radius: ${rel} uses only the 5/8/13/21/111 scale (or var(--r-*)/var(--sp-*)/var(--radius))`, () => {
    const css = readFileSync(file, "utf8");
    const offenders = [];
    let m;
    RADIUS_RE.lastIndex = 0;
    while ((m = RADIUS_RE.exec(css))) {
      const tokens = tokenizeRadiusValue(m[1]);
      const bad = tokens.filter((t) => !isAllowedToken(t));
      if (bad.length) {
        const line = css.slice(0, m.index).split("\n").length;
        offenders.push(`"${m[1].trim()}" at line ${line} (bad: ${bad.join(", ")})`);
      }
    }
    assert.deepEqual(offenders, [], `off-scale border-radius in ${rel}: ${offenders.join("; ")}`);
  });
}
