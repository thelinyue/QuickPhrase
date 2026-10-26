import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 隔离数据、隐藏窗口，只通过 CDP 操作 DOM。原生菜单实际创建和读取，弹出与实体点击不执行。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'category-menus-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const server = net.createServer();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;
await new Promise(resolve => server.close(resolve));
const app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser, page, settings; const results = [], menus = [];
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const transfer = async (cmd, args = {}, devicePart = false) => {
  const [library, account] = await Promise.all([invoke('get_library'), invoke('get_team_status')]);
  return invoke(cmd, { merge: false, restoreSettings: false, operationId: randomUUID(), ...args, target: { source: 'private', libraryId: library.libraryId, sessionEpoch: account.sessionEpoch, devicePart } });
};
const check = (label, fn) => fn().then(() => { results.push(label); console.log('通过：' + label); });
const click = async locator => { await locator.waitFor(); for (let i = 0; i < 30 && !(await locator.isEnabled()); i++) await delay(50); assert.ok(await locator.isEnabled()); await locator.evaluate(el => el.click()); };
const setInput = async (view, selector, value) => view.locator(selector).evaluate((el, value) => { const prototype = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; const setter = Object.getOwnPropertyDescriptor(prototype, 'value').set; setter.call(el, value); el.dispatchEvent(new Event('input', { bubbles: true })); }, value);
const submit = async () => { await page.locator('dialog form').evaluate(form => form.requestSubmit()); await page.locator('dialog').waitFor({ state: 'detached' }); };
async function openMenu(locator, right = false) {
  await page.evaluate(() => { window.__categoryQA.menu = null; document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })); });
  await page.locator('.context-menu').waitFor({ state: 'detached' });
  if (right) await locator.evaluate(el => { const box = el.getBoundingClientRect(); el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: box.x + box.width / 2, clientY: box.y + box.height / 2 })); });
  else await click(locator);
  await page.waitForFunction(() => document.querySelector('.category-context-menu') || window.__categoryQA.menu?.ready);
  if (await page.locator('.category-context-menu').count()) {
    const items = await page.locator('.category-context-menu button').evaluateAll(buttons => buttons.map(b => ({ text: b.textContent, enabled: !b.disabled })));
    await page.evaluate(items => { window.__categoryQA.menu = { kind: 'dom', ready: true, items }; }, items);
    menus.push(items); return items;
  }
  const native = await page.evaluate(async () => {
    const menu = window.__categoryQA.menu;
    const entries = await window.__TAURI_INTERNALS__.invoke('plugin:menu|items', { rid: menu.rid, kind: 'Menu' });
    return Promise.all(entries.map(async ([rid, id, kind]) => ({ rid, id, kind, text: await window.__TAURI_INTERNALS__.invoke('plugin:menu|text', { rid, kind }), enabled: await window.__TAURI_INTERNALS__.invoke('plugin:menu|is_enabled', { rid, kind }) })));
  });
  menus.push(native.map(({ text, enabled }) => ({ text, enabled }))); return native;
}
const action = async text => {
  if (await page.evaluate(() => window.__categoryQA.menu?.kind === 'dom')) { await click(page.getByRole('menuitem', { name: text, exact: true })); return; }
  await page.evaluate(text => {
  const item = window.__categoryQA.menu.items.find(i => i.text === text);
  if (!item || item.enabled === false) throw new Error('菜单项不可操作：' + text);
  const channel = Number(item.handler.split(':')[1]);
  window.__TAURI_INTERNALS__.runCallback(channel, { index: 0, message: 'qa-menu-action' });
  }, text);
};
const hook = async view => view.evaluate(() => {
  if (window.__categoryQA) return;
  const original = window.fetch.bind(window);
  window.__categoryQA = { menu: null, savePath: null, commands: [], heights: [], deliveries: [], mockDelivery: false };
  window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url);
    if (url.hostname !== 'ipc.localhost') return original(input, init);
    const command = decodeURIComponent(url.pathname.slice(1)); let args = JSON.parse(init.body);
    const qa = window.__categoryQA; qa.commands.push(command);
    if (command === 'set_common_dock_height') qa.heights.push(args.height);
    const response = value => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    // 发送交互只记录路由参数，绝不执行系统焦点、剪贴板或按键操作。
    if (command === 'get_target' && qa.mockDelivery) return response({ hwnd: 1, focus: 1, pid: 1 });
    if (command === 'deliver_content') { if (!qa.mockDelivery) throw new Error('隔离验证未开启发送记录'); qa.deliveries.push(args); return response({ status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null }); }
    if (command === 'copy_phrase_text') throw new Error('隔离界面验证禁止操作系统剪贴板');
    if (command === 'plugin:menu|popup') return response(null);
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(command)) return response(null);
    if (command === 'plugin:webview|create_webview_window') args = { ...args, options: { ...args.options, visible: false, focus: false, skipTaskbar: true } };
    if (command === 'plugin:dialog|confirm') return response(true);
    if (command === 'plugin:dialog|message') return response('Ok');
    if (command === 'plugin:dialog|save') return response(qa.savePath);
    const result = await original(input, { ...init, body: JSON.stringify(args) });
    if (command === 'get_library') qa.library = await result.clone().json();
    if (command === 'plugin:menu|new' && args.kind === 'Menu') { const created = await result.clone().json(); qa.menu = { rid: created[0], items: args.options.items, ready: true }; }
    return result;
  };
});
try {
  for (let i = 0; i < 50; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:' + port); break; }
    catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); await delay(200); }
  }
  assert.ok(browser);
  const context = browser.contexts()[0]; context.setDefaultTimeout(7000);
  page = context.pages().find(p => !new URL(p.url()).hash);
  await page.locator('.main-window').waitFor(); await hook(page);
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, path.join(evidence, 'data'));
  for (let i = 0; ; i++) {
    try { await invoke('save_settings', { settings: { ...initial.settings, dock: false, dockCommon: false, pinned: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' } }); break; }
    catch (error) { if (i >= 30 || !String(error).includes('服务尚未启动')) throw error; await delay(100); }
  }
  const top = page.getByRole('button', { name: '一级分类菜单', exact: true });
  const primaryLabels = ['新增话术', '新增二级分类', '新增同级分类', '重命名', '删除', '快速排序一级分类', '复制到', '移动到', '导出该一级分类的话术', '复制到共享话术', '设置一级分类背景色', '设置一级分类区域高度'];
  await check('新库没有预设分类，空态可创建首个分类，最后一个分类仍可删除', async () => {
    assert.deepEqual(initial.categories, []);
    await page.getByText('还没有一级分类', { exact: true }).waitFor();
    await page.screenshot({ path: path.join(evidence, 'phrase-no-categories.png') });
    await click(page.locator('.empty-state button'));
    await setInput(page, 'dialog input[name=name]', '首个分类验证'); await submit();
    const first = (await invoke('get_library')).categories[0];
    const items = await openMenu(top); assert.equal(items.find(i => i.text === '删除').enabled, true);
    await page.keyboard.press('Escape');
    await invoke('delete_category', { id: first.id });
    assert.deepEqual((await invoke('get_library')).categories, []);
    await page.getByText('还没有一级分类', { exact: true }).waitFor();
    await click(page.getByRole('button', { name: '文件', exact: true }));
    await page.getByText('还没有一级分类', { exact: true }).waitFor();
    await page.screenshot({ path: path.join(evidence, 'files-no-categories.png') });
    await click(page.locator('.empty-state button'));
    await setInput(page, 'dialog input[name=name]', '首个文件分类验证'); await submit();
    assert.equal((await invoke('get_library')).categories[0].kind, 'file');
    await page.getByRole('button', { name: '首个文件分类验证', exact: true }).evaluate(el => el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
    await click(page.getByRole('menuitem', { name: '删除', exact: true }));
    await page.getByText('还没有一级分类', { exact: true }).waitFor();
    assert.deepEqual((await invoke('get_library')).categories, []);
    await click(page.getByRole('button', { name: '话术', exact: true }));
  });
  // 后续业务验收显式创建夹具，产品空库不生成这些分类。
  const phraseRoot = await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  await invoke('save_category', { category: { id: '', name: '默认', kind: 'file', parentId: null, position: 0, background: '' } });
  await page.getByRole('button', { name: '常用', exact: true }).waitFor();
  await check('仅剩一个一级分类时允许删除，排序和迁移禁用，菜单方向键跳过禁用项', async () => {
    const menu = await openMenu(top); assert.deepEqual(menu.map(i => i.text), primaryLabels);
    for (const label of ['快速排序一级分类', '复制到', '移动到', '复制到共享话术']) assert.equal(menu.find(i => i.text === label).enabled, false);
    await page.keyboard.press('ArrowDown'); assert.equal(await page.evaluate(() => document.activeElement.textContent), '新增话术');
    await page.keyboard.press('End'); assert.equal(await page.evaluate(() => document.activeElement.textContent), '设置一级分类区域高度');
    await page.keyboard.press('ArrowUp'); assert.equal(await page.evaluate(() => document.activeElement.textContent), '设置一级分类背景色');
    await page.keyboard.press('ArrowUp'); await page.keyboard.press('ArrowUp');
    assert.equal(await page.evaluate(() => document.activeElement.textContent), '删除');
    await page.keyboard.press('Escape'); await page.locator('.context-menu').waitFor({ state: 'detached' });
  });
  const category = (name, parentId = null) => invoke('save_category', { category: { id: '', name, kind: 'phrase', parentId, position: 0, background: '' } });
  const target = await category('目标分类'); const second = await category('另一分类');
  const pro = await category('PRO', phraseRoot); const other = await category('通用', phraseRoot);
  const merged = await category('pro', target);
  const phrase = (title, rootId, groupId, shortcut = '') => invoke('save_phrase', { phrase: { id: '', title, rootId, groupId, shortcut, position: 0, color: '#ff5100', background: '', segments: [{ blocks: [{ kind: 'text', text: title + '正文' }] }] } });
  const original = await phrase('示例话术', phraseRoot, pro, 'Ctrl+Alt+1'); await phrase('未分组话术', phraseRoot, null);
  await invoke('save_common_phrase', { phrase: { id: '', text: '谢谢', position: 0 } });
  await page.getByRole('button', { name: '常用', exact: true }).waitFor();
  await check('吸附图标以20像素原位绘制，保持按钮尺寸', async () => {
    const size = await page.getByRole('button', { name: '吸附', exact: true }).evaluate(el => ({ button: el.getBoundingClientRect().width, icon: el.querySelector('svg').getBoundingClientRect().width }));
    assert.deepEqual(size, { button: 26, icon: 20 });
  });
  await check('右上角一级分类菜单只保留分类操作', async () => {
    const menu = await openMenu(top);
    assert.deepEqual(menu.map(i => i.text), primaryLabels);
  });
  await check('背景色菜单首次打开界面设置并预选分类，色板仅修改指定标签', async () => {
    await action('设置一级分类背景色');
    for (let i = 0; i < 30 && !settings; i++) { settings = context.pages().find(p => p.url().includes('#settings')); if (!settings) await delay(100); }
    assert.ok(settings); await hook(settings); await settings.locator('.interface-settings').waitFor();
    assert.equal(await settings.getByLabel('背景色所属分类', { exact: true }).inputValue(), phraseRoot); assert.equal(await page.locator('dialog').count(), 0);
    await click(settings.getByRole('button', { name: '设置 常用 背景色', exact: true }));
    assert.equal(await settings.getByRole('group', { name: '分类背景色色板' }).getByRole('button').count(), 12);
    await settings.screenshot({ path: path.join(evidence, 'category-color-settings.png') });
    await settings.getByRole('button', { name: '颜色 #84c394', exact: true }).evaluate(el => el.focus());
    await settings.keyboard.press('Escape'); assert.equal(await settings.locator('.category-color-popover').count(), 0); assert.equal(await settings.locator('.interface-settings').count(), 1);
    await click(settings.getByRole('button', { name: '设置 常用 背景色', exact: true }));
    await click(settings.getByRole('button', { name: '颜色 #84c394', exact: true }));
    await page.waitForFunction(() => document.querySelector('.category-tabs .active').style.backgroundColor === 'rgb(132, 195, 148)');
    const lib = await invoke('get_library'); assert.equal(lib.categories.find(c => c.id === phraseRoot).background, '#84c394'); assert.equal(lib.categories.find(c => c.id === target).background, '');
  });
  await check('分类栏高度保存，标签换行且正文区域仍可用', async () => {
    await openMenu(top); await action('设置一级分类区域高度'); await setInput(page, 'dialog input[name="height"]', '87'); await submit();
    await page.waitForFunction(() => document.querySelector('.category-tabs').getBoundingClientRect().height === 87);
    assert.equal(await page.locator('.category-tabs').evaluate(el => getComputedStyle(el).flexWrap), 'wrap');
    assert.ok((await page.locator('.list-area').boundingBox()).height > 400);
  });
  await check('一级分类右键与右上角菜单共用分类操作，32像素行高、文字留白与8像素圆角', async () => {
    const menu = await openMenu(page.getByRole('button', { name: '常用', exact: true }), true);
    assert.deepEqual(menu.map(i => i.text), primaryLabels); assert.equal(menu.find(i => i.text === '删除').enabled, true);
    const geometry = await page.locator('.category-context-menu').evaluate(el => { const style = getComputedStyle(el); return { width: el.getBoundingClientRect().width, radius: style.borderRadius, rows: [...el.querySelectorAll('button')].map(b => ({ height: b.getBoundingClientRect().height, padding: getComputedStyle(b).paddingLeft })) }; });
    assert.equal(geometry.width, 176); assert.equal(geometry.radius, '8px'); assert.ok(geometry.rows.every(r => r.height === 32 && r.padding === '23px'));
    await page.screenshot({ path: path.join(evidence, 'primary-category-menu.png') });
    const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight })); await page.setViewportSize({ width: 300, height: 400 });
    await page.getByRole('button', { name: '常用', exact: true }).evaluate(el => el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 298, clientY: 398 })));
    const box = await page.locator('.category-context-menu').boundingBox(); assert.ok(box.x >= 2 && box.y >= 2 && box.x + box.width <= 300 && box.y + box.height <= 400);
    const layer = await page.locator('.category-context-menu').evaluate(el => { const toast = document.querySelector('.review-toast'); return { menu: Number(getComputedStyle(el).zIndex), toast: toast ? Number(getComputedStyle(toast).zIndex) : 0 }; });
    assert.ok(layer.menu > layer.toast, '错误提示不能盖住菜单项目');
    await page.screenshot({ path: path.join(evidence, 'primary-category-menu-compact.png') }); await page.setViewportSize(viewport);
  });
  await check('一级分类右键和右上入口新增二级分类归属正确，新增同级分类仍创建一级分类', async () => {
    for (const right of [true, false]) {
      await click(page.getByRole('button', { name: '常用', exact: true }));
      if (right) await openMenu(page.getByRole('button', { name: '目标分类', exact: true }), true);
      else { await click(page.getByRole('button', { name: '目标分类', exact: true })); await openMenu(top); }
      await action('新增二级分类');
      await page.getByRole('heading', { name: '新增二级分类', exact: true }).waitFor();
      const name = right ? '右键新增二级验证' : '右上新增二级验证';
      await setInput(page, 'dialog input[name="name"]', name); await submit();
      const lib = await invoke('get_library'); const created = lib.categories.find(c => c.name === name);
      assert.ok(created); assert.equal(created.parentId, target); assert.equal(created.kind, 'phrase');
      assert.equal(await page.locator('.category-tabs .active').textContent(), '目标分类');
      assert.equal(lib.categories.filter(c => c.kind === 'phrase' && !c.parentId).length, 3);
      await invoke('delete_category', { id: created.id });
    }
    await openMenu(page.getByRole('button', { name: '目标分类', exact: true }), true); await action('新增同级分类');
    await page.getByRole('heading', { name: '新增一级分类', exact: true }).waitFor();
    await setInput(page, 'dialog input[name="name"]', '右键新增同级验证'); await submit();
    const created = (await invoke('get_library')).categories.find(c => c.name === '右键新增同级验证');
    assert.ok(created); assert.equal(created.parentId, null); assert.equal(created.kind, 'phrase');
    await invoke('delete_category', { id: created.id });
    await click(page.getByRole('button', { name: '常用', exact: true }));
  });
  await check('设置窗口已打开时背景色入口切换页面和分类，菜单仍绑定所点击分类', async () => {
    await click(settings.getByRole('button', { name: '常用设置', exact: true }));
    await openMenu(page.getByRole('button', { name: '目标分类', exact: true }), true); await action('设置一级分类背景色');
    await settings.locator('.interface-settings').waitFor();
    await settings.waitForFunction(target => document.querySelector('select[aria-label="背景色所属分类"]').value === target, target);
    await click(settings.getByRole('button', { name: '常用设置', exact: true }));
    await openMenu(page.getByRole('button', { name: '常用', exact: true }), true);
    await click(page.getByRole('button', { name: '目标分类', exact: true })); await action('设置一级分类背景色');
    await settings.locator('.interface-settings').waitFor();
    await settings.waitForFunction(id => document.querySelector('select[aria-label="背景色所属分类"]').value === id, phraseRoot);
    assert.equal(await page.locator('.category-tabs .active').textContent(), '目标分类'); assert.equal(context.pages().filter(p => p.url().includes('#settings')).length, 1);
    await click(page.getByRole('button', { name: '常用', exact: true }));
  });
  await check('二级分类右键保持所属一级分类，新增话术预选正确分组', async () => {
    const heading = page.locator('.group-heading').filter({ hasText: 'PRO' });
    await openMenu(heading, true);
    assert.deepEqual((await page.evaluate(() => window.__categoryQA.menu.items)).map(i => i.text), ['新增话术', '新增同级分类', '重命名', '删除', '删除但保留话术', '复制到', '移动到', '快速排序二级分类']);
    assert.equal(await page.locator('.category-tabs .active').textContent(), '常用');
    await action('新增话术');
    await page.waitForFunction(() => window.__categoryQA.commands.includes('plugin:webview|create_webview_window'));
    let editor;
    for (let i = 0; i < 30 && !editor; i++) { editor = context.pages().find(p => p.url().includes('#editor')); if (!editor) await delay(100); }
    assert.ok(editor); await editor.locator('.editor-form').waitFor(); await hook(editor);
    assert.equal(await editor.locator('.editor-categories select').nth(0).inputValue(), phraseRoot);
    assert.equal(await editor.locator('.editor-categories select').nth(1).inputValue(), pro);
    await editor.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|close', {})).catch(error => { if (!String(error).includes('has been closed')) throw error; });
  });
  await check('复制菜单动作同名分组合并，副本快捷键清空', async () => {
    await openMenu(page.locator('.group-heading').filter({ hasText: 'PRO' }), true); await action('复制到');
    await page.locator('dialog select[name="target"]').selectOption(target); await submit();
    const lib = await invoke('get_library'); const copied = lib.phrases.find(p => p.rootId === target && p.title === '示例话术');
    assert.ok(copied); assert.equal(copied.groupId, merged); assert.equal(copied.shortcut, ''); assert.notEqual(copied.id, original);
    assert.ok(lib.phrases.some(p => p.id === original && p.rootId === phraseRoot));
  });
  await check('一级与二级快速排序共用多选排序，确认持久化，取消不保存', async () => {
    await openMenu(top); await action('快速排序一级分类');
    let sort;
    for (let i = 0; i < 30 && !sort; i++) { sort = context.pages().find(p => p.url().includes('#category-sort')); if (!sort) await delay(100); }
    assert.ok(sort); await hook(sort); await sort.locator('.common-sort-list').waitFor();
    await sort.locator(`[data-sort-id="${target}"]`).evaluate(el => { el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); });
    await sort.locator(`[data-sort-id="${target}"]`).evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', altKey: true, bubbles: true })));
    await click(sort.getByRole('button', { name: '确定', exact: true })); await delay(200);
    assert.equal((await invoke('get_library')).categories.filter(c => c.kind === 'phrase' && !c.parentId)[0].id, target);
    await openMenu(page.locator('.group-heading').filter({ hasText: 'PRO' }), true); await action('快速排序二级分类');
    sort = null;
    for (let i = 0; i < 30 && !sort; i++) { sort = context.pages().find(p => p.url().includes('#category-sort')); if (!sort) await delay(100); }
    assert.ok(sort); await hook(sort); await sort.locator('.common-sort-list').waitFor();
    assert.equal(await sort.getByRole('listbox', { name: '分类排序列表' }).getByRole('option').count(), 2);
    await click(sort.getByRole('button', { name: '取消', exact: true }));
    assert.deepEqual((await invoke('get_library')).categories.filter(c => c.parentId === phraseRoot).map(c => c.id), [pro, other]);
  });
  await check('分类导出菜单只导出当前分类及其分组', async () => {
    const output = path.join(evidence, '分类导出.zip');
    await openMenu(top); await action('导出该一级分类的话术');
    const exportView = await (async () => { for (let i = 0; i < 50; i++) { const found = context.pages().find(p => p.url().includes('#settings')); if (found) return found; await delay(100); } throw new Error('设置窗口未打开'); })();
    await hook(exportView); await exportView.locator('.data-transfer').waitFor();
    assert.equal(await exportView.getByLabel('导出分类').inputValue(), phraseRoot);
    await exportView.evaluate(output => { window.__categoryQA.savePath = output; }, output);
    await click(exportView.getByRole('button', { name: '导出', exact: true })); await exportView.getByText('导出完成', { exact: true }).waitFor();
    await click(exportView.getByRole('button', { name: '关闭窗口', exact: true }));
    settings = null;
    const preview = await transfer('preview_import', { path: output }); assert.equal(preview.phrases, 2); assert.equal(preview.commonPhrases, 0); assert.equal(preview.categories, 3);
  });
  await check('话术空白菜单新增话术和二级分类，归属当前一级分类，取消不写库', async () => {
    await click(page.getByRole('button', { name: '另一分类', exact: true }));
    const blank = async () => {
      const prevented = await page.locator('.content-list').evaluate(el => !el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 150, clientY: 450 })));
      assert.ok(prevented); assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['新增话术', '新增二级分类']);
    };
    await blank(); await page.screenshot({ path: path.join(evidence, 'blank-create-menu.png') });
    await click(page.getByRole('menuitem', { name: '新增话术', exact: true }));
    let editor; for (let i = 0; i < 40 && !editor; i++) { editor = context.pages().find(p => p.url().includes('#editor')); if (!editor) await delay(50); }
    assert.ok(editor); await editor.locator('.editor-form').waitFor(); await hook(editor);
    assert.equal(await editor.locator('.editor-categories select').nth(0).inputValue(), second);
    assert.equal(await editor.locator('.editor-categories select').nth(1).inputValue(), '');
    const before = (await invoke('get_library')).phrases.length; await click(editor.getByRole('button', { name: '取消', exact: true }));
    for (let i = 0; i < 40 && !editor.isClosed(); i++) await delay(50); assert.ok(editor.isClosed());
    assert.equal((await invoke('get_library')).phrases.length, before);
    await blank(); await click(page.getByRole('menuitem', { name: '新增二级分类', exact: true }));
    await setInput(page, 'dialog input[name="name"]', '空白菜单新分组'); await click(page.locator('dialog').getByRole('button', { name: '取消', exact: true }));
    assert.ok(!(await invoke('get_library')).categories.some(c => c.name === '空白菜单新分组'));
    await blank(); await click(page.getByRole('menuitem', { name: '新增二级分类', exact: true }));
    await setInput(page, 'dialog input[name="name"]', '空白菜单新分组'); await submit();
    const saved = (await invoke('get_library')).categories.find(c => c.name === '空白菜单新分组'); assert.equal(saved.parentId, second);
    await invoke('delete_category', { id: saved.id, recycle: false });
    await click(page.getByRole('button', { name: '文件', exact: true }));
    await page.locator('.content-list').evaluate(el => el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 150, clientY: 450 })));
    assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['关联文件', '刷新']); await click(page.getByRole('menuitem', { name: '刷新', exact: true }));
    await click(page.getByRole('button', { name: '话术', exact: true })); await click(page.getByRole('button', { name: '常用', exact: true }));
    await click(page.getByRole('button', { name: '主菜单', exact: true })); assert.ok((await page.getByRole('menuitem').allTextContents()).includes('界面设置'));
    await click(page.getByRole('menuitem', { name: '界面设置', exact: true }));
  });
  for (let i = 0; i < 30 && !settings; i++) { settings = context.pages().find(p => p.url().includes('#settings')); if (!settings) await delay(100); }
  assert.ok(settings); await hook(settings); await settings.locator('.interface-settings').waitFor();
  await check('参考设置页真实开关、标签色板、显示模式及高度同步主窗口', async () => {
    assert.equal(await settings.locator('.settings-sidebar nav button').count(), 9);
    await settings.getByLabel('背景色所属分类', { exact: true }).selectOption(phraseRoot);
    await click(settings.getByRole('button', { name: '设置 常用 背景色', exact: true }));
    await settings.screenshot({ path: path.join(evidence, 'settings-palette.png') });
    await click(settings.getByRole('button', { name: '颜色 #88b3fa', exact: true }));
    await page.waitForFunction(() => document.querySelector('.category-tabs .active').style.backgroundColor === 'rgb(136, 179, 250)');
    await click(settings.getByLabel('只显示标题', { exact: true })); await page.waitForFunction(() => document.querySelectorAll('.row-preview').length === 0);
    await click(settings.getByLabel('只显示内容', { exact: true })); await page.waitForFunction(() => document.querySelectorAll('.row-title').length === 0);
    await click(settings.getByLabel('显示标题和内容', { exact: true })); await page.locator('.row-preview').first().waitFor();
    await settings.screenshot({ path: path.join(evidence, 'settings.png') });
    await page.screenshot({ path: path.join(evidence, 'main.png') });
    assert.equal((await invoke('get_library')).settings.categoryHeight, 87);
  });
  await check('移动菜单保留快捷键并切换到目标，删除保留话术不会丢失分段', async () => {
    await openMenu(page.locator('.group-heading').filter({ hasText: 'PRO' }), true); await action('移动到'); await page.locator('dialog select[name="target"]').selectOption(second); await submit();
    await page.waitForFunction(() => document.querySelector('.category-tabs .active').textContent === '另一分类');
    let lib = await invoke('get_library'); assert.equal(lib.phrases.find(p => p.id === original).shortcut, 'Ctrl+Alt+1');
    await openMenu(page.locator('.group-heading').filter({ hasText: 'PRO' }), true); await action('删除但保留话术');
    await page.waitForFunction(() => document.querySelectorAll('.group-heading').length === 0);
    lib = await invoke('get_library'); const retained = lib.phrases.find(p => p.id === original); assert.equal(retained.groupId, null); assert.equal(retained.rootId, second); assert.equal(retained.shortcut, 'Ctrl+Alt+1'); assert.equal(retained.segments[0].blocks[0].text, '示例话术正文');
  });
  let duplicateRoot1, duplicateRoot2, duplicateGroup1, duplicateGroup2, selectedPhrase, hoveredPhrase;
  await check('同级一级、二级分类允许重名新增和重命名，原名称与独立ID保留', async () => {
    for (let i = 0; i < 2; i++) { await click(page.getByRole('button', { name: '新增一级分类', exact: true })); await setInput(page, 'dialog input[name="name"]', '111'); await submit(); }
    let lib = await invoke('get_library');
    const roots = lib.categories.filter(c => c.kind === 'phrase' && !c.parentId && c.name === '111');
    assert.equal(roots.length, 2); [duplicateRoot1, duplicateRoot2] = roots.map(c => c.id); assert.notEqual(duplicateRoot1, duplicateRoot2);
    await page.locator('.category-tabs button[title="111（同名 2）"]').waitFor();
    assert.equal(await page.locator('.category-tabs .active').getAttribute('title'), '111（同名 2）');
    await openMenu(top); await action('重命名'); await setInput(page, 'dialog input[name="name"]', '112'); await submit();
    await page.locator('.category-tabs button').filter({ hasText: /^112$/ }).waitFor();
    await openMenu(top); await action('重命名'); await setInput(page, 'dialog input[name="name"]', '111'); await submit();
    duplicateGroup1 = await category('111', duplicateRoot2);
    const group = page.locator('.group-heading').filter({ hasText: /^111$/ }); await group.waitFor();
    await openMenu(group, true); await action('新增同级分类'); await setInput(page, 'dialog input[name="name"]', '111'); await submit();
    lib = await invoke('get_library'); const groups = lib.categories.filter(c => c.parentId === duplicateRoot2 && c.name === '111');
    assert.equal(groups.length, 2); duplicateGroup2 = groups[1].id; assert.notEqual(duplicateGroup1, duplicateGroup2);
    await openMenu(group.nth(1), true); await action('重命名'); await setInput(page, 'dialog input[name="name"]', '112'); await submit();
    await openMenu(page.locator('.group-heading').filter({ hasText: /^112$/ }), true); await action('重命名'); await setInput(page, 'dialog input[name="name"]', '111'); await submit();
    assert.equal((await invoke('get_library')).categories.filter(c => c.parentId === duplicateRoot2 && c.name === '111').length, 2);
    selectedPhrase = await phrase('选中色验证', duplicateRoot2, duplicateGroup1);
    hoveredPhrase = await phrase('悬停色验证', duplicateRoot2, duplicateGroup2);
  });
  await check('编辑器重名选择项带序号，保存使用所选分类ID而非名称', async () => {
    await openMenu(top); await action('新增话术');
    let editor;
    for (let i = 0; i < 30 && !editor; i++) { editor = context.pages().find(p => p.url().includes('#editor')); if (!editor) await delay(100); }
    assert.ok(editor); await editor.locator('.editor-form').waitFor(); await hook(editor);
    const selects = editor.locator('.editor-categories select');
    const rootOptions = await selects.nth(0).locator('option').evaluateAll(options => options.map(o => ({ id: o.value, label: o.textContent })));
    assert.equal(rootOptions.find(o => o.id === duplicateRoot1).label, '111（同名 1）');
    assert.equal(rootOptions.find(o => o.id === duplicateRoot2).label, '111（同名 2）');
    await selects.nth(0).selectOption(duplicateRoot2);
    assert.deepEqual(await selects.nth(1).locator('option').allTextContents(), ['不选二级分类', '111（同名 1）', '111（同名 2）']);
    await selects.nth(1).selectOption(duplicateGroup2); await setInput(editor, '#phrase-title', '选择器验证'); await editor.locator('.rich-segment').evaluate(el => { el.textContent = '同名分类仍独立关联'; el.dispatchEvent(new InputEvent('input', { bubbles: true })); });
    await editor.screenshot({ path: path.join(evidence, 'duplicate-category-editor.png') });
    await editor.locator('.editor-form').evaluate(form => form.requestSubmit());
    for (let i = 0; i < 40 && !editor.isClosed(); i++) await delay(50);
    assert.ok(editor.isClosed());
    const saved = (await invoke('get_library')).phrases.find(p => p.title === '选择器验证');
    assert.equal(saved.rootId, duplicateRoot2); assert.equal(saved.groupId, duplicateGroup2);
    await click(page.locator('.category-tabs button[title="111（同名 1）"]'));
    assert.equal(await page.locator('.content-list .content-row').count(), 0);
    await click(page.locator('.category-tabs button[title="111（同名 2）"]'));
    await page.locator(`[data-row="${selectedPhrase}"]`).waitFor();
  });
  await check('同名一级分类背景色入口按ID预选，复用设置窗口不会混淆分类', async () => {
    for (const [id, title] of [[duplicateRoot1, '111（同名 1）'], [duplicateRoot2, '111（同名 2）']]) {
      await openMenu(page.locator(`.category-tabs button[title="${title}"]`), true); await action('设置一级分类背景色');
      await settings.waitForFunction(id => document.querySelector('select[aria-label="背景色所属分类"]').value === id, id);
    }
    assert.equal(context.pages().filter(p => p.url().includes('#settings')).length, 1);
    await settings.screenshot({ path: path.join(evidence, 'primary-category-settings-navigation.png') });
  });
  const appearance = row => row.evaluate(el => ({ background: getComputedStyle(el).backgroundColor, selected: el.getAttribute('aria-selected'), send: getComputedStyle(el.querySelector('.row-send')).visibility, positions: [...el.querySelectorAll('.row-icon,.row-index,.row-title,.row-preview')].map(child => child.getBoundingClientRect().x) }));
  const hover = async row => { const box = await row.boundingBox(); assert.ok(box); await page.mouse.move(box.x + box.width - 6, box.y + box.height / 2); };
  // 鼠标事件仅由 CDP 发送到隐藏 WebView，不移动系统鼠标，也不改变外部窗口焦点。
  const leaveRows = () => page.mouse.move(315, 12);
  await check('默认主题选中为灰蓝，悬停为浅灰蓝，移出恢复且发送列不跳动', async () => {
    const a = page.locator(`.content-list [data-row="${selectedPhrase}"]`), b = page.locator(`.content-list [data-row="${hoveredPhrase}"]`);
    await click(a); await leaveRows();
    const selected = await appearance(a); assert.equal(selected.background, 'rgb(223, 232, 240)'); assert.equal(selected.send, 'hidden');
    const normal = await appearance(b); assert.equal(normal.selected, 'false');
    await page.screenshot({ path: path.join(evidence, 'phrase-selected.png') });
    await hover(b); const hovered = await appearance(b); assert.equal(hovered.background, 'rgb(240, 243, 246)'); assert.equal(hovered.send, 'visible');
    assert.deepEqual(hovered.positions, normal.positions); assert.equal((await appearance(a)).background, 'rgb(223, 232, 240)');
    await page.screenshot({ path: path.join(evidence, 'phrase-selected-and-hovered.png') });
    await hover(a); assert.equal((await appearance(a)).background, 'rgb(240, 243, 246)');
    await leaveRows(); const restored = await appearance(a); assert.equal(restored.background, 'rgb(223, 232, 240)'); assert.equal(restored.selected, 'true'); assert.deepEqual(restored.positions, selected.positions);
    assert.equal((await appearance(b)).background, 'rgba(0, 0, 0, 0)'); assert.equal((await appearance(b)).send, 'hidden');
  });
  await check('点击空白取消话术与短语选中，切换两处区域不残留选中底色', async () => {
    await page.evaluate(() => { window.__categoryQA.mockDelivery = true; window.__categoryQA.deliveries = []; });
    const row = page.locator(`.content-list [data-row="${selectedPhrase}"]`), common = page.locator('.main-window [data-common]').first();
    await click(row); await leaveRows(); assert.equal((await appearance(row)).selected, 'true');
    await click(common); await leaveRows(); assert.equal((await appearance(row)).selected, 'false');
    assert.equal((await appearance(row)).background, 'rgba(0, 0, 0, 0)'); assert.equal(await common.getAttribute('aria-selected'), 'true');
    await click(row); assert.equal(await common.getAttribute('aria-selected'), 'false');
    await click(page.locator('.content-list')); await leaveRows();
    assert.equal((await appearance(row)).selected, 'false'); assert.equal((await appearance(row)).background, 'rgba(0, 0, 0, 0)');
    await click(common); await click(page.locator('.content-list')); await leaveRows();
    assert.equal(await common.getAttribute('aria-selected'), 'false'); assert.equal(await common.evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 255)');
    await page.screenshot({ path: path.join(evidence, 'phrase-and-common-unselected.png') });
    await page.evaluate(() => { window.__categoryQA.mockDelivery = false; window.__categoryQA.deliveries = []; });
  });
  await check('搜索结果复用悬停与选中样式，上下选择后移出恢复正确颜色', async () => {
    await setInput(page, '.search-bar input', '色验证');
    await page.waitForFunction(() => document.querySelectorAll('#search-results .content-row').length === 2);
    const a = page.locator('#search-results .content-row').nth(0), b = page.locator('#search-results .content-row').nth(1);
    await leaveRows(); assert.equal((await appearance(a)).background, 'rgb(223, 232, 240)');
    await hover(b); assert.equal((await appearance(b)).background, 'rgb(240, 243, 246)'); assert.equal((await appearance(a)).selected, 'true');
    await page.screenshot({ path: path.join(evidence, 'search-selected-and-hovered.png') });
    await leaveRows();
    await page.locator('.search-bar input').evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true })));
    await page.waitForFunction(() => document.querySelectorAll('#search-results .content-row')[1].getAttribute('aria-selected') === 'true');
    assert.equal((await appearance(b)).background, 'rgb(223, 232, 240)'); assert.equal((await appearance(a)).background, 'rgba(0, 0, 0, 0)');
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
  });
  await check('完整备份不合并导入保留重名分类，原始分类和话术不被覆盖', async () => {
    const output = path.join(evidence, '重名分类备份.zip');
    await transfer('export_data', { path: output, kind: 'backup', categoryId: duplicateRoot2 });
    const preview = await transfer('preview_import', { path: output }); assert.equal(preview.categories, 3); assert.equal(preview.phrases, 3);
    await transfer('confirm_import', { previewId: preview.id });
    const lib = await invoke('get_library'); const roots = lib.categories.filter(c => !c.parentId && c.name === '111');
    assert.equal(roots.length, 3);
    assert.ok(lib.categories.some(c => c.id === duplicateRoot2 && c.name === '111'));
    assert.equal(lib.categories.filter(c => c.parentId === duplicateRoot2 && c.name === '111').length, 2);
    const importedRoot = roots.find(c => c.id !== duplicateRoot1 && c.id !== duplicateRoot2);
    assert.equal(lib.categories.filter(c => c.parentId === importedRoot.id && c.name === '111').length, 2);
    const imported = lib.phrases.filter(p => p.rootId === importedRoot.id); assert.equal(imported.length, 3);
    assert.equal(new Set(imported.map(p => p.groupId)).size, 2); assert.ok(!imported.some(p => p.id === selectedPhrase || p.id === hoveredPhrase));
  });
  await check('左下菜单在按钮上方留4像素，窄窗滚动且不遮挡工具栏，移除五项', async () => {
    const disposable = await category('删除保留验证'); const retainedId = await phrase('保留删除测试', disposable, null);
    await page.getByRole('button', { name: '删除保留验证', exact: true }).waitFor(); await click(page.getByRole('button', { name: '删除保留验证', exact: true }));
    await page.getByRole('button', { name: '主菜单', exact: true }).click();
    for (const name of ['新增话术', '新增一级分类', '新增二级分类', '删除一级分类但保留话术', '退出闪语']) assert.equal(await page.getByRole('menuitem', { name, exact: true }).count(), 0);
    for (const name of ['账号', '导入数据', '导出数据', '常用设置', '界面设置', '回收站']) assert.equal(await page.getByRole('menuitem', { name, exact: true }).count(), 1);
    const trigger = await page.getByRole('button', { name: '主菜单', exact: true }).boundingBox(); const menu = await page.getByRole('menu').boundingBox();
    assert.ok(Math.abs(trigger.y - menu.y - menu.height - 4) < 1); assert.equal(menu.x, Math.max(2, trigger.x));
    await page.screenshot({ path: path.join(evidence, 'main-menu.png') }); await page.keyboard.press('Escape');
    const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight })); await page.setViewportSize({ width: 300, height: 400 });
    await page.getByRole('button', { name: '主菜单', exact: true }).click();
    const compactTrigger = await page.getByRole('button', { name: '主菜单', exact: true }).boundingBox(); const compactMenu = await page.getByRole('menu').boundingBox();
    assert.ok(Math.abs(compactTrigger.y - compactMenu.y - compactMenu.height - 4) < 1); assert.ok(compactMenu.y >= 2);
    assert.ok(compactMenu.y + compactMenu.height <= compactTrigger.y - 4);
    await page.getByRole('menuitem', { name: '账号', exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(evidence, 'main-menu-compact.png') }); await page.keyboard.press('Escape');
    // 未登录的话术菜单较短；文件菜单多两项，用它验证空间不足时确实滚动。
    await click(page.getByRole('button', { name: '文件', exact: true })); await page.getByRole('button', { name: '主菜单', exact: true }).click();
    assert.ok(await page.getByRole('menu').evaluate(el => el.scrollHeight > el.clientHeight));
    const fileTrigger = await page.getByRole('button', { name: '主菜单', exact: true }).boundingBox(); const fileMenu = await page.getByRole('menu').boundingBox();
    assert.ok(Math.abs(fileTrigger.y - fileMenu.y - fileMenu.height - 4) < 1); assert.ok(fileMenu.y >= 2);
    await page.screenshot({ path: path.join(evidence, 'main-file-menu-compact.png') }); await page.keyboard.press('Escape');
    await click(page.getByRole('button', { name: '话术', exact: true })); await page.setViewportSize(viewport);
    const lib = await invoke('get_library'); assert.ok(lib.categories.some(c => c.id === disposable)); assert.equal(lib.phrases.find(p => p.id === retainedId).rootId, disposable); assert.ok(lib.phrases.some(p => p.id === selectedPhrase));
  });
  await check('话术一级标签默认高度自动换行，长标签不溢出且列表可用', async () => {
    const current = await invoke('get_library'); await invoke('save_settings', { settings: { ...current.settings, categoryHeight: 31 } });
    const longName = '这是用于验证一级话术分类自动换行的较长分类名称'.repeat(2).slice(0, 40); await category(longName);
    await page.getByRole('button', { name: longName, exact: true }).waitFor();
    await page.waitForFunction(() => { const el = document.querySelector('.category-tabs'); return el.getBoundingClientRect().height > 31 && el.getBoundingClientRect().height <= 200; });
    const sizes = await page.locator('.category-tabs').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth, lines: new Set(Array.from(el.querySelectorAll('button')).map(b => b.getBoundingClientRect().y)).size })); assert.ok(sizes.scroll <= sizes.width + 1); assert.ok(sizes.lines > 1);
    assert.ok((await page.locator('.content-list').boundingBox()).height > 100); await page.screenshot({ path: path.join(evidence, 'category-auto-wrap.png') });
  });
  await check('列表设置双键快捷键、删除取消及冲突提示，搜索复用逗号格式标签', async () => {
    await click(page.locator('.category-tabs button[title="111（同名 2）"]'));
    const a = page.locator(`.content-list [data-row="${selectedPhrase}"]`);
    const menu = async row => { await row.evaluate(el => { const r = el.getBoundingClientRect(); el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: r.left + 100, clientY: r.top + 10 })); }); await click(page.getByRole('menuitem', { name: '自定义快捷键', exact: true })); await page.getByLabel('第一个按键', { exact: true }).waitFor(); };
    await menu(a); await page.getByLabel('功能键', { exact: true }).selectOption('Ctrl');
    await page.getByLabel('第一个按键', { exact: true }).press('1'); await page.getByLabel('第二个按键', { exact: true }).press('2');
    await page.screenshot({ path: path.join(evidence, 'shortcut-recorder.png') }); await submit();
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === selectedPhrase).shortcut, 'Ctrl+1+2');
    await a.locator('.row-shortcut').waitFor(); assert.equal(await a.locator('.row-shortcut').textContent(), 'Ctrl,1,2');
    await menu(a); await click(page.getByRole('button', { name: '删除话术快捷键', exact: true })); await click(page.locator('dialog').getByRole('button', { name: '取消', exact: true }));
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === selectedPhrase).shortcut, 'Ctrl+1+2');
    const b = page.locator(`.content-list [data-row="${hoveredPhrase}"]`); await menu(b);
    await page.getByLabel('功能键', { exact: true }).selectOption('Ctrl'); await page.getByLabel('第一个按键', { exact: true }).press('1');
    await page.locator('dialog form').evaluate(form => form.requestSubmit()); await page.locator('dialog [role="alert"]').waitFor();
    assert.ok((await page.locator('dialog [role="alert"]').textContent()).includes('冲突')); assert.equal((await invoke('get_library')).phrases.find(p => p.id === hoveredPhrase).shortcut, '');
    await page.screenshot({ path: path.join(evidence, 'shortcut-prefix-conflict.png') });
    await page.getByLabel('功能键', { exact: true }).selectOption(''); await page.getByLabel('第一个按键', { exact: true }).press('y'); await page.getByLabel('第二个按键', { exact: true }).press('c'); await submit();
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === hoveredPhrase).shortcut, 'Y+C');
    await page.screenshot({ path: path.join(evidence, 'shortcut-list-labels.png') });
    await page.locator('.search-bar input').fill('选中色验证');
    // 搜索快捷键沿用悬停才显示的规则，仍核对可见标签和逗号格式。
    await page.locator('#search-results .content-row').first().hover(); await page.locator('#search-results .row-shortcut').waitFor();
    assert.equal(await page.locator('#search-results .row-shortcut').textContent(), 'Ctrl,1,2'); await page.locator('.search-bar input').press('Escape');
    await menu(b); await click(page.getByRole('button', { name: '删除话术快捷键', exact: true })); await submit();
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === hoveredPhrase).shortcut, ''); assert.equal(await b.locator('.row-shortcut').count(), 0);
  });
  await check('编辑器快捷键确认仅改草稿，话术保存后持久化双键序列', async () => {
    await page.locator('.content-list').evaluate(el => el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 150, clientY: 450 })));
    await click(page.getByRole('menuitem', { name: '新增话术', exact: true }));
    let editor; for (let i = 0; i < 40 && !editor; i++) { editor = context.pages().find(p => p.url().includes('#editor')); if (!editor) await delay(50); }
    assert.ok(editor); await editor.locator('.editor-form').waitFor(); await hook(editor);
    assert.equal(await editor.locator('.editor-categories select').nth(0).inputValue(), duplicateRoot2);
    await click(editor.getByRole('button', { name: '自定义快捷键', exact: true }));
    await editor.getByLabel('第一个按键', { exact: true }).press('1'); await editor.getByLabel('第二个按键', { exact: true }).press('1');
    await editor.locator('dialog form').evaluate(form => form.requestSubmit()); await editor.locator('dialog').waitFor({ state: 'detached' });
    assert.ok(!(await invoke('get_library')).phrases.some(p => p.shortcut === '1+1'));
    await click(editor.getByRole('button', { name: '1+1', exact: true })); await click(editor.getByRole('button', { name: '自定义快捷键使用说明', exact: true }));
    await editor.screenshot({ path: path.join(evidence, 'shortcut-editor-help.png') }); await click(editor.locator('dialog').getByRole('button', { name: '取消', exact: true }));
    await setInput(editor, '#phrase-title', '双键序列保存验证'); await editor.locator('.rich-segment').evaluate(el => { el.textContent = '双键话术'; el.dispatchEvent(new InputEvent('input', { bubbles: true })); });
    await editor.locator('.editor-form').evaluate(form => form.requestSubmit()); for (let i = 0; i < 40 && !editor.isClosed(); i++) await delay(50); assert.ok(editor.isClosed());
    const p = (await invoke('get_library')).phrases.find(p => p.title === '双键序列保存验证'); assert.equal(p.shortcut, '1+1'); assert.equal(p.rootId, duplicateRoot2);
  });
  await check('底部短语横条按宽度换行，隐藏整栏且不留下菜单或新增按钮，设置可恢复', async () => {
    const current = (await invoke('get_library')).settings; await invoke('save_settings', { settings: { ...current, dockCommon: true } });
    for (const text of ['不客气，有问题随时联系我们哈。', '好的，有问题随时联系我们哈。', '收到 这边看下日志稍后给您回复', '接受一下', '麻烦输入下验证码']) await invoke('save_common_phrase', { phrase: { id: '', text, position: 0 } });
    const count = (await invoke('get_library')).commonPhrases.length;
    const dock = context.pages().find(p => p.url().includes('#common-dock')); assert.ok(dock); await hook(dock); await dock.setViewportSize({ width: 580, height: 180 });
    await dock.getByRole('option', { name: '麻烦输入下验证码', exact: true }).waitFor();
    const positions = await dock.locator('[data-common]').evaluateAll(els => els.map(el => el.getBoundingClientRect().top)); assert.ok(Math.max(...positions) > Math.min(...positions));
    const before = await dock.locator('.common-phrases').boundingBox(); await dock.locator('.common-phrases').screenshot({ path: path.join(evidence, 'common-dock-wrapped.png') });
    await dock.waitForFunction(height => window.__categoryQA.heights.includes(height), Math.ceil(before.height));
    await click(dock.getByRole('button', { name: '隐藏短语', exact: true })); await dock.locator('.common-phrases').waitFor({ state: 'detached' });
    assert.equal(await dock.getByRole('option').count(), 0);
    assert.equal(await dock.getByRole('button', { name: '常用短语菜单', exact: true }).count(), 0);
    assert.equal(await dock.getByRole('button', { name: '新增常用短语', exact: true }).count(), 0);
    const hidden = (await invoke('get_library')).settings; assert.equal(hidden.dockCommon, false); assert.equal(hidden.showCommon, current.showCommon);
    assert.equal(await dock.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|is_visible', {})), false);
    await invoke('save_settings', { settings: { ...hidden, dockCommon: true } });
    await dock.getByRole('option', { name: '麻烦输入下验证码', exact: true }).waitFor(); assert.equal(await dock.getByRole('option').count(), count);
  });
  const dock = context.pages().find(p => p.url().includes('#common-dock')); assert.ok(dock); await hook(dock);
  const choose = async (view, label) => {
    const input = view.getByLabel(label, { exact: true });
    const checked = await input.getAttribute('type') === 'checkbox' ? !await input.isChecked() : true;
    await click(input);
    // React 首次提交前也可能暂时没有禁用控件；同时核对目标值，才能确认本次保存完成。
    for (let i = 0; i < 100 && (!await input.isEnabled() || await input.isChecked() !== checked); i++) await delay(20);
    assert.ok(await input.isEnabled()); assert.equal(await input.isChecked(), checked);
  };
  const number = async (label, value) => { const input = settings.getByLabel(label, { exact: true }); await setInput(settings, `[aria-label="${label}"]`, String(value)); await input.evaluate(el => el.dispatchEvent(new FocusEvent('focusout', { bubbles: true }))); await settings.waitForFunction(() => !document.querySelector('.settings-content input:disabled')); };
  await check('常用设置默认点击规则保持，配置后话术与两处短语只按指定方式提交一次', async () => {
    await click(settings.getByRole('button', { name: '常用设置', exact: true }));
    assert.equal((await invoke('get_library')).settings.phraseDoubleClick, 'insert'); assert.equal((await invoke('get_library')).settings.commonSingleClick, 'insert');
    const doubleRow = settings.locator('.settings-row').filter({ hasText: '话术双击' });
    assert.equal(await settings.locator('.settings-row').filter({ hasText: '常用短语单击' }).count(), 0);
    await click(doubleRow.getByLabel('直接发送', { exact: true })); await page.waitForFunction(() => !document.querySelector('.settings-content input:disabled'));
    await page.waitForFunction(() => document.querySelector('[data-row]'));
    for (const view of [page, dock]) await view.evaluate(() => { window.__categoryQA.mockDelivery = true; });
    const sample = page.locator('.content-list [data-row]').first(); const id = await sample.getAttribute('data-row');
    const library = await invoke('get_library'); const contentId = `private:${library.libraryId}:phrase:${id}:${library.privateRevisions[`phrase:${id}`]}`;
    await page.waitForFunction(() => document.querySelector('.main-window')); await sample.evaluate(el => el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, detail: 2 })));
    await page.waitForFunction(() => window.__categoryQA.deliveries.length === 1);
    assert.deepEqual(await page.evaluate(() => window.__categoryQA.deliveries.map(({id,kind,mode}) => ({id,kind,mode}))), [{ id: contentId, kind: 'phrase', mode: 'send' }]);
    const shortId = (await invoke('get_library')).commonPhrases[0].id;
    const gesture = async view => { await view.locator(`[data-common="${shortId}"]`).evaluate(el => { for (const [type, detail] of [['click',1],['click',2],['dblclick',2]]) el.dispatchEvent(new MouseEvent(type, { bubbles: true, detail })); }); };
    await gesture(page); await page.waitForFunction(() => window.__categoryQA.deliveries.length === 2);
    await gesture(dock); await dock.waitForFunction(() => window.__categoryQA.deliveries.length === 1);
    assert.equal(await dock.evaluate(() => window.__categoryQA.deliveries[0].mode), 'insert');
    await dock.waitForFunction(() => window.__categoryQA.library?.settings.commonSingleClick === 'insert');
    await gesture(dock); await dock.waitForFunction(() => window.__categoryQA.deliveries.length === 2); assert.equal(await dock.evaluate(() => window.__categoryQA.deliveries[1].mode), 'insert');
    await click(doubleRow.getByLabel('输入到编辑区', { exact: true })); await settings.waitForFunction(() => !document.querySelector('.settings-content input:disabled'));
    await choose(settings, '保留搜索结果界面'); await choose(settings, 'Ctrl+Enter 键');
    await number('自定义分段间隔', 650); const saved = (await invoke('get_library')).settings;
    assert.equal(saved.enter, 'ctrl-enter'); assert.equal(saved.exitSearch, false); assert.equal(saved.interval, 650);
    assert.equal(await settings.getByText('吸附时自动隐藏', { exact: true }).count(), 0);
    assert.equal(await settings.getByText('贴边显示防误触', { exact: true }).count(), 0);
    await settings.screenshot({ path: path.join(evidence, 'settings-general-aligned.png') });
  });
  await check('七种主题、字体大小及标题色板保存后同步主窗口和底部面板', async () => {
    await click(settings.getByRole('button', { name: '界面设置', exact: true }));
    const theme = settings.getByLabel('主题风格', { exact: true }); assert.equal(await theme.locator('option').count(), 7);
    await theme.selectOption('green'); await page.waitForFunction(() => document.documentElement.dataset.theme === 'green'); await dock.waitForFunction(() => document.documentElement.dataset.theme === 'green');
    assert.equal(await page.locator('.main-titlebar').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(62, 114, 86)');
    await settings.getByLabel('话术字体大小', { exact: true }).selectOption('16');
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.content-list')).fontSize === '16px');
    await click(settings.getByRole('button', { name: '设置 话术标题 字体颜色', exact: true })); assert.equal(await settings.getByRole('group', { name: '标题字体色色板' }).getByRole('button').count(), 8);
    await settings.screenshot({ path: path.join(evidence, 'settings-title-color-palette.png') });
    await click(settings.getByRole('button', { name: '颜色 #0000ff', exact: true }));
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.content-list .row-title')).color === 'rgb(0, 0, 255)');
    const saved = (await invoke('get_library')).settings; assert.equal(saved.theme, 'green'); assert.equal(saved.fontSize, 16); assert.equal(saved.titleColor, '#0000ff');
  });
  await check('私人和文件默认背景色独立生效，分类自身颜色优先且恢复默认不改其他分类', async () => {
    await click(settings.getByRole('button', { name: '设置 个人分类 背景色', exact: true })); await click(settings.getByRole('button', { name: '颜色 #ff9800', exact: true }));
    await page.waitForFunction(() => document.querySelector('.category-tabs .active').style.backgroundColor === 'rgb(255, 152, 0)');
    await click(settings.getByRole('button', { name: '设置 文件分类 背景色', exact: true })); await click(settings.getByRole('button', { name: '颜色 #929eaa', exact: true }));
    await click(page.getByRole('button', { name: '文件', exact: true }));
    await page.waitForFunction(() => document.querySelector('.category-tabs .active').style.backgroundColor === 'rgb(146, 158, 170)');
    await click(page.getByRole('button', { name: '话术', exact: true })); await click(page.getByRole('button', { name: '常用', exact: true }));
    assert.equal(await page.locator('.category-tabs .active').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(136, 179, 250)');
    const before = (await invoke('get_library')).categories.map(c => ({ id: c.id, background: c.background }));
    await click(settings.getByRole('button', { name: '设置 个人分类 背景色', exact: true })); await click(settings.getByRole('button', { name: '恢复默认颜色', exact: true }));
    assert.deepEqual((await invoke('get_library')).categories.map(c => ({ id: c.id, background: c.background })), before);
    await click(page.locator('.category-tabs button[title="111（同名 2）"]'));
  });
  await check('面板开关、类目顺序与自由拉宽设置同步，原生窗口可调整属性实际变化', async () => {
    await choose(settings, '显示主窗口搜索功能'); await page.locator('#search-bar').waitFor({ state: 'detached' });
    await choose(settings, '显示主窗口常用短语'); await page.locator('.common-phrases').waitFor({ state: 'detached' });
    await choose(settings, '显示底部吸附常用短语'); await dock.locator('.common-phrases').waitFor({ state: 'detached' });
    await choose(settings, '显示底部吸附搜索栏'); await dock.getByRole('textbox', { name: '底部搜索话术、文件', exact: true }).waitFor();
    const order = settings.getByRole('group', { name: '类目显示位置排序', exact: true });
    assert.equal(await order.locator('select').count(), 2);
    const saves = await settings.evaluate(() => window.__categoryQA.commands.filter(c => c === 'save_settings').length);
    await click(order.getByRole('combobox').nth(0)); await settings.keyboard.press('Escape');
    assert.equal(await settings.evaluate(() => window.__categoryQA.commands.filter(c => c === 'save_settings').length), saves);
    await order.getByRole('combobox').nth(0).selectOption('文件');
    await page.waitForFunction(() => document.querySelector('.content-tabs button').textContent === '文件');
    assert.deepEqual(await order.locator('select').evaluateAll(selects => selects.map(s => s.value)), ['文件', '话术']);
    const scopeOrder = settings.getByRole('group', { name: '话术库显示位置排序', exact: true });
    await scopeOrder.getByRole('combobox').nth(0).selectOption('共享话术');
    await page.waitForFunction(() => document.querySelector('.scope-bar').firstElementChild.classList.contains('team-selector'));
    assert.deepEqual(await scopeOrder.locator('select').evaluateAll(selects => selects.map(s => s.value)), ['共享话术', '个人话术']);
    assert.equal((await invoke('get_library')).settings.filesFirst, true);
    for (const control of [...await order.locator('select').all(), ...await scopeOrder.locator('select').all()]) assert.equal(await control.evaluate(el => getComputedStyle(el).appearance), 'none');
    const firstBox = await order.locator('select').nth(0).boundingBox(); const secondBox = await order.locator('select').nth(1).boundingBox(); const scopeBox = await scopeOrder.boundingBox(); const themeBox = await settings.getByRole('combobox', { name: '主题风格', exact: true }).boundingBox();
    assert.equal(firstBox.y, secondBox.y); assert.equal(firstBox.height, secondBox.height); assert.equal(firstBox.x, themeBox.x); assert.equal(Math.round(secondBox.x + secondBox.width), Math.round(themeBox.x + themeBox.width));
    assert.equal(firstBox.y, scopeBox.y); assert.equal(firstBox.height, scopeBox.height);
    await order.getByRole('combobox').nth(1).selectOption('文件'); await page.waitForFunction(() => document.querySelector('.content-tabs button').textContent === '话术');
    assert.deepEqual(await order.locator('select').evaluateAll(selects => selects.map(s => s.value)), ['话术', '文件']);
    await settings.screenshot({ path: path.join(evidence, 'settings-order.png') });
    await scopeOrder.getByRole('combobox').nth(0).selectOption('个人话术'); await page.waitForFunction(() => document.querySelector('.scope-bar').firstElementChild.textContent === '个人话术');
    // 设置通知由主窗口异步应用；等待真实原生属性，不能把 IPC 返回当成窗口已更新。
    await choose(settings, '支持窗口自由拉宽拉窄'); await page.waitForFunction(async () => !await window.__TAURI_INTERNALS__.invoke('plugin:window|is_resizable')); assert.equal(await invoke('plugin:window|is_resizable'), false);
    await choose(settings, '支持窗口自由拉宽拉窄'); await page.waitForFunction(async () => await window.__TAURI_INTERNALS__.invoke('plugin:window|is_resizable')); assert.equal(await invoke('plugin:window|is_resizable'), true);
    await choose(settings, '记住主窗口吸附状态'); assert.equal((await invoke('get_library')).settings.rememberDock, false);
    await choose(settings, '记住主窗口吸附状态');
    for (const label of ['显示主窗口搜索功能', '显示主窗口常用短语', '显示底部吸附常用短语']) await choose(settings, label);
    assert.equal(await dock.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|is_visible', {})), false);
  });
  await check('分类与侧边短语高度验证，底部短语1至3行及自适应可滚动访问全部记录', async () => {
    await number('文件分类区域高度', 200); await click(page.getByRole('button', { name: '文件', exact: true }));
    await page.waitForFunction(() => document.querySelector('.category-tabs').getBoundingClientRect().height === 200);
    await number('文件分类区域高度', 201); assert.equal(await settings.getByLabel('文件分类区域高度', { exact: true }).inputValue(), '200'); assert.equal((await invoke('get_library')).settings.fileCategoryHeight, 200);
    await number('侧边短语区域最大高度', 72); await page.waitForFunction(() => document.querySelector('.common-phrases').getBoundingClientRect().height <= 72);
    await click(page.getByRole('button', { name: '话术', exact: true }));
    for (const [label, count] of [['一行',1],['两行',2],['三行',3],['完全自适应',0]]) {
      await choose(settings, label); await dock.waitForFunction(count => document.querySelector('.common-phrases').style.maxHeight === (count ? count * 27 + 1 : 150) + 'px', count);
      assert.equal(await dock.locator('[data-common]').count(), (await invoke('get_library')).commonPhrases.length);
      if (count) assert.ok((await dock.locator('.common-phrases').boundingBox()).height <= count * 27 + 1);
    }
    await number('文件分类区域高度', 31); await number('侧边短语区域最大高度', 144);
    if (await settings.getByRole('status').count()) await click(settings.getByRole('status'));
    await settings.locator('.settings-content').evaluate(el => { el.scrollTop = 0; }); await settings.screenshot({ path: path.join(evidence, 'settings-interface-aligned.png') });
    const geometry = await settings.locator('.settings-content').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth })); assert.ok(geometry.scroll <= geometry.width + 1);
  });
  await check('底部搜索使用真实搜索服务，匹配方式、高亮、Enter插入、发送按钮、右键及Esc正确路由', async () => {
    await click(settings.getByRole('button', { name: '常用设置', exact: true }));
    await choose(settings, '模糊匹配'); const input = dock.getByRole('textbox', { name: '底部搜索话术、文件', exact: true });
    await input.fill('悬停 色验证'); await dock.locator('.dock-search-results [data-row]').first().waitFor(); assert.ok(await dock.locator('.dock-search-results mark').count());
    await input.press('Enter'); await dock.waitForFunction(() => window.__categoryQA.deliveries.length === 3); assert.equal(await dock.evaluate(() => window.__categoryQA.deliveries[2].mode), 'insert'); assert.equal(await input.inputValue(), '悬停 色验证');
    await choose(settings, '精确匹配'); await dock.getByText('没有找到匹配的话术', { exact: true }).waitFor();
    await input.fill('悬停色验证'); await dock.locator('.dock-search-results [data-row]').first().waitFor();
    const hit = dock.locator('.dock-search-results [data-row]').first(); const id = await hit.getAttribute('data-row');
    const library = await invoke('get_library'); const contentId = `private:${library.libraryId}:phrase:${id}:${library.privateRevisions[`phrase:${id}`]}`;
    const hitBox = await hit.boundingBox(); await dock.mouse.move(hitBox.x + 10, hitBox.y + hitBox.height / 2);
    await click(hit.getByRole('button', { name: /^直接发送/ })); await dock.waitForFunction(() => window.__categoryQA.deliveries.length === 4); assert.equal(await dock.evaluate(() => window.__categoryQA.deliveries[3].id), contentId); assert.equal(await dock.evaluate(() => window.__categoryQA.deliveries[3].mode), 'send');
    await hit.evaluate(el => el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 12 })));
    await dock.waitForFunction(() => window.__categoryQA.menu?.ready);
    await dock.evaluate(() => { const item = window.__categoryQA.menu.items.find(i => i.text === '直接发送'); window.__TAURI_INTERNALS__.runCallback(Number(item.handler.split(':')[1]), { index: 0, message: 'qa-send-route' }); });
    await dock.waitForFunction(() => window.__categoryQA.deliveries.length === 5);
    await dock.locator('.common-dock-content').screenshot({ path: path.join(evidence, 'bottom-search-results.png') });
    await input.press('Escape'); await dock.locator('.dock-search-results').waitFor({ state: 'detached' }); assert.equal(await input.inputValue(), '');
    await choose(settings, '退出搜索结果界面'); await input.fill('悬停色验证'); await dock.locator('.dock-search-results [data-row]').first().waitFor();
    await input.press('Enter'); await dock.locator('.dock-search-results').waitFor({ state: 'detached' }); assert.equal(await input.inputValue(), '');
  });
  await check('新增设置完整备份可预览和恢复，隐藏客户端与正式数据保持隔离', async () => {
    const output = path.join(evidence, '设置备份.zip'); await transfer('export_data', { path: output, kind: 'backup', categoryId: null });
    const before = (await invoke('get_library')).settings; const preview = await transfer('preview_import', { path: output, restoreSettings: true });
    await invoke('save_settings', { settings: { ...before, theme: 'orange', fontSize: 12, dockSearch: false, filesFirst: false, teamsFirst: !before.teamsFirst } });
    await assert.rejects(transfer('confirm_import', { previewId: preview.id }), /已更新/);
    await transfer('refresh_import_preview', { previewId: preview.id });
    await transfer('confirm_import', { previewId: preview.id });
    // 个人内容仅恢复便携设置；设备设置必须经独立预览和确认，避免跨账号覆盖本机布局。
    const after = (await invoke('get_library')).settings;
    assert.deepEqual(after, { ...before, dockSearch: false, teamsFirst: !before.teamsFirst });
    const devicePreview = await transfer('preview_import', { path: output, restoreSettings: true }, true);
    await transfer('confirm_import', { previewId: devicePreview.id }, true);
    assert.deepEqual((await invoke('get_library')).settings, before);
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'green');
    for (const view of [page, dock, settings]) assert.equal(await view.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|is_visible', {})), false);
  });
  const commands = await page.evaluate(() => window.__categoryQA.commands);
  assert.ok(!commands.includes('copy_phrase_text'));
  const deliveries = await Promise.all([page, dock].map(view => view.evaluate(() => window.__categoryQA.deliveries)));
  const report = { date: new Date().toISOString(), scope: 'settings-shortcuts-blank-menu-common-dock', results, menus, deliveries, limits: ['窗口保持隐藏；一级菜单使用实际DOM点击，二级分类菜单实际创建原生对象并通过回调验证，未使用实体键鼠', '点击与搜索发送仅记录统一接口参数，get_target和deliver_content使用模拟结果，未执行系统焦点、剪贴板或按键', '菜单最小尺寸采用300×400页面视口，未替代真实DPI或多显示器验收', '未执行真实快捷键触发、发送、吸附、新装、升级及卸载验收'] };
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ evidence, scope: report.scope, checks: results.length }, null, 2));
} catch (error) {
  if (page) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  const diagnostics = page ? await page.evaluate(() => ({ commands: window.__categoryQA?.commands, menu: window.__categoryQA?.menu, invokeDescriptor: { writable: Object.getOwnPropertyDescriptor(window.__TAURI_INTERNALS__, 'invoke')?.writable, configurable: Object.getOwnPropertyDescriptor(window.__TAURI_INTERNALS__, 'invoke')?.configurable } })).catch(() => null) : null;
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ results, error: String(error), stack: error.stack, diagnostics }, null, 2));
  throw error;
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  for (let i = 0; i < 25 && app.exitCode === null; i++) await delay(40);
  if (app.exitCode === null) app.kill();
}
