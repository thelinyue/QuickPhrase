import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

test("management build configuration targets an isolated output directory", () => {
  const config = readFileSync("vite.management.config.mjs", "utf8");
  assert.match(config, /outDir:\s*["']dist\/management["']/);
  assert.match(config, /management:\s*["']management\.html["']/);
});

test("management artifact excludes the browser prototype and wallpaper", () => {
  assert.equal(existsSync("dist/management/management.html"), true, "run npm run build:management first");
  assert.equal(existsSync("dist/management/index.html"), false);
  assert.equal(existsSync("dist/management/assets/quickphrase-wallpaper.png"), false);
});

test("management host loads each initial data source once and defers secondary status", () => {
  const source = readFileSync("src/ManagementHostApp.jsx", "utf8");
  assert.equal((source.match(/bridge\.request\("phrase\.list"/g) || []).length, 1);
  assert.equal((source.match(/bridge\.request\("category\.list"/g) || []).length, 1);
  assert.equal((source.match(/bridge\.request\("settings\.get"/g) || []).length, 1);
  assert.match(source, /loadSecondary/);
});
