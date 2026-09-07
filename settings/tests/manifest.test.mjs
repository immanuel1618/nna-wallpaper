// settings/tests/manifest.test.mjs — validates widgets/<id>/widget.json against the stage 5
// round 2 manifest additions (owner decision D17: block cards with previews and grouped, plain
// language settings pages). Every built-in widget (the internal "_test-page" fixture is excluded)
// must carry description/icon/groups, every settings[] entry must belong to exactly one group and
// carry a bilingual label/help, and defaultSize must fit within minSize.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../..");
const widgetsDir = path.join(repoRoot, "widgets");

function listWidgetIds() {
  return readdirSync(widgetsDir)
    .filter((name) => !name.startsWith("_"))
    .filter((name) => statSync(path.join(widgetsDir, name)).isDirectory())
    .filter((name) => {
      try { statSync(path.join(widgetsDir, name, "widget.json")); return true; } catch { return false; }
    })
    .sort();
}

function loadManifest(id) {
  return JSON.parse(readFileSync(path.join(widgetsDir, id, "widget.json"), "utf8"));
}

function isBilingual(value) {
  return !!value && typeof value === "object" && typeof value.ru === "string" && value.ru.trim() !== ""
    && typeof value.en === "string" && value.en.trim() !== "";
}

const ids = listWidgetIds();

test("at least the 10 built-in widgets are present (sanity check for the glob above)", () => {
  assert.ok(ids.length >= 10, `expected at least 10 widgets, found ${ids.length}: ${ids.join(", ")}`);
});

for (const id of ids) {
  const manifest = loadManifest(id);

  test(`${id}: has a bilingual description`, () => {
    assert.ok(isBilingual(manifest.description), `widgets/${id}/widget.json: description must be {ru, en}`);
  });

  test(`${id}: has an icon name`, () => {
    assert.equal(typeof manifest.icon, "string");
    assert.ok(manifest.icon.trim() !== "", `widgets/${id}/widget.json: icon must be a non-empty string`);
  });

  test(`${id}: has a groups array`, () => {
    assert.ok(Array.isArray(manifest.groups), `widgets/${id}/widget.json: groups must be an array`);
  });

  test(`${id}: every group has an id and a bilingual label`, () => {
    for (const g of manifest.groups || []) {
      assert.equal(typeof g.id, "string");
      assert.ok(g.id.trim() !== "", `widgets/${id}/widget.json: group is missing an id`);
      assert.ok(isBilingual(g.label), `widgets/${id}/widget.json: group "${g.id}" needs a bilingual label`);
      assert.ok(Array.isArray(g.keys), `widgets/${id}/widget.json: group "${g.id}" needs a keys array`);
    }
  });

  test(`${id}: every setting belongs to exactly one group`, () => {
    const settingKeys = (manifest.settings || []).map((f) => f.key);
    const groups = manifest.groups || [];
    for (const key of settingKeys) {
      const owners = groups.filter((g) => (g.keys || []).includes(key));
      assert.equal(owners.length, 1, `widgets/${id}/widget.json: setting "${key}" must be in exactly one group (found in ${owners.length})`);
    }
    // and every key a group claims must actually exist on a setting (catches typos/renames)
    for (const g of groups) {
      for (const key of g.keys || []) {
        assert.ok(settingKeys.includes(key), `widgets/${id}/widget.json: group "${g.id}" references unknown setting "${key}"`);
      }
    }
  });

  test(`${id}: every setting has a bilingual label and help`, () => {
    for (const field of manifest.settings || []) {
      assert.ok(isBilingual(field.label), `widgets/${id}/widget.json: setting "${field.key}" needs a bilingual label`);
      assert.ok(isBilingual(field.help), `widgets/${id}/widget.json: setting "${field.key}" needs a bilingual help text`);
    }
  });

  test(`${id}: defaultSize fits within minSize`, () => {
    const def = manifest.defaultSize;
    const min = manifest.minSize;
    assert.ok(def && min, `widgets/${id}/widget.json: defaultSize and minSize are required`);
    assert.ok(def.cols >= min.cols, `widgets/${id}/widget.json: defaultSize.cols (${def.cols}) < minSize.cols (${min.cols})`);
    assert.ok(def.rows >= min.rows, `widgets/${id}/widget.json: defaultSize.rows (${def.rows}) < minSize.rows (${min.rows})`);
  });
}
