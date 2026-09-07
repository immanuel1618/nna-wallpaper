// settings/tests/voice.test.mjs — scans every string/template literal in settings/**/*.js for the
// characters banned from interface copy (docs/DESIGN-SYSTEM.md, owner's brief): long dashes (—),
// exclamation marks (!), emoji, and the pictogram glyphs ▲▼✕←↑↓ (settings/icons.js has real icons
// for all of these now — see chevron-up/down, close, arrow-left/up/down). Only literal *content*
// is checked, not comments: a `—`/`!` inside a `//` or `/* */` comment explaining "why" is fine,
// it never reaches the UI. The scanner is a small hand-rolled tokenizer (quotes, template
// literals with ${...} interpolation skipped, comments skipped) — good enough for this codebase's
// plain, no-framework JS, not a general-purpose parser.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const settingsDir = path.resolve(here, "..");

function listJsFiles(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    const st = statSync(full);
    if (st.isDirectory()) out.push(...listJsFiles(full));
    else if (name.endsWith(".js")) out.push(full);
  }
  return out;
}

const PICTOGRAMS = ["▲", "▼", "✕", "←", "↑", "↓"];
// A conservative emoji range: pictographs, symbols, dingbats, arrows/technical block used by
// emoji sets. Deliberately does not flag box-drawing or plain punctuation.
const EMOJI_RE = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}]/u;

/** Walks `src` char by char, tracking whether we're inside a comment or a string/template
 * literal, and calls `onLiteral(content, line)` for every literal's *content* (escapes resolved
 * as raw `\` + char pairs — good enough since none of the banned characters need escaping). */
function scanLiterals(src, onLiteral) {
  let i = 0;
  const n = src.length;
  let line = 1;
  while (i < n) {
    const c = src[i];
    const c2 = src[i + 1];
    if (c === "/" && c2 === "/") {
      while (i < n && src[i] !== "\n") i++;
      continue;
    }
    if (c === "/" && c2 === "*") {
      const end = src.indexOf("*/", i + 2);
      const stop = end === -1 ? n : end + 2;
      for (let k = i; k < stop; k++) if (src[k] === "\n") line++;
      i = stop;
      continue;
    }
    if (c === '"' || c === "'") {
      const quote = c;
      const startLine = line;
      let content = "";
      i++;
      while (i < n && src[i] !== quote) {
        if (src[i] === "\\") { content += src[i] + (src[i + 1] || ""); i += 2; continue; }
        if (src[i] === "\n") line++;
        content += src[i]; i++;
      }
      i++; // closing quote
      onLiteral(content, startLine);
      continue;
    }
    if (c === "`") {
      const startLine = line;
      let content = "";
      i++;
      while (i < n) {
        if (src[i] === "\\") { content += src[i] + (src[i + 1] || ""); i += 2; continue; }
        if (src[i] === "`") { i++; break; }
        if (src[i] === "$" && src[i + 1] === "{") {
          // Skip the interpolated expression (may itself contain nested braces/strings we don't
          // need to inspect — its own tokens get scanned when scanLiterals reaches that code).
          i += 2;
          let depth = 1;
          while (i < n && depth > 0) {
            if (src[i] === "{") depth++;
            else if (src[i] === "}") depth--;
            if (src[i] === "\n") line++;
            i++;
          }
          continue;
        }
        if (src[i] === "\n") line++;
        content += src[i]; i++;
      }
      onLiteral(content, startLine);
      continue;
    }
    if (c === "\n") line++;
    i++;
  }
}

function checkFile(file) {
  const src = readFileSync(file, "utf8");
  const offenders = [];
  scanLiterals(src, (content, line) => {
    if (!content) return;
    if (content.includes("—")) offenders.push(`L${line}: long dash (—) in ${JSON.stringify(content.slice(0, 60))}`);
    if (content.includes("!")) offenders.push(`L${line}: exclamation mark in ${JSON.stringify(content.slice(0, 60))}`);
    for (const p of PICTOGRAMS) {
      if (content.includes(p)) offenders.push(`L${line}: pictogram "${p}" in ${JSON.stringify(content.slice(0, 60))}`);
    }
    if (EMOJI_RE.test(content)) offenders.push(`L${line}: emoji in ${JSON.stringify(content.slice(0, 60))}`);
  });
  return offenders;
}

const files = listJsFiles(settingsDir);

test("scanned file set is non-empty (sanity check for the glob above)", () => {
  assert.ok(files.length > 10, `expected more than 10 settings/**/*.js files, found ${files.length}`);
});

for (const file of files) {
  const rel = path.relative(path.resolve(settingsDir, ".."), file);
  test(`voice: ${rel} has no —, !, emoji or ▲▼✕←↑↓ in string/template literals`, () => {
    const offenders = checkFile(file);
    assert.deepEqual(offenders, [], `${rel}:\n${offenders.join("\n")}`);
  });
}
