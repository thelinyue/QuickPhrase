import test from "node:test";
import assert from "node:assert/strict";
import {
  hasUnsavedChanges,
  restoreLibraryContextAfterSave,
  nextSelectionAfterDelete,
} from "../src/managementNavigation.js";

test("detects unsaved editor or settings changes from baseline", () => {
  const baseline = { title: "A", enabled: false };
  assert.equal(hasUnsavedChanges(baseline, { title: "A", enabled: false }), false);
  assert.equal(hasUnsavedChanges(baseline, { title: "B", enabled: false }), true);
});

test("restores library context when a saved phrase still matches it", () => {
  const context = { activeFilter: "favorite", query: "网络", selectedId: "phrase-1", scrollTop: 240 };
  const restored = restoreLibraryContextAfterSave(context, { id: "phrase-1", favorite: true, title: "网络回复" });
  assert.deepEqual(restored, { ...context, selectedId: "phrase-1" });
});

test("returns to all phrases when a new saved phrase is outside the old search context", () => {
  const context = { activeFilter: "favorite", query: "旧词", selectedId: "phrase-old", scrollTop: 240 };
  const restored = restoreLibraryContextAfterSave(context, { id: "phrase-new", favorite: false, title: "新的标准回复" }, { isVisible: false, isNew: true });
  assert.deepEqual(restored, { activeFilter: "all", query: "", selectedId: "phrase-new", scrollTop: 0 });
});

test("selects the adjacent phrase after deleting the current one", () => {
  assert.equal(nextSelectionAfterDelete(["a", "b", "c"], "b"), "c");
  assert.equal(nextSelectionAfterDelete(["a", "b", "c"], "c"), "b");
  assert.equal(nextSelectionAfterDelete(["a"], "a"), null);
});
