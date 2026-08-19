import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

test("management build exposes phrase-library and settings surfaces", () => {
  const config = readFileSync("vite.management.config.mjs", "utf8");
  const source = readFileSync("src/ManagementHostApp.jsx", "utf8");
  assert.match(config, /management\.html/);
  assert.match(source, /phrase-library/);
  assert.match(source, /settings/);
  assert.match(source, /colorKey/);
  assert.match(source, /color-swatch/);
  assert.equal(existsSync("dist/management/management.html"), true, "run npm run build:management first");
});

test("settings surface does not expose phrase editing entry points", () => {
  const source = readFileSync("src/ManagementHostApp.jsx", "utf8");
  assert.match(source, /surface/);
  assert.match(source, /phrase-library-header/);
  assert.match(source, /settings-only-header/);
  assert.match(source, /phrase-library-rail/);
  assert.match(source, /libraryExpanded/);
  assert.match(source, /话术库/);
});

test("native launcher starts with the search box and supports double-click insertion", () => {
  const xaml = readFileSync("desktop/QuickPhrase.Desktop/LauncherWindow.xaml", "utf8");
  const code = readFileSync("desktop/QuickPhrase.Desktop/LauncherWindow.xaml.cs", "utf8");
  assert.doesNotMatch(xaml, /TextBlock Text="闪语"/);
  assert.match(xaml, /MouseDoubleClick/);
  assert.match(code, /MouseDoubleClick|DoubleClick/);
  assert.match(code, /DeliveryRequested/);
});
