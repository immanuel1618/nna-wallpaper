import test from "node:test";
import assert from "node:assert/strict";
import { mapTabName, matchesQuery, filterSidebar, firstMatch } from "../shell-logic.js";

test("mapTabName: new page keys pass through unchanged", () => {
  for (const key of ["layout", "blocks", "appearance", "topbar", "dock", "taskbar", "cursor", "planner", "general", "about"]) {
    assert.deepEqual(mapTabName(key), { page: key });
  }
});

test("mapTabName: old greek tab names map to their pre-redesign page", () => {
  assert.deepEqual(mapTabName("alpha"), { page: "layout" });
  assert.deepEqual(mapTabName("beta"), { page: "blocks" });
  assert.deepEqual(mapTabName("gamma"), { page: "appearance" });
  assert.deepEqual(mapTabName("delta"), { page: "planner" });
  assert.deepEqual(mapTabName("epsilon"), { page: "general" });
  assert.deepEqual(mapTabName("zeta"), { page: "taskbar" });
});

test("mapTabName: launch/events deep-link into blocks with a sub-selection", () => {
  assert.deepEqual(mapTabName("launch"), { page: "blocks", selection: "launch" });
  assert.deepEqual(mapTabName("events"), { page: "blocks", selection: "events" });
});

test("mapTabName: misc aliases and unknown values", () => {
  assert.deepEqual(mapTabName("config"), { page: "general" });
  assert.deepEqual(mapTabName("nna-config"), { page: "general" });
  assert.deepEqual(mapTabName("theme"), { page: "appearance" });
  assert.deepEqual(mapTabName(undefined), { page: "layout" });
  assert.deepEqual(mapTabName("nonsense"), { page: "layout" });
});

test("matchesQuery: empty query always matches; case/whitespace-insensitive substring otherwise", () => {
  assert.equal(matchesQuery("Верхняя строка", ""), true);
  assert.equal(matchesQuery("Верхняя строка", "  "), true);
  assert.equal(matchesQuery("Top Bar", "top"), true);
  assert.equal(matchesQuery("Top Bar", "TOP"), true);
  assert.equal(matchesQuery("Top Bar", "xyz"), false);
});

const SAMPLE_ITEMS = [
  { key: "layout", title: "Layout", keywords: ["monitors", "grid"] },
  { key: "topbar", title: "Top bar", keywords: ["menu bar", "modules"] },
  { key: "cursor", title: "Cursor", keywords: ["pointer", "arrow"] },
];

test("filterSidebar: empty query returns every key", () => {
  assert.deepEqual(filterSidebar(SAMPLE_ITEMS, ""), ["layout", "topbar", "cursor"]);
});

test("filterSidebar: matches by title", () => {
  assert.deepEqual(filterSidebar(SAMPLE_ITEMS, "cursor"), ["cursor"]);
});

test("filterSidebar: matches by keyword, not just title", () => {
  assert.deepEqual(filterSidebar(SAMPLE_ITEMS, "grid"), ["layout"]);
  assert.deepEqual(filterSidebar(SAMPLE_ITEMS, "menu"), ["topbar"]);
});

test("filterSidebar: no match returns an empty list", () => {
  assert.deepEqual(filterSidebar(SAMPLE_ITEMS, "zzz"), []);
});

test("firstMatch: returns the first matching key, or null", () => {
  assert.equal(firstMatch(SAMPLE_ITEMS, "top"), "topbar");
  assert.equal(firstMatch(SAMPLE_ITEMS, "zzz"), null);
  assert.equal(firstMatch(SAMPLE_ITEMS, ""), "layout");
});
