import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 比较本轮未启动客户端与运行客户端时的鼠标钩子链延迟，所有窗口和数据均为隔离验收。
const repo = process.cwd(), root = path.join(repo, '.build', 'mouse-latency-' + Date.now());
await fs.mkdir(root, { recursive: true });
const run = promisify(execFile), delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const samples = [], results = [], output = path.join(root, 'native.json'), controls = path.join(root, 'control.json');
let app, host, browser, page, hostHandle, mainHandle, nonce = 0;
const poll = async (fn, label) => { for (let i = 0; i < 100; i++) { const value = await fn(); if (value) return value; await delay(40); } throw new Error('等待超时：' + label); };
const read = async () => { try { return JSON.parse(await fs.readFile(output, 'utf8')); } catch { return null; } };
const ps = args => run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: [mainHandle].filter(Boolean), ...args };
  const temp = controls + '.tmp'; await fs.writeFile(temp, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temp, controls); break; } catch (error) { if (i >= 20 || !['EBUSY', 'EPERM', 'EACCES'].includes(error.code)) throw error; await delay(20); } }
  return poll(async () => { const state = await read(); return state?.nonce === request.nonce && state; }, '隔离窗口响应');
};
const measure = async name => {
  const value = JSON.parse((await ps(['-File', path.join(repo, 'tests/mouse-latency.ps1')])).stdout);
  samples.push({ name, ...value }); console.log(name + '：' + JSON.stringify(value)); return value;
};
try {
  const fixture = path.join(root, 'ShanyuDockHost.exe');
  await ps(['-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture]);
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' });
  hostHandle = (await poll(read, '隔离吸附窗口')).hwnd;
  const withoutApp = await measure('未启动客户端');
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(root, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); return null; } }, '隔离客户端');
  page = await poll(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash), '主窗口WebView');
  await page.locator('.main-window').waitFor();
  const library = await invoke('get_library'); assert.equal(library.dataDir, path.join(root, 'data'));
  mainHandle = JSON.parse((await ps(['-File', path.join(repo, 'tests/native-driver.ps1'), '-ProcessId', String(app.pid), '-Title', '闪语'])).stdout).handle;
  const settings = { ...library.settings, startup: false, dock: false, dockCommon: false, dockSearch: false, autoHide: false, pinned: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' };
  await poll(async () => { try { await invoke('save_settings', { settings }); return true; } catch (error) { if (String(error).includes('尚未启动')) return false; throw error; } }, '监听服务');
  await control('focus'); await measure('客户端关闭吸附');
  await invoke('save_settings', { settings: { ...settings, dock: true, dockCommon: true } });
  await control('focus');
  await poll(async () => { const state = await control('inspect'), main = state.windows.find(w => w.handle === mainHandle); return state.foreground === hostHandle && main.visible && main.frame.left === state.windows[0].frame.right; }, '实际吸附生效');
  await measure('客户端正在吸附');
  await Promise.all([
    measure('目标连续移动缩放'),
    (async () => { for (let i = 0; i < 40; i++) { await control('move', { x: 500 + i % 5 * 20, y: 100 + i % 3 * 20, width: 600 + i % 2 * 60, height: 520 + i % 3 * 20 }); await delay(60); } })(),
  ]);
  // 普通性能回归使用同次运行的系统测量作为参照，允许8ms调度波动，拒绝每轮30ms阻塞。
  const limit = Math.max(10, withoutApp.p95Ms + 8);
  for (const sample of samples.slice(1)) results.push({ name: sample.name, passed: sample.p95Ms <= limit, p95Ms: sample.p95Ms, limitMs: limit });
  assert.ok(results.every(result => result.passed), '客户端不得让鼠标钩子链出现固定周期的额外等待');
  await invoke('window_action', { action: 'exit' }); await poll(() => app.exitCode !== null, '隔离客户端退出');
  const afterExit = await measure('客户端退出');
  results.push({ name: '客户端退出', passed: afterExit.p95Ms <= limit, p95Ms: afterExit.p95Ms, limitMs: limit });
  assert.ok(results.at(-1).passed, '客户端退出后不得残留鼠标延迟');
} catch (error) {
  results.push({ name: '鼠标延迟检查', passed: false, error: String(error) }); console.error(error); process.exitCode = 1;
} finally {
  if (app?.exitCode === null) { await invoke('window_action', { action: 'exit' }).catch(() => {}); await poll(() => app.exitCode !== null, '隔离客户端退出').catch(() => app.kill()); }
  await browser?.close().catch(() => {});
  if (host?.exitCode === null) { await control('close').catch(() => {}); await poll(() => host.exitCode !== null, '隔离宿主退出').catch(() => host.kill()); }
  await fs.writeFile(path.join(root, 'report.json'), JSON.stringify({ date: new Date().toISOString(), samples, results, scope: '每组120次1像素往返鼠标移动，不点击、不按键、不访问剪贴板、不操作真实聊天窗口' }, null, 2));
  console.log('验收记录：' + root);
}
