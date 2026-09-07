import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiDir = path.resolve(here, "..");
const tokensPath = path.join(uiDir, "tokens.css");
const css = readFileSync(tokensPath, "utf8");

const PALETTE_HEX = [
  "0B0B0B", "161616", "434343", "808080", "C8C8C8", "FFFFFF",
  "5E1119", "8C1A25", "B8BDC2", "6E7276", "D9DEE2", "8E9498",
  "F2F5F7", "9AA0A6",
];

test("ui/tokens.css contains all 9 v3 core colors", () => {
  for (const hex of PALETTE_HEX.slice(0, 9)) {
    assert.ok(css.toUpperCase().includes("#" + hex), `missing color #${hex}`);
  }
});

test("ui/tokens.css contains all v3 palette colors (including chrome gradient stops)", () => {
  for (const hex of PALETTE_HEX) {
    assert.ok(css.toUpperCase().includes("#" + hex), `missing color #${hex}`);
  }
});

test("ui/tokens.css contains the full font-size row 10..178", () => {
  const sizes = [10, 13, 16, 20, 26, 33, 42, 53, 68, 86, 110, 140, 178];
  for (const s of sizes) {
    assert.ok(
      css.includes(`--fs-${s}: ${s}px`),
      `missing --fs-${s}`
    );
  }
});

test("ui/tokens.css declares Roboto Flex and JetBrains Mono @font-face rules", () => {
  assert.match(css, /@font-face\s*{[^}]*font-family:\s*"Roboto Flex"/);
  assert.match(css, /@font-face\s*{[^}]*font-family:\s*"JetBrains Mono"[^}]*font-weight:\s*400/);
  assert.match(css, /@font-face\s*{[^}]*font-family:\s*"JetBrains Mono"[^}]*font-weight:\s*500/);
  assert.match(css, /font-weight:\s*100 1000/);
  assert.match(css, /font-stretch:\s*25% 151%/);
});

test("font files referenced from ui/tokens.css exist alongside their OFL license", () => {
  const fontsDir = path.join(uiDir, "fonts");
  const files = [
    "RobotoFlex-Variable.ttf",
    "JetBrainsMono-Regular.ttf",
    "JetBrainsMono-Medium.ttf",
  ];
  for (const f of files) {
    assert.ok(existsSync(path.join(fontsDir, f)), `missing font file ${f}`);
  }
  assert.ok(existsSync(path.join(fontsDir, "OFL-RobotoFlex.txt")), "missing OFL-RobotoFlex.txt");
  assert.ok(existsSync(path.join(fontsDir, "OFL-JetBrainsMono.txt")), "missing OFL-JetBrainsMono.txt");
});

test("ui/tokens.css defines the legacy aliases used by wallpaper/settings/topbar", () => {
  const names = [
    "--bg-page", "--bg-surface", "--fg", "--fg-body", "--fg-muted", "--fg-ghost",
    "--border", "--border-swatch", "--btn", "--btn-hover", "--glass",
    "--font-display", "--radius", "--gap", "--pad",
    "--bg", "--raised", "--raised-2", "--body", "--muted", "--faint", "--ghost",
    "--line", "--line-strong", "--chip", "--chip-hi", "--display", "--greek",
    "--mono", "--text", "--r-block", "--r-card", "--r-pill", "--r-media",
  ];
  for (const name of names) {
    const re = new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s*:");
    assert.ok(re.test(css), `missing alias ${name}`);
  }
});
