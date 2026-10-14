import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 隐藏的实际客户端使用独立数据库；系统复制、打开文件、发送和窗口聚焦只记录请求。
// 数据管理、图片导入、备份、窗口创建和 DOM 交互仍执行实际代码，不触碰正式客户端。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'library-ui-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port; await new Promise(resolve => server.close(resolve));
const app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser, context, page; const results = [], views = new Set();
const check = async (label, fn) => { await fn(); results.push(label); console.log('通过：' + label); };
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const click = async locator => { await locator.waitFor(); for (let i = 0; i < 60 && !await locator.isEnabled(); i++) await delay(50); assert.ok(await locator.isEnabled()); await locator.evaluate(el => el.click()); };
const hook = async view => view.evaluate(() => {
  if (window.__libraryQA) return;
  const original = window.fetch.bind(window); window.__libraryQA = { requests: [], edits: [], paths: null, confirm: true };
  const edit = document.execCommand.bind(document);
  document.execCommand = (...args) => { const before = document.querySelector('.rich-segment')?.innerHTML; const result = edit(...args); window.__libraryQA.edits.push({ command: args[0], result, before, after: document.querySelector('.rich-segment')?.innerHTML }); return result; };
  window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const cmd = decodeURIComponent(url.pathname.slice(1)); let args = JSON.parse(init.body); const qa = window.__libraryQA; qa.requests.push({ cmd, args });
    const response = value => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    if (['plugin:window|show', 'plugin:window|set_focus', 'plugin:menu|popup', 'copy_linked_files', 'open_linked_file', 'copy_phrase_text', 'open_directory'].includes(cmd)) return response(null);
    if (cmd === 'get_target') return response({ hwnd: 1, focus: 2, pid: 3 });
    if (cmd === 'deliver_content') return response({ status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null });
    if (cmd === 'plugin:webview|create_webview_window') args = { ...args, options: { ...args.options, visible: false, focus: false, skipTaskbar: true } };
    if (cmd === 'plugin:dialog|confirm') return response(qa.confirm);
    if (cmd === 'plugin:dialog|message') return response(args.buttons === 'OkCancel' && !qa.confirm ? 'Cancel' : 'Ok');
    if (cmd === 'plugin:dialog|open') return response(qa.paths);
    return original(input, { ...init, body: JSON.stringify(args) });
  };
});
async function view(route, selector) {
  let found;
  for (let i = 0; i < 100; i++) { found = context.pages().find(p => !p.isClosed() && p.url().includes('#' + route)); if (found) break; await delay(50); }
  assert.ok(found, '窗口未创建：' + route); await found.locator(selector).waitFor(); await hook(found); views.add(found); return found;
}
async function right(locator) {
  await page.evaluate(() => document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
  await locator.evaluate(el => { const b = el.getBoundingClientRect(); el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: b.x + b.width / 2, clientY: b.y + b.height / 2 })); });
  await page.locator('.context-menu').waitFor();
}
const menu = label => click(page.getByRole('menuitem', { name: label, exact: true }));
const row = id => page.locator('.content-list [data-row="' + id + '"]');
const choose = (id, options = {}) => row(id).evaluate((el, options) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, ...options })), options);
const form = async (target, selector = 'dialog') => { await target.locator(selector + ' form').evaluate(el => el.requestSubmit()); await target.locator(selector).waitFor({ state: 'detached' }); };
const category = (name, kind = 'phrase', parentId = null) => invoke('save_category', { category: { id: '', name, kind, parentId, position: 0, background: '#84c394' } });
const phrase = (title, rootId, groupId = null, blocks = [{ kind: 'text', text: '您好，欢迎使用闪语。' }], shortcut = '') => invoke('save_phrase', { phrase: { id: '', rootId, groupId, title, color: '#ff5100', background: '#ffffcc', shortcut, position: 0, segments: [{ blocks }] }, afterId: null });
const requests = target => target.evaluate(() => window.__libraryQA.requests);
const finish = async (target, name = '确定') => { const closed = target.waitForEvent('close'); await click(target.getByRole('button', { name, exact: true })); await closed; };
const moveFirst = async target => { const first = target.getByRole('option').first(); await first.evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))); await target.getByRole('option', { selected: true }).waitFor(); await first.evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true }))); };
// 只派发本页的粘贴事件，不读写系统剪贴板；图片解码、落盘与保存仍走实际 Rust 服务。
const pasteImage = (input, bytes) => input.evaluate((el, bytes) => {
  const clipboardData = new DataTransfer(); clipboardData.items.add(new File([Uint8Array.from(bytes)], '截图.png', { type: 'image/png' }));
  el.dispatchEvent(new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData }));
}, bytes);
async function deletion(id, isCategory = false) {
  if (isCategory) await right(page.getByRole('button', { name: id, exact: true })); else await right(row(id));
  await menu('删除'); return view('delete-confirm', '.deletion-window');
}
async function searchRecycle(target, query) {
  await target.getByRole('textbox', { name: '搜索回收站' }).fill(query);
  await target.locator('.recycle-search').evaluate(el => el.requestSubmit());
  await target.waitForFunction(() => !document.querySelector('.recycle-empty')?.textContent.includes('正在读取'));
  await delay(100);
}
const recycleIds = target => target.locator('.recycle-list [role="option"]').evaluateAll(els => els.map(el => el.title));
try {
  for (let i = 0; i < 70; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:' + port); break; }
    catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); await delay(150); }
  }
  assert.ok(browser); context = browser.contexts()[0]; context.setDefaultTimeout(8000);
  page = context.pages().find(p => !new URL(p.url()).hash); await page.locator('.main-window').waitFor(); await hook(page); views.add(page);
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, path.join(evidence, 'data'));
  assert.deepEqual(initial.categories, []);
  const fileCategoryName = '默认';
  const fileRoot = await category(fileCategoryName, 'file');
  for (let i = 0; ; i++) {
    try { await invoke('save_settings', { settings: { ...initial.settings, dock: false, dockCommon: false, pinned: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' } }); break; }
    catch (error) { if (i >= 30 || !String(error).includes('服务尚未启动')) throw error; await delay(100); }
  }
  const root = await category('客服'), group = await category('通用', 'phrase', root);
  const textId = await phrase('文字话术', root), imageId = await phrase('进群图文', root, group);
  const sample = path.join(repo, 'src-tauri/icons/icon.png'), sourceFiles = ['原文件 A.txt', '原文件 B.txt', '原文件 C.txt', '原文件 D.txt'].map(name => path.join(evidence, name));
  for (const file of sourceFiles) await fs.writeFile(file, '源文件保留：' + path.basename(file));
  await click(page.getByRole('button', { name: '客服', exact: true }));
  await check('新增和编辑话术粘贴图片保留原选区及分段，保存重开仍可读取本地图片', async () => {
    const bytes = [...await fs.readFile(path.join(repo, 'public/shanyu.png'))];
    await click(page.getByRole('button', { name: '一级分类菜单', exact: true })); await menu('新增话术');
    let editor = await view('editor', '.editor-window'); let input = editor.getByRole('textbox', { name: '第1段文字', exact: true });
    await editor.waitForFunction(() => document.querySelector('.app-symbol')?.naturalWidth > 0);
    await editor.locator('#phrase-title').fill('粘贴图片验证'); await input.fill('前文替换后文');
    await input.evaluate(el => { el.focus(); const range = document.createRange(); range.setStart(el.firstChild, 2); range.setEnd(el.firstChild, 4); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); });
    await pasteImage(input, bytes); await editor.waitForFunction(() => document.querySelector('.inline-image img')?.naturalWidth > 0);
    assert.equal(await input.textContent(), '前文×后文');
    for (const [name, file] of [['默认颜色', 'default'], ['绿色', 'green'], ['蓝色', 'blue'], ['灰色', 'gray']]) {
      await click(editor.getByRole('button', { name, exact: true }));
      await editor.locator('.color-palette').screenshot({ path: path.join(evidence, 'title-color-' + file + '.png') });
    }
    await click(editor.getByRole('button', { name: '默认颜色', exact: true }));
    await editor.screenshot({ path: path.join(evidence, 'new-editor-pasted-image.png') }); await finish(editor);
    let saved = (await invoke('get_library')).phrases.find(p => p.title === '粘贴图片验证'); assert.ok(saved);
    assert.deepEqual(saved.segments[0].blocks.map(b => b.kind), ['text', 'image', 'text']);
    assert.equal(saved.segments[0].blocks[0].text, '前文'); assert.equal(saved.segments[0].blocks[2].text, '后文');
    assert.ok(saved.segments[0].blocks[1].path.startsWith(path.join(evidence, 'data', 'images')));
    await right(row(saved.id)); await menu('编辑'); editor = await view('editor', '.editor-window');
    await editor.waitForFunction(() => document.querySelector('.inline-image img')?.naturalWidth > 0);
    await click(editor.getByRole('button', { name: '添加分段', exact: true })); input = editor.getByRole('textbox', { name: '第2段文字', exact: true });
    await input.fill('第二段内容'); await input.evaluate(el => { el.focus(); const range = document.createRange(); range.setStart(el.firstChild, 2); range.collapse(true); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); });
    await pasteImage(input, bytes); await editor.waitForFunction(() => [...document.querySelectorAll('.inline-image img')].length === 2 && [...document.querySelectorAll('.inline-image img')].every(img => img.naturalWidth > 0));
    await editor.screenshot({ path: path.join(evidence, 'edit-editor-pasted-image.png') }); await finish(editor);
    saved = (await invoke('get_library')).phrases.find(p => p.id === saved.id);
    assert.equal(saved.segments.length, 2); assert.deepEqual(saved.segments[1].blocks.map(b => b.kind), ['text', 'image', 'text']);
    assert.equal(saved.segments[1].blocks[0].text, '第二'); assert.equal(saved.segments[1].blocks[2].text, '段内容');
    for (const segment of saved.segments) for (const block of segment.blocks) if (block.kind === 'image') assert.ok((await fs.stat(block.path)).size > 0);
    await right(row(saved.id)); await menu('编辑'); editor = await view('editor', '.editor-window');
    await editor.waitForFunction(() => [...document.querySelectorAll('.inline-image img')].length === 2 && [...document.querySelectorAll('.inline-image img')].every(img => img.naturalWidth > 0));
    await finish(editor, '取消'); await invoke('delete_item', { id: saved.id, kind: 'phrase', recycle: false });
  });
  await check('图片插入选区、撤销重做、日期、文件和分段按原顺序保存', async () => {
    await right(row(imageId)); await menu('编辑'); const editor = await view('editor', '.editor-window');
    const input = editor.getByRole('textbox', { name: '第1段文字', exact: true }); await input.fill('前文选区后文');
    await input.evaluate(el => { el.focus(); const range = document.createRange(); range.setStart(el.firstChild, 2); range.setEnd(el.firstChild, 4); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true })); });
    await editor.evaluate(paths => window.__libraryQA.paths = paths, [sample]); await click(editor.getByRole('button', { name: '图片', exact: true }));
    await editor.waitForFunction(() => document.querySelector('.inline-image img')?.naturalWidth > 0);
    assert.equal(await input.evaluate(el => el.childNodes[0].textContent), '前文'); assert.equal(await input.evaluate(el => el.lastChild.textContent), '后文');
    assert.equal(await input.evaluate(() => document.execCommand('undo')), true); await editor.locator('.inline-image').waitFor({ state: 'detached' }); assert.equal(await input.textContent(), '前文选区后文');
    assert.equal(await input.evaluate(() => document.execCommand('redo')), true); await editor.locator('.inline-image').waitFor();
    await input.evaluate(el => { el.focus(); const range = document.createRange(); range.setStart(el.lastChild, 0); range.collapse(true); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true })); });
    await click(editor.getByRole('button', { name: '日期', exact: true }));
    await editor.evaluate(paths => window.__libraryQA.paths = paths, [sourceFiles[0]]); await click(editor.getByRole('button', { name: '文件', exact: true })); await click(editor.getByRole('button', { name: '本机文件', exact: true })); await editor.locator('.inline-file').waitFor();
    await click(editor.getByRole('button', { name: '添加分段', exact: true })); await editor.getByRole('textbox', { name: '第2段文字', exact: true }).fill('第二段\n保留中文与换行');
    await editor.screenshot({ path: path.join(evidence, 'inline-image-editor.png') }); await click(editor.getByRole('button', { name: '确定', exact: true }));
    await editor.waitForEvent('close').catch(() => {});
    const saved = (await invoke('get_library')).phrases.find(p => p.id === imageId); assert.equal(saved.segments.length, 2);
    assert.deepEqual(saved.segments[0].blocks.map(b => b.kind), ['text', 'image', 'text', 'file', 'text']);
    assert.equal(saved.segments[0].blocks[0].text, '前文'); assert.match(saved.segments[0].blocks[2].text, /^\d{4}-\d{2}-\d{2}$/); assert.equal(saved.segments[0].blocks[4].text, '后文');
    assert.equal(saved.segments[1].blocks[0].text, '第二段\n保留中文与换行');
  });
  await check('图文话术图标随保存内容变化；重开能编辑行内图片，搜索复用图片图标', async () => {
    await page.locator('[data-row="' + imageId + '"] .row-icon[title="含图片话术"]').waitFor();
    assert.equal(await row(textId).locator('.row-icon').getAttribute('title'), '文字话术');
    await page.screenshot({ path: path.join(evidence, 'phrase-image-icons.png') });
    await right(row(imageId)); await menu('编辑'); const editor = await view('editor', '.editor-window');
    await editor.waitForFunction(() => document.querySelector('.inline-image img')?.naturalWidth > 0);
    await click(editor.getByRole('button', { name: '删除附件 icon.png', exact: true })); await editor.locator('.inline-image').waitFor({ state: 'detached' });
    await click(editor.getByRole('button', { name: '确定', exact: true })); await page.locator('[data-row="' + imageId + '"] .row-icon[title="文字话术"]').waitFor();
    const p = (await invoke('get_library')).phrases.find(p => p.id === imageId); const images = await invoke('import_images', { paths: [sample] }); p.segments[0].blocks.splice(1, 0, ...images); await invoke('save_phrase', { phrase: p, afterId: null });
    await page.getByRole('textbox', { name: '搜索话术、本地文件' }).fill('进群图文'); await page.locator('#search-results .row-icon[title="含图片话术"]').waitFor();
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
  });
  let originalImage;
  await check('删除确认默认回收、取消无写入；快速连点只删除一次', async () => {
    let deleting = await deletion(imageId); assert.equal(await deleting.getByRole('checkbox').isChecked(), true); await deleting.screenshot({ path: path.join(evidence, 'delete-phrase.png') });
    await click(deleting.getByRole('button', { name: '取消', exact: true })); assert.ok((await invoke('get_library')).phrases.some(p => p.id === imageId)); assert.equal((await invoke('get_recycle_bin')).length, 0);
    originalImage = (await invoke('get_library')).phrases.find(p => p.id === imageId);
    deleting = await deletion(imageId); await deleting.getByRole('button', { name: '确定', exact: true }).evaluate(el => { el.click(); el.click(); });
    await page.waitForFunction(id => !document.querySelector('[data-row="' + id + '"]'), imageId);
    const records = await invoke('get_recycle_bin'); assert.equal(records.length, 1); assert.equal(records[0].kind, 'phrase');
  });
  await click(page.getByRole('button', { name: '主菜单', exact: true })); await menu('回收站'); const recycle = await view('recycle-bin', '.recycle-window');
  await check('回收站显示分类、标题背景、分段、图片和附件，右侧可滚动', async () => {
    await recycle.getByRole('textbox', { name: '回收话术标题' }).waitFor(); assert.equal(await recycle.getByRole('textbox', { name: '回收话术标题' }).inputValue(), '进群图文');
    await recycle.waitForFunction(() => document.querySelector('.recycle-content img')?.naturalWidth > 0);
    assert.equal(await recycle.locator('.recycle-color').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 204)');
    assert.ok((await recycle.locator('.recycle-detail').textContent()).includes('附件：原文件 A.txt'));
    assert.equal(await recycle.locator('.recycle-detail').evaluate(el => getComputedStyle(el).overflowY), 'auto');
    await recycle.screenshot({ path: path.join(evidence, 'recycle-phrase-detail.png') });
  });
  await check('还原弹窗只有个人话术，取消不改数据；确认完整恢复原ID及内容', async () => {
    await click(recycle.getByRole('button', { name: '还原', exact: true })); await recycle.locator('dialog').waitFor();
    assert.ok((await recycle.locator('dialog').textContent()).includes('个人话术')); assert.ok(!(await recycle.locator('dialog').textContent()).includes('公司话术'));
    await recycle.screenshot({ path: path.join(evidence, 'restore-dialog.png') }); await click(recycle.locator('dialog').getByRole('button', { name: '取消', exact: true })); assert.equal((await invoke('get_recycle_bin')).length, 1);
    await click(recycle.getByRole('button', { name: '还原', exact: true })); await form(recycle); await recycle.getByText('回收站为空', { exact: true }).waitFor();
    assert.deepEqual((await invoke('get_library')).phrases.find(p => p.id === imageId), originalImage);
  });
  await check('分类删除包含全部二级分类与话术，还原保留层级、排序和配色', async () => {
    const before = await invoke('get_library'); const deleting = await deletion('客服', true); await deleting.screenshot({ path: path.join(evidence, 'delete-category.png') }); await click(deleting.getByRole('button', { name: '确定', exact: true }));
    await recycle.getByRole('textbox', { name: '一级分类名称' }).waitFor(); assert.equal(await recycle.getByRole('textbox', { name: '一级分类名称' }).inputValue(), '客服');
    await recycle.screenshot({ path: path.join(evidence, 'recycle-category-detail.png') }); await click(recycle.getByRole('button', { name: '还原', exact: true })); await form(recycle);
    await page.getByRole('button', { name: '客服', exact: true }).waitFor(); const after = await invoke('get_library');
    assert.deepEqual(after.categories.filter(c => c.id === root || c.parentId === root), before.categories.filter(c => c.id === root || c.parentId === root));
    assert.deepEqual(after.phrases.filter(p => p.rootId === root), before.phrases.filter(p => p.rootId === root)); await click(page.getByRole('button', { name: '客服', exact: true }));
  });
  await check('明确取消回收后永久删除话术，不增加回收记录', async () => {
    const deleting = await deletion(textId); await click(deleting.getByRole('checkbox')); assert.ok((await deleting.locator('form').textContent()).includes('删除后无法恢复'));
    await click(deleting.getByRole('button', { name: '确定', exact: true })); await page.waitForFunction(id => !document.querySelector('[data-row="' + id + '"]'), textId); assert.equal((await invoke('get_recycle_bin')).length, 0);
  });
  await check('删除后移动二级分类，逐条还原重建并复用原位置，活动内容保持已移动状态', async () => {
    const origin = await category('恢复来源'), target = await category('恢复目标'), oldGroup = await category('待恢复分组', 'phrase', origin);
    const first = await phrase('移动恢复一', origin, oldGroup), second = await phrase('移动恢复二', origin, oldGroup), active = await phrase('继续使用', origin, oldGroup);
    await invoke('delete_item', { id: first, kind: 'phrase' }); await invoke('delete_item', { id: second, kind: 'phrase' }); await invoke('move_copy_category', { id: origin, targetId: target, copy: false });
    await searchRecycle(recycle, '移动恢复一'); await click(recycle.getByRole('button', { name: '还原', exact: true })); await form(recycle);
    await searchRecycle(recycle, '移动恢复二'); await click(recycle.getByRole('button', { name: '还原', exact: true })); await form(recycle);
    const lib = await invoke('get_library'), a = lib.phrases.find(p => p.id === first), b = lib.phrases.find(p => p.id === second), c = lib.phrases.find(p => p.id === active);
    assert.equal(a.rootId, origin); assert.equal(b.groupId, a.groupId); assert.notEqual(a.groupId, oldGroup); assert.equal(c.rootId, target); assert.equal(c.groupId, oldGroup);
    assert.equal((await invoke('get_recycle_bin')).length, 0);
  });
  await check('回收站关键词、全拼、首字母搜索和10条分页，恢复末页后回到有效页', async () => {
    for (let i = 0; i < 11; i++) { const id = await phrase('分页' + i, root); await invoke('delete_item', { id, kind: 'phrase' }); }
    for (const query of ['分页', 'fenye', 'fy']) { await searchRecycle(recycle, query); assert.equal((await recycleIds(recycle)).length, 10); }
    await click(recycle.getByRole('button', { name: '下一页', exact: true })); assert.equal((await recycleIds(recycle)).length, 1); await recycle.screenshot({ path: path.join(evidence, 'recycle-last-page.png') });
    await click(recycle.getByRole('button', { name: '还原', exact: true })); await form(recycle);
    await recycle.waitForFunction(() => document.querySelectorAll('.recycle-list [role="option"]').length === 10); assert.equal(await recycle.getByRole('button', { name: '上一页', exact: true }).isDisabled(), true);
    await searchRecycle(recycle, '无匹配词'); await recycle.getByText('没有匹配的回收记录', { exact: true }).waitFor();
  });
  await check('快捷键冲突保留回收记录，可明确不恢复快捷键后重试', async () => {
    const conflict = await phrase('冲突恢复', root, null, undefined, 'Ctrl+Alt+1'); await invoke('delete_item', { id: conflict, kind: 'phrase' }); await phrase('活动快捷键', root, null, undefined, 'Ctrl+Alt+1+C');
    await searchRecycle(recycle, '冲突恢复'); await click(recycle.getByRole('button', { name: '还原', exact: true }));
    await recycle.locator('dialog form').evaluate(el => el.requestSubmit()); await recycle.locator('dialog [role="alert"]').waitFor(); assert.ok((await recycle.locator('[role="alert"]').textContent()).includes('已被占用'));
    assert.equal((await invoke('get_recycle_bin', { query: '冲突恢复' })).length, 1); await recycle.screenshot({ path: path.join(evidence, 'restore-shortcut-conflict.png') });
    await click(recycle.getByRole('checkbox', { name: '恢复时不恢复快捷键', exact: true })); await form(recycle);
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === conflict).shortcut, '');
  });
  await check('永久删除和清空都有确认；清空包括当前搜索结果以外的记录', async () => {
    await searchRecycle(recycle, '分页'); await recycle.getByRole('textbox', { name: '回收话术标题' }).waitFor();
    await click(recycle.getByRole('button', { name: '永久删除该记录', exact: true })); await click(recycle.locator('dialog').getByRole('button', { name: '取消', exact: true })); assert.equal((await invoke('get_recycle_bin')).length, 10);
    await click(recycle.getByRole('button', { name: '永久删除该记录', exact: true })); await form(recycle); assert.equal((await invoke('get_recycle_bin')).length, 9);
    await searchRecycle(recycle, '分页10'); if (!(await recycleIds(recycle)).length) await searchRecycle(recycle, '分页9');
    await click(recycle.getByRole('button', { name: '清空当前回收站', exact: true })); assert.ok((await recycle.locator('dialog').textContent()).includes('搜索结果之外')); await form(recycle); assert.equal((await invoke('get_recycle_bin')).length, 0);
  });
  await click(page.getByRole('button', { name: '本地文件', exact: true })); await invoke('add_files', { categoryId: fileRoot, paths: sourceFiles });
  const targetCategory = await category('目标文件', 'file'); let files = (await invoke('get_library')).files; const fileIds = files.map(f => f.id);
  await check('本地文件和分类菜单完整，显示名修改保留源文件', async () => {
    await right(page.locator('.category-tabs button').first());
    assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['新增分类', '重命名', '删除', '快速排序']);
    await right(row(fileIds[0])); assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['发到输入区（双击）', '直接发送', '复制文件', '新增', '修改显示名', '移除关联', '快速排序文件', '移动到其它分类', '打开文件', '打开文件位置', '重新关联文件', '刷新文件关联状态', '如何批量操作？']);
    await page.screenshot({ path: path.join(evidence, 'file-context-menu.png') }); await menu('修改显示名'); await page.locator('dialog input[name="name"]').fill('自定义显示名'); await form(page);
    assert.equal((await invoke('get_library')).files.find(f => f.id === fileIds[0]).name, '自定义显示名'); assert.ok(await fs.stat(sourceFiles[0]));
  });
  await check('文件分类新增、重命名和删除取消／确认均生效，删除只移除关联', async () => {
    await right(page.locator('.category-tabs button').first()); await menu('新增分类'); await page.locator('dialog input[name="name"]').fill('临时文件分类'); await form(page);
    const temp = (await invoke('get_library')).categories.find(c => c.kind === 'file' && c.name === '临时文件分类'); assert.ok(temp);
    await right(page.getByRole('button', { name: '临时文件分类', exact: true })); await menu('重命名'); await page.locator('dialog input[name="name"]').fill('临时重命名'); await form(page);
    assert.equal((await invoke('get_library')).categories.find(c => c.id === temp.id).name, '临时重命名'); await invoke('add_files', { categoryId: temp.id, paths: [sourceFiles[2]] });
    await page.evaluate(() => window.__libraryQA.confirm = false); await right(page.getByRole('button', { name: '临时重命名', exact: true })); await menu('删除'); await delay(80); assert.ok((await invoke('get_library')).categories.some(c => c.id === temp.id));
    await page.evaluate(() => window.__libraryQA.confirm = true); await right(page.getByRole('button', { name: '临时重命名', exact: true })); await menu('删除'); await page.getByRole('button', { name: '临时重命名', exact: true }).waitFor({ state: 'detached' });
    assert.equal((await invoke('get_library')).files.length, 4); assert.equal((await invoke('get_recycle_bin')).length, 0); assert.ok(await fs.stat(sourceFiles[2]));
    await click(page.locator('.category-tabs').getByRole('button', { name: fileCategoryName, exact: true }));
  });
  await check('文件分类快速排序支持取消和确认，保持文件所属分类', async () => {
    const before = (await invoke('get_library')).categories.filter(c => c.kind === 'file').map(c => c.id);
    await right(page.locator('.category-tabs button').first()); await menu('快速排序'); let sorting = await view('category-sort', '.common-sort'); await moveFirst(sorting); await finish(sorting, '取消');
    assert.deepEqual((await invoke('get_library')).categories.filter(c => c.kind === 'file').map(c => c.id), before);
    await right(page.locator('.category-tabs button').first()); await menu('快速排序'); sorting = await view('category-sort', '.common-sort'); await moveFirst(sorting); await finish(sorting);
    const lib = await invoke('get_library'); assert.deepEqual(lib.categories.filter(c => c.kind === 'file').map(c => c.id), [before[1], before[0]]); assert.ok(lib.files.every(f => f.categoryId === fileRoot));
  });
  await check('文件新增窗口批量关联，取消不写库；批量操作说明可正常打开', async () => {
    const added = ['新增 E.txt', '新增 F.txt'].map(name => path.join(evidence, name)); for (const file of added) await fs.writeFile(file, '批量新增');
    await right(row(fileIds[0])); await menu('新增'); let editor = await view('file-editor', '.file-window'); await editor.evaluate(paths => window.__libraryQA.paths = paths, added);
    await click(editor.getByRole('button', { name: '＋ 添加文件', exact: true })); assert.equal(await editor.locator('.chosen-files span').count(), 2); await finish(editor, '取消'); assert.equal((await invoke('get_library')).files.length, 4);
    await right(row(fileIds[0])); await menu('新增'); editor = await view('file-editor', '.file-window'); await editor.evaluate(paths => window.__libraryQA.paths = paths, added); await click(editor.getByRole('button', { name: '＋ 添加文件', exact: true })); await finish(editor);
    const addedRecords = (await invoke('get_library')).files.filter(f => added.includes(f.path)); assert.equal(addedRecords.length, 2); await invoke('remove_linked_files', { ids: addedRecords.map(f => f.id) });
    for (const file of added) assert.ok(await fs.stat(file)); await right(row(fileIds[0])); await menu('如何批量操作？'); assert.ok((await page.locator('dialog').textContent()).includes('Ctrl+A')); await form(page);
  });
  await check('文件右键发送、插入、复制和打开位置绑定被点击的文件', async () => {
    for (const label of ['发到输入区（双击）', '直接发送', '复制文件', '打开文件', '打开文件位置']) { await right(row(fileIds[1])); await menu(label); }
    const calls = await requests(page); const deliveries = calls.filter(c => c.cmd === 'deliver_content'); assert.deepEqual(deliveries.map(c => [c.args.id, c.args.mode]), [[fileIds[1], 'insert'], [fileIds[1], 'send']]);
    assert.deepEqual(calls.filter(c => c.cmd === 'copy_linked_files').at(-1).args.ids, [fileIds[1]]); assert.equal(calls.filter(c => c.cmd === 'open_linked_file').at(-1).args.reveal, true);
  });
  await check('文件快速排序取消不保存，确定按主键保存顺序', async () => {
    const order = (await invoke('get_library')).files.map(f => f.id); await right(row(fileIds[0])); await menu('快速排序文件'); let sorting = await view('file-sort', '.common-sort');
    await moveFirst(sorting); await finish(sorting, '取消'); assert.deepEqual((await invoke('get_library')).files.map(f => f.id), order);
    await right(row(fileIds[0])); await menu('快速排序文件'); sorting = await view('file-sort', '.common-sort'); await moveFirst(sorting); await sorting.screenshot({ path: path.join(evidence, 'file-sort.png') });
    await finish(sorting); const expected = [order[1], order[0], ...order.slice(2)]; assert.deepEqual((await invoke('get_library')).files.map(f => f.id), expected);
  });
  await check('Ctrl和Shift多选禁用单项动作，搜索右键回到单项，复制绑定当时的集合', async () => {
    files = (await invoke('get_library')).files; const ids = files.map(f => f.id); await choose(ids[0]); await choose(ids[2], { shiftKey: true });
    assert.equal(await page.locator('.file-row[aria-selected="true"]').count(), 3); await choose(ids[1], { ctrlKey: true }); assert.equal(await page.locator('.file-row[aria-selected="true"]').count(), 2);
    await right(row(ids[0])); for (const label of ['直接发送', '修改显示名', '打开文件']) assert.equal(await page.getByRole('menuitem', { name: label, exact: true }).isDisabled(), true);
    await menu('复制文件'); assert.deepEqual((await requests(page)).filter(c => c.cmd === 'copy_linked_files').at(-1).args.ids, [ids[0], ids[2]]);
    await page.getByRole('textbox', { name: '搜索话术、本地文件' }).fill('自定义显示名'); await page.locator('#search-results .file-row').waitFor(); await right(page.locator('#search-results .file-row'));
    assert.equal(await page.getByRole('menuitem', { name: '修改显示名', exact: true }).isEnabled(), true); await menu('复制文件'); assert.deepEqual((await requests(page)).filter(c => c.cmd === 'copy_linked_files').at(-1).args.ids, [fileIds[0]]);
    await click(page.getByRole('button', { name: '清空搜索', exact: true }));
  });
  await check('失效文件可刷新和重新关联，源文件保留且不进入回收站', async () => {
    await fs.unlink(sourceFiles[3]); await right(row(fileIds[3])); await menu('刷新文件关联状态'); await row(fileIds[3]).locator('.row-status').waitFor();
    await right(row(fileIds[3])); assert.equal(await page.getByRole('menuitem', { name: '打开文件', exact: true }).isDisabled(), true); assert.equal(await page.getByRole('menuitem', { name: '重新关联文件', exact: true }).isEnabled(), true);
    await menu('重新关联文件'); const editor = await view('file-editor', '.file-window'); await editor.evaluate(paths => window.__libraryQA.paths = paths, [sourceFiles[2]]);
    await click(editor.getByRole('button', { name: '＋ 添加文件', exact: true })); await click(editor.getByRole('button', { name: '确定', exact: true }));
    assert.equal((await invoke('get_library')).files.find(f => f.id === fileIds[3]).missing, false); assert.equal((await invoke('get_recycle_bin')).length, 0);
  });
  await check('文件批量移动和取消／确认移除只操作关联，不删除源文件', async () => {
    await choose(fileIds[0]); await choose(fileIds[1], { ctrlKey: true }); await right(row(fileIds[0])); await menu('移动到其它分类'); await page.locator('dialog select[name="target"]').selectOption(targetCategory); await form(page);
    await page.getByRole('button', { name: '目标文件', exact: true }).waitFor(); const moved = (await invoke('get_library')).files.filter(f => f.categoryId === targetCategory); assert.equal(moved.length, 2);
    await choose(fileIds[0]); await choose(fileIds[1], { ctrlKey: true }); await page.evaluate(() => window.__libraryQA.confirm = false); await right(row(fileIds[0])); await menu('移除关联'); await delay(100); assert.equal((await invoke('get_library')).files.length, 4);
    await page.evaluate(() => window.__libraryQA.confirm = true); await right(row(fileIds[0])); await menu('移除关联'); await page.waitForFunction(() => document.querySelectorAll('.content-list .file-row').length === 0); assert.equal((await invoke('get_library')).files.length, 2);
    for (const file of sourceFiles.slice(0, 3)) assert.ok(await fs.stat(file)); assert.equal((await invoke('get_recycle_bin')).length, 0);
  });
  await check('文件菜单在小窗口内滚动，所有界面保留直角', async () => {
    await click(page.locator('.category-tabs').getByRole('button', { name: fileCategoryName, exact: true })); await page.setViewportSize({ width: 300, height: 400 }); await right(row(fileIds[2]));
    const bounds = await page.locator('.context-menu').evaluate(el => { const b = el.getBoundingClientRect(); return { x: b.x, y: b.y, right: b.right, bottom: b.bottom, height: b.height, scroll: el.scrollHeight, overflow: getComputedStyle(el).overflowY }; });
    assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.right <= 300 && bounds.bottom <= 400); assert.ok(bounds.scroll > bounds.height); assert.equal(bounds.overflow, 'auto'); await page.screenshot({ path: path.join(evidence, 'file-menu-small-window.png') });
    assert.ok(await page.locator('.context-menu').evaluate(el => { const b = el.getBoundingClientRect(); return !!document.elementFromPoint(b.x + b.width / 2, Math.min(b.bottom - 5, 340))?.closest('.context-menu'); }));
    for (const target of views) if (!target.isClosed()) assert.deepEqual(await target.locator('.client-window').evaluate(el => { const s = getComputedStyle(el); return [s.borderTopLeftRadius, s.borderTopRightRadius, s.borderBottomLeftRadius, s.borderBottomRightRadius]; }), ['0px', '0px', '0px', '0px']);
  });
  // 缓存管理的批量删除只在本脚本独立库执行，素材与源文件逐项读取后核对保留。
  await page.setViewportSize({ width: 330, height: 720 });
  const cacheRoot = (await invoke('get_library')).categories.find(c => c.kind === 'phrase' && !c.parentId).id;
  const cacheImages = await invoke('import_images', { paths: [sample] });
  await phrase('缓存管理图文', cacheRoot, null, [...cacheImages, { kind: 'file', path: sourceFiles[0], name: '原文件 A.txt' }]);
  const oldDeleted = await phrase('保留旧回收记录', cacheRoot); await invoke('delete_item', { id: oldDeleted, kind: 'phrase', recycle: true });
  for (const text of ['缓存管理短语一', '缓存管理短语二']) await invoke('save_common_phrase', { phrase: { id: '', text, position: 0 } });
  await click(page.getByRole('button', { name: '设置', exact: true })); const cacheView = await view('settings', '.settings-window');
  await click(cacheView.getByRole('button', { name: '缓存管理', exact: true }));
  const currentLibrary = () => invoke('get_library'); const currentRecycle = () => invoke('get_recycle_bin');
  const orderedPhrases = library => library.phrases.slice().sort((a, b) => a.id.localeCompare(b.id));
  const assetsDir = path.join(evidence, 'data', 'images');
  const assetNames = await fs.readdir(assetsDir); const assetContents = await Promise.all(assetNames.map(name => fs.readFile(path.join(assetsDir, name))));
  const retainedSources = sourceFiles.slice(0, 3); const sourceContents = await Promise.all(retainedSources.map(file => fs.readFile(file)));
  const assertAssetsRetained = async () => {
    assert.deepEqual(await fs.readdir(assetsDir), assetNames);
    for (let i = 0; i < assetNames.length; i++) assert.deepEqual(await fs.readFile(path.join(assetsDir, assetNames[i])), assetContents[i]);
    for (let i = 0; i < retainedSources.length; i++) assert.deepEqual(await fs.readFile(retainedSources[i]), sourceContents[i]);
  };
  await check('缓存页按参考图分区，展示本地删除与缓存清理入口，窗口保持隐藏', async () => {
    assert.deepEqual(await cacheView.locator('.cache-settings h2').allTextContents(), ['一键删除（请谨慎操作）', '清理本地缓存']);
    assert.deepEqual(await cacheView.locator('.cache-settings .setting-buttons button').allTextContents(), ['个人话术', '常用短语', '清理缩略图', '清理临时文件']);
    assert.equal(await cacheView.getByText(/云端|公司话术|共享话术|表情/).count(), 0);
    await click(cacheView.getByRole('button', { name: '打开本地缓存文件夹', exact: true }));
    assert.deepEqual((await requests(cacheView)).filter(c => c.cmd === 'open_directory').at(-1).args, { kind: 'cache' });
    const buttons = await cacheView.locator('.setting-buttons button').evaluateAll(els => els.map(el => { const b = el.getBoundingClientRect(); return { x: b.x, y: b.y, width: b.width, height: b.height }; }));
    assert.equal(buttons[0].y, buttons[1].y); assert.equal(buttons[2].y, buttons[3].y); for (const button of buttons) assert.equal(button.height, 32);
    assert.equal(await invoke('plugin:window|is_visible', {}, cacheView), false);
    await cacheView.screenshot({ path: path.join(evidence, 'cache-management-aligned.png') });
  });
  await check('两类整库删除均需确认，取消保留全部内容；个人话术默认勾选回收', async () => {
    const before = await currentLibrary();
    for (const label of ['个人话术', '常用短语']) {
      await click(cacheView.getByRole('button', { name: label, exact: true })); await cacheView.locator('dialog[open]').waitFor();
      if (label === '个人话术') { assert.equal(await cacheView.locator('input[name="recycle"]').isChecked(), true); await cacheView.screenshot({ path: path.join(evidence, 'cache-delete-phrases-confirm.png') }); }
      else { assert.ok((await cacheView.locator('dialog').textContent()).includes('删除后无法恢复')); await cacheView.screenshot({ path: path.join(evidence, 'cache-delete-common-confirm.png') }); }
      await click(cacheView.getByRole('button', { name: '取消', exact: true })); await cacheView.locator('dialog').waitFor({ state: 'detached' });
    }
    assert.deepEqual(await currentLibrary(), before); assert.equal((await requests(cacheView)).filter(c => c.cmd === 'delete_local_content').length, 0);
  });
  await check('确认期间另一窗口新增内容，两类旧确认均拒绝删除并保留错误提示', async () => {
    for (const label of ['个人话术', '常用短语']) {
      await click(cacheView.getByRole('button', { name: label, exact: true })); await cacheView.locator('dialog[open]').waitFor();
      if (label === '个人话术') await phrase('确认期间新增', cacheRoot);
      else await invoke('save_common_phrase', { phrase: { id: '', text: '确认期间新增', position: 0 } });
      const before = await currentLibrary(), recycled = await currentRecycle();
      await cacheView.locator('dialog form').evaluate(el => el.requestSubmit()); await cacheView.getByRole('alert').waitFor();
      assert.ok((await cacheView.getByRole('alert').textContent()).includes('内容列表已变化')); assert.deepEqual(await currentLibrary(), before); assert.deepEqual(await currentRecycle(), recycled);
      await click(cacheView.getByRole('button', { name: '取消', exact: true }));
    }
  });
  await check('清理缩略图和临时文件实际落盘，保留数据库内容、素材及源文件', async () => {
    const before = await currentLibrary(), recycled = await currentRecycle();
    await fs.writeFile(path.join(evidence, 'data', 'temp', '缓存.txt'), '临时缓存');
    for (const label of ['清理缩略图', '清理临时文件']) {
      await click(cacheView.getByRole('button', { name: label, exact: true })); await cacheView.getByRole('status').filter({ hasText: '缓存已清理' }).waitFor(); await click(cacheView.getByRole('status'));
    }
    assert.deepEqual(await fs.readdir(path.join(evidence, 'data', 'thumbnails')), []); assert.deepEqual(await fs.readdir(path.join(evidence, 'data', 'temp')), []);
    assert.equal(await invoke('cache_info'), 0); assert.deepEqual(await currentLibrary(), before); assert.deepEqual(await currentRecycle(), recycled); await assertAssetsRetained();
  });
  await check('删除全部个人话术默认进入回收站，保留其他本地内容并可完整还原', async () => {
    const before = await currentLibrary(), oldRecords = await currentRecycle();
    await click(cacheView.getByRole('button', { name: '个人话术', exact: true })); await form(cacheView);
    const after = await currentLibrary(); assert.equal(after.phrases.length, 0);
    for (const key of ['categories', 'commonPhrases', 'files', 'settings']) assert.deepEqual(after[key], before[key]); await assertAssetsRetained();
    const records = await currentRecycle(); const added = records.filter(r => !oldRecords.some(old => old.id === r.id));
    assert.equal(added.length, before.phrases.length); assert.deepEqual(records.filter(r => oldRecords.some(old => old.id === r.id)), oldRecords);
    await invoke('restore_recycled', { ids: added.map(r => r.id), withoutShortcuts: false });
    assert.deepEqual(orderedPhrases(await currentLibrary()), orderedPhrases(before)); assert.deepEqual(await currentRecycle(), oldRecords);
  });
  await check('常用短语确认后永久删除，只清空短语且不增加回收记录', async () => {
    const before = await currentLibrary(), oldRecords = await currentRecycle();
    await click(cacheView.getByRole('button', { name: '常用短语', exact: true })); await form(cacheView);
    const after = await currentLibrary(); assert.equal(after.commonPhrases.length, 0);
    for (const key of ['phrases', 'categories', 'files', 'settings']) assert.deepEqual(after[key], before[key]); assert.deepEqual(await currentRecycle(), oldRecords);
    await assertAssetsRetained(); await cacheView.waitForFunction(() => [...document.querySelectorAll('.cache-settings button')].find(el => el.textContent === '常用短语')?.disabled);
  });
  await check('话术明确取消回收后永久删除，空库禁用入口，最小设置窗口布局无溢出', async () => {
    const before = await currentLibrary(), oldRecords = await currentRecycle();
    await click(cacheView.getByRole('button', { name: '个人话术', exact: true })); await click(cacheView.locator('input[name="recycle"]')); await form(cacheView);
    const after = await currentLibrary(); assert.equal(after.phrases.length, 0);
    for (const key of ['categories', 'files', 'settings']) assert.deepEqual(after[key], before[key]); assert.deepEqual(await currentRecycle(), oldRecords); await assertAssetsRetained();
    await cacheView.waitForFunction(() => [...document.querySelectorAll('.cache-settings button')].find(el => el.textContent === '个人话术')?.disabled);
    if (await cacheView.getByRole('status').count()) await click(cacheView.getByRole('status'));
    await cacheView.setViewportSize({ width: 480, height: 480 }); const geometry = await cacheView.locator('.settings-content').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth })); assert.ok(geometry.scroll <= geometry.width + 1);
    await cacheView.screenshot({ path: path.join(evidence, 'cache-management-empty-compact.png') });
  });
  const report = { date: new Date().toISOString(), scope: 'recycle-file-library-inline-images-local-cache', checks: results.length, results, limits: ['实际客户端保持隐藏并使用独立测试数据；未操作正式客户端或实体键鼠', '发送、剪贴板复制和系统打开只验证请求身份与参数，实际系统效果未在本轮验收', '中文与换行通过DOM输入验证，实体中文输入法、不同DPI和多显示器未在本轮验收', '新装、升级、卸载未在本轮重验'] };
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ evidence, checks: results.length }, null, 2));
} catch (error) {
  for (const target of views) if (!target.isClosed()) await target.screenshot({ path: path.join(evidence, 'failure-' + (target.url().split('#')[1]?.split('?')[0] || 'main') + '.png') }).catch(() => {});
  const diagnostics = [];
  for (const target of views) if (!target.isClosed()) diagnostics.push({ url: target.url(), state: await target.evaluate(() => ({ requests: window.__libraryQA?.requests, edits: window.__libraryQA?.edits, content: document.querySelector('.rich-segment')?.innerHTML })).catch(() => null) });
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ results, error: String(error), stack: error.stack, diagnostics }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  for (let i = 0; i < 25 && app.exitCode === null; i++) await delay(40);
  if (app.exitCode === null) app.kill();
}
