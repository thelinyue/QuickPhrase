import assert from 'node:assert/strict';
import path from 'node:path';
import fs from 'node:fs/promises';
import { chromium } from 'playwright';

const [installDir, output] = process.argv.slice(2);
let browser;
for (let i = 0; i < 60; i++) {
  try { browser = await chromium.connectOverCDP('http://127.0.0.1:9333'); break; }
  catch { await new Promise(resolve => setTimeout(resolve, 200)); }
}
if (!browser) throw new Error('安装后的客户端没有提供可检查的 WebView2 窗口。');
const page = browser.contexts()[0].pages()[0]; await page.locator('.main-window').waitFor();
assert.equal(await page.locator('.brand-name').innerText(), '闪语');
const result = await page.evaluate(async () => {
  const library = await window.__TAURI_INTERNALS__.invoke('get_library');
  return { dataDir: library.dataDir, phraseCount: library.phrases.length, fileCount: library.files.length };
});
const relative = path.relative(installDir, result.dataDir);
assert.ok(path.isAbsolute(relative) || relative === '..' || relative.startsWith(`..${path.sep}`));
await fs.writeFile(output, JSON.stringify(result, null, 2));
// 仅断开此检查进程，保留客户端运行，以验收升级时对运行程序的关闭处理。
process.exit(0);
