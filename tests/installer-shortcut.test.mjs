import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const script = await readFile(new URL("../installer/QuickPhrase.iss", import.meta.url), "utf8");

function section(name) {
  const match = script.match(new RegExp(`\\[${name}\\]([\\s\\S]*?)(?=\\n\\[|$)`, "i"));
  assert.ok(match, `安装器脚本缺少 [${name}] 节。`);
  return match[1];
}


test("installer creates the desktop shortcut through native tasks and application icons", () => {
  const tasks = section("Tasks");

  assert.match(tasks, /Name:\s*"desktopicon";\s*Description:\s*"创建桌面快捷方式\(&D\)"/);
  assert.doesNotMatch(tasks, /Name:\s*"desktopicon";[^\r\n]*\bunchecked\b/i);
  const icons = section("Icons");
  assert.match(
    icons,
    /Name:\s*"\{group\}\\闪语";\s*Filename:\s*"\{app\}\\\{#AppExeName\}";\s*WorkingDir:\s*"\{app\}";\s*IconFilename:\s*"\{app\}\\\{#AppExeName\}";\s*IconIndex:\s*0/,
  );
  assert.match(
    icons,
    /Name:\s*"\{autodesktop\}\\闪语";\s*Filename:\s*"\{app\}\\\{#AppExeName\}";\s*WorkingDir:\s*"\{app\}";\s*IconFilename:\s*"\{app\}\\\{#AppExeName\}";\s*IconIndex:\s*0;\s*Tasks:\s*desktopicon/,
  );
});

test("installer offers a default checked launch option on the finished page", () => {
  const run = section("Run");

  assert.match(
    run,
    /Filename:\s*"\{app\}\\\{#AppExeName\}";\s*Description:\s*"打开闪语\(&L\)";\s*WorkingDir:\s*"\{app\}";\s*Flags:\s*nowait\s+postinstall\s+skipifsilent/,
  );
  assert.doesNotMatch(run, /Filename:[^\r\n]*打开闪语[^\r\n]*\bunchecked\b/i);
});

test("interactive uninstall offers opt-in local data cleanup and silent uninstall preserves data", () => {
  const code = section("Code");

  assert.match(code, /TNewCheckBox\.Create\(Form\)/);
  assert.match(code, /Caption\s*:=\s*'删除本地数据和日志（不可恢复）'/);
  assert.match(code, /CleanupDataCheckBox\.Checked\s*:=\s*False/);
  assert.match(code, /DeleteLocalDataRequested\s*:=\s*False/);
  assert.match(code, /if\s+UninstallSilent\s+then[\s\S]*?Result\s*:=\s*True/);
  assert.match(code, /ExpandConstant\('\{localappdata\}\\QuickPhrase'\)/);
  assert.match(code, /if\s+not\s+IsExpectedUserDataRoot\(DataRoot\)\s+then/);
  assert.match(code, /DelTree\(DataRoot,\s*True,\s*True,\s*True\)/);
  assert.match(code, /DeleteLocalDataIfRequested;/);
});

test("installer no longer contains the former custom desktop shortcut implementation", () => {
  assert.doesNotMatch(script, /DesktopShortcutCheckBox|CreateDesktopShortcut|CreateComObject|IShellLinkW|IPersistFile/);
  assert.doesNotMatch(script, /\[UninstallDelete\][\s\S]*\{autodesktop\}\\闪语\.lnk/i);
});


