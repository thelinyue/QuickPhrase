import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 仅使用隔离 Debug 客户端和宿主；真实拖动、悬停，不触碰聊天内容、剪贴板或用户配置。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'edge-hide-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
let app, host, browser, page, handles = {}, nonce = 0;
const read = async () => { try { return JSON.parse(await fs.readFile(output, 'utf8')); } catch { return null; } };
const poll = async (fn, label) => { for (let i = 0; i < 160; i++) { const value = await fn(); if (value) return value; await delay(35); } throw new Error('等待超时：' + label); };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: Object.values(handles), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (e) { if (i > 20 || !['EACCES', 'EPERM', 'EBUSY'].includes(e.code)) throw e; await delay(20); } }
  return poll(async () => { const s = await read(); if (s?.nonce === request.nonce && ['pointer', 'drag'].includes(command)) assert.equal(s.pointerError, 0, '系统鼠标操作失败'); return s?.nonce === request.nonce && s; }, command);
};
const inspect = () => control('inspect');
const main = state => state.windows.find(w => w.handle === handles.main);
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
const check = async (name, fn) => { try { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); } catch (e) { results.push({ name, passed: false, error: String(e) }); throw e; } };
const save = async patch => { const { settings } = await invoke('get_library'); await invoke('save_settings', { settings: { ...settings, ...patch } }); };
const away = async () => { const w = main(await inspect()).work; await control('pointer', { x: Math.round((w.left + w.right) / 2), y: w.top + 30 }); };
const atEdge = async side => {
  const m = main(await inspect());
  await control('pointer', { x: side === 'right' ? m.work.right - 1 : m.work.left + 1, y: m.rect.top + 80 });
};
const hidden = async side => poll(async () => {
  const m = main(await inspect()); const strip = Math.round(2 * m.dpi / 96);
  return Math.abs((side === 'right' ? m.work.right - m.clip.left : m.clip.right - m.work.left) - strip) <= 1 && m;
}, side + '收起');
const expanded = async side => poll(async () => {
  const m = main(await inspect()); return Math.abs(side === 'right' ? m.frame.right - m.work.right : m.frame.left - m.work.left) <= 1 && m;
}, side + '展开');
const drag = async side => {
  await invoke('window_action', { action: 'show' }); await delay(250);
  await control('raise', { handle: handles.main });
  await control('focus', { handle: handles.main });
  const m = main(await inspect()), box = await page.locator('.main-titlebar .titlebar-spacer').boundingBox(), scale = m.dpi / 96;
  const fromX = Math.round(m.client.x + (box.x + box.width / 2) * scale), fromY = Math.round(m.client.y + (box.y + box.height / 2) * scale);
  const dx = side === 'right' ? m.work.right - m.frame.right : m.work.left - m.frame.left;
  await control('pointer', { x: fromX, y: fromY });
  assert.equal((await inspect()).windowAtCursor, handles.main, '拖动起点必须命中隔离主窗口');
  await control('drag', { handle: handles.main, fromX, fromY, x: fromX + dx, y: fromY });
  snapshots.push({ name: 'drag-' + side, state: await inspect() });
  await delay(180); await away();
};
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' }); await poll(read, '宿主');
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('客户端提前退出'); return null; } }, 'WebView');
  page = await poll(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash), '主页面'); await page.locator('.main-window').waitFor();
  handles.main = (await native(['-ProcessId', String(app.pid), '-Title', '闪语'])).handle;
  await save({ dock: false, dockCommon: false, dockSearch: false, pinned: true }); await invoke('window_action', { action: 'show' }); await delay(300);
  await check('新装默认开启且首次独立显示保留12DIP边距', async () => {
    const { settings } = await invoke('get_library'); assert.equal(settings.edgeAutoHide, true); assert.equal(settings.edgeRevealDelay, 500);
    const m = main(await inspect()); snapshots.push({ name: 'initial', m }); assert.ok(m.work.right - m.frame.right >= 10 * m.dpi / 96);
  });
  for (const side of ['right', 'left']) {
    await check(side + '真实拖动后仅留2DIP，短暂经过取消，连续悬停展开且宽度不变', async () => {
      const width = main(await inspect()).rect.right - main(await inspect()).rect.left;
      await drag(side); const h = await hidden(side); assert.equal(h.rect.right - h.rect.left, width);
      const foreground = (await inspect()).foreground;
      await atEdge(side); await delay(200); await away(); await delay(550); await hidden(side);
      await atEdge(side); await delay(200); const early = main(await inspect()); assert.deepEqual(early.rect, h.rect);
      const e = await expanded(side); assert.equal(e.rect.right - e.rect.left, width); assert.equal((await inspect()).foreground, foreground);
      snapshots.push({ name: side, hidden: h, expanded: e }); await away(); await hidden(side);
    });
  }
  await check('0和1秒等待均生效，显式唤起及输入期间保持展开', async () => {
    await save({ edgeRevealDelay: 1000 }); await atEdge('left'); await delay(650); await hidden('left'); await expanded('left');
    await away(); await hidden('left'); await save({ edgeRevealDelay: 0 }); await atEdge('left'); await expanded('left');
    await away(); await hidden('left'); await invoke('window_action', { action: 'show' }); await expanded('left');
    await delay(400); await expanded('left');
    await page.locator('.search-bar input').focus(); await atEdge('left'); await away(); await delay(400); await expanded('left');
    await page.locator('.search-bar input').evaluate(el => el.blur()); await hidden('left');
  });
  await check('关闭立即展开并可自由移动，设置页默认及窄窗口无横向溢出', async () => {
    await save({ edgeAutoHide: false }); await expanded('left'); await delay(400); await expanded('left');
    await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
    const settingsPage = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置窗口');
    await settingsPage.getByText('自动适配 Windows 缩放比例').waitFor();
    assert.equal(await settingsPage.getByRole('radio', { name: '等0.5秒' }).isDisabled(), true);
    await settingsPage.getByRole('checkbox', { name: '悬浮时贴边自动隐藏' }).evaluate(el => el.click());
    await poll(async () => !(await settingsPage.getByRole('radio', { name: '等0.5秒' }).isDisabled()), '界面开启贴边隐藏');
    await settingsPage.getByRole('radio', { name: '等0.5秒' }).evaluate(el => el.click());
    await poll(async () => (await invoke('get_library')).settings.edgeRevealDelay === 500, '保存展开等待时间');
    for (const width of [594, 480]) {
      await settingsPage.setViewportSize({ width, height: 620 });
      assert.equal(await settingsPage.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await settingsPage.screenshot({ path: path.join(evidence, 'settings-' + width + '.png') });
    }
    await settingsPage.getByRole('button', { name: '关闭窗口' }).evaluate(el => el.click()); await poll(() => settingsPage.isClosed(), '关闭设置');
  });
  await check('设置打开及设置输入期间左右贴边仍收起，设置窗口位置和焦点不变', async () => {
    for (const side of ['left', 'right']) {
      await save({ edgeAutoHide: true, edgeRevealDelay: 0 }); await drag(side); await hidden(side);
      await atEdge(side); await expanded(side);
      await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
      const settingsPage = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置窗口');
      const settingsWindow = await native(['-ProcessId', String(app.pid), '-Title', '设置 · 闪语']);
      await native(['-Action', 'focus', '-Handle', String(settingsWindow.handle)]);
      await away(); await hidden(side);
      const before = await native(['-Handle', String(settingsWindow.handle)]);
      // 用设置中已有输入控件验证产品的真实焦点上报，不伪造原生输入标志。
      const input = settingsPage.locator('input:not([type=checkbox]):not([type=radio]):not([type=hidden])').first();
      await input.focus(); await input.fill('250');
      await atEdge(side); await expanded(side); await away(); await hidden(side);
      const after = await native(['-Handle', String(settingsWindow.handle)]);
      assert.deepEqual(after.rect, before.rect); assert.equal((await inspect()).foreground, settingsWindow.handle);
      await settingsPage.getByRole('button', { name: '关闭窗口' }).evaluate(el => el.click()); await poll(() => settingsPage.isClosed(), '关闭设置');
    }
  });
  await check('手动隐藏与非托盘最小化清除边条，重新显示恢复完整尺寸', async () => {
    await save({ edgeAutoHide: true, edgeRevealDelay: 500, tray: false }); await drag('right'); await hidden('right');
    await invoke('window_action', { action: 'hide' }); await poll(async () => !main(await inspect()).visible, '手动隐藏');
    await atEdge('right'); await delay(700); assert.equal(main(await inspect()).visible, false);
    await invoke('window_action', { action: 'show' }); await expanded('right');
    await drag('right'); await hidden('right'); await invoke('window_action', { action: 'minimize' });
    await poll(async () => main(await inspect()).iconic, '最小化'); await delay(250);
    await invoke('window_action', { action: 'show' }); await expanded('right'); assert.equal(main(await inspect()).iconic, false);
  });
  await check('贴边收起后切换聊天吸附恢复完整侧栏，退出吸附恢复正常独立宽度', async () => {
    await drag('right'); const h = await hidden('right'); await save({ dock: true });
    await control('focus'); await poll(async () => { const s = await inspect(), m = main(s); return m.visible && m.frame.left === s.windows[0].frame.right && m.frame.top === s.windows[0].frame.top; }, '吸附');
    const m = main(await inspect()); assert.equal(m.rect.right - m.rect.left, h.rect.right - h.rect.left);
    await save({ dock: false }); await delay(250); const restored = main(await inspect()); assert.ok(restored.frame.right <= restored.work.right + 1);
  });
} catch (error) {
  console.error(error); process.exitCode = 1;
  snapshots.push({ name: 'failure', state: await inspect().catch(() => null) });
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
} finally {
  await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (app) await poll(() => app.exitCode !== null, '退出').catch(() => {});
  await browser?.close().catch(() => {});
  if (host?.exitCode === null) await control('close').catch(() => {});
  for (const child of [app, host]) if (child?.exitCode === null) child.kill();
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ results, snapshots, limits: ['真实多显示器与150%/200%系统DPI未实测；几何单测覆盖对应比例', '真实企业微信及发送另行验收'] }, null, 2));
  console.log('验收记录：' + evidence);
}
