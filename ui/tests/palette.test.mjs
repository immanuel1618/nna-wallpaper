import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../..");

// NNA1618 tokens v3 — единственные допустимые hex-цвета (регистронезависимо).
// rgba(...) с этими же цифрами (для прозрачности) допустимы — тест ищет только hex-литералы.
const ALLOWED = new Set([
  "0B0B0B", "161616", "434343", "808080", "C8C8C8", "FFFFFF",
  "5E1119", "8C1A25",
  "B8BDC2", "6E7276", "D9DEE2", "8E9498", "F2F5F7", "9AA0A6",
  "1D1D1D", "676767", "000000", "050505",
]);

const HEX_RE = /#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})\b/g;

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

function expandShortHex(hex) {
  if (hex.length === 3) {
    return hex
      .split("")
      .map((c) => c + c)
      .join("");
  }
  return hex;
}

const targets = new Set([
  ...listCssFiles(path.join(repoRoot, "ui")),
  path.join(repoRoot, "wallpaper", "nna-brand.css"),
  path.join(repoRoot, "wallpaper", "nna-blocks.css"),
  path.join(repoRoot, "wallpaper", "layout.css"),
  path.join(repoRoot, "settings", "design-system.css"),
  path.join(repoRoot, "settings", "layout.css"),
  path.join(repoRoot, "topbar", "bar.css"),
  path.join(repoRoot, "topbar", "popup", "popup.css"),
  path.join(repoRoot, "dock", "dock.css"),
]);

test("scanned file set is non-empty (sanity check for the glob above)", () => {
  assert.ok(targets.size >= 6, `expected at least 6 css files, found ${targets.size}`);
});

for (const file of targets) {
  const rel = path.relative(repoRoot, file);
  test(`palette: ${rel} uses only NNA1618 v3 hex colors`, () => {
    const css = readFileSync(file, "utf8");
    const offenders = [];
    let m;
    HEX_RE.lastIndex = 0;
    while ((m = HEX_RE.exec(css))) {
      const hex = expandShortHex(m[1]).toUpperCase();
      if (!ALLOWED.has(hex)) {
        const line = css.slice(0, m.index).split("\n").length;
        offenders.push(`#${m[1]} (as #${hex}) at line ${line}`);
      }
    }
    assert.deepEqual(offenders, [], `off-palette colors in ${rel}: ${offenders.join(", ")}`);
  });
}
