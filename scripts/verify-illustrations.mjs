import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 复用本地验收的隔离方式：只启动隐藏的 Debug 客户端，截图与数据库均留在本次目录。
// 系统发送、剪贴板、文件打开及窗口唤起被抑制；界面、SQLite 和素材加载仍使用实际代码。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'illustrations-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port; await new Promise(resolve => server.close(resolve));
const app = spawn(path.join(repo, 'src-tauri/target/debug/shanyu.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser, context, page; const views = new Set(), results = [];
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const click = async locator => { await locator.waitFor(); for (let i = 0; i < 50 && !await locator.isEnabled(); i++) await delay(50); assert.ok(await locator.isEnabled()); await locator.evaluate(el => el.click()); };
const check = async (label, fn) => { await fn(); results.push(label); console.log('通过：' + label); };
const hook = async view => view.evaluate(() => {
  if (window.__illustrationQA) return;
  const original = window.fetch.bind(window); window.__illustrationQA = { readMode: '', releaseRead: null };
  window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const cmd = decodeURIComponent(url.pathname.slice(1)); let args = JSON.parse(init.body);
    const response = (value, state = 'ok') => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': state, 'Content-Type': 'application/json' } });
    if (['plugin:window|show', 'plugin:window|set_focus', 'plugin:menu|popup', 'copy_phrase_text', 'copy_linked_files', 'open_linked_file', 'open_directory', 'native_status'].includes(cmd)) return response(null);
    if (['deliver_content', 'get_target'].includes(cmd)) throw new Error('插画验收禁止执行系统发送');
    if (cmd === 'plugin:webview|create_webview_window') args = { ...args, options: { ...args.options, visible: false, focus: false, skipTaskbar: true } };
    if (cmd === 'get_recycle_bin') {
      if (window.__illustrationQA.readMode === 'error') return response('模拟回收站读取失败', 'error');
      if (window.__illustrationQA.readMode === 'wait') await new Promise(resolve => window.__illustrationQA.releaseRead = resolve);
    }
    return original(input, { ...init, body: JSON.stringify(args) });
  };
});
async function view(route, selector) {
  let found;
  for (let i = 0; i < 100; i++) { found = context.pages().find(p => !p.isClosed() && p.url().includes('#' + route)); if (found) break; await delay(50); }
  assert.ok(found, '窗口未创建：' + route); await found.locator(selector).waitFor(); await hook(found); views.add(found); return found;
}
const screenshot = async (view, name) => {
  await view.waitForFunction(() => [...document.images].every(img => img.complete && img.naturalWidth > 0));
  await view.screenshot({ path: path.join(evidence, name + '.png') });
};
const within = async locator => assert.ok(await locator.evaluate(el => { const b = el.getBoundingClientRect(); return b.width > 0 && b.height > 0 && b.x >= 0 && b.y >= 0 && b.right <= innerWidth + 1 && b.bottom <= innerHeight + 1; }), '元素超出窗口');
const dismiss = async view => { if (await view.getByRole('status').count()) await click(view.getByRole('status')); };
const patchSettings = async patch => { const library = await invoke('get_library'); await invoke('save_settings', { settings: { ...library.settings, ...patch } }); };
async function searchRecycle(recycle, query) { await recycle.getByRole('textbox', { name: '搜索回收站' }).fill(query); await recycle.locator('.recycle-search').evaluate(el => el.requestSubmit()); }
try {
  for (let i = 0; i < 80; i++) { try { browser = await chromium.connectOverCDP('http://127.0.0.1:' + port); break; } catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); await delay(150); } }
  assert.ok(browser); context = browser.contexts()[0]; context.setDefaultTimeout(8000);
  page = context.pages().find(p => !new URL(p.url()).hash); await page.locator('.main-window').waitFor(); await hook(page); views.add(page);
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, path.join(evidence, 'data'));
  for (let i = 0; ; i++) {
    try { await patchSettings({ dock: false, dockCommon: false, dockSearch: false, pinned: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' }); break; }
    catch (error) { if (i >= 30 || !String(error).includes('服务尚未启动')) throw error; await delay(100); }
  }
  await dismiss(page);
  await check('话术空态加载96像素插画，新增入口仍打开正确编辑窗口', async () => {
    await page.setViewportSize({ width: 330, height: 720 }); await page.locator('.empty-illustration').waitFor(); await screenshot(page, 'phrase-empty');
    assert.equal(await page.locator('.empty-illustration').evaluate(el => el.clientWidth), 96); await within(page.getByRole('button', { name: '新增话术', exact: true }));
    await click(page.getByRole('button', { name: '新增话术', exact: true }));
  });
  const editor = await view('editor', '.editor-window');
  await check('快捷键教程展开显示两种按键顺序，收起与取消不保存', async () => {
    await click(editor.getByRole('button', { name: '自定义快捷键', exact: true }));
    const tutorial = editor.getByRole('button', { name: '自定义快捷键使用教程', exact: true }); assert.equal(await tutorial.getAttribute('aria-expanded'), 'false');
    await click(tutorial); assert.equal(await tutorial.getAttribute('aria-expanded'), 'true'); assert.equal(await editor.locator('.shortcut-examples figure').count(), 2);
    await screenshot(editor, 'shortcut-tutorial'); await editor.setViewportSize({ width: 480, height: 480 }); await within(editor.locator('dialog').getByRole('button', { name: '确定', exact: true })); await screenshot(editor, 'shortcut-small');
    await click(tutorial); assert.equal(await editor.locator('.shortcut-examples').count(), 0); assert.equal(await tutorial.getAttribute('aria-expanded'), 'false');
    await click(editor.locator('dialog').getByRole('button', { name: '取消', exact: true })); await click(editor.getByRole('button', { name: '取消', exact: true }));
    assert.equal((await invoke('get_library')).phrases.length, 0);
  });
  await check('文件空态加载96像素插画，新增关联入口保持可用', async () => {
    await click(page.locator('.content-tabs').getByRole('button', { name: '本地文件', exact: true })); await screenshot(page, 'files-empty');
    assert.ok((await page.locator('.empty-illustration').getAttribute('src')).endsWith('files-empty.png'));
    await click(page.getByRole('button', { name: '新增关联文件', exact: true })); const fileEditor = await view('file-editor', '.file-window');
    await click(fileEditor.getByRole('button', { name: '取消', exact: true })); assert.equal((await invoke('get_library')).files.length, 0);
  });
  await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '吸附设置', exact: true }));
  const settings = await view('settings', '.settings-window');
  await check('左右吸附示意随设置切换，侧栏与聊天窗口真实交换位置', async () => {
    for (const side of ['left', 'right']) {
      await click(settings.getByLabel(side === 'left' ? '聊天窗口左侧' : '聊天窗口右侧', { exact: true })); await settings.waitForFunction(side => document.querySelector('.dock-preview')?.dataset.side === side, side);
      const boxes = await settings.locator('.dock-preview').evaluate(el => [el.querySelector('.dock-preview-sidebar'), el.querySelector('.dock-preview-chat')].map(node => node.getBoundingClientRect().x));
      assert.equal(boxes[0] < boxes[1], side === 'left'); await screenshot(settings, 'dock-' + side);
    }
  });
  await check('底部搜索与常用短语四种开关组合同步到位置示意', async () => {
    for (const [dockSearch, dockCommon] of [[true, false], [false, true], [true, true], [false, false]]) {
      await patchSettings({ dockSearch, dockCommon }); await settings.waitForFunction(({ dockSearch, dockCommon }) => !!document.querySelector('.dock-preview-search') === dockSearch && !!document.querySelector('.dock-preview-common') === dockCommon, { dockSearch, dockCommon });
      assert.equal(await settings.locator('.dock-preview-bottom').count(), dockSearch || dockCommon ? 1 : 0);
    }
    await patchSettings({ dockSearch: true, dockCommon: true }); await settings.locator('.dock-preview-common').waitFor(); await screenshot(settings, 'dock-bottom');
  });
  await check('关于页复用64像素品牌图标，版本与数据目录入口保留', async () => {
    await click(settings.getByRole('button', { name: '关于', exact: true })); await screenshot(settings, 'about');
    assert.equal(await settings.locator('.about-brand img').evaluate(el => el.clientWidth), 64); await within(settings.getByRole('button', { name: '打开数据文件夹', exact: true }));
  });
  await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '回收站', exact: true }));
  const recycle = await view('recycle-bin', '.recycle-window');
  await check('未搜索的空回收站显示120像素插画，搜索无结果只显示提示', async () => {
    await recycle.locator('.recycle-empty-illustrated').waitFor(); await screenshot(recycle, 'recycle-empty'); assert.equal(await recycle.locator('.empty-illustration').evaluate(el => el.clientWidth), 120);
    await searchRecycle(recycle, '不存在的记录'); await recycle.getByText('没有匹配的回收记录', { exact: true }).waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0); await screenshot(recycle, 'recycle-no-results');
    await searchRecycle(recycle, ''); await recycle.locator('.recycle-empty-illustrated').waitFor();
  });
  await check('回收站读取中和读取失败均不显示空态插画', async () => {
    await recycle.evaluate(() => window.__illustrationQA.readMode = 'wait'); await patchSettings({}); await recycle.getByText('正在读取回收站…', { exact: true }).waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0);
    await recycle.waitForFunction(() => window.__illustrationQA.releaseRead); await recycle.evaluate(() => { window.__illustrationQA.readMode = ''; window.__illustrationQA.releaseRead(); }); await recycle.locator('.recycle-empty-illustrated').waitFor();
    await recycle.evaluate(() => window.__illustrationQA.readMode = 'error'); await patchSettings({}); await recycle.getByRole('status').filter({ hasText: '模拟回收站读取失败' }).waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0); assert.equal(await recycle.getByText('回收站为空', { exact: true }).count(), 0);
    await screenshot(recycle, 'recycle-read-error'); await recycle.evaluate(() => window.__illustrationQA.readMode = ''); await patchSettings({}); await recycle.locator('.recycle-empty-illustrated').waitFor(); await dismiss(recycle);
  });
  await check('七种主题下六处界面没有横向溢出，较小窗口内新增按钮可达', async () => {
    await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true })); await click(page.getByRole('button', { name: '新增话术', exact: true }));
    const themedEditor = await view('editor', '.editor-window'); await click(themedEditor.getByRole('button', { name: '自定义快捷键', exact: true })); await click(themedEditor.getByRole('button', { name: '自定义快捷键使用教程', exact: true }));
    for (const theme of ['default', 'green', 'blue', 'black', 'pink', 'lavender', 'orange']) {
      await patchSettings({ theme }); await settings.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      for (const target of [page, recycle, themedEditor]) await target.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true })); await screenshot(page, 'theme-' + theme + '-phrase');
      await click(page.locator('.content-tabs').getByRole('button', { name: '本地文件', exact: true })); await screenshot(page, 'theme-' + theme + '-files');
      await click(settings.getByRole('button', { name: '吸附设置', exact: true })); await screenshot(settings, 'theme-' + theme + '-dock');
      await click(settings.getByRole('button', { name: '关于', exact: true })); await screenshot(settings, 'theme-' + theme + '-about');
      await screenshot(recycle, 'theme-' + theme + '-recycle'); await screenshot(themedEditor, 'theme-' + theme + '-shortcut');
      for (const target of [page, settings, recycle, themedEditor]) assert.ok(await target.locator('.client-window').evaluate(el => el.scrollWidth <= el.clientWidth + 1));
      assert.ok(await themedEditor.locator('dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1));
    }
    await click(themedEditor.locator('dialog').getByRole('button', { name: '取消', exact: true })); await click(themedEditor.getByRole('button', { name: '取消', exact: true }));
    await patchSettings({ theme: 'default' }); await page.setViewportSize({ width: 300, height: 400 }); await within(page.getByRole('button', { name: '新增关联文件', exact: true })); await screenshot(page, 'files-small');
    await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true })); await within(page.getByRole('button', { name: '新增话术', exact: true })); await screenshot(page, 'phrase-small');
    await click(settings.getByRole('button', { name: '吸附设置', exact: true })); await settings.setViewportSize({ width: 480, height: 480 }); await within(settings.getByLabel('聊天窗口左侧', { exact: true })); await settings.locator('.dock-preview').scrollIntoViewIfNeeded(); await within(settings.locator('.dock-preview')); await screenshot(settings, 'dock-small');
    await click(settings.getByRole('button', { name: '关于', exact: true })); await within(settings.getByRole('button', { name: '打开数据文件夹', exact: true })); await screenshot(settings, 'about-small');
    await recycle.setViewportSize({ width: 640, height: 400 }); await within(recycle.locator('.empty-illustration')); await screenshot(recycle, 'recycle-small');
  });
  await check('话术、文件与回收站有内容时隐藏插画；普通搜索无结果保持文字提示', async () => {
    await page.setViewportSize({ width: 330, height: 720 });
    const id = await invoke('save_phrase', { phrase: { id: '', rootId: 'common', groupId: null, title: '插画状态验证', color: '#ff5100', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '仅用于隔离界面验收' }] }] }, afterId: null });
    await page.locator('.content-row').waitFor(); assert.equal(await page.locator('.content-list .empty-illustration').count(), 0);
    await page.getByRole('textbox', { name: '搜索话术、本地文件', exact: true }).fill('不存在的话术'); await page.getByText('没有找到匹配的内容', { exact: true }).waitFor(); assert.equal(await page.locator('.search-panel img').count(), 0); await click(page.getByRole('button', { name: '清空搜索', exact: true }));
    const source = path.join(evidence, '文件关联验证.txt'); await fs.writeFile(source, '隔离验收文件'); await invoke('add_files', { paths: [source], categoryId: 'local-files' }); await click(page.locator('.content-tabs').getByRole('button', { name: '本地文件', exact: true })); await page.locator('.file-row').waitFor(); assert.equal(await page.locator('.content-list .empty-illustration').count(), 0);
    await invoke('delete_item', { id, kind: 'phrase', recycle: true }); await recycle.locator('.recycle-list [role="option"]').waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0);
    for (const target of views) if (!target.isClosed()) assert.equal(await invoke('plugin:window|is_visible', {}, target), false);
  });
  const report = { date: new Date().toISOString(), scope: 'six-illustrations', checks: results.length, results, limits: ['真实WebView2与SQLite使用隔离数据，窗口保持隐藏；较小尺寸通过CDP视口验证', '七种主题逐一检查六处界面并保存截图；未重验实体快捷键、真实发送、系统DPI、多显示器和安装流程'] };
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ evidence, checks: results.length }, null, 2));
} catch (error) {
  for (const target of views) if (!target.isClosed()) await target.screenshot({ path: path.join(evidence, 'failure-' + (target.url().split('#')[1]?.split('?')[0] || 'main') + '.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ results, error: String(error), stack: error.stack }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  for (let i = 0; i < 25 && app.exitCode === null; i++) await delay(40);
  if (app.exitCode === null) app.kill();
}
