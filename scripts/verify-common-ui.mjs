import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 隔离数据库、真实 Win32 点击和独立菜单；投递拦截为模拟回执，不操作聊天和剪贴板。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'common-ui-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
let app, host, browser, page, dock, menu, libraryId, nonce = 0, handles = {};
const poll = async (fn, label) => { for (let i = 0; i < 160; i++) { const value = await fn(); if (value) return value; await delay(35); } throw new Error('等待超时：' + label); };
const read = async () => { try { return JSON.parse(await fs.readFile(output, 'utf8')); } catch { return null; } };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: Object.values(handles), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (e) { if (i > 20 || !['EACCES', 'EPERM', 'EBUSY'].includes(e.code)) throw e; await delay(20); } }
  return poll(async () => { const s = await read(); return s?.nonce === request.nonce && s; }, command);
};
const inspect = () => control('inspect');
const windowOf = (s, name) => s.windows.find(w => w.handle === handles[name]);
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
const save = async patch => { const { settings } = await invoke('get_library'); await invoke('save_settings', { settings: { ...settings, ...patch } }); await delay(100); };
const clickNative = async (view, name, locator, command = 'click') => {
  const box = await locator.boundingBox(), w = windowOf(await inspect(), name), scale = w.dpi / 96;
  assert.ok(box); const x = Math.round(w.client.x + (box.x + box.width / 2) * scale), y = Math.round(w.client.y + (box.y + box.height / 2) * scale);
  await control(command, { x, y }); await delay(120);
};
const check = async (name, fn) => { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); };
const geometry = async () => {
  const s = await inspect(), w = windowOf(s, 'dock');
  const dom = await dock.evaluate(() => {
    const rect = selector => { const r = document.querySelector(selector).getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, bottom: r.bottom }; };
    return { input: rect('.dock-search-bar'), common: rect('.dock-common-slot'), upper: rect('.dock-search-upper'), content: rect('.common-dock-content') };
  });
  return { ...dom, screenY: w.client.y + Math.round(dom.input.y * w.dpi / 96), native: w };
};
const menuShown = () => poll(async () => windowOf(await inspect(), 'menu').visible, '菜单显示');
const menuHidden = () => poll(async () => !windowOf(await inspect(), 'menu').visible, '菜单关闭');
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' }); await poll(read, '宿主');
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('客户端提前退出'); return null; } }, 'WebView');
  const pages = browser.contexts()[0]; pages.setDefaultTimeout(6000);
  page = await poll(() => pages.pages().find(p => !new URL(p.url()).hash), '主窗口'); await page.locator('.main-window').waitFor();
  dock = await poll(() => pages.pages().find(p => p.url().includes('#common-dock')), '底栏');
  menu = await poll(() => pages.pages().find(p => p.url().includes('#common-menu')), '菜单');
  for (const [name, title] of [['main', '闪语'], ['dock', '常用短语 · 闪语'], ['menu', '短语菜单 · 闪语']]) handles[name] = (await native(['-ProcessId', String(app.pid), '-Title', title])).handle;
  await invoke('mark_usage_guide_shown'); libraryId = (await invoke('get_library')).libraryId;
  const root = await invoke('save_category', { libraryId, category: { id: '', name: '搜索测试', kind: 'phrase', parentId: null, position: 0, background: '' } });
  for (let i = 0; i < 18; i++) await invoke('save_phrase', { libraryId, phrase: { id: '', rootId: root, groupId: null, title: '搜索测试' + i, color: '', background: '', shortcut: '', position: i, segments: [{ blocks: [{ kind: 'text', text: '测试内容' }] }] }, afterId: null });
  for (let i = 0; i < 24; i++) await invoke('save_common_phrase', { libraryId, phrase: { id: '', text: '常用短语' + i, position: i } });
  for (const query of ['搜索测试', '连接问题', '设备状态', '使用帮助', '文件处理', '网络诊断', '共享权限', '存储空间', '登录异常', '同步进度']) await invoke('record_search_history', { libraryId, query });
  await save({ dock: true, dockCommon: true, dockSearch: true, dockCommonRows: 0, pinned: false, showCommon: true, showSearch: true });
  await control('move', { x: 30, y: 40, width: 988, height: 620 }); await control('focus');
  await poll(async () => windowOf(await inspect(), 'dock').visible, '底栏吸附');
  for (const view of [page, dock]) await view.evaluate(() => {
    const fetch = window.fetch.bind(window); window.__commonQA = { deliveries: [] };
    window.fetch = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input.url);
      const response = value => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
      if (url.hostname === 'ipc.localhost' && url.pathname === '/get_target') return response({ hwnd: 1, focus: 2, pid: 3 });
      if (url.hostname === 'ipc.localhost' && url.pathname === '/deliver_content') { window.__commonQA.deliveries.push(JSON.parse(init.body)); return response({ status: 'submitted', completedSegments: 1, completedBlocks: 1 }); }
      return fetch(input, init);
    };
  });
  await check('主窗口历史默认隐藏，点击搜索才显示，内部选择可用且离开或窗口失焦收起', async () => {
    // 安装版可能占用默认快捷键，先关闭隔离实例的冲突提示，避免挡住搜索点击点。
    if (await page.getByRole('status').count()) await clickNative(page, 'main', page.getByRole('status'));
    assert.equal(await page.locator('.search-history').count(), 0);
    await page.screenshot({ path: path.join(evidence, 'main-history-idle.png') });
    const input = page.locator('#search-bar input'), before = await input.boundingBox();
    await clickNative(page, 'main', input); await page.locator('.search-history').waitFor();
    assert.equal((await input.boundingBox()).y, before.y);
    await page.screenshot({ path: path.join(evidence, 'main-history-active.png') });
    await clickNative(page, 'main', page.locator('.titlebar-spacer')); await page.locator('.search-history').waitFor({ state: 'detached' });
    await clickNative(page, 'main', input); await page.locator('.search-history').waitFor();
    await control('focus'); await page.locator('.search-history').waitFor({ state: 'detached' });
    await clickNative(page, 'main', input); await page.locator('.search-history').waitFor();
    await clickNative(page, 'main', page.locator('.search-history').getByRole('button', { name: '搜索测试', exact: true }));
    await page.locator('.search-panel .content-row').first().waitFor();
    assert.equal(await input.inputValue(), '搜索测试'); assert.equal(await page.locator('.search-history').count(), 0);
    assert.equal(await page.evaluate(() => window.__commonQA.deliveries.length), 0);
    await clickNative(page, 'main', page.getByRole('button', { name: '关闭搜索', exact: true })); await page.locator('.search-history').waitFor();
    await control('focus'); await page.locator('.search-history').waitFor({ state: 'detached' });
  });
  await check('主窗口300/330/600 DIP覆盖浮层不改变列表与搜索框，遮罩不穿透', async () => {
    await save({ dock: false, pinned: true, edgeAutoHide: false, dockCommon: false, dockSearch: false });
    await invoke('window_action', { action: 'show' });
    await control('raise', { handle: handles.main });
    for (const width of [300, 330, 600]) {
      const w = windowOf(await inspect(), 'main'), scale = w.dpi / 96;
      await control('move', { handle: handles.main, x: 80, y: 80, width: Math.round(width * scale) + w.rect.right - w.rect.left - w.clientRect.right, height: 720 });
      await control('focus', { handle: handles.main }); await delay(150);
      const rects = () => page.evaluate(() => Object.fromEntries(['.list-area', '#search-bar', '.common-phrases'].map(selector => { const r = document.querySelector(selector).getBoundingClientRect(); return [selector, { x: r.x, y: r.y, width: r.width, height: r.height }]; })));
      const before = await rects();
      await clickNative(page, 'main', page.locator('#search-bar input'));
      await page.locator('.main-search-history').waitFor();
      assert.deepEqual(await rects(), before);
      const geometry = await page.locator('.main-search-history').evaluate(el => {
        const r = el.getBoundingClientRect(), title = document.querySelector('.main-titlebar').getBoundingClientRect(), search = document.querySelector('#search-bar').getBoundingClientRect(), header = el.querySelector('header'), items = el.querySelector('.search-history-items');
        return { width: r.width, top: r.top, bottom: r.bottom, titleBottom: title.bottom, inputTop: search.top, headerFits: header.scrollWidth === header.clientWidth, tagsHeight: items.clientHeight, hit: document.elementFromPoint(r.x + 5, r.y + 5).closest('.main-search-history') !== null };
      });
      assert.ok(Math.abs(geometry.width - width) <= 2); assert.ok(geometry.top >= geometry.titleBottom && geometry.bottom + 10 <= geometry.inputTop + 1);
      assert.ok(geometry.headerFits && geometry.hit && geometry.tagsHeight <= 76);
      await page.screenshot({ path: path.join(evidence, 'main-history-' + width + '.png') });
      await clickNative(page, 'main', page.locator('.search-history').getByRole('button', { name: '搜索测试', exact: true }));
      await page.locator('.search-panel .content-row').first().waitFor(); assert.deepEqual(await rects(), before);
      await clickNative(page, 'main', page.getByRole('button', { name: '关闭搜索', exact: true }));
      assert.deepEqual(await rects(), before);
      const count = await page.evaluate(() => window.__commonQA.deliveries.length);
      await clickNative(page, 'main', page.locator('.search-backdrop'));
      await page.locator('.main-search-history').waitFor({ state: 'detached' });
      assert.equal(await page.evaluate(() => window.__commonQA.deliveries.length), count);
      snapshots.push({ width, mainGeometry: geometry, rectangles: before });
    }
  });
  await check('独立清空确认取消、失败留窗、重复提交保护、实时清空与安全焦点恢复', async () => {
    const openConfirm = async () => {
      await clickNative(page, 'main', page.locator('#search-bar input'));
      await clickNative(page, 'main', page.getByRole('button', { name: '清空搜索记录' }));
      const dialog = await poll(() => pages.pages().find(p => p.url().includes('#search-history-confirm')), '清空确认');
      await dialog.getByRole('button', { name: '取消', exact: true }).waitFor();
      handles.confirm = (await native(['-ProcessId', String(app.pid), '-Title', '提示'])).handle;
      return dialog;
    };
    let dialog = await openConfirm();
    assert.equal(await dialog.evaluate(() => document.activeElement.textContent), '取消');
    const box = await dialog.locator('.search-history-confirm').boundingBox(); assert.equal(box.width, 384); assert.equal(box.height, 256);
    const firstHandle = handles.confirm;
    await invoke('open_search_history_confirm', { libraryId }); assert.equal(handles.confirm, firstHandle);
    assert.equal(pages.pages().filter(p => p.url().includes('#search-history-confirm')).length, 1);
    await dialog.screenshot({ path: path.join(evidence, 'clear-history-confirm.png') });
    await clickNative(dialog, 'confirm', dialog.getByRole('button', { name: '取消', exact: true }));
    await poll(() => dialog.isClosed(), '取消关闭');
    assert.equal((await invoke('get_search_history', { libraryId })).length, 10);
    await page.locator('.main-search-history').waitFor(); assert.equal(await page.evaluate(() => document.activeElement.matches('#search-bar input')), true);
    dialog = await openConfirm();
    await dialog.evaluate(() => {
      const fetch = window.fetch.bind(window); window.__clearQA = { fail: true, count: 0 };
      window.fetch = async (input, init) => {
        const url = new URL(typeof input === 'string' ? input : input.url);
        if (url.hostname === 'ipc.localhost' && url.pathname === '/clear_search_history') {
          window.__clearQA.count++;
          if (window.__clearQA.fail) { await new Promise(r => setTimeout(r, 300)); return new Response(JSON.stringify('隔离测试：磁盘写入失败'), { headers: { 'Tauri-Response': 'error', 'Content-Type': 'application/json' } }); }
        }
        return fetch(input, init);
      };
    });
    await clickNative(dialog, 'confirm', dialog.getByRole('button', { name: '确定', exact: true }), 'double-click');
    await dialog.getByRole('alert').waitFor();
    assert.equal(await dialog.evaluate(() => window.__clearQA.count), 1);
    assert.equal((await invoke('get_search_history', { libraryId })).length, 10);
    await invoke('record_search_history', { libraryId, query: '确认打开后新增' });
    await dialog.evaluate(() => { window.__clearQA.fail = false; });
    await clickNative(dialog, 'confirm', dialog.getByRole('button', { name: '确定', exact: true }));
    await poll(() => dialog.isClosed(), '清空成功关闭');
    assert.deepEqual(await invoke('get_search_history', { libraryId }), []);
    await page.locator('.main-search-history').waitFor(); assert.equal(await page.getByRole('button', { name: '清空搜索记录' }).isDisabled(), true);
    assert.equal(await dock.locator('.search-history-items button').count(), 0);
    await page.screenshot({ path: path.join(evidence, 'main-history-empty.png') });
    await invoke('record_search_history', { libraryId, query: '取消保留' });
    dialog = await openConfirm(); await control('focus');
    await invoke('close_search_history_confirm', {}, dialog).catch(e => { if (!dialog.isClosed()) throw e; }); await poll(() => dialog.isClosed(), '外部焦点关闭');
    assert.notEqual((await inspect()).foreground, handles.main);
    dialog = await openConfirm();
    const dataDir = (await invoke('get_library')).dataDir;
    await run('python', ['-c', "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute(\"UPDATE sync_meta SET value='isolated-new-library' WHERE key='libraryId'\"); c.commit()", path.join(dataDir, 'shanyu.sqlite3')], { windowsHide: true });
    await poll(() => dialog.isClosed(), '切库失效');
    await run('python', ['-c', "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute(\"UPDATE sync_meta SET value=? WHERE key='libraryId'\",(sys.argv[2],)); c.commit()", path.join(dataDir, 'shanyu.sqlite3'), libraryId], { windowsHide: true });
    assert.deepEqual(await invoke('get_search_history', { libraryId }), ['取消保留']);
    dialog = await openConfirm(); await invoke('window_action', { action: 'hide' }); await poll(() => dialog.isClosed(), '来源隐藏关闭');
    for (const query of ['搜索测试', '连接问题', '设备状态', '使用帮助', '文件处理', '网络诊断', '共享权限', '存储空间', '登录异常', '同步进度']) await invoke('record_search_history', { libraryId, query });
    delete handles.confirm;
    await control('unpin', { handle: handles.main }); await save({ dock: true, pinned: false, dockCommon: true, dockSearch: true });
    await invoke('window_action', { action: 'show' }); await control('focus');
  });
  await check('宽窄底栏保留多行配置，聚焦历史及结果展开时输入框屏幕坐标不变', async () => {
    for (const width of [988, 360]) {
      await control('move', { x: 30, y: 40, width, height: 620 }); await control('focus'); await delay(250);
      for (const rows of [1, 2, 3, 0]) {
        await save({ dockCommonRows: rows }); await control('focus'); await delay(200);
        const before = await geometry(); assert.equal(before.input.height, 30); assert.equal(before.common.bottom, before.input.y);
        assert.ok(before.common.height <= (rows ? rows * 27 + 1 : 150));
        await clickNative(dock, 'dock', dock.locator('.dock-search-bar input'));
        await dock.locator('.search-history').waitFor(); await delay(100);
        const history = await geometry(); assert.equal(history.screenY, before.screenY);
        const tags = await dock.locator('.search-history-items').evaluate(el => ({ rows: new Set([...el.children].map(b => b.offsetTop)).size, overflow: el.scrollWidth > el.clientWidth }));
        assert.equal(tags.overflow, false); assert.ok(width === 360 ? tags.rows > 1 : tags.rows === 1);
        await clickNative(dock, 'dock', dock.locator('.search-history').getByRole('button', { name: '搜索测试', exact: true }));
        await dock.locator('.dock-search-results .content-row').first().waitFor(); await delay(150);
        const expanded = await geometry(); assert.equal(expanded.screenY, before.screenY); assert.ok(expanded.content.height <= 420);
        assert.equal(await dock.locator('.search-history').count(), 0);
        assert.equal(await dock.locator('.dock-result-panel > header').textContent(), '搜索结果×');
        assert.equal(await dock.evaluate(() => { const r = document.querySelector('.dock-result-panel').getBoundingClientRect(); return document.elementFromPoint(r.x + 5, r.y + 5).closest('.common-phrases') !== null; }), false);
        if (rows === 0) { await dock.screenshot({ path: path.join(evidence, 'search-' + width + '.png') }); snapshots.push({ width, before, history, expanded }); }
        if (rows === 0 && width === 360) {
          await dock.locator('.search-bar input').fill('搜索测试17');
          await dock.waitForFunction(() => document.querySelectorAll('.dock-search-results .content-row').length === 1); await delay(150);
          assert.equal((await geometry()).screenY, before.screenY);
          await dock.screenshot({ path: path.join(evidence, 'search-single-result.png') });
        }
        await clickNative(dock, 'dock', dock.getByRole('button', { name: '关闭底部搜索结果' }));
        await dock.locator('.search-history').waitFor(); await delay(100);
        assert.equal((await geometry()).screenY, before.screenY); assert.equal(await dock.locator('.search-bar input').inputValue(), '');
        await dock.locator('.search-bar input').press('Escape'); await delay(100); assert.equal((await geometry()).screenY, before.screenY);
        await control('focus'); await delay(100); assert.equal(await dock.locator('.search-history').count(), 0);
        if (rows === 0) await dock.screenshot({ path: path.join(evidence, 'idle-' + width + '.png') });
      }
    }
  });
  await check('两处旧单击配置均只插入一次，双击不重复，真实右键菜单只有四个管理操作', async () => {
    await control('move', { x: 30, y: 40, width: 988, height: 620 });
    for (const mode of ['select', 'send']) {
      await save({ commonSingleClick: mode });
      for (const [view, name] of [[page, 'main'], [dock, 'dock']]) {
        await control('focus'); await delay(120);
        await view.evaluate(() => { window.__commonQA.deliveries = []; });
        const phrase = view.locator('[data-common]').first();
        await clickNative(view, name, phrase, 'double-click');
        await poll(async () => (await view.evaluate(() => window.__commonQA.deliveries.length)) === 1, '插入回执');
        assert.equal(await view.evaluate(() => window.__commonQA.deliveries[0].mode), 'insert');
        const targetBefore = await invoke('get_target', {}, menu);
        await clickNative(view, name, phrase, 'right-click'); await menuShown();
        assert.deepEqual(await invoke('get_target', {}, menu), targetBefore, '菜单不能改写原聊天目标');
        assert.deepEqual(await menu.getByRole('menuitem').allTextContents(), ['新增个人短语', '修改这个短语', '删除这个短语', '快速排序短语']);
        assert.equal(await view.evaluate(() => window.__commonQA.deliveries.length), 1);
        await menu.screenshot({ path: path.join(evidence, 'menu-' + name + '.png') });
        const w = windowOf(await inspect(), 'menu'); assert.equal(w.appWindow, false);
        assert.ok(w.frame.left >= w.work.left && w.frame.right <= w.work.right && w.frame.bottom <= w.work.bottom);
        await menu.getByRole('menuitem').first().press('Escape'); await menuHidden();
        assert.equal((await inspect()).foreground, handles[name]);
      }
    }
  });
  await check('空白菜单、键盘导航与来源移动关闭；旧请求和非法窗口不能执行菜单动作', async () => {
    await control('focus'); await delay(120);
    await clickNative(dock, 'dock', dock.getByRole('button', { name: '常用短语菜单' })); await menuShown();
    assert.deepEqual(await menu.getByRole('menuitem').allTextContents(), ['新增个人短语', '快速排序短语']);
    await menu.getByRole('menuitem').first().press('End'); assert.equal(await menu.evaluate(() => document.activeElement.textContent), '快速排序短语');
    const old = await invoke('get_common_menu', {}, menu);
    await menu.getByRole('menuitem').last().press('Tab'); await menuHidden();
    await invoke('choose_common_menu', { requestId: old.requestId, action: 'add' }, menu).then(() => assert.fail('旧菜单应失效'), e => assert.match(String(e), /失效/));
    await invoke('get_common_menu').then(() => assert.fail('来源不能读取菜单状态'), e => assert.match(String(e), /不能读取/));
    await clickNative(dock, 'dock', dock.locator('[data-common]').first(), 'right-click'); await menuShown();
    await control('move', { x: 45, y: 50, width: 988, height: 620 }); await menuHidden();
    await clickNative(dock, 'dock', dock.locator('[data-common]').first(), 'right-click'); await menuShown();
    await control('focus'); await menuHidden();
  });
  await check('贴边展开后菜单及清空确认暂停收起，屏幕右侧避让且Esc后恢复自动隐藏', async () => {
    await save({ dock: false, pinned: true, edgeAutoHide: true, edgeRevealDelay: 0 });
    await invoke('window_action', { action: 'show' }); await delay(250);
    await control('raise', { handle: handles.main }); await control('focus', { handle: handles.main });
    const m = windowOf(await inspect(), 'main'), box = await page.locator('.titlebar-spacer').first().boundingBox(), scale = m.dpi / 96;
    const fromX = Math.round(m.client.x + (box.x + box.width / 2) * scale), fromY = Math.round(m.client.y + (box.y + box.height / 2) * scale);
    await control('drag', { handle: handles.main, fromX, fromY, x: fromX + m.work.right - m.frame.right, y: fromY });
    await control('pointer', { x: m.work.left + 50, y: 20 });
    await poll(async () => { const w = windowOf(await inspect(), 'main'); return w.work.right - w.clip.left <= 2 * scale + 1; }, '主窗口收起');
    await control('pointer', { x: m.work.right - 1, y: m.frame.top + 80 });
    await poll(async () => windowOf(await inspect(), 'main').frame.right === m.work.right, '主窗口展开');
    await clickNative(page, 'main', page.locator('[data-common]').first(), 'right-click'); await menuShown();
    await control('pointer', { x: m.work.left + 50, y: 20 }); await delay(400);
    assert.equal(windowOf(await inspect(), 'main').frame.right, m.work.right);
    const popup = windowOf(await inspect(), 'menu'); assert.ok(popup.frame.right <= popup.work.right && popup.frame.bottom <= popup.work.bottom);
    await control('pointer', { x: m.work.right - 30, y: m.frame.top + 100 });
    await menu.getByRole('menuitem').first().press('Escape'); await menuHidden();
    await control('pointer', { x: m.work.right - 1, y: m.frame.top + 80 });
    await poll(async () => windowOf(await inspect(), 'main').frame.right === m.work.right, '清空前贴边展开');
    await clickNative(page, 'main', page.locator('#search-bar input'));
    await clickNative(page, 'main', page.getByRole('button', { name: '清空搜索记录' }));
    const dialog = await poll(() => pages.pages().find(p => p.url().includes('#search-history-confirm')), '贴边清空确认');
    await dialog.getByRole('button', { name: '取消', exact: true }).waitFor();
    await control('pointer', { x: m.work.left + 50, y: 20 }); await delay(400);
    assert.equal(windowOf(await inspect(), 'main').frame.right, m.work.right);
    await dialog.getByRole('button', { name: '取消', exact: true }).press('Escape');
    await poll(() => dialog.isClosed(), 'Esc取消清空确认');
    await control('focus');
    await poll(async () => { const w = windowOf(await inspect(), 'main'); return w.work.right - w.clip.left <= 2 * scale + 1; }, '确认关闭后自动收起');
  });
} catch (error) {
  results.push({ passed: false, error: String(error), stack: error.stack }); process.exitCode = 1; console.error(error);
  snapshots.push({ name: 'failure', state: await inspect().catch(() => null) });
  snapshots.push({ name: 'search-focus', state: await page?.evaluate(() => ({ focused: document.hasFocus(), active: document.activeElement?.tagName, history: !!document.querySelector('.search-history') })).catch(() => null) });
  await dock?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
} finally {
  await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (app) await poll(() => app.exitCode !== null, '退出').catch(() => {});
  await browser?.close().catch(() => {});
  if (host?.exitCode === null) await control('close').catch(() => {});
  for (const child of [app, host]) if (child?.exitCode === null) child.kill();
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ results, snapshots, limits: ['投递仅模拟回执，不操作真实聊天或剪贴板', '实际100%系统DPI；多屏及其他系统DPI未验收'] }, null, 2));
  console.log('验收记录：' + evidence);
}
