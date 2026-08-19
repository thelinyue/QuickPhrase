import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const script = await readFile(new URL("../installer/QuickPhrase.iss", import.meta.url), "utf8");

test("installer exposes the desktop shortcut option on the finished page", () => {
  assert.doesNotMatch(script, /Name:\s*"\{autodesktop\}\\闪语";.*Tasks:\s*desktopicon/);
  assert.doesNotMatch(script, /Name:\s*"desktopicon";/);
  assert.match(script, /TNewCheckBox/);
  assert.match(script, /Caption\s*:=\s*'创建桌面快捷方式\(&D\)'/);
  assert.match(script, /Checked\s*:=\s*True/);
  assert.match(script, /wpFinished/);
});

test("installer creates or overwrites the current-user desktop shortcut", () => {
  assert.match(script, /\{autodesktop\}\\闪语\.lnk/);
  assert.match(script, /ExpandConstant\('\{app\}\\\{#AppExeName\}'\)/);
  assert.match(script, /WorkingDirectory\s*:=\s*ExpandConstant\('\{app\}'\)/);
  assert.match(script, /SetWorkingDirectory\(WorkingDirectory\)/);
  assert.match(script, /IPersistFile/);
  assert.match(script, /PersistFile\.Save\(.*True\)/);
  assert.match(script, /[\[]UninstallDelete[\]][\s\S]*Type:\s*files;\s*Name:\s*"\{autodesktop\}\\闪语\.lnk"/);
});

test("installer handles silent installation and shortcut failures without aborting", () => {
  assert.match(script, /WizardSilent/);
  assert.match(script, /ssPostInstall/);
  assert.match(script, /try[\s\S]*except/);
  assert.match(script, /桌面快捷方式创建失败，但安装已完成/);
});


