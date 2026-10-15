import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 使用隐藏的实际 Windows 客户端及独立数据库。搜索、记录、事件和高度 IPC 实际执行，
// 投递只模拟回执，绝不操作真实聊天、系统剪贴板、输入焦点或实体按键。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'search-history-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const data = path.join(evidence, 'data'), checks = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let app, browser, page, dock, libraryId;
const invoke = (cmd, args = {}, target = page) => target.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const click = async locator => { await locator.waitFor({ state: 'attached' }); assert.ok(await locator.isEnabled()); await locator.evaluate(el => el.click()); };
const setInput = (target, value) => target.locator('.search-bar input').evaluate((el, value) => {
  el.focus(); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, value);
  el.dispatchEvent(new Event('input', { bubbles: true }));
}, value);
const check = async (label, fn) => { await fn(); checks.push(label); console.log('通过：' + label); };
const record = query => invoke('record_search_history', { libraryId, query });
const read = () => invoke('get_search_history', { libraryId });
const clear = () => invoke('clear_search_history', { libraryId });

async function hook(target) {
  await target.evaluate(() => {
    const original = window.fetch.bind(window);
    window.__historyQA = { requests: [], heights: [], deliveries: [], status: 'submitted' };
    window.fetch = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input.url);
      if (url.hostname !== 'ipc.localhost') return original(input, init);
      const cmd = decodeURIComponent(url.pathname.slice(1)); const args = JSON.parse(init.body), qa = window.__historyQA;
      qa.requests.push({ cmd, args });
      if (cmd === 'search_scoped_content' && qa.searchDelay) await new Promise(resolve => setTimeout(resolve, qa.searchDelay));
      const response = value => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
      if (cmd === 'get_target') return response({ hwnd: 1, focus: 2, pid: 3 });
      if (cmd === 'deliver_content') { qa.deliveries.push(args); return response({ status: qa.status, completedSegments: qa.status === 'submitted' ? 1 : 0, completedBlocks: qa.status === 'submitted' ? 1 : 0, error: qa.status === 'submitted' ? null : '隔离验证：操作已停止' }); }
      if (['copy_phrase_text', 'copy_linked_files', 'open_linked_file'].includes(cmd)) throw new Error('隔离验证禁止系统复制或打开文件');
      if (['plugin:window|show', 'plugin:window|set_focus', 'plugin:menu|popup', 'show_phrase_preview'].includes(cmd)) return response(null);
      if (cmd === 'set_common_dock_height') { qa.heights.push(args.height); qa.geometry = args; }
      return original(input, init);
    };
  });
}
async function start() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], {
    cwd: repo, windowsHide: true, stdio: 'ignore',
    env: { ...process.env, SHANYU_TEST_DATA_DIR: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port },
  });
  browser = null;
  for (let i = 0; i < 80; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:' + port); break; }
    catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); await delay(150); }
  }
  assert.ok(browser, 'WebView2 调试连接超时');
  const context = browser.contexts()[0]; context.setDefaultTimeout(7000);
  page = context.pages().find(p => !new URL(p.url()).hash);
  assert.ok(page); await page.locator('.main-window').waitFor();
  for (let i = 0; i < 80; i++) { dock = context.pages().find(p => new URL(p.url()).hash === '#common-dock'); if (dock) break; await delay(100); }
  assert.ok(dock); await dock.locator('.common-dock').waitFor();
  await hook(page); await hook(dock);
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, data); libraryId = initial.libraryId;
  for (let i = 0; ; i++) {
    try {
      await invoke('save_settings', { libraryId, settings: { ...initial.settings, showSearch: true, showCommon: true, dockSearch: true, dockCommon: false, dock: false, pinned: false, exitSearch: false, customHotkeys: false, sequenceHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' } }); break;
    } catch (error) { if (i >= 30 || !String(error).includes('服务尚未启动')) throw error; await delay(100); }
  }
  await invoke('mark_usage_guide_shown');
  await page.locator('.search-bar input').waitFor(); await dock.locator('.search-bar input').waitFor(); await dock.locator('.search-bar input').focus();
}
async function stop() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  if (app) { for (let i = 0; i < 30 && app.exitCode === null; i++) await delay(40); if (app.exitCode === null) app.kill(); }
}
async function waitResults(target) { await target.locator('.search-panel .content-row,.dock-search-results .content-row').first().waitFor(); }

try {
  await start();
  await check('首次记录为空，不占显示空间；两处搜索均可用', async () => {
    assert.deepEqual(await read(), []); assert.equal(await page.locator('.search-history').count(), 0); assert.equal(await dock.locator('.search-history').count(), 0);
    const root = await invoke('save_category', { libraryId, category: { id: '', name: '验证分类', kind: 'phrase', parentId: null, position: 0, background: '' } });
    for (const title of ['OS支持UPS型号', 'pro-ups型号', 'OS如何连接UPS', 'OS管理员权限', 'OS恢复出厂设置', 'UGOS重置系统']) await invoke('save_phrase', { libraryId, phrase: { id: '', rootId: root, groupId: null, title, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '您好，这边为您提供 UPS SSH DNS 故障检查说明及连接方式。' }] }] }, afterId: null });
    await setInput(page, 'UPS'); await waitResults(page); assert.deepEqual(await read(), []);
  });
  await check('两处搜索仅悬停显示快捷键，相同库通知不重查，后台更新不卸载结果行', async () => {
    let current = await invoke('get_library'); const first = current.phrases[0];
    await invoke('save_phrase', { libraryId, phrase: { ...first, shortcut: 'Q+W' }, afterId: null });
    await setInput(dock, 'UPS');
    for (const target of [page, dock]) {
      await waitResults(target);
      const row = target.locator('.search-panel .content-row,.dock-search-results .content-row').filter({ has: target.locator('.row-shortcut') }).first();
      await target.mouse.move(1, 1);
      await target.waitForFunction(() => !document.querySelector('.search-panel .row-send:disabled,.dock-search-results .row-send:disabled'));
      assert.equal(await row.locator('.row-shortcut').evaluate(el => getComputedStyle(el).visibility), 'hidden');
      const textGeometry = () => row.evaluate(el => { const parent = el.getBoundingClientRect(), text = el.querySelector('.row-preview').getBoundingClientRect(); return { x: text.x - parent.x, y: text.y - parent.y, width: text.width, height: text.height }; });
      const before = await textGeometry(); await row.hover();
      assert.equal(await row.locator('.row-shortcut').evaluate(el => getComputedStyle(el).visibility), 'visible');
      assert.deepEqual(await textGeometry(), before);
      await target.mouse.move(1, 1);
      await target.evaluate(() => {
        const row = document.querySelector('.search-panel .content-row,.dock-search-results .content-row');
        window.__historyQA.row = row; window.__historyQA.searchCount = window.__historyQA.requests.filter(r => r.cmd === 'search_scoped_content').length;
      });
    }
    for (let i = 0; i < 5; i++) await invoke('plugin:event|emit', { event: 'library-changed', payload: null });
    await delay(500);
    for (const target of [page, dock]) {
      assert.equal(await target.evaluate(() => window.__historyQA.requests.filter(r => r.cmd === 'search_scoped_content').length === window.__historyQA.searchCount), true);
      assert.equal(await target.evaluate(() => window.__historyQA.row.isConnected), true);
      await target.evaluate(() => { window.__historyQA.searchDelay = 350; });
    }
    current = await invoke('get_library');
    await invoke('save_phrase', { libraryId, phrase: { ...current.phrases[0], title: current.phrases[0].title + ' 更新' }, afterId: null });
    for (const target of [page, dock]) {
      await target.waitForFunction(() => !!document.querySelector('.search-panel .row-send:disabled,.dock-search-results .row-send:disabled'));
      assert.equal(await target.evaluate(() => window.__historyQA.row.isConnected), true);
    }
    for (const target of [page, dock]) {
      await target.waitForFunction(() => !document.querySelector('.search-panel .row-send:disabled,.dock-search-results .row-send:disabled'));
      assert.equal(await target.evaluate(() => window.__historyQA.row.isConnected), true);
      await target.evaluate(() => { window.__historyQA.searchDelay = 0; });
    }
    await page.screenshot({ path: path.join(evidence, 'search-stable.png') });
  });
  await check('主窗口使用成功后记录；跨窗口事件自动刷新；失败与中断不记录', async () => {
    await click(page.locator('.search-panel .row-send').first());
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
    await page.getByRole('region', { name: '搜索记录' }).getByRole('button', { name: 'UPS', exact: true }).waitFor();
    await click(dock.getByRole('button', { name: '关闭底部搜索结果', exact: true }));
    await dock.getByRole('region', { name: '搜索记录' }).getByRole('button', { name: 'UPS', exact: true }).waitFor();
    assert.deepEqual(await read(), ['UPS']);
    for (const status of ['failed', 'interrupted']) {
      await page.evaluate(status => { window.__historyQA.status = status; }, status);
      await setInput(page, 'SSH'); await waitResults(page); await click(page.locator('.search-panel .row-send').first());
      await page.getByRole('status').filter({ hasText: '操作已停止' }).waitFor();
      assert.deepEqual(await read(), ['UPS']); await click(page.getByRole('status'));
    }
    await page.evaluate(() => { window.__historyQA.status = 'submitted'; });
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
  });
  await check('历史去空白、重复置顶和最多10条；两处标签自动换行、超高滚动且长词可查看全文', async () => {
    const terms = ['SSH', 'DNS', '网络检查', '存储检查', '日志分析', '文件传输', '设备配置', '供电状态', '连接状态', '非常长的搜索关键词用于验证省略显示并保留完整内容'.repeat(3)];
    for (const term of terms) await record(term);
    await record(' UPS '); const items = await read(); assert.equal(items.length, 10); assert.equal(items[0], 'UPS'); assert.equal(items.filter(x => x === 'UPS').length, 1);
    await page.waitForFunction(() => document.querySelectorAll('.search-history-items button').length === 10);
    await page.setViewportSize({ width: 300, height: 400 }); await dock.setViewportSize({ width: 300, height: 420 });
    for (const target of [page, dock]) {
      const geometry = await target.locator('.search-history').evaluate(el => {
        const items = el.querySelector('.search-history-items'), buttons = [...items.querySelectorAll('button')];
        const long = buttons.find(b => b.title.length > 50);
        return { height: el.getBoundingClientRect().height, tagsHeight: items.clientHeight, headerHeight: el.querySelector('header')?.getBoundingClientRect().height || 0, wrap: getComputedStyle(items).flexWrap, rows: new Set(buttons.map(b => b.offsetTop)).size, scroll: items.scrollHeight > items.clientHeight, horizontalOverflow: items.scrollWidth > items.clientWidth, long: { title: long.title, textOverflow: getComputedStyle(long).textOverflow, clipped: long.scrollWidth > long.clientWidth } };
      });
      assert.ok(geometry.tagsHeight <= 76); assert.ok(geometry.height <= (target === dock ? 107 : geometry.headerHeight + 76 + 18));
      assert.equal(geometry.wrap, 'wrap'); assert.ok(geometry.rows > 1); assert.ok(geometry.scroll); assert.equal(geometry.horizontalOverflow, false);
      assert.equal(geometry.long.textOverflow, 'ellipsis'); assert.ok(geometry.long.clipped);
    }
    await page.screenshot({ path: path.join(evidence, 'main-wrapping.png') }); await dock.screenshot({ path: path.join(evidence, 'dock-wrapping.png') });
  });
  await check('历史点击切换结果浮层，关闭恢复记录，再点同词重新查询，首条选中且不投递', async () => {
    for (const target of [page, dock]) {
      const before = await target.evaluate(() => ({ searches: window.__historyQA.requests.filter(r => r.cmd === 'search_scoped_content').length, deliveries: window.__historyQA.deliveries.length }));
      const history = target.getByRole('region', { name: '搜索记录' }).getByRole('button', { name: 'UPS', exact: true });
      for (let i = 0; i < 2; i++) {
        if (i > 0) await click(target.getByRole('button', { name: target === page ? '关闭搜索' : '关闭底部搜索结果', exact: true }));
        await click(history); await waitResults(target);
        assert.equal(await target.locator('.search-history').count(), 0, '结果浮层替换历史标签');
        await target.waitForFunction(n => window.__historyQA.requests.filter(r => r.cmd === 'search_scoped_content').length === n, before.searches + i + 1);
        assert.equal(await target.locator('.search-bar input').inputValue(), 'UPS');
        assert.equal(await target.locator('.search-bar input').evaluate(el => document.activeElement === el), true);
        assert.equal(await target.locator('.search-panel .content-row,.dock-search-results .content-row').first().getAttribute('aria-selected'), 'true');
      }
      const after = await target.evaluate(() => ({ deliveries: window.__historyQA.deliveries.length, search: window.__historyQA.requests.filter(r => r.cmd === 'search_scoped_content').at(-1).args }));
      assert.equal(after.deliveries, before.deliveries); assert.equal(after.search.scope, target === page ? 'private' : 'all');
    }
    const search = await page.locator('.search-bar').boundingBox(), panel = await page.locator('.search-panel').boundingBox(), backdrop = await page.locator('.search-backdrop').boundingBox();
    assert.ok(panel.y + panel.height <= search.y && search.y - panel.y - panel.height <= 12, '结果浮层直接指向搜索框');
    const titlebar = await page.locator('.main-titlebar').boundingBox();
    assert.equal(backdrop.y, titlebar.y + titlebar.height); assert.equal(backdrop.y + backdrop.height, search.y);
    assert.equal(await page.locator('.common-phrases').count(), 1); assert.equal(await page.locator('.search-kind').textContent(), '话术');
    const dockPanel = await dock.locator('.dock-result-panel').boundingBox(), dockInput = await dock.locator('.dock-search-bar').boundingBox();
    assert.equal(dockInput.y - dockPanel.y - dockPanel.height, 10, '底部结果尖角位于搜索框上方的间隙内');
    assert.equal(await dock.locator('.dock-result-panel > header').textContent(), '搜索结果×');
    await dock.screenshot({ path: path.join(evidence, 'dock-history-results.png') });
    await page.screenshot({ path: path.join(evidence, 'main-results-compact.png') });
    await page.setViewportSize({ width: 300, height: 720 });
    await page.screenshot({ path: path.join(evidence, 'main-results.png') });
  });
  await check('底部使用结果更新两处记录，内容高度变化通过实际Rust IPC提交', async () => {
    await setInput(dock, 'DNS'); await waitResults(dock); await click(dock.locator('.dock-search-results .row-send').first());
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
    await click(dock.getByRole('button', { name: '清空底部搜索', exact: true }));
    await page.waitForFunction(() => document.querySelector('.search-history-items button').textContent === 'DNS');
    await dock.waitForFunction(() => document.querySelector('.search-history-items button').textContent === 'DNS');
    await dock.waitForFunction(() => window.__historyQA.heights.at(-1) === Math.ceil(document.querySelector('.common-dock-content').getBoundingClientRect().height));
    const heights = await dock.evaluate(() => window.__historyQA.heights);
    assert.ok(heights.length > 1); assert.ok(new Set(heights).size > 1); assert.ok(heights.every(h => h >= 28 && h <= 420));
  });
  await check('底部标签、收起恢复、紧凑结果及420 DIP高度预算，按钮始终居中且完整可见', async () => {
    const categoryId = await invoke('save_category', { category: { id: '', name: '本地文件验证', kind: 'file', parentId: null, position: 0, background: '' } });
    const filePath = path.join(evidence, 'UPS说明.txt'); await fs.writeFile(filePath, '隔离布局验证');
    await invoke('add_files', { categoryId, paths: [filePath] });
    for (let i = 0; i < 30; i++) await invoke('save_common_phrase', { libraryId, phrase: { id: '', text: '常用短语' + i, position: i } });
    let current = await invoke('get_library');
    await invoke('save_settings', { settings: { ...current.settings, dockCommon: true, dockCommonRows: 0 } });
    for (let i = 0; i < 10; i++) await invoke('save_phrase', { libraryId, phrase: { ...current.phrases[0], id: '', title: 'UPS多行结果' + i, shortcut: '' }, afterId: null });
    await setInput(dock, 'UPS'); await waitResults(dock);
    const validate = async () => {
      await dock.waitForFunction(() => {
        const el = document.querySelector('.common-dock-content'), upper = document.querySelector('.dock-search-upper'), geometry = window.__historyQA.geometry;
        return geometry?.height === Math.ceil(el.getBoundingClientRect().height) && geometry.aboveHeight === Math.max(0, Math.ceil(upper.getBoundingClientRect().height - document.querySelector('.dock-common-slot').getBoundingClientRect().height));
      });
      const bounds = await dock.evaluate(() => {
        const rect = el => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, bottom: r.bottom, right: r.right }; };
        const bar = document.querySelector('.dock-search-bar'), result = document.querySelector('.dock-search-results');
        return { content: rect(document.querySelector('.common-dock-content')), bar: rect(bar), buttons: [...bar.querySelectorAll('button')].map(rect), result: rect(result), scroll: result.scrollHeight > result.clientHeight, common: rect(document.querySelector('.common-phrases')), geometry: window.__historyQA.geometry };
      });
      assert.ok(bounds.content.height <= 420); assert.equal(bounds.bar.height, 30); assert.ok(bounds.result.height <= 228);
      assert.ok(bounds.common.bottom <= bounds.content.bottom);
      assert.equal(bounds.geometry.aboveHeight, bounds.bar.y - bounds.common.height);
      for (const button of bounds.buttons) { assert.ok(Math.abs(button.y + button.height / 2 - bounds.bar.y - bounds.bar.height / 2) <= 1); assert.ok(button.x >= 0 && button.right <= bounds.content.right); }
      return bounds;
    };
    await dock.setViewportSize({ width: 300, height: 420 });
    assert.ok((await validate()).scroll, '窄窗口优先压缩结果区并提供滚动'); await dock.screenshot({ path: path.join(evidence, 'dock-search-narrow.png') });
    await dock.setViewportSize({ width: 988, height: 420 });
    await validate(); await dock.screenshot({ path: path.join(evidence, 'dock-search-wide.png') });
    await click(dock.getByRole('tab', { name: '本地文件', exact: true }));
    assert.equal(await dock.locator('.dock-search-results .content-row').count(), 1);
    assert.ok((await validate()).result.height < 80, '单条文件不撑大结果区');
    await dock.screenshot({ path: path.join(evidence, 'dock-search-file.png') });
    await click(dock.getByRole('button', { name: '收起底部搜索' }));
    assert.equal(await dock.locator('.search-bar input').count(), 0); assert.equal(await dock.locator('.search-history').count(), 0);
    assert.equal(await dock.locator('.common-phrases').count(), 1); assert.equal((await invoke('get_library')).settings.dockSearch, true);
    await dock.screenshot({ path: path.join(evidence, 'dock-search-collapsed.png') });
    await click(dock.getByRole('button', { name: '展开底部搜索' })); await waitResults(dock);
    assert.equal(await dock.locator('.search-bar input').inputValue(), 'UPS');
    assert.equal(await dock.getByRole('tab', { name: '本地文件' }).getAttribute('aria-selected'), 'true');
    await click(dock.getByRole('tab', { name: '话术', exact: true }));
    await setInput(dock, '不存在的搜索词'); await dock.getByText('没有找到匹配的话术', { exact: true }).waitFor();
    assert.ok((await validate()).result.height < 80);
    await click(dock.getByRole('button', { name: '清空底部搜索' }));
    current = await invoke('get_library'); await invoke('save_settings', { settings: { ...current.settings, dockCommon: false } });
    await invoke('set_common_dock_height', { height: 420, aboveHeight: 421 }, dock).then(() => assert.fail('非法向上高度必须拒绝'), error => assert.match(String(error), /高度无效/));
    await invoke('set_common_dock_height', { height: 50, aboveHeight: 20 }, page).then(() => assert.fail('非底栏窗口必须拒绝'), error => assert.match(String(error), /高度无效/));
  });
  await check('退出并重启实际客户端，记录持久保留并在两处恢复', async () => {
    const before = await read(), id = libraryId;
    await stop(); await start(); assert.equal(libraryId, id); assert.deepEqual(await read(), before);
    for (const target of [page, dock]) { await target.locator('.search-bar input').focus(); await target.waitForFunction(() => document.querySelector('.search-history-items button')?.textContent === 'DNS'); }
  });
  await check('清空搜索不删记录；清空记录不改当前输入结果，两窗口立即刷新', async () => {
    await setInput(page, 'UPS'); await waitResults(page);
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
    assert.equal(await page.locator('.search-bar input').inputValue(), ''); assert.equal((await read()).length, 10);
    await setInput(page, 'UPS'); await waitResults(page); const count = await page.locator('.search-panel .content-row').count();
    await click(dock.getByRole('button', { name: '清空搜索记录', exact: true }));
    await page.locator('.search-history').waitFor({ state: 'detached' }); await dock.locator('.search-history').waitFor({ state: 'detached' });
    assert.deepEqual(await read(), []); assert.equal(await page.locator('.search-bar input').inputValue(), 'UPS'); assert.equal(await page.locator('.search-panel .content-row').count(), count);
    await invoke('clear_search_history', { libraryId: '错误个人库' }).then(() => assert.fail('旧库操作必须拒绝'), error => assert.match(String(error), /个人库已切换/));
  });
  await check('隐藏搜索同时隐藏记录，不影响保存；客户端窗口始终隐藏', async () => {
    await record('UPS'); const current = await invoke('get_library');
    await invoke('save_settings', { libraryId, settings: { ...current.settings, showSearch: false, dockSearch: false } });
    for (const target of [page, dock]) { await target.locator('.search-history').waitFor({ state: 'detached' }); assert.equal(await invoke('plugin:window|is_visible', {}, target), false); }
    assert.deepEqual(await read(), ['UPS']); await clear();
  });
  await check('1000/10000条真实SQLite与IPC拼音搜索耗时及连续输入', async () => {
    const timings = [], execute = promisify(execFile);
    const root = await invoke('save_category', { libraryId, category: { id: '', name: '性能样本', kind: 'phrase', parentId: null, position: 99, background: '' } });
    const current = await invoke('get_library');
    await invoke('save_settings', { libraryId, settings: { ...current.settings, showSearch: true, searchMode: 'fuzzy' } });
    for (const count of [1000, 10000]) {
      await execute('python', ['-c', `import sqlite3,sys
c=sqlite3.connect(sys.argv[1]); c.execute('PRAGMA foreign_keys=ON')
with c:
 c.execute('UPDATE sync_control SET capture=0 WHERE id=1')
 for i in range(int(sys.argv[3])):
  c.execute('INSERT OR IGNORE INTO phrases VALUES(?,?,?,?,?,?,?,?)',('perf-'+str(i),sys.argv[2],None,'性能技术手册'+str(i)+' UPS设备2026','','','',i))
 c.execute('UPDATE sync_control SET capture=1 WHERE id=1')`, path.join(current.dataDir, 'shanyu.sqlite3'), root, String(count)], { windowsHide: true });
      for (const query of ['性能', 'XINGNENGJISHUSHOUCE', 'xn jssc', 'UPSsb2026']) {
        const sample = await page.evaluate(async query => { const start = performance.now(); const hits = await window.__TAURI_INTERNALS__.invoke('search_scoped_content', { query, scope: 'private', teamId: null }); return { ms: performance.now() - start, count: hits.length }; }, query);
        assert.equal(sample.count, count); timings.push({ records: count, query, ...sample });
      }
      const final = 'xingnengjishushouce' + (count - 1), started = Date.now();
      for (const value of ['x', 'xi', 'xing', 'xingneng', final]) { await setInput(page, value); await delay(35); }
      await page.waitForFunction(title => { const rows = document.querySelectorAll('.search-panel .content-row'); return rows.length === 1 && rows[0].textContent.includes(title) && document.querySelector('#search-results').getAttribute('aria-busy') === 'false'; }, '性能技术手册' + (count - 1), { timeout: 30000 });
      timings.push({ records: count, continuousInputMs: Date.now() - started, finalQuery: final, finalResults: 1 });
    }
    await fs.writeFile(path.join(evidence, 'performance.json'), JSON.stringify({ build: 'Debug custom-protocol', timings }, null, 2));
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ date: new Date().toISOString(), checks, limits: ['真实WebView2、SQLite、IPC和窗口间事件；投递仅模拟成功／失败／中断回执', '300 DIP窄视口和默认视口布局检查，不替代真实系统DPI、企业微信、吸附及安装升级验收'] }, null, 2));
  console.log(JSON.stringify({ evidence, checks: checks.length }, null, 2));
} catch (error) {
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error), stack: error.stack }, null, 2));
  throw error;
} finally { await stop(); }
