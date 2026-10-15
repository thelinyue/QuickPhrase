import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 只操作独立数据库、Debug 客户端与本机宿主；鼠标经过真实输入队列，不读写剪贴板或聊天内容。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'minimized-float-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [], handles = {};
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
const exe = path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), data = path.join(evidence, 'data');
let app, host, browser, page, floating, nonce = 0, env;
const read = async () => { try { return JSON.parse(await fs.readFile(output, 'utf8')); } catch { return null; } };
const poll = async (fn, label) => { for (let i = 0; i < 160; i++) { const value = await fn(); if (value) return value; await delay(35); } throw new Error('等待超时：' + label); };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: Object.values(handles), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (e) { if (i > 20 || !['EACCES', 'EPERM', 'EBUSY'].includes(e.code)) throw e; await delay(20); } }
  return poll(async () => { const state = await read(); if (state?.nonce === request.nonce && ['pointer', 'drag', 'click'].includes(command)) assert.equal(state.pointerError, 0); return state?.nonce === request.nonce && state; }, command);
};
const inspect = () => control('inspect');
const win = (state, name) => state.windows.find(w => w.handle === handles[name]);
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
const focusHost = async () => {
  await control('focus');
  const state = await inspect();
  if (state.foreground !== state.hwnd) await native(['-Action', 'focus', '-Handle', String(state.hwnd), '-X', '40', '-Y', '60']);
  await poll(async () => { const s = await inspect(); return s.foreground === s.hwnd; }, '确认宿主已在前台');
};
const save = async patch => { const { settings } = await invoke('get_library'); await invoke('save_settings', { settings: { ...settings, ...patch } }); };
const check = async (name, fn) => { try { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); snapshots.push({ name, state: await inspect() }); } catch (e) { results.push({ name, passed: false, error: String(e) }); throw e; } };
const restored = () => poll(async () => { const s = await inspect(); return win(s, 'main').visible && !win(s, 'main').iconic && !win(s, 'float').visible && s; }, '完整还原主窗口并收起小条');
const show = async () => { await invoke('window_action', { action: 'show' }); return restored(); };
const minimized = expected => poll(async () => { const s = await inspect(), main = win(s, 'main'); return (!main.visible || main.iconic) && win(s, 'float').visible === expected && s; }, '最小化及悬浮窗状态');
const minimize = async (expected = true) => { await invoke('window_action', { action: 'minimize' }); return minimized(expected); };
async function launch() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  env = { ...process.env, SHANYU_TEST_DATA_DIR: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port };
  app = spawn(exe, ['--autostart'], { windowsHide: true, stdio: 'ignore', env });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port, { timeout: 1500 }); } catch { if (app.exitCode !== null) throw new Error('客户端提前退出'); return null; } }, 'WebView');
  page = await poll(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash), '主页面'); await page.locator('.main-window').waitFor();
  floating = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#minimized-float')), '独立悬浮页面'); await floating.getByRole('button', { name: '打开闪语' }).waitFor({ state: 'attached' });
  for (const [name, title] of [['main', '闪语'], ['float', '悬浮窗 · 闪语'], ['bar', '常用短语 · 闪语']]) handles[name] = (await native(['-ProcessId', String(app.pid), '-Title', title])).handle;
}
async function stop() {
  if (app?.exitCode === null) { await invoke('window_action', { action: 'exit' }); await poll(() => app.exitCode !== null, '退出隔离客户端'); }
  await browser?.close(); browser = null;
}
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' }); await poll(read, '宿主');
  await launch();
  await check('新装默认开启，后台启动隐藏小条，独立入口不加载主App', async () => {
    assert.equal((await invoke('get_library')).settings.showFloatingOnMinimize, true);
    assert.equal(win(await inspect(), 'float').visible, false);
    assert.equal(await floating.evaluate(() => performance.getEntriesByType('resource').some(entry => /\/App-[^/]+\.js/.test(entry.name))), false);
  });
  await save({ dock: false, dockCommon: false, dockSearch: false, edgeAutoHide: false });
  for (const tray of [false, true]) for (const enabled of [false, true]) {
    await check(`托盘=${tray}、悬浮窗=${enabled}独立生效，显示不抢焦点`, async () => {
      await show(); await save({ tray, showFloatingOnMinimize: enabled });
      await focusHost(); const foreground = (await inspect()).foreground;
      const s = await minimize(enabled), main = win(s, 'main');
      assert.equal(s.foreground, foreground); assert.equal(tray ? !main.visible : main.iconic, true);
      if (enabled) {
        const f = win(s, 'float'); assert.ok(f.toolWindow && !f.appWindow && f.noActivate && f.topmost);
        assert.equal(f.clientRect.right, f.dpi); assert.equal(f.clientRect.bottom, Math.round(40 * f.dpi / 96));
        await control('click', { x: f.rect.left + 25, y: f.rect.top + 20 });
        const restoredState = await restored(); assert.equal(restoredState.foreground, handles.main);
      } else await show();
    });
  }
  await check('最小化期间切换开关立即生效，并同步主题', async () => {
    await minimize(); await save({ showFloatingOnMinimize: false }); await minimized(false);
    await save({ showFloatingOnMinimize: true, theme: 'green' }); await minimized(true);
    await poll(() => floating.evaluate(() => document.documentElement.dataset.theme === 'green'), '主题更新');
    await floating.screenshot({ path: path.join(evidence, 'float-green.png') });
    await save({ theme: 'default' }); await floating.screenshot({ path: path.join(evidence, 'float.png') });
  });
  await check('32像素图标完整显示，原生圆角外不拦截鼠标', async () => {
    const geometry = await floating.locator('.minimized-float').evaluate(el => {
      const image = el.querySelector('img').getBoundingClientRect();
      return { width: image.width, height: image.height, radius: getComputedStyle(el).borderTopLeftRadius, overflow: el.scrollWidth > el.clientWidth };
    });
    assert.deepEqual(geometry, { width: 32, height: 32, radius: '12px', overflow: false });
    const f = win(await inspect(), 'float');
    for (const [x, y] of [[f.rect.left + 1, f.rect.top + 1], [f.rect.right - 2, f.rect.bottom - 2]]) {
      await control('pointer', { x, y }); assert.notEqual((await inspect()).windowAtCursor, handles.float);
    }
    await control('pointer', { x: f.rect.left + 48, y: f.rect.top + 20 });
    assert.equal((await inspect()).windowAtCursor, handles.float);
  });
  let remembered;
  await check('真实拖动不抢焦点、不误还原，松开后记住坐标', async () => {
    await focusHost(); const before = await inspect(), f = win(before, 'float');
    await control('drag', { handle: handles.float, fromX: f.rect.left + 30, fromY: f.rect.top + 20, x: f.rect.left - 90, y: f.rect.top + 90 });
    await delay(250);
    const after = await minimized(true), moved = win(after, 'float');
    assert.equal(after.foreground, before.foreground);
    assert.ok(Math.abs(moved.rect.left - f.rect.left) > 60);
    remembered = { x: moved.rect.left, y: moved.rect.top };
    await control('click', { x: moved.rect.left + 25, y: moved.rect.top + 20 }); await restored();
    await minimize(); const again = win(await inspect(), 'float'); assert.equal(again.rect.left, remembered.x); assert.equal(again.rect.top, remembered.y);
  });
  await check('手动隐藏、其他窗口最小化及自动跟随隐藏不显示小条', async () => {
    await invoke('window_action', { action: 'hide' });
    await poll(async () => { const s = await inspect(); return !win(s, 'main').visible && !win(s, 'float').visible; }, '手动隐藏');
    await show(); await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
    const settings = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置页面');
    await settings.getByRole('checkbox', { name: '最小化显示悬浮窗' }).waitFor();
    for (const width of [594, 480]) { await settings.setViewportSize({ width, height: 620 }); assert.equal(await settings.evaluate(() => document.documentElement.scrollWidth > innerWidth), false); await settings.screenshot({ path: path.join(evidence, 'settings-' + width + '.png') }); }
    await settings.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|minimize'));
    assert.equal(win(await inspect(), 'float').visible, false);
    await settings.getByRole('button', { name: '关闭窗口' }).evaluate(el => el.click()); await poll(() => settings.isClosed(), '关闭设置');
    await save({ dock: true }); await control('focus'); await poll(async () => { const s = await inspect(); return win(s, 'main').visible && s; }, '跟随宿主');
    await control('viewer'); await poll(async () => !win(await inspect(), 'main').visible, '切换非吸附窗口隐藏');
    assert.equal(win(await inspect(), 'float').visible, false);
    await save({ dock: false }); await show();
  });
  await check('显示快捷键、搜索快捷键及重复手动启动均还原并收起小条', async () => {
    await save({ searchHotkey: '8', windowHotkey: '9', commonHotkey: '0', customHotkeys: false });
    for (const key of ['57', '56']) { await minimize(); await native(['-Action', 'key', '-Key', key, '-Alt']); await restored(); }
    await minimize(); await control('allow-foreground', { processId: app.pid });
    const duplicate = spawn(exe, [], { windowsHide: true, stdio: 'ignore', env }); await new Promise(resolve => duplicate.on('exit', resolve)); await restored();
  });
  await check('快速交替最小化、隐藏、还原不被旧动作覆盖', async () => {
    for (let i = 0; i < 12; i++) {
      await page.evaluate(async i => { const call = action => window.__TAURI_INTERNALS__.invoke('window_action', { action }); await call('minimize'); await call(i % 2 ? 'hide' : 'show'); await call('show'); }, i);
      await restored();
    }
    await delay(300); await restored();
  });
  await check('贴边动画中最小化先清除边条，还原保持完整尺寸', async () => {
    await save({ edgeAutoHide: true, tray: false }); await show(); await control('focus', { handle: handles.main });
    const s = await inspect(), m = win(s, 'main'), box = await page.locator('.main-titlebar .titlebar-spacer').boundingBox(), scale = m.dpi / 96;
    const fromX = Math.round(m.client.x + (box.x + box.width / 2) * scale), fromY = Math.round(m.client.y + (box.y + box.height / 2) * scale);
    await control('drag', { handle: handles.main, fromX, fromY, x: fromX + m.work.right - m.frame.right, y: fromY });
    await control('pointer', { x: m.work.left + 30, y: m.work.top + 30 });
    await minimize(); const after = await show(); assert.equal(win(after, 'main').rect.right - win(after, 'main').rect.left, m.rect.right - m.rect.left);
    await save({ edgeAutoHide: false });
  });
  await check('正常退出重开保留坐标，启动仍不主动显示小条', async () => {
    await minimize(); const rect = win(await inspect(), 'float').rect; remembered = { x: rect.left, y: rect.top };
    await stop(); await launch(); assert.equal(win(await inspect(), 'float').visible, false);
    await minimize(); const again = win(await inspect(), 'float'); assert.equal(again.rect.left, remembered.x); assert.equal(again.rect.top, remembered.y);
  });
  await check('已移除屏幕的极端坐标重开后恢复到可见工作区', async () => {
    await stop();
    await run('python', ['-c', "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute(\"UPDATE sync_meta SET value=? WHERE key='minimizedFloatPosition'\", ('{\"x\":2147483000,\"y\":-2147483000}',)); c.commit(); c.close()", path.join(data, 'shanyu.sqlite3')], { windowsHide: true });
    await launch(); await minimize(); const f = win(await inspect(), 'float');
    assert.ok(f.rect.left >= f.work.left && f.rect.right <= f.work.right && f.rect.top >= f.work.top && f.rect.bottom <= f.work.bottom);
  });
} catch (error) {
  console.error(error); process.exitCode = 1;
  snapshots.push({ name: 'failure', state: await inspect().catch(() => null) });
  await floating?.screenshot({ path: path.join(evidence, 'failure-float.png') }).catch(() => {});
  await page?.screenshot({ path: path.join(evidence, 'failure-main.png') }).catch(() => {});
} finally {
  await stop().catch(() => {});
  if (host?.exitCode === null) await control('close').catch(() => {});
  for (const child of [app, host]) if (child?.exitCode === null) child.kill();
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ results, snapshots, limits: ['真实企业微信、实际多屏及150%/200%系统DPI尚未验收；几何单测覆盖对应比例', '托盘实体点击与本轮安装升级未实测，托盘共用的还原队列由显示快捷键覆盖'] }, null, 2));
  console.log('验收记录：' + evidence);
}
