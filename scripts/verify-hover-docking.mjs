import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 真实 WebView 和 Win32 窗口使用隔离数据库。宿主不访问剪贴板，发送回调仅记录身份。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'hover-docking-' + Date.now());
const dpiTest = process.argv.includes('--dpi');
const previewOnly = process.argv.includes('--preview-only');
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port; await new Promise(resolve => server.close(resolve));
let app, host, browser, context, page, preview, mainHandle, barHandle, previewHandle, hostHandle, nonce = 0, lastNative;
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
const poll = async (check, label) => { for (let i = 0; i < 100; i++) { const value = await check(); if (value) return value; await delay(40); } throw new Error('等待超时：' + label); };
const read = async () => { try { lastNative = JSON.parse(await fs.readFile(output, 'utf8')); return lastNative; } catch { return null; } };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: [mainHandle, barHandle, previewHandle].filter(Boolean), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (error) { if (i >= 20 || !['EBUSY', 'EACCES', 'EPERM'].includes(error.code)) throw error; await delay(20); } }
  return poll(async () => { const state = await read(); return state?.nonce === request.nonce && state; }, '原生宿主响应');
};
const inspect = async () => { await control('inspect'); await delay(60); return poll(read, '读取原生窗口状态'); };
const windowOf = (state, handle) => state.windows.find(w => w.handle === handle);
const intersects = (a, b) => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
const assertDockOrder = state => {
  // Windows 可能在窗口之间保留辅助句柄，验证实际相对顺序而非必须紧邻。
  assert.ok(windowOf(state, hostHandle).zOrder < windowOf(state, mainHandle).zOrder, '目标先于侧栏显示');
  assert.ok(windowOf(state, mainHandle).zOrder < windowOf(state, barHandle).zOrder, '侧栏先于底栏显示');
};
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const check = async (name, fn) => { if (previewOnly && !['首次短文预览', '快速掠过', '鼠标进入浮层', '屏幕右下边缘'].some(prefix => name.startsWith(prefix))) return; await fn(); results.push({ name, passed: true }); console.log('通过：' + name); };
const row = id => page.locator('[data-row="' + id + '"]').first();
async function hover(locator, view = page, handle = mainHandle) {
  const box = await locator.boundingBox(), state = windowOf(await inspect(), handle), scale = state.dpi / 96;
  const x = box.x + Math.min(box.width - 8, 110), y = box.y + box.height / 2;
  await control('pointer', { x: Math.round(state.client.x + x * scale), y: Math.round(state.client.y + y * scale) });
  await view.mouse.move(x, y);
}
const popupVisible = async () => windowOf(await inspect(), previewHandle).visible;
async function focusHost() {
  await control('focus');
  if ((await inspect()).foreground !== hostHandle) {
    await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), '-Action', 'focus', '-Handle', String(hostHandle), '-X', '40', '-Y', '60'], { windowsHide: true });
  }
  await poll(async () => (await inspect()).foreground === hostHandle, '激活隔离吸附宿主');
}
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls, ...(dpiTest ? ['--dpi-unaware-target'] : [])], { windowsHide: true, stdio: 'ignore' });
  hostHandle = (await poll(read, '验收宿主')).hwnd;
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); return null; } }, '实际WebView');
  context = browser.contexts()[0]; context.setDefaultTimeout(6000);
  page = await poll(() => context.pages().find(p => !new URL(p.url()).hash), '主窗口页面');
  await page.locator('.main-window').waitFor();
  preview = await poll(() => context.pages().find(p => p.url().includes('#phrase-preview')), '预览窗口页面');
  await page.evaluate(() => {
    const original = window.fetch.bind(window); window.__hoverQA = []; window.__hoverEvents = [];
    window.fetch = (input, init) => { const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname === 'ipc.localhost' && url.pathname.includes('phrase_preview')) window.__hoverQA.push({ command: url.pathname, args: JSON.parse(init.body), time: performance.now() }); return original(input, init); };
    for (const type of ['pointermove', 'pointerdown', 'mousedown', 'scroll', 'keydown']) window.addEventListener(type, event => {
      window.__hoverEvents.push({ type, x: event.clientX, y: event.clientY, key: event.key, detail: event.detail, target: event.target.className, time: performance.now() });
      if (window.__hoverEvents.length > 30) window.__hoverEvents.shift();
    }, true);
    window.addEventListener('resize', () => { window.__hoverEvents.push({ type: 'resize', width: innerWidth, height: innerHeight, time: performance.now() }); });
  });
  // 句柄只通过指定进程和固定标题读取，不查找或操作正式客户端。
  const nativeHandle = async title => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), '-ProcessId', String(app.pid), '-Title', title], { windowsHide: true, encoding: 'utf8' })).stdout).handle;
  mainHandle = await nativeHandle('闪语'); barHandle = await nativeHandle('常用短语 · 闪语'); previewHandle = await nativeHandle('话术预览 · 闪语');
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, path.join(evidence, 'data'));
  // 点击分类的原生验收也需要真实分类，必须在几何专项开始前准备，避免点到新增按钮。
  const phraseRoot = initial.categories.find(category => category.kind === 'phrase' && !category.parentId)?.id
    ?? await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  await page.locator('.category-tabs button:not(.add-category)').first().waitFor();
  const settings = { ...initial.settings, startup: false, dock: false, autoHide: false, pinned: false, dockCommon: false, dockSearch: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' };
  await poll(async () => { try { await invoke('save_settings', { settings }); return true; } catch (error) { if (!String(error).includes('服务尚未启动')) throw error; return false; } }, '原生设置服务');
  await check('Alt+Q松键后上下键导航搜索结果，不误唤起Windows系统菜单', async () => {
    const nativeKey = async (key, args = []) => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), '-Action', 'key', '-Handle', String(mainHandle), '-Key', String(key), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
    await invoke('save_settings', { settings: { ...settings, dock: true, showSearch: true, searchHotkey: 'Q', windowHotkey: 'W', commonHotkey: 'E' } });
    await focusHost(); await delay(180);
    for (const from of ['host', 'main']) {
      await nativeKey(81, ['-Alt', '-HoldMilliseconds', '350']);
      if (from === 'main') await control('alt-menu', { handle: mainHandle });
      for (const key of [40, 38]) {
        const native = await nativeKey(key); snapshots.push({ name: 'alt-q-' + from + '-arrow-' + key, native });
        // 系统菜单会进入原生模态循环，失败前取消菜单，避免阻塞 WebView 诊断与退出。
        if (native.menuMode) { await nativeKey(27); await control('cancel-move', { handle: mainHandle }); }
        assert.equal(native.menuMode, false, 'Alt+Q之后的上下键不能打开系统菜单');
      }
    }
    const search = page.getByRole('textbox', { name: '搜索话术、文件', exact: true });
    assert.equal(await search.evaluate(e => e === document.activeElement), true);
    const ids = [];
    for (const title of ['搜索甲', '搜索乙']) ids.push(await invoke('save_phrase', { phrase: { id: '', title, rootId: phraseRoot, groupId: null, shortcut: '', color: '', background: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '隔离验证' }] }] }, afterId: null }));
    await search.fill('搜索'); await poll(async () => await page.locator('#search-results .content-row').count() === 2, '两条搜索结果');
    const rows = await page.locator('#search-results .content-row').evaluateAll(elements => elements.map(e => e.dataset.row));
    for (const [key, index] of [[40, 1], [38, 0]]) {
      const native = await nativeKey(key); if (native.menuMode) await nativeKey(27);
      assert.equal(native.menuMode, false);
      assert.equal(await page.locator('#search-results .selected').getAttribute('data-row'), rows[index]);
    }
    await nativeKey(27);
    const menu = await nativeKey(32, ['-Alt']); await nativeKey(27); await control('cancel-move', { handle: mainHandle });
    assert.equal(menu.menuMode, true, '主动按Alt+空格仍可打开系统菜单');
    for (const id of ids) await invoke('delete_item', { id, kind: 'phrase' });
    await invoke('save_settings', { settings });
  });
  await check('吸附标题栏与底栏双击不会最大化，窗口尺寸与内容绘制保持正常', async () => {
    await invoke('save_settings', { settings: { ...settings, dock: true, freeResize: true } });
    const work = windowOf(await inspect(), hostHandle).work;
    await control('move', { x: work.left + 200, y: work.top + 120, width: 600, height: 540 });
    await focusHost(); await delay(220);
    for (const freeResize of [true, false]) {
      await invoke('save_settings', { settings: { ...settings, dock: true, freeResize } }); await delay(180);
      for (const [name, selector] of [['titlebar', '.main-titlebar .titlebar-spacer'], ['footer', '.main-footer .footer-spacer']]) {
        const before = windowOf(await inspect(), mainHandle), box = await page.locator(selector).boundingBox(), scale = before.dpi / 96;
        await page.evaluate(() => { window.__hoverEvents = []; });
        await control('double-click', { x: Math.round(before.client.x + (box.x + box.width / 2) * scale), y: Math.round(before.client.y + (box.y + box.height / 2) * scale) });
        await delay(250);
        const after = windowOf(await inspect(), mainHandle), viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
        const events = await page.evaluate(() => window.__hoverEvents);
        snapshots.push({ name: 'double-click-' + name + '-' + freeResize, before, after, viewport, events });
        await page.screenshot({ path: path.join(evidence, 'double-click-' + name + '-' + freeResize + '.png') });
        assert.equal(after.zoomed, false, '吸附窗口不能进入最大化状态');
        assert.deepEqual(after.frame, before.frame, '双击不能改变吸附尺寸和位置');
        assert.ok(viewport.width > 0 && viewport.height > 0, 'WebView不能缩为零尺寸');
        assert.ok(Math.abs(viewport.width * scale - after.clientRect.right) <= 1, '内容宽度与吸附窗口一致');
        assert.ok(Math.abs(viewport.height * scale - after.clientRect.bottom) <= 1, '内容高度与吸附窗口一致');
        await control('cancel-move', { handle: mainHandle }); await delay(80);
        await page.getByRole('button', { name: '文件', exact: true }).click();
        await page.getByRole('button', { name: '话术', exact: true }).click();
      }
    }
    await invoke('save_settings', { settings }); await delay(180);
  });
  await check('吸附和独立主窗口双击底栏品牌不选中文字，尺寸与内容保持正常', async () => {
    for (const dock of [true, false]) {
      await invoke('save_settings', { settings: { ...settings, dock, freeResize: true } });
      if (dock) await focusHost(); else await invoke('window_action', { action: 'show' });
      await delay(180);
      const before = windowOf(await inspect(), mainHandle), box = await page.locator('.main-footer .brand-name').boundingBox(), scale = before.dpi / 96;
      await page.evaluate(() => window.getSelection()?.removeAllRanges());
      await control('double-click', { x: Math.round(before.client.x + (box.x + box.width / 2) * scale), y: Math.round(before.client.y + (box.y + box.height / 2) * scale) });
      await delay(200);
      const after = windowOf(await inspect(), mainHandle), content = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, selected: !!window.getSelection()?.toString() }));
      snapshots.push({ name: 'double-click-brand-' + dock, before, after, content });
      await page.screenshot({ path: path.join(evidence, 'double-click-brand-' + dock + '.png') });
      assert.equal(content.selected, false, '双击底栏品牌不能选中文字');
      assert.equal(after.zoomed, false);
      assert.deepEqual(after.frame, before.frame);
      assert.ok(Math.abs(content.width * scale - after.clientRect.right) <= 1);
      assert.ok(Math.abs(content.height * scale - after.clientRect.bottom) <= 1);
    }
    await invoke('save_settings', { settings });
  });
  await check('双击主窗口和吸附横条的短语栏不选中加号，新增短语仍可打开', async () => {
    await invoke('save_settings', { settings: { ...settings, dock: true, showCommon: true, dockCommon: true } });
    await focusHost(); await delay(180);
    const common = context.pages().find(p => p.url().includes('#common-dock'));
    for (const [name, view, handle] of [['main', page, mainHandle], ['dock', common, barHandle]]) {
      const before = windowOf(await inspect(), handle), bar = await view.locator('.common-phrases').boundingBox(), add = await view.locator('.add-common').boundingBox(), scale = before.dpi / 96;
      await view.evaluate(() => window.getSelection()?.removeAllRanges());
      await control('double-click', { x: Math.round(before.client.x + Math.min(add.x + add.width + 12, bar.x + bar.width - 10) * scale), y: Math.round(before.client.y + (add.y + add.height / 2) * scale) });
      await delay(180);
      const selected = await view.evaluate(() => !!window.getSelection()?.toString());
      snapshots.push({ name: 'double-click-common-' + name, selected, native: await inspect() });
      await view.screenshot({ path: path.join(evidence, 'double-click-common-' + name + '.png') });
      assert.equal(selected, false, '短语栏双击空白不能选中加号或文字');
      await view.getByRole('button', { name: '新增常用短语', exact: true }).click();
      const editor = await poll(() => context.pages().find(p => p.url().includes('#common-editor')), '新增短语编辑窗口');
      await editor.locator('.common-editor').waitFor();
      await editor.getByRole('button', { name: '关闭窗口', exact: true }).click();
      await focusHost(); await delay(120);
    }
    await invoke('save_settings', { settings });
  });
  await check('主窗口关闭吸附后仍保持窄窗，已最大化窗口恢复正常尺寸再吸附且不抢焦点', async () => {
    await invoke('save_settings', { settings: { ...settings, freeResize: true } });
    await invoke('window_action', { action: 'show' }); await delay(180);
    assert.equal(await invoke('plugin:window|is_maximizable'), false, '主窗口最大化始终关闭，避免反复改原生样式');
    const before = windowOf(await inspect(), mainHandle), box = await page.locator('.main-titlebar .titlebar-spacer').boundingBox(), scale = before.dpi / 96;
    await control('double-click', { x: Math.round(before.client.x + (box.x + box.width / 2) * scale), y: Math.round(before.client.y + (box.y + box.height / 2) * scale) });
    await delay(200);
    assert.equal(windowOf(await inspect(), mainHandle).zoomed, false, '独立主窗口双击保持窄窗');
    await control('cancel-move', { handle: mainHandle }); await delay(80);
    // 原生强制最大化模拟已有最大化状态，不能当作主窗口支持最大化的用户入口。
    await control('maximize', { handle: mainHandle });
    await poll(async () => windowOf(await inspect(), mainHandle).zoomed, '强制最大化隔离主窗口');
    await focusHost();
    await invoke('save_settings', { settings: { ...settings, dock: true, freeResize: true } });
    const restored = await poll(async () => {
      const state = await inspect(), main = windowOf(state, mainHandle), target = windowOf(state, hostHandle);
      return !main.zoomed && main.visible && main.frame.left === target.frame.right && main.frame.bottom === target.frame.bottom && state;
    }, '最大化主窗口还原后吸附');
    const main = windowOf(restored, mainHandle), viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    assert.equal(restored.foreground, hostHandle, '还原并跟随不能抢走目标焦点');
    assert.equal(main.rect.right - main.rect.left, before.rect.right - before.rect.left, '最大化宽度不能污染侧栏宽度');
    assert.ok(Math.abs(viewport.width * scale - main.clientRect.right) <= 1);
    assert.ok(Math.abs(viewport.height * scale - main.clientRect.bottom) <= 1);
    snapshots.push({ name: 'maximized-to-docked', before, restored, viewport });
    await invoke('save_settings', { settings });
  });
  await check('主窗口最大化限制不影响独立排序窗口的最大化和还原', async () => {
    await invoke('save_category', { category: { id: '', name: '最大化验收', kind: 'phrase', parentId: null, position: 1, background: '' } });
    await page.getByRole('button', { name: '一级分类菜单', exact: true }).click();
    await page.getByRole('menuitem', { name: '快速排序一级分类', exact: true }).click();
    const sort = await poll(() => context.pages().find(p => p.url().includes('#category-sort')), '独立排序窗口');
    await sort.locator('.common-sort').waitFor();
    assert.equal(await invoke('plugin:window|is_maximizable', {}, sort), true);
    const maximize = sort.getByRole('button', { name: '最大化或还原窗口', exact: true });
    await maximize.click(); await poll(() => invoke('plugin:window|is_maximized', {}, sort), '独立排序窗口最大化');
    await sort.screenshot({ path: path.join(evidence, 'sort-maximized.png') });
    await maximize.click(); await poll(async () => !await invoke('plugin:window|is_maximized', {}, sort), '独立排序窗口还原');
    await sort.getByRole('button', { name: '关闭窗口', exact: true }).click();
    await focusHost();
  });
  await check('吸附侧栏和底栏跟随目标层级，任务栏遮挡关系与目标一致', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    for (const pinned of [true, false]) {
      await invoke('save_settings', { settings: { ...settings, dock: true, pinned, dockCommon: true } });
      await control('move', { x: work.left + 600, y: work.top + 80, width: 640, height: work.bottom - work.top + 120 }); await focusHost(); await delay(180);
      const state = await inspect(), main = windowOf(state, mainHandle), bar = windowOf(state, barHandle), target = windowOf(state, hostHandle), taskbar = state.taskbar;
      snapshots.push({ name: 'target-layer-' + (pinned ? 'remembered-pin' : 'normal'), state });
      assert.equal(target.topmost, false);
      assert.equal(main.topmost, target.topmost, '吸附侧栏不能自行全局置顶');
      assert.equal(bar.topmost, target.topmost, '底栏与目标处于同一层级');
      assert.equal(main.frame.bottom, target.frame.bottom, '保留完整高度，允许任务栏遮挡');
      assert.equal(main.frame.left, target.frame.right, '侧栏固定贴在目标外侧');
      assert.equal(bar.frame.top, target.frame.bottom, '底栏不会向内回挪');
      assertDockOrder(state);
      assert.ok(taskbar.visible && taskbar.frame.bottom > taskbar.frame.top, '读取实际可见系统任务栏');
      const pointY = taskbar.frame.top + 4;
      assert.ok(pointY < main.frame.bottom && pointY < target.frame.bottom, '目标和侧栏都延伸到任务栏下面');
      // Windows 可让普通超高目标覆盖任务栏；以目标实际遮挡结果检查侧栏和底栏，
      // 不能假设任务栏始终覆盖目标，也不能允许面板单独改变层级。
      const targetProbe = await control('probe', { pointX: target.frame.left + 40, pointY });
      assert.ok([taskbar.handle, target.handle].includes(targetProbe.probedWindow), '目标探测点不能被其他窗口覆盖');
      const targetCoversTaskbar = targetProbe.probedWindow === target.handle;
      for (const [pointX, expected] of [[target.frame.left + 40, targetCoversTaskbar ? target.handle : taskbar.handle], [main.frame.left + 12, targetCoversTaskbar ? mainHandle : taskbar.handle]]) {
        const probed = await control('probe', { pointX, pointY });
        snapshots.push({ name: 'taskbar-covers-' + pointX, pointX, pointY, state: probed });
        assert.equal(probed.probedWindow, expected, '任务栏对目标和侧栏保持相同遮挡关系');
      }
      await control('move', { x: work.left + 600, y: work.bottom - 540 + 4, width: 640, height: 540 }); await delay(180);
      const nearTaskbar = await inspect(), bottom = windowOf(nearTaskbar, barHandle);
      assert.equal(bottom.frame.top, windowOf(nearTaskbar, hostHandle).frame.bottom);
      const bottomProbe = await control('probe', { pointX: bottom.frame.left + 40, pointY });
      assert.equal(bottomProbe.probedWindow, targetCoversTaskbar ? barHandle : taskbar.handle, '底栏与目标保持相同任务栏遮挡关系');
      snapshots.push({ name: 'taskbar-covers-bottom-' + pinned, state: bottomProbe });
      await invoke('save_settings', { settings: { ...settings, dock: false, pinned, dockCommon: true } }); await delay(180);
      assert.equal(windowOf(await inspect(), mainHandle).topmost, pinned, '退出吸附后恢复用户独立窗口的置顶选择');
      assert.equal((await invoke('get_library')).settings.pinned, pinned, '自动跟随不改写置顶设置');
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    // 连续切换时界面线程等待后台设置响应，后台定位不能反向同步等待界面处理窗口消息。
    for (let i = 0; i < 12; i++) await Promise.all([
      invoke('save_settings', { settings: { ...settings, dock: i % 2 === 0, dockCommon: i % 3 !== 0 } }),
      invoke('get_library'),
    ]);
    await invoke('save_settings', { settings });
  });
  await check('普通或置顶目标外侧连续点击侧栏与底栏，短语可见且目标关系和输入焦点保留', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    await invoke('save_common_phrase', { phrase: { id: '', text: '底栏验证', position: 0 } });
    for (const pinned of [false, true]) {
      await invoke('save_settings', { settings: { ...settings, dock: true, pinned, dockCommon: true } });
      await control(pinned ? 'raise' : 'unpin');
      await control('move', { x: work.left + 360, y: work.top + 120, width: 640, height: 540 }); await focusHost(); await delay(180);
      await poll(async () => windowOf(await inspect(), barHandle).visible, '目标底部短语显示');
      const box = await page.locator('.category-tabs button:not(.add-category)').first().boundingBox(), main = windowOf(await inspect(), mainHandle), scale = main.dpi / 96;
      // 直接连续点击分类：既覆盖从聊天窗口进入侧栏，也覆盖前台句柄未变化的再次点击。
      for (let click = 0; click < 3; click++) {
        await control('click', { x: Math.round(main.client.x + (box.x + box.width / 2) * scale), y: Math.round(main.client.y + (box.y + box.height / 2) * scale) }); await delay(180);
        const state = await inspect(), bar = windowOf(state, barHandle), sidebar = windowOf(state, mainHandle);
        assert.equal(state.foreground, mainHandle, '实际点击后侧栏处于前台');
        assert.equal(bar.visible, true, '进入侧栏不能隐藏底部短语');
        assert.equal(sidebar.topmost, windowOf(state, hostHandle).topmost, '点击不改变目标层级');
        assert.equal(bar.topmost, sidebar.topmost, '底栏保留相同层级');
        assert.equal((await invoke('get_target')).hwnd, hostHandle, '侧栏操作保留聊天目标');
        const target = windowOf(state, hostHandle);
        assertDockOrder(state);
        assert.equal(sidebar.frame.left, target.frame.right); assert.equal(bar.frame.top, target.frame.bottom);
        assert.equal(intersects(sidebar.frame, target.frame), false); assert.equal(intersects(bar.frame, target.frame), false);
        const pointX = bar.frame.left + (bar.frame.right - bar.frame.left) / 2, pointY = bar.frame.top + 12;
        const probed = await control('probe', { pointX, pointY });
        snapshots.push({ name: 'sidebar-click-' + (pinned ? 'pinned' : 'normal') + '-' + click, pointX, pointY, state: probed });
        assert.equal(probed.probedWindow, barHandle, '点击侧栏后底栏实际可见');
        await control('click', { x: pointX, y: pointY }); await delay(180);
        await poll(async () => {
          const state = await inspect();
          return windowOf(state, hostHandle).zOrder < windowOf(state, mainHandle).zOrder && windowOf(state, mainHandle).zOrder < windowOf(state, barHandle).zOrder;
        }, '底栏点击后恢复目标、侧栏、底栏的顺序');
        const clickedBottom = await inspect();
        assert.equal(clickedBottom.foreground, barHandle, '跟随定位不夺取用户点击底栏后的输入焦点');
        assert.equal(windowOf(clickedBottom, barHandle).visible, true);
        assertDockOrder(clickedBottom);
        assert.equal((await invoke('get_target')).hwnd, hostHandle);
      }
    }
    await control('unpin');
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('侧栏超出屏幕顶部、任务栏和上下边缘时仍与目标等高，保留完整宽度', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    for (const side of ['right', 'left']) {
      await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side } }); await focusHost(); await delay(180);
      const fullWidth = windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left;
      for (const [name, y, height] of [['above', work.top - 120, 560], ['below', work.bottom - 160, 560], ['taller', work.top - 120, work.bottom - work.top + 240], ['outside-above', work.top - 800, 560], ['outside-below', work.bottom + 80, 560]]) {
        await control('move', { x: work.left + 600, y, width: 600, height }); await delay(180);
        const state = await inspect(), main = windowOf(state, mainHandle), target = windowOf(state, hostHandle);
        assert.equal(main.visible, true);
        assert.equal(main.frame.top, target.frame.top, side + '-' + name + ' 顶部对齐');
        assert.equal(main.frame.bottom, target.frame.bottom, side + '-' + name + ' 底部对齐');
        assert.equal(main.rect.right - main.rect.left, fullWidth, '屏幕裁切不能改变用户宽度');
        assert.equal(side === 'left' ? main.frame.right : main.frame.left, side === 'left' ? target.frame.left : target.frame.right);
        assert.equal(intersects(main.frame, target.frame), false, '纵向越界不向目标内部回挪');
        assert.equal(state.foreground, hostHandle, '尺寸跟随不抢焦点');
        snapshots.push({ name: 'full-size-' + side + '-' + name, main, target });
      }
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('底栏在目标横跨屏幕边缘或宽于工作区时保持目标全宽与完整高度', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    await invoke('save_settings', { settings: { ...settings, dockCommon: true, dockSearch: true } }); await focusHost();
    await poll(async () => windowOf(await inspect(), barHandle).visible, '显示底栏'); await delay(180);
    const bar = windowOf(await inspect(), barHandle), fullHeight = bar.frame.bottom - bar.frame.top;
    for (const [name, x, y, width] of [['left', work.left - 120, work.top + 120, 640], ['right', work.right - 200, work.top + 120, 640], ['wider', work.left - 120, work.top + 120, work.right - work.left + 240], ['bottom', work.left + 200, work.bottom - 160, 640]]) {
      await control('move', { x, y, width, height: 560 }); await delay(180);
      const state = await inspect(), current = windowOf(state, barHandle), target = windowOf(state, hostHandle);
      assert.equal(current.visible, true);
      assert.equal(current.frame.left, target.frame.left, name + ' 左端对齐');
      assert.equal(current.frame.right, target.frame.right, name + ' 右端对齐');
      assert.equal(current.frame.top, target.frame.bottom, name + ' 始终贴在目标下方');
      assert.equal(intersects(current.frame, target.frame), false, '底栏不能覆盖目标');
      assert.equal(current.frame.bottom - current.frame.top, fullHeight, '遮挡不能压缩底栏高度');
      assert.equal(state.foreground, hostHandle, '底栏跟随不抢焦点');
      snapshots.push({ name: 'full-size-bottom-' + name, bar: current, target });
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('四个方向连续移动和缩放时立即探测目标边缘，两处吸附面板不会覆盖目标内容', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    const moves = [[360, 120, 600, 480], [440, 120, 600, 480], [360, 120, 600, 480], [360, 200, 600, 480], [360, 120, 600, 480], [360, 120, 680, 560], [360, 120, 600, 480]];
    for (const side of ['left', 'right']) {
      for (const topmost of [false, true]) {
        await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side, dockCommon: true } });
        await control(topmost ? 'raise' : 'unpin'); await focusHost(); await delay(180);
        for (const [x, y, width, height] of moves) {
          const state = await control('move', { x: work.left + x, y: work.top + y, width, height, probeTarget: true });
          assert.equal(state.targetProbes.length, 4);
          for (const point of state.targetProbes) assert.equal(point.window, hostHandle, '移动后立即检查，目标内部边缘不能被旧面板位置覆盖');
          snapshots.push({ name: 'moving-' + side + '-' + topmost, state });
          await delay(100);
          const settled = await inspect(), main = windowOf(settled, mainHandle), bar = windowOf(settled, barHandle), target = windowOf(settled, hostHandle);
          assert.equal(side === 'left' ? main.frame.right : main.frame.left, side === 'left' ? target.frame.left : target.frame.right);
          assert.equal(bar.frame.top, target.frame.bottom);
          assertDockOrder(settled);
          assert.equal(intersects(main.frame, target.frame), false); assert.equal(intersects(bar.frame, target.frame), false);
          assert.equal(settled.foreground, hostHandle, '连续跟随不抢焦点');
        }
      }
    }
    await control('unpin');
    await invoke('save_settings', { settings: { ...settings, dockCommon: true } }); await delay(180);
    const onlyBottom = await inspect();
    assert.ok(windowOf(onlyBottom, hostHandle).zOrder < windowOf(onlyBottom, barHandle).zOrder, '没有侧栏时底栏位于目标后方');
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  const images = await invoke('import_images', { paths: [path.join(repo, 'public/shanyu.png')] });
  const steps = '1、创建临时管理员账户，保留现有账户和数据。\n2、打开SSH服务，记录问题发生时间。\n3、提供识别码，保持网络连接。\n4、处理完成后删除临时账户并关闭SSH。\n';
  const savePhrase = (title, segments) => invoke('save_phrase', { phrase: { id: '', rootId: phraseRoot, groupId: null, title, color: '', background: '', shortcut: '', position: 0, segments }, afterId: null });
  const longId = await savePhrase('PRO-远程排查', [{ blocks: [{ kind: 'text', text: '您好，请按以下步骤准备远程排查：\n' }, ...images, { kind: 'text', text: steps.repeat(7) }, { kind: 'file', path: 'C:/仅作预览.txt', name: '排查说明.txt' }] }, { blocks: [{ kind: 'text', text: '第二段：问题处理完成后，请及时关闭临时访问。' }] }]);
  const shortId = await savePhrase('简短回复', [{ blocks: [{ kind: 'text', text: '您好，已收到您的反馈。\n我们会继续跟进。' }] }]);
  await row(shortId).waitFor(); await invoke('window_action', { action: 'show' });
  await control('move', { handle: mainHandle, x: 80, y: 120, width: 330, height: 600 }); await delay(160);
  await invoke('save_settings', { settings: { ...settings, pinned: true } }); await focusHost();
  await check('首次短文预览按客户区测量，正文没有空白膨胀或多余滚动条', async () => {
    await hover(row(shortId)); await poll(popupVisible, '首次短文预览');
    assert.ok(await preview.locator('.phrase-preview-body').evaluate(el => Math.abs(el.clientHeight - el.firstElementChild.scrollHeight) <= 2));
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭首次预览');
  });
  await check('快速掠过不弹出；停留后在鼠标右下显示轻量图文提示且不激活窗口', async () => {
    await hover(row(longId)); await delay(100); await control('pointer', { x: 40, y: 40 }); await page.mouse.move(-20, -20); await delay(450); assert.equal(await popupVisible(), false);
    await focusHost(); const foreground = (await inspect()).foreground;
    await hover(row(longId)); await poll(popupVisible, '长文预览');
    await preview.getByRole('region', { name: '话术正文预览' }).waitFor(); await preview.waitForFunction(() => [...document.images].every(img => img.naturalWidth > 0));
    const state = await inspect(), popup = windowOf(state, previewHandle), main = windowOf(state, mainHandle);
    assert.equal(state.foreground, foreground); assert.equal(popup.noActivate, true);
    const gap = Math.round(12 * popup.dpi / 96);
    assert.equal(popup.rect.left - state.cursor.x, gap, '按实际鼠标定位，而非整行右侧');
    assert.equal(popup.rect.top - state.cursor.y, gap);
    assert.equal(await preview.evaluate(() => innerWidth), 300);
    const appearance = await preview.locator('.phrase-preview-window').evaluate(el => ({ border: getComputedStyle(el).borderColor, header: getComputedStyle(el.querySelector('header')).backgroundColor, buttons: el.querySelectorAll('button').length, footers: el.querySelectorAll('footer').length }));
    assert.equal(appearance.border, 'rgb(75, 100, 121)'); assert.ok(!['rgba(0, 0, 0, 0)', 'rgb(255, 255, 255)'].includes(appearance.header), '预览辅助区域使用主题浅底'); assert.equal(appearance.buttons, 0); assert.equal(appearance.footers, 0);
    assert.ok(popup.rect.right <= popup.work.right && popup.rect.bottom <= popup.work.bottom);
    assert.ok(Math.abs(popup.rect.bottom - (popup.work.bottom - Math.round(8 * popup.dpi / 96))) <= 1, '长文使用鼠标下方全部可用高度');
    assert.equal(await preview.getByText('第 2 段', { exact: true }).count(), 1); assert.equal(await preview.getByText('排查说明.txt', { exact: true }).count(), 1);
    await preview.screenshot({ path: path.join(evidence, 'long-preview.png') }); snapshots.push({ name: 'long-preview', main, popup, foreground: state.foreground, foregroundPid: state.foregroundPid });
  });
  await check('鼠标进入浮层可滚动查看末尾；短文自适应高度，Esc关闭且不改变选择', async () => {
    await hover(preview.locator('.phrase-preview-body'), preview, previewHandle); await preview.mouse.wheel(0, 2000); await delay(800);
    assert.equal(await popupVisible(), true); assert.ok(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollTop > 0));
    await preview.screenshot({ path: path.join(evidence, 'long-preview-end.png') });
    const height = windowOf(await inspect(), previewHandle).rect.bottom - windowOf(await inspect(), previewHandle).rect.top;
    await hover(row(shortId)); await poll(async () => (await preview.locator('header').textContent()).includes('简短回复') && await popupVisible(), '短文预览');
    const popup = windowOf(await inspect(), previewHandle); assert.ok(popup.rect.bottom - popup.rect.top < height);
    assert.equal(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollHeight > el.clientHeight), false, '短文无需多余滚动条');
    assert.equal(await row(shortId).getAttribute('aria-selected'), 'false'); await preview.screenshot({ path: path.join(evidence, 'short-preview.png') });
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), 'Esc关闭');
    await hover(row(longId)); await poll(async () => (await preview.locator('header').textContent()).includes('PRO-远程排查') && await popupVisible(), '再次打开长文');
    assert.equal(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollTop), 0, '切换话术从正文开头阅读');
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭长文');
  });
  await check('屏幕右下边缘自动向左上展开，预览完整可见且长文可滚动到底', async () => {
    const original = windowOf(await inspect(), mainHandle).rect;
    const work = windowOf(await inspect(), mainHandle).work;
    await control('move', { handle: mainHandle, x: work.right - 330, y: work.bottom - 400, width: 330, height: 400 }); await delay(160); await focusHost();
    await hover(row(longId)); await poll(popupVisible, '边缘预览');
    const state = await inspect(), popup = windowOf(state, previewHandle), gap = Math.round(12 * popup.dpi / 96);
    assert.equal(state.cursor.x - popup.rect.right, gap);
    assert.equal(state.cursor.y - popup.rect.bottom, gap);
    assert.ok(popup.rect.left >= work.left && popup.rect.right <= work.right && popup.rect.top >= work.top && popup.rect.bottom <= work.bottom, '预览外框完整保留在可用屏幕内');
    assert.equal(popup.noActivate, true); assert.equal(state.foreground, hostHandle);
    await control('pointer', { x: popup.client.x + 40, y: popup.client.y + 90 });
    await preview.locator('.phrase-preview-body').evaluate(el => { el.scrollTop = el.scrollHeight; });
    assert.equal(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 1), true);
    await preview.screenshot({ path: path.join(evidence, 'adaptive-edge-preview.png') });
    snapshots.push({ name: 'adaptive-preview-at-edge', cursor: state.cursor, popup });
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭边缘预览');
    await control('move', { handle: mainHandle, x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(160);
  });
  await check('连续移动、缩放、拖动暂停和长时间闲置均保持完整宽度，旧自动隐藏设置不再收窄', async () => {
    await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: true } }); await focusHost(); await delay(200);
    assert.equal((await inspect()).foreground, hostHandle, '必须激活独立吸附宿主');
    const work = windowOf(await inspect(), hostHandle).work;
    await control('pointer', { x: work.left + 600, y: work.top + 200 });
    for (let i = 0; i < 12; i++) { await control('move', { x: work.left + 600 + i * 5, y: work.top + 120 + i * 3, width: 600, height: 500 + i }); await delay(i === 6 ? 1100 : 100); const main = windowOf(await inspect(), mainHandle); assert.ok(main.frame.right - main.frame.left >= 286 * main.dpi / 96); }
    const expanded = windowOf(await inspect(), mainHandle), fullWidth = expanded.rect.right - expanded.rect.left;
    await delay(3000); assert.equal(windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left, fullWidth);
    await control('move', { x: work.left + 680, y: work.top + 140, width: 650, height: 550 }); await delay(100);
    assert.equal(windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left, fullWidth);
    await delay(850);
    const restored = windowOf(await inspect(), mainHandle); assert.equal(restored.rect.right - restored.rect.left, fullWidth); assert.equal((await inspect()).foreground, hostHandle);
    snapshots.push({ name: 'drag-resize-restore', fullWidth, restored });
  });
  await check('左右屏幕边缘保持固定外侧和完整宽度，超出部分允许不可见且不覆盖目标', async () => {
    const work = windowOf(await inspect(), hostHandle).work;
    for (const side of ['right', 'left']) {
      await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: false, dockSide: side } }); await focusHost();
      await control('move', { x: side === 'right' ? work.right - 670 : work.left + 30, y: work.top + 120, width: 640, height: 550 }); await delay(200);
      const state = await inspect(), main = windowOf(state, mainHandle), target = windowOf(state, hostHandle);
      assert.ok(main.frame.right - main.frame.left >= 286 * main.dpi / 96);
      assert.equal(side === 'right' ? main.frame.left : main.frame.right, side === 'right' ? target.frame.right : target.frame.left);
      assert.ok(side === 'right' ? main.frame.right > work.right : main.frame.left < work.left, '边缘空间不足时仍位于外侧');
      assert.equal(intersects(main.frame, target.frame), false);
      assert.ok(target.zOrder < main.zOrder);
      // 探测目标内部，外侧越界时不能通过回挪侧栏换取可见性。
      const point = { x: side === 'right' ? target.frame.right - 18 : target.frame.left + 18, y: target.frame.top + 200 };
      await control('pointer', point);
      const visible = await control('probe', { pointX: point.x, pointY: point.y }); assert.equal(visible.probedWindow, hostHandle, '贴边时目标内容不能被侧栏盖住');
      assert.ok([hostHandle, mainHandle].includes(visible.foreground));
      assert.equal(windowOf(visible, mainHandle).topmost, false, '普通目标贴边时侧栏仍在普通窗口层');
      assert.equal((await invoke('get_library')).settings.pinned, false, '跟随层级不能改写置顶设置');
      snapshots.push({ name: 'edge-' + side, main, target: windowOf(visible, hostHandle) }); await page.screenshot({ path: path.join(evidence, 'edge-' + side + '.png') });
    }
    await invoke('save_settings', { settings });
    await delay(150); assert.equal(windowOf(await inspect(), mainHandle).topmost, false, '关闭吸附恢复非置顶');
  });
  if (dpiTest) {
    const monitors = (await inspect()).monitors;
    const dpiName = '固定目标 DPI 时，100%／150% 双屏往返保持用户逻辑宽度和底栏内容高度';
    if (monitors.length < 2) {
      results.push({ name: dpiName, skipped: true, reason: '只有一个显示器，未执行真实跨屏 DPI 验收' });
      console.log('跳过：' + dpiName + '（只有一个显示器）');
    } else {
      await check(dpiName, async () => {
        const original = windowOf(await inspect(), hostHandle).rect, observedDpi = new Set();
        const barPage = context.pages().find(p => p.url().includes('#common-dock'));
        for (let i = 0; i < 6; i++) await invoke('save_common_phrase', { phrase: { id: '', text: '跨屏换行验证短语' + i, position: i + 1 } });
        const assertSize = async (side, label) => poll(async () => {
          const state = await inspect(), main = windowOf(state, mainHandle), bar = windowOf(state, barHandle), target = windowOf(state, hostHandle);
          const contentHeight = await barPage.locator('.common-dock-content').evaluate(element => Math.max(28, Math.min(420, Math.ceil(element.getBoundingClientRect().height))));
          if (!main.visible || !bar.visible || Math.abs((main.rect.right - main.rect.left) * 96 / main.dpi - 380) > 1
            || side === 'right' && main.frame.left !== target.frame.right || side === 'left' && main.frame.right !== target.frame.left
            || main.frame.top !== target.frame.top || main.frame.bottom !== target.frame.bottom
            || bar.frame.left !== target.frame.left || bar.frame.right !== target.frame.right || bar.frame.top !== target.frame.bottom
            || bar.frame.bottom - bar.frame.top !== Math.max(1, Math.floor(contentHeight * bar.dpi / 96))) return false;
          assert.equal(target.dpi, 96, '目标保持固定 DPI，不能借用其值换算闪语尺寸');
          assert.equal(state.foreground, hostHandle, '跨屏跟随不抢焦点');
          assertDockOrder(state); observedDpi.add(main.dpi);
          snapshots.push({ name: label, state, contentHeight }); return state;
        }, label);
        for (const side of ['right', 'left']) {
          await invoke('save_settings', { settings }); await delay(180);
          const main = windowOf(await inspect(), mainHandle);
          await control('move', { handle: mainHandle, x: main.rect.left, y: main.rect.top, width: Math.round(380 * main.dpi / 96), height: main.rect.bottom - main.rect.top }); await delay(180);
          await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side, dockCommon: true, dockSearch: true } }); await focusHost();
          for (let round = 0; round < 10; round++) for (const [index, monitor] of monitors.entries()) {
            const work = monitor.work, width = Math.min(600, Math.floor((work.right - work.left) / 3));
            await control('move', { x: side === 'right' ? work.left + 40 : work.right - width - 40, y: work.top + 80, width, height: Math.min(400, work.bottom - work.top - 160) });
            await assertSize(side, 'dpi-' + side + '-' + round + '-' + index);
          }
        }
        assert.ok(observedDpi.has(96) && observedDpi.has(144), '专项要求实际覆盖 100% 和 150% 显示器');
        // 目标留在一屏、侧栏进入相邻另一屏，验证两者不能共用目标的缩放来源。
        const pair = monitors.flatMap(a => monitors.filter(b => a.monitor.right === b.monitor.left && Math.min(a.work.bottom, b.work.bottom) - Math.max(a.work.top, b.work.top) >= 560).map(b => [a, b]))[0];
        if (pair) for (const side of ['right', 'left']) {
          await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side, dockCommon: true, dockSearch: true } });
          const boundary = pair[0].monitor.right;
          await control('move', { x: side === 'right' ? boundary - 600 : boundary, y: Math.max(pair[0].work.top, pair[1].work.top) + 80, width: 600, height: 400 });
          const state = await assertSize(side, 'different-monitors-' + side);
          assert.notDeepEqual(windowOf(state, mainHandle).monitor, windowOf(state, hostHandle).monitor, '侧栏与目标实际分处不同显示器');
        } else {
          results.push({ name: '侧栏与目标分处不同显示器', skipped: true, reason: '未找到可容纳宿主的水平相邻显示器' });
        }
        await invoke('save_settings', { settings });
        await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top });
      });
    }
  }
} catch (error) {
  console.error(error); results.push({ name: 'failure', passed: false, error: String(error) }); process.exitCode = 1;
  snapshots.push({ name: 'failure-state', native: await inspect().catch(() => lastNative), requests: await page?.evaluate(() => window.__hoverQA).catch(() => null), events: await page?.evaluate(() => window.__hoverEvents).catch(() => null), previewText: await preview?.locator('body').innerText().catch(() => null) });
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (host && host.exitCode === null) await control('focus').then(() => control('close')).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  for (const process of [app, host]) if (process && process.exitCode === null) { await delay(150); if (process.exitCode === null) process.kill(); }
  const dpiPassed = results.some(result => result.passed && result.name.startsWith('固定目标 DPI'));
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ date: new Date().toISOString(), scope: previewOnly ? 'preview-only' : 'full', results, snapshots, limits: [dpiPassed ? '实际企业微信和200%系统缩放未操作；分屏场景是否执行以专项结果为准' : '实际企业微信、多显示器和150%/200%系统缩放未操作', '原生窗口与鼠标检查仅操作隔离宿主和隔离客户端，无剪贴板或聊天发送'] }, null, 2));
  console.log('悬停与吸附记录：' + path.join(evidence, 'report.json'));
}
