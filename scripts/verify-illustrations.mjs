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
const app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
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
// 分组后的长表单允许滚动；先用实际滚动到达控件，再检查其完整边界，不能只检查存在。
const within = async locator => { await locator.scrollIntoViewIfNeeded(); assert.ok(await locator.evaluate(el => { const b = el.getBoundingClientRect(); return b.width > 0 && b.height > 0 && b.x >= 0 && b.y >= 0 && b.right <= innerWidth + 1 && b.bottom <= innerHeight + 1; }), '元素超出窗口'); };
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
  // 隔离验收显式准备分类，不依赖产品自动初始化。
  const phraseRoot = await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  const fileRoot = await invoke('save_category', { category: { id: '', name: '默认', kind: 'file', parentId: null, position: 0, background: '' } });
  await check('话术空态加载96像素插画，新增入口仍打开正确编辑窗口', async () => {
    await page.setViewportSize({ width: 330, height: 720 }); await page.locator('.empty-illustration').waitFor(); await screenshot(page, 'phrase-empty');
    assert.equal(await page.locator('.empty-illustration').evaluate(el => el.clientWidth), 96); await within(page.getByRole('button', { name: '新增话术', exact: true }));
    await click(page.getByRole('button', { name: '新增话术', exact: true }));
  });
  const editor = await view('editor', '.editor-window');
  await check('快捷键说明展开显示两种按键顺序，收起与取消不保存', async () => {
    await click(editor.getByRole('button', { name: '自定义快捷键', exact: true }));
    const tutorial = editor.getByRole('button', { name: '自定义快捷键使用说明', exact: true }); assert.equal(await tutorial.getAttribute('aria-expanded'), 'false');
    await click(tutorial); assert.equal(await tutorial.getAttribute('aria-expanded'), 'true'); assert.equal(await editor.locator('.shortcut-examples figure').count(), 2);
    assert.equal(await editor.getByRole('button', { name: '打开视频教程', exact: true }).count(), 0);
    await screenshot(editor, 'shortcut-help'); await editor.setViewportSize({ width: 360, height: 360 });
    const dialog = editor.locator('dialog');
    assert.ok(await dialog.evaluate(el => el.scrollHeight > el.clientHeight), '窄窗口说明应可滚动');
    await dialog.locator('.shortcut-help p').last().scrollIntoViewIfNeeded(); await within(dialog.locator('.shortcut-help p').last());
    await dialog.getByRole('button', { name: '确定', exact: true }).scrollIntoViewIfNeeded(); await within(dialog.getByRole('button', { name: '确定', exact: true })); await screenshot(editor, 'shortcut-small');
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
  await check('快捷键设置移除视频入口，生效范围与全局开关对齐且提示完整可见', async () => {
    await click(settings.getByRole('button', { name: '快捷键', exact: true }));
    assert.equal(await settings.getByRole('button', { name: '使用教程', exact: true }).count(), 0);
    const scope = settings.getByText('生效范围', { exact: true }), global = settings.getByRole('checkbox', { name: '全局生效', exact: true });
    await within(scope); await within(global);
    const heading = await scope.boundingBox(), checkbox = await global.boundingBox(); assert.ok(checkbox.x > heading.x + heading.width);
    await within(settings.getByText('默认仅在闪语或目标窗口生效；勾选“全局生效”后可用于其他窗口。', { exact: true }));
    await screenshot(settings, 'shortcut-settings');
    await click(settings.getByRole('button', { name: '吸附设置', exact: true }));
  });
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
    await recycle.evaluate(() => window.__illustrationQA.readMode = 'error'); await patchSettings({}); await recycle.getByRole('alert').filter({ hasText: '模拟回收站读取失败' }).waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0); assert.equal(await recycle.getByText('回收站为空', { exact: true }).count(), 0);
    await screenshot(recycle, 'recycle-read-error'); await recycle.evaluate(() => window.__illustrationQA.readMode = ''); await patchSettings({}); await recycle.locator('.recycle-empty-illustrated').waitFor(); await dismiss(recycle);
  });
  await check('七种主题下六处界面没有横向溢出，较小窗口内新增按钮可达', async () => {
    await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true })); await click(page.getByRole('button', { name: '新增话术', exact: true }));
    const themedEditor = await view('editor', '.editor-window'); await click(themedEditor.getByRole('button', { name: '自定义快捷键', exact: true })); await click(themedEditor.getByRole('button', { name: '自定义快捷键使用说明', exact: true }));
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
    const id = await invoke('save_phrase', { phrase: { id: '', rootId: phraseRoot, groupId: null, title: '插画状态验证', color: '#ff5100', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '仅用于隔离界面验收' }] }] }, afterId: null });
    await page.locator('.content-row').waitFor(); assert.equal(await page.locator('.content-list .empty-illustration').count(), 0);
    await page.getByRole('textbox', { name: '搜索话术、本地文件', exact: true }).fill('不存在的话术'); await page.getByText('没有找到匹配的内容', { exact: true }).waitFor(); assert.equal(await page.locator('.search-panel img').count(), 0); await click(page.getByRole('button', { name: '清空搜索', exact: true }));
    const source = path.join(evidence, '文件关联验证.txt'); await fs.writeFile(source, '隔离验收文件'); await invoke('add_files', { paths: [source], categoryId: fileRoot }); await click(page.locator('.content-tabs').getByRole('button', { name: '本地文件', exact: true })); await page.locator('.file-row').waitFor(); assert.equal(await page.locator('.content-list .empty-illustration').count(), 0);
    await invoke('delete_item', { id, kind: 'phrase', recycle: true }); await recycle.locator('.recycle-list [role="option"]').waitFor(); assert.equal(await recycle.locator('.empty-illustration').count(), 0);
    for (const target of views) if (!target.isClosed()) assert.equal(await invoke('plugin:window|is_visible', {}, target), false);
  });
  const styles = (target, selector) => target.locator(selector).first().evaluate(el => { const s = getComputedStyle(el); return { color: s.color, background: s.backgroundColor, shadow: s.boxShadow, radius: s.borderRadius }; });
  await page.setViewportSize({ width: 330, height: 720 }); await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true }));
  const title = '青羽主题验证';
  const id = await invoke('save_phrase', { phrase: { id: '', rootId: phraseRoot, groupId: null, title, color: '#ff5100', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '默认与自定义颜色保持独立' }] }] }, afterId: null });
  await page.getByText(title, { exact: true }).waitFor();
  const row = page.locator(`[data-row="${id}"]`); await click(row); await page.mouse.move(315, 12);
  const preview = context.pages().find(p => p.url().includes('#phrase-preview')), candidates = context.pages().find(p => p.url().includes('#shortcut-candidates'));
  for (const target of [preview, candidates]) { assert.ok(target); await hook(target); views.add(target); }
  const item = (await invoke('get_library')).phrases.find(p => p.id === id);
  const showPreview = (theme, titleColor, fontSize = 14) => invoke('plugin:event|emit', { event: 'phrase-preview', payload: { phrase: item, fontSize, titleColor, theme, maxHeight: 180 } });
  await invoke('plugin:event|emit', { event: 'shortcut-candidates', payload: { shortcut: 'Ctrl+1', phrases: [item], selectedIndex: 0, target: { hwnd: 0, focus: 0, pid: 0 } } });
  await candidates.locator('[data-candidate]').waitFor();
  await check('七种主题共用浅色标题栏、中性文字和32像素行，主色与选中态跨窗同步', async () => {
    const colors = { default: ['rgb(42, 117, 101)', 'rgb(232, 242, 237)'], green: ['rgb(62, 114, 86)', 'rgb(233, 242, 236)'], blue: ['rgb(48, 105, 126)', 'rgb(232, 240, 244)'], black: ['rgb(69, 81, 91)', 'rgb(237, 240, 242)'], pink: ['rgb(150, 86, 114)', 'rgb(245, 237, 241)'], lavender: ['rgb(104, 98, 148)', 'rgb(240, 238, 246)'], orange: ['rgb(155, 95, 48)', 'rgb(247, 240, 234)'] };
    for (const [theme, [accent, selected]] of Object.entries(colors)) {
      await patchSettings({ theme, titleColor: '#ff5100' });
      for (const target of [page, settings, recycle, candidates]) await target.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      await showPreview(theme, '#ff5100'); await preview.locator('.phrase-preview-content').waitFor();
      await preview.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      assert.equal((await styles(page, '.main-titlebar')).background, 'rgb(247, 248, 249)');
      assert.equal((await styles(page, '.main-footer')).background, 'rgb(247, 248, 249)');
      assert.equal((await styles(page, '.content-tabs .active')).color, accent);
      assert.equal(await row.evaluate(el => el.getBoundingClientRect().height), 32);
      const text = 'rgb(38, 49, 59)';
      assert.equal((await styles(page, `[data-row="${id}"] .row-title`)).color, text);
      assert.equal((await styles(preview, 'header strong')).color, text);
      assert.equal((await styles(candidates, '[aria-selected="true"]')).background, selected);
    }
    await patchSettings({ theme: 'default' }); await page.waitForFunction(() => document.documentElement.dataset.theme === 'default');
    await showPreview('default', '#ff5100');
    assert.equal((await styles(page, `[data-row="${id}"]`)).background, 'rgb(232, 242, 237)');
    assert.ok((await styles(page, `[data-row="${id}"]`)).shadow.includes('2px'));
    assert.equal((await styles(page, '.category-tabs .active')).radius, '8px');
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === id).color, '#ff5100');
  });
  await check('全局自定义色覆盖默认标记，单条话术颜色优先，恢复默认不改记录', async () => {
    await patchSettings({ titleColor: '#652f7b' });
    await page.waitForFunction(id => getComputedStyle(document.querySelector(`[data-row="${id}"] .row-title`)).color === 'rgb(101, 47, 123)', id);
    await showPreview('default', '#652f7b'); await preview.waitForFunction(() => getComputedStyle(document.querySelector('header strong')).color === 'rgb(101, 47, 123)');
    const custom = { ...item, color: '#6b4a15', background: '#fff0c7' }; await invoke('save_phrase', { phrase: custom, afterId: null });
    await page.waitForFunction(id => getComputedStyle(document.querySelector(`[data-row="${id}"] .row-title`)).color === 'rgb(107, 74, 21)', id);
    await invoke('plugin:event|emit', { event: 'phrase-preview', payload: { phrase: custom, fontSize: 14, titleColor: '#652f7b', theme: 'default', maxHeight: 180 } });
    await preview.waitForFunction(() => getComputedStyle(document.querySelector('header strong')).color === 'rgb(107, 74, 21)');
    await invoke('save_phrase', { phrase: item, afterId: null }); await patchSettings({ titleColor: '#ff5100' }); await showPreview('default', '#ff5100');
    await page.waitForFunction(id => getComputedStyle(document.querySelector(`[data-row="${id}"] .row-title`)).color === 'rgb(38, 49, 59)', id);
  });
  await check('新库默认14号字体，保存12号后换主题仍保留，10至18号字体不撑高单行', async () => {
    await click(settings.getByRole('button', { name: '界面设置', exact: true }));
    assert.equal((await invoke('get_library')).settings.fontSize, 14);
    await patchSettings({ fontSize: 12, theme: 'blue' }); await patchSettings({ theme: 'default' });
    assert.equal((await invoke('get_library')).settings.fontSize, 12);
    for (let fontSize = 10; fontSize <= 18; fontSize++) {
      await patchSettings({ fontSize });
      await page.waitForFunction(fontSize => getComputedStyle(document.querySelector('.content-list')).fontSize === fontSize + 'px', fontSize);
      await settings.waitForFunction(fontSize => document.querySelector('select[aria-label="话术字体大小"]')?.value === String(fontSize), fontSize);
      assert.equal(await row.evaluate(el => el.getBoundingClientRect().height), 32);
    }
    await patchSettings({ fontSize: 14 });
  });
  await check('本地文件悬停覆盖选中色，移出恢复且标题与发送列不跳动', async () => {
    await click(page.locator('.content-tabs').getByRole('button', { name: '本地文件', exact: true }));
    const file = page.locator('.file-row').first(); await click(file); await page.mouse.move(315, 12);
    assert.equal((await styles(page, '.file-row')).background, 'rgb(232, 242, 237)');
    const before = await file.locator('.row-title').boundingBox(); await file.hover();
    assert.equal((await styles(page, '.file-row')).background, 'rgb(240, 243, 242)'); assert.deepEqual(await file.locator('.row-title').boundingBox(), before);
    await page.mouse.move(315, 12); assert.equal((await styles(page, '.file-row')).background, 'rgb(232, 242, 237)');
    await click(page.locator('.content-tabs').getByRole('button', { name: '话术', exact: true }));
  });
  await check('满列表、长标题与多分类在标准及最小视口保持单行，滚动后操作可达', async () => {
    for (const name of ['售前咨询', '订单服务', '名称较长的售后支持分类']) await invoke('save_category', { category: { id: '', name, kind: 'phrase', parentId: null, position: 0, background: '' } });
    const groups = [];
    for (const name of ['咨询与介绍', '跟进与确认']) groups.push(await invoke('save_category', { category: { id: '', name, kind: 'phrase', parentId: phraseRoot, position: 0, background: '' } }));
    const titles = ['欢迎咨询', '产品介绍', '价格说明', '发货时间', '开票说明', '付款方式', '订单查询', '物流查询', '使用指引', '操作步骤', '注意事项', '问题排查', '进度同步', '资料补充', '电话回访', '确认收货', '售后申请', '服务反馈', '感谢支持', '较长的话术标题与正文摘要用于验证最小窗口省略显示'];
    for (let i = 0; i < titles.length; i++) await invoke('save_phrase', { phrase: { ...item, id: '', title: titles[i], groupId: i < 6 ? null : groups[i < 13 ? 0 : 1], segments: [{ blocks: [{ kind: 'text', text: '您好，我们会为您详细说明相关信息，请稍候。' }] }] }, afterId: null });
    for (const text of ['您好', '收到，谢谢', '请稍候']) await invoke('save_common_phrase', { phrase: { id: '', text, position: 0 } });
    await page.waitForFunction(count => document.querySelectorAll('.content-list [data-row]').length === count, titles.length + 1);
    for (const [width, height, fontSize] of [[330, 720, 14], [300, 400, 10], [300, 400, 18]]) {
      await page.setViewportSize({ width, height }); await patchSettings({ fontSize });
      await page.waitForFunction(fontSize => getComputedStyle(document.querySelector('.content-list')).fontSize === fontSize + 'px', fontSize);
      assert.ok(await page.locator('.content-list').evaluate(el => el.scrollHeight > el.clientHeight));
      const rows = await page.locator('.content-list [data-row]').evaluateAll(elements => elements.map(el => ({ height: el.getBoundingClientRect().height, text: [...el.querySelectorAll('.row-title,.row-preview')].map(span => ({ height: span.getBoundingClientRect().height, whiteSpace: getComputedStyle(span).whiteSpace })) })));
      for (const row of rows) { assert.equal(row.height, 32); assert.ok(row.text.every(span => span.height <= 32 && span.whiteSpace === 'nowrap')); }
      assert.ok(await page.locator('.main-window').evaluate(el => el.scrollWidth <= el.clientWidth + 1));
      await within(page.locator('.content-list [data-row]').last()); await within(page.getByRole('button', { name: '设置', exact: true })); await screenshot(page, 'full-list-' + width + '-' + fontSize);
    }
    await page.setViewportSize({ width: 330, height: 720 }); await patchSettings({ fontSize: 14 }); await page.locator('.content-list').evaluate(el => { el.scrollTop = 0; }); await click(row); await page.mouse.move(315, 12);
  });
  await check('候选1、10和12条快照的32像素行、表头与底栏不裁切，超出10条独立滚动', async () => {
    for (const count of [1, 10, 12]) {
      const phrases = Array.from({ length: count }, (_, i) => ({ ...item, id: item.id + '-' + i, title: i % 2 ? '这是需要省略的很长的候选话术标题' : '候选' + (i + 1), segments: [{ blocks: [{ kind: 'text', text: '您好，感谢您的咨询。\n我们会尽快核实情况并回复您，感谢您的耐心等待。' }] }] }));
      await candidates.setViewportSize({ width: 370, height: 62 + 32 * Math.min(count, 10) });
      await invoke('plugin:event|emit', { event: 'shortcut-candidates', payload: { shortcut: 'Ctrl+1', phrases, selectedIndex: count - 1, target: { hwnd: 0, focus: 0, pid: 0 } } });
      await candidates.waitForFunction(count => document.querySelectorAll('[data-candidate]').length === count, count);
      assert.equal(await candidates.locator('header').evaluate(el => el.getBoundingClientRect().height), 32);
      assert.equal(await candidates.locator('footer').evaluate(el => el.getBoundingClientRect().height), 28);
      assert.equal(await candidates.locator('[data-candidate]').first().evaluate(el => el.getBoundingClientRect().height), 32);
      await within(candidates.locator('[aria-selected="true"]')); await within(candidates.locator('footer'));
      if (count > 10) assert.ok(await candidates.locator('.shortcut-candidates-list').evaluate(el => el.scrollHeight > el.clientHeight));
      const geometry = await candidates.locator('[data-candidate]').evaluateAll(rows => rows.map(row => {
        const content = row.querySelector('.candidate-content'), title = row.querySelector('.candidate-label'), preview = row.querySelector('.candidate-preview');
        return { width: content.getBoundingClientRect().width, title: title.getBoundingClientRect().width, preview: preview.getBoundingClientRect().width, overflow: preview.scrollWidth > preview.clientWidth, gap: preview.getBoundingClientRect().left - title.getBoundingClientRect().right };
      }));
      for (const rect of geometry) { assert.ok(rect.title <= rect.width * .4 + 1); assert.ok(rect.preview > 0); assert.equal(rect.gap, 8); assert.ok(rect.overflow); }
      if (count > 1) assert.ok(geometry[0].preview > geometry[1].preview, '短标题应给正文留出更多空间');
      await screenshot(candidates, 'candidate-content-' + count);
    }
    await invoke('plugin:event|emit', { event: 'shortcut-candidates', payload: { shortcut: 'Ctrl+1', phrases: [item], selectedIndex: 0, target: { hwnd: 0, focus: 0, pid: 0 } } });
    await candidates.waitForFunction(() => document.querySelectorAll('[data-candidate]').length === 1);
  });
  await check('类目排序四个控件文字居中，窄窗口保持完整可达', async () => {
    await click(settings.getByRole('button', { name: '界面设置', exact: true }));
    for (const width of [594, 480]) {
      await settings.setViewportSize({ width, height: 520 });
      const controls = settings.locator('.setting-tab-order select'); assert.equal(await controls.count(), 4);
      for (const control of await controls.all()) {
        await within(control);
        assert.deepEqual(await control.evaluate(el => ({ align: getComputedStyle(el).textAlign, last: getComputedStyle(el).textAlignLast, height: el.getBoundingClientRect().height })), { align: 'center', last: 'center', height: 32 });
      }
      await screenshot(settings, 'category-order-centered-' + width);
    }
    const before = (await invoke('get_library')).settings.filesFirst;
    const first = settings.getByRole('combobox', { name: '类目第1位', exact: true });
    await first.selectOption(before ? '话术' : '本地文件');
    await settings.waitForFunction(value => document.querySelector('[aria-label="类目第1位"]').value === value, before ? '话术' : '本地文件');
    assert.equal((await invoke('get_library')).settings.filesFirst, !before);
    await patchSettings({ filesFirst: before });
  });
  await check('永久删除确认与候选读取错误使用红色，悬停仍保持危险操作配色', async () => {
    await click(recycle.locator('.recycle-list [role="option"]').first()); await recycle.getByRole('button', { name: '永久删除该记录', exact: true }).waitFor();
    await click(recycle.getByRole('button', { name: '永久删除该记录', exact: true }));
    const danger = recycle.getByRole('dialog', { name: '永久删除', exact: true }), primary = danger.getByRole('button', { name: '确定', exact: true });
    assert.equal(await primary.evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(184, 67, 67)'); assert.equal(await primary.evaluate(el => getComputedStyle(el).color), 'rgb(255, 255, 255)');
    await primary.hover(); assert.ok(await primary.evaluate(el => { const rgb = getComputedStyle(el).backgroundColor.match(/[\d.]+/g).map(Number); return rgb[0] > rgb[1] && rgb[0] > rgb[2]; }));
    await screenshot(recycle, 'theme-default-danger'); await recycle.keyboard.press('Escape'); await danger.waitFor({ state: 'detached' });
    // 显示快照没有原生待选状态，实际只读选择命令应返回错误，不触发发送。
    assert.equal(await invoke('get_shortcut_candidates'), null); await click(candidates.locator('[data-candidate]').first()); await candidates.locator('.candidate-error').waitFor();
    assert.equal(await candidates.locator('footer').evaluate(el => getComputedStyle(el).color), 'rgb(184, 67, 67)'); await screenshot(candidates, 'theme-default-candidate-error');
    await invoke('plugin:event|emit', { event: 'shortcut-candidates', payload: { shortcut: 'Ctrl+1', phrases: [item], selectedIndex: 0, target: { hwnd: 0, focus: 0, pid: 0 } } }); await candidates.locator('.candidate-error').waitFor({ state: 'detached' });
  });
  await check('主题默认正文、标题、提示与七种主题实心按钮白字对比度达到4.5比1', async () => {
    const palette = await page.evaluate(() => { const s = getComputedStyle(document.documentElement); return Object.fromEntries(['surface', 'panel', 'text', 'muted', 'title', 'accent', 'selection', 'hover'].map(key => [key, s.getPropertyValue('--theme-' + key).trim()])); });
    const luminance = hex => { const digits = hex.slice(1); const normalized = digits.length === 3 ? [...digits].map(c => c + c).join('') : digits; const values = [0, 2, 4].map(i => parseInt(normalized.slice(i, i + 2), 16) / 255).map(v => v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4); return values[0] * .2126 + values[1] * .7152 + values[2] * .0722; };
    for (const [foreground, background] of [[palette.text, palette.surface], [palette.muted, palette.panel], [palette.muted, palette.selection], [palette.title, palette.hover], [palette.title, palette.selection], ...['#2a7565', '#3e7256', '#30697e', '#45515b', '#965672', '#686294', '#9b5f30'].map(color => ['#ffffff', color])]) {
      const [light, dark] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
      assert.ok((light + .05) / (dark + .05) >= 4.5, `${foreground} / ${background} 对比度不足`);
    }
    for (const target of [page, preview, candidates]) assert.ok(await target.locator('section').first().evaluate(el => el.scrollWidth <= el.clientWidth + 1));
    await dismiss(page); await screenshot(page, 'theme-default-main');
    await preview.setViewportSize({ width: 300, height: 180 }); await screenshot(preview, 'theme-default-preview');
    await candidates.setViewportSize({ width: 370, height: 94 }); await screenshot(candidates, 'theme-default-candidates');
    await settings.setViewportSize({ width: 594, height: 620 }); await click(settings.getByRole('button', { name: '界面设置', exact: true })); await settings.locator('.settings-content').evaluate(el => { el.scrollTop = 0; }); await screenshot(settings, 'theme-default-settings');
  });
  const report = { date: new Date().toISOString(), scope: 'illustrations-and-theme', checks: results.length, results, limits: ['真实WebView2与SQLite使用隔离数据，窗口保持隐藏；较小尺寸通过CDP视口验证', '七种主题逐一检查六处界面并保存截图；预览与候选种入显示快照，未重验实体快捷键、真实发送、系统DPI、多显示器和安装流程'] };
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
