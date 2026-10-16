import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

// 在隔离数据目录启动真实 Tauri 客户端。发送只进入本机标准剪贴板宿主，绝不操作聊天会话。
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = path.join(repo, '.build', `native-${Date.now()}`);
const executable = path.join(repo, 'src-tauri', 'target', 'debug', 'QuickPhrase.exe');
const run = promisify(execFile);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const powershell = args => run('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
const native = async args => JSON.parse((await powershell(['-File', path.join(repo, 'tests', 'native-driver.ps1'), ...args])).stdout.trim());
const results = [];
let host, dockHost, app, browser, page, context, hostInfo, mainHandle;
const eventsPath = path.join(root, 'events.json');
const controlPath = path.join(root, 'control.json');
const clipboardMarker = '闪语本机验收剪贴板';
const dockOnly = process.argv.includes('--dock-only');
const read = async filename => {
  for (let i = 0; i < 1000; i++) {
    try { return JSON.parse(await fs.readFile(filename, 'utf8')); }
    catch { await delay(30); }
  }
  throw new Error(`验收宿主没有提供有效记录：${filename}`);
};
const control = async (command, fields = {}, filename = controlPath) => {
  const temporary = `${filename}.tmp`;
  await fs.writeFile(temporary, JSON.stringify({ command, ...fields, nonce: Date.now() }));
  // WinForms 读取期间短暂持有文件句柄；原子替换并重试，避免传输层截断 JSON。
  for (let i = 0; ; i++) {
    try { await fs.rename(temporary, filename); break; }
    catch (error) { if (i >= 50 || !['EBUSY', 'EPERM', 'EACCES'].includes(error.code)) throw error; await delay(10); }
  }
  await delay(130);
};
const invoke = (cmd, args = {}, window = page) => window.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const transfer = async (cmd, args = {}) => {
  const [library, account] = await Promise.all([invoke('get_library'), invoke('get_team_status')]);
  return invoke(cmd, { merge: false, restoreSettings: false, operationId: randomUUID(), ...args, target: { source: 'private', libraryId: library.libraryId, sessionEpoch: account.sessionEpoch } });
};
const poll = async (check, description, attempts = 75) => {
  for (let i = 0; i < attempts; i++) { if (await check()) return; await delay(40); }
  throw new Error(`等待超时：${description}`);
};
const receipt = () => read(eventsPath);
const sent = async () => (await receipt()).events.filter(e => e.kind === 'send');
const prepare = async (draft = '', ctrlEnter = false) => {
  await control('reset', { draft, ctrlEnter });
  await control('copy', { text: clipboardMarker });
  // Windows 的前台切换可能延后处理；只重试激活宿主，绝不重试已经提交的内容。
  let foreground;
  for (let i = 0; i < 3; i++) {
    await native(['-Action', 'focus', '-Handle', String(hostInfo.hwnd)]);
    await control('focus'); // 原生点击取得前台权限后，将光标放回草稿末尾。
    foreground = await native(['-Handle', String(hostInfo.hwnd)]);
    if (foreground.foreground === hostInfo.hwnd) break;
  }
  assert.equal(foreground.foreground, hostInfo.hwnd, `插入前必须激活独立输入宿主（当前进程 ${foreground.pid}）`);
  await invoke('window_action', { action: 'show' });
  // 页面重载后用非拖动区域的真实点击返回客户端，避免标题栏拖动占用系统鼠标捕获。
  const tab = await page.locator('.content-tabs button.active').boundingBox();
  for (let i = 0; i < 3; i++) {
    foreground = await native(['-Action', 'focus', '-Handle', mainHandle, '-X', String(Math.round(tab.x + tab.width / 2)), '-Y', String(Math.round(tab.y + tab.height / 2))]);
    if (foreground.foreground === Number(mainHandle)) break;
  }
  assert.equal(foreground.foreground, Number(mainHandle), '投递前必须实际激活隔离客户端，不重试投递');
  await delay(140);
  const target = await invoke('get_target');
  assert.equal(target.hwnd, hostInfo.hwnd);
  assert.equal(target.focus, hostInfo.focus);
  return target;
};
const test = async (name, fn) => {
  if (dockOnly && !name.startsWith('吸附代表宿主') && !name.startsWith('底部短语')) return;
  try { await fn(); results.push({ name, passed: true }); console.log(`通过：${name}`); }
  catch (error) {
    const messages = page && !page.isClosed() ? await page.locator('[role="status"]').allTextContents().catch(() => []) : [];
    if (page && !page.isClosed()) await page.screenshot({ path: path.join(root, 'failure.png') }).catch(() => {});
    results.push({ name, passed: false, error: String(error), messages }); throw error;
  }
};
const deliver = (id, mode, target, kind = 'phrase') => invoke('deliver_content', { id, kind, mode, target });
const row = id => page.locator(`[data-row="${id}"]`).first();
const common = (id, window = page) => window.locator(`[data-common="${id}"]`);
const commonIds = window => window.locator('[data-common]').evaluateAll(rows => rows.map(row => row.dataset.common));
const routeWindow = async route => {
  await poll(async () => context.pages().some(p => p.url().includes(`#${route}`)), `${route} 窗口`);
  const window = context.pages().find(p => p.url().includes(`#${route}`));
  await window.locator(`.${route}`).waitFor(); return window;
};
// 短语菜单是 Windows 原生菜单，按系统提供的菜单项矩形点击，不能用 DOM 菜单替代验收。
const menuSelect = async (index, screenshot) => {
  await poll(async () => (await native(['-ProcessId', String(app.pid), '-WindowClass', '#32768']).catch(() => null))?.visible, '原生短语菜单');
  const menu = await native(['-ProcessId', String(app.pid), '-WindowClass', '#32768', ...(screenshot ? ['-Screenshot', path.join(root, screenshot)] : [])]);
  assert.equal(menu.pid, app.pid, '原生菜单必须属于隔离验收客户端');
  await native(['-Action', 'menu', '-Handle', String(menu.handle), '-MenuIndex', String(index)]);
};
const dragSort = async (window, ids) => {
  const rows = window.locator('[data-sort-id]');
  await rows.nth(0).click(); await rows.nth(2).click({ modifiers: ['Control'] });
  assert.equal(await window.locator('[data-sort-id][aria-selected="true"]').count(), 2);
  const from = await rows.nth(2).boundingBox(); const to = await rows.nth(3).boundingBox();
  await window.mouse.move(from.x + 30, from.y + from.height / 2); await window.mouse.down();
  await window.mouse.move(to.x + 30, to.y + to.height - 2, { steps: 8 }); await window.mouse.up();
  const expected = [ids[1], ids[3], ids[0], ids[2]];
  assert.deepEqual(await rows.evaluateAll(rows => rows.map(row => row.dataset.sortId)), expected);
  return expected;
};

await fs.mkdir(root, { recursive: true });
try {
  if (process.platform !== 'win32') throw new Error('原生验收只能在 Windows 上运行。');
  await fs.access(executable);
  // Debug 的隔离数据模式有独立单实例标识，可以保留用户正在使用的正式客户端。
  const existing = (await powershell(['-Command', `$qaExecutable = '${executable.replace(/'/g, "''")}'; Get-CimInstance Win32_Process -Filter \"Name='QuickPhrase.exe'\" | Where-Object { $_.ExecutablePath -eq $qaExecutable } | Select-Object -ExpandProperty ProcessId`])).stdout.trim();
  if (existing) throw new Error('已有 Debug 验收实例正在运行，请先结束该实例。');
  host = spawn('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests', 'native-host.ps1'), '-EventsPath', eventsPath, '-ControlPath', controlPath], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  host.stderr.on('data', bytes => console.error(bytes.toString()));
  hostInfo = await read(eventsPath);
  app = spawn(executable, ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(root, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9332' } });
  for (let i = 0; i < 60; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9332'); break; }
    catch { await delay(250); }
  }
  if (!browser) throw new Error('无法连接闪语 WebView2 调试端口。');
  context = browser.contexts()[0]; context.setDefaultTimeout(5000); page = context.pages().find(p => !new URL(p.url()).hash);
  await page.locator('.main-window').waitFor();
  const library = await invoke('get_library');
  assert.equal(library.dataDir, path.join(root, 'data'));
  const settings = { ...library.settings, dock: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' };
  // 无可吸附目标时主窗口应隐藏，先等待后台服务并关闭吸附，再进入独立窗口验收。
  await poll(async () => {
    try { await invoke('save_settings', { settings }); return true; }
    catch (error) { if (String(error).includes('服务尚未启动')) return false; throw error; }
  }, 'Windows服务启动完成');
  await invoke('window_action', { action: 'show' });
  await poll(async () => {
    const window = await native(['-ProcessId', String(app.pid), '-Title', '闪语']).catch(() => null);
    if (window?.visible) mainHandle = String(window.handle);
    return window?.visible;
  }, '主窗口启动完成');
  // 隔离验收显式准备分类，不依赖产品自动初始化。
  const phraseRoot = await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  const fileRoot = await invoke('save_category', { category: { id: '', name: '默认', kind: 'file', parentId: null, position: 0, background: '' } });
  const phrase = { id: '', rootId: phraseRoot, groupId: null, title: '', color: '#ff5100', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '文字' }] }] };
  const savePhrase = changes => invoke('save_phrase', { phrase: { ...phrase, ...changes }, afterId: null });
  const a = await savePhrase({ title: '选中项 A', segments: [{ blocks: [{ kind: 'text', text: '这是 A' }] }] });
  const b = await savePhrase({ title: '发送项 B', segments: [{ blocks: [{ kind: 'text', text: '这是 B' }] }] });

  await test('吸附置顶开关、搜索显示持久化和空白右键菜单', async () => {
    // 回归主线程与跟随线程的循环等待：设置写入期间查询库，连续开关也不能超时。
    for (let i = 0; i < 12; i++) {
      await Promise.all([invoke('save_settings', { settings: { ...settings, pinned: i % 2 === 0 } }), invoke('get_library')]);
    }
    assert.equal(await page.getByRole('button', { name: '默认', exact: true }).count(), 0);
    await page.getByRole('button', { name: '吸附', exact: true }).click();
    await poll(async () => (await invoke('get_library')).settings.dock, '打开吸附');
    assert.equal(await page.getByRole('button', { name: '吸附', exact: true }).getAttribute('aria-pressed'), 'true');
    await page.getByRole('button', { name: '吸附', exact: true }).click(); await poll(async () => !(await invoke('get_library')).settings.dock, '关闭吸附');
    await page.getByRole('button', { name: '置顶', exact: true }).click(); await poll(async () => (await native(['-Handle', mainHandle])).topmost, '实际窗口置顶');
    await page.getByRole('button', { name: '置顶', exact: true }).click(); await poll(async () => !(await native(['-Handle', mainHandle])).topmost, '取消置顶');
    await page.getByRole('button', { name: '隐藏搜索框', exact: true }).click(); await poll(async () => !(await invoke('get_library')).settings.showSearch, '保存搜索隐藏');
    await page.reload(); await page.locator('.main-window').waitFor(); assert.equal(await page.locator('.search-bar').count(), 0);
    await page.getByRole('button', { name: '显示搜索框', exact: true }).click(); await page.locator('.search-bar input').waitFor();
    await page.evaluate(() => document.addEventListener('contextmenu', e => { window.__qaMenuPrevented = e.defaultPrevented; }));
    const list = page.locator('.content-list'); const rect = await list.boundingBox();
    await list.click({ button: 'right', position: { x: 15, y: rect.height - 20 } });
    await page.getByRole('menuitem', { name: '新增话术', exact: true }).waitFor(); assert.equal(await page.evaluate(() => window.__qaMenuPrevented), true);
    assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['新增话术', '新增二级分类']);
    await native(['-Handle', mainHandle, '-Screenshot', path.join(root, 'blank-menu.png')]);
    await page.getByRole('menuitem', { name: '新增话术', exact: true }).click({ button: 'right' });
    assert.equal(await page.evaluate(() => window.__qaMenuPrevented), true); await page.keyboard.press('Escape');
  });

  await test('悬停不跳动；选中 A、双击发送 B 只提交一次', async () => {
    await prepare('已有草稿：'); await row(a).click();
    const before = await row(b).locator('.row-title').boundingBox(); await row(b).hover();
    assert.equal((await row(b).locator('.row-title').boundingBox()).x, before.x);
    await page.screenshot({ path: path.join(root, 'main-hover.png') });
    await row(b).locator('.row-send').dblclick({ delay: 250 }); await poll(async () => (await sent()).length > 0, '发送 B'); await delay(350);
    assert.equal((await sent()).length, 1); assert.equal((await sent())[0].value.text, '已有草稿：这是 B');
    assert.equal(await row(a).getAttribute('aria-selected'), 'true');
    assert.equal((await receipt()).events.filter(e => e.kind === 'paste-text').length, 1);
    assert.equal((await native(['-ClipboardExpected', '这是 B'])).clipboardMatches, true);
  });
  await test('双击正文插入并保留草稿；右键直接发送使用同一流程', async () => {
    await prepare('已有草稿：'); await row(b).locator('.row-title').dblclick(); await poll(async () => (await receipt()).draft === '已有草稿：这是 B', '双击插入');
    assert.equal((await sent()).length, 0);
    await prepare(); await row(b).click({ button: 'right' }); await page.getByRole('menuitem', { name: '直接发送', exact: true }).click();
    await poll(async () => (await sent()).length === 1, '右键发送'); assert.equal((await sent())[0].value.text, '这是 B');
  });
  const commonDock = context.pages().find(p => p.url().includes('#common-dock'));
  await commonDock.locator('.common-dock').waitFor();
  let shortId;
  if (dockOnly) {
    for (const text of ['好的，马上为您处理', '收到，谢谢', '请提供截图', '麻烦输入验证码']) {
      const id = await invoke('save_common_phrase', { phrase: { id: '', text, position: 0 } });
      shortId ||= id;
    }
  }
  await test('独立短语编辑16字限制、单击选中、双击插入和原生菜单发送', async () => {
    await page.getByRole('button', { name: '新增常用短语', exact: true }).click(); const editor = await routeWindow('common-editor');
    const input = editor.getByRole('textbox', { name: '短语内容', exact: true });
    await input.fill('字'.repeat(15) + '😀尾'); assert.equal(Array.from(await input.inputValue()).length, 16); assert.equal(await input.inputValue(), '字'.repeat(15) + '😀');
    await input.fill('好的，稍等一下'); await editor.screenshot({ path: path.join(root, 'common-editor.png') });
    await editor.getByRole('button', { name: '确定', exact: true }).click(); await poll(async () => !context.pages().includes(editor), '保存短语');
    shortId = (await invoke('get_library')).commonPhrases[0].id;
    await assert.rejects(invoke('save_common_phrase', { phrase: { id: '', text: '字'.repeat(17), position: 0 } }), /16/);
    await poll(async () => (await commonIds(commonDock)).includes(shortId), '两处短语栏同步');
    await prepare('已有草稿：'); await common(shortId).click(); await delay(200);
    assert.equal(await common(shortId).getAttribute('aria-selected'), 'true'); assert.equal((await receipt()).draft, '已有草稿：'); assert.equal((await sent()).length, 0);
    await common(shortId).dblclick(); await poll(async () => (await receipt()).draft === '已有草稿：好的，稍等一下', '常用短语双击插入'); assert.equal((await sent()).length, 0);
    await prepare('已有草稿：'); await common(shortId).click({ button: 'right' }); await menuSelect(1, 'common-menu.png');
    await poll(async () => (await sent()).length === 1, '短语原生菜单发送'); assert.equal((await sent())[0].value.text, '已有草稿：好的，稍等一下');
  });
  await test('短语原生菜单修改删除、面板开关和Ctrl多选拖动取消确认', async () => {
    await prepare(); await common(shortId).click({ button: 'right' }); await menuSelect(2); const editor = await routeWindow('common-editor');
    await editor.getByRole('textbox', { name: '短语内容', exact: true }).fill('好的，马上为您处理'); await editor.getByRole('button', { name: '确定', exact: true }).click();
    await poll(async () => !context.pages().includes(editor), '修改短语'); await poll(async () => await common(shortId, commonDock).innerText() === '好的，马上为您处理', '修改同步');
    const ids = [shortId];
    for (const text of ['收到，谢谢', '请提供截图', '麻烦输入验证码']) ids.push(await invoke('save_common_phrase', { phrase: { id: '', text, position: 0 } }));
    const deleted = await invoke('save_common_phrase', { phrase: { id: '', text: '删除验收', position: 0 } });
    await common(deleted).waitFor(); await common(deleted).click({ button: 'right' }); await menuSelect(3);
    await poll(async () => (await native(['-ProcessId', String(app.pid), '-Title', '删除常用短语']).catch(() => null))?.visible, '短语删除确认');
    await native(['-Action', 'key', '-Key', '13']); await poll(async () => !(await invoke('get_library')).commonPhrases.some(p => p.id === deleted), '删除短语');
    await page.getByRole('button', { name: '隐藏常用短语', exact: true }).click(); await poll(async () => await page.locator('.common-phrases').count() === 0, '隐藏主面板');
    assert.deepEqual(await commonIds(commonDock), ids); await native(['-Action', 'key', '-Key', '48', '-Alt']); await page.locator('.common-phrases').waitFor();
    assert.equal(await page.locator('[data-common]').first().evaluate(button => button === document.activeElement), true, '常用短语快捷键聚焦第一个短语');
    await common(shortId).click({ button: 'right' }); await menuSelect(5); let sorting = await routeWindow('common-sort');
    await dragSort(sorting, ids); await sorting.screenshot({ path: path.join(root, 'common-sort.png') });
    await sorting.getByRole('button', { name: '取消', exact: true }).click(); await poll(async () => !context.pages().includes(sorting), '取消排序');
    assert.deepEqual((await invoke('get_library')).commonPhrases.map(p => p.id), ids);
    await common(shortId).click({ button: 'right' }); await menuSelect(5); sorting = await routeWindow('common-sort'); const expected = await dragSort(sorting, ids);
    await sorting.getByRole('button', { name: '确定', exact: true }).click(); await poll(async () => !context.pages().includes(sorting), '确认排序');
    assert.deepEqual((await invoke('get_library')).commonPhrases.map(p => p.id), expected);
    await poll(async () => JSON.stringify(await commonIds(page)) === JSON.stringify(expected) && JSON.stringify(await commonIds(commonDock)) === JSON.stringify(expected), '排序同步');
    await page.reload(); await page.locator('.main-window').waitFor(); assert.deepEqual(await commonIds(page), expected);
    await page.screenshot({ path: path.join(root, 'main-common.png') });
  });
  const filePath = path.join(root, '发送验收.txt'); await fs.writeFile(filePath, '关联源文件应当保留');
  const images = await invoke('import_images', { paths: [path.join(repo, 'src-tauri', 'icons', 'icon.png')] });
  const mixed = await savePhrase({ title: '混合内容', segments: [{ blocks: [{ kind: 'text', text: '开头' }, images[0], { kind: 'text', text: '图片之后' }, { kind: 'file', path: filePath, name: '发送验收.txt' }] }, { blocks: [{ kind: 'text', text: '第二段' }] }] });
  const multi = await savePhrase({ title: '中断验收', segments: [{ blocks: [{ kind: 'text', text: '第一段' }] }, { blocks: [{ kind: 'text', text: '第二段' }] }] });
  await test('原剪贴板文字、图片、文件及无法读取的延迟格式均不阻止插入', async () => {
    for (const source of ['text', 'image', 'files', 'delayed']) {
      const target = await prepare('已有草稿：');
      if (source === 'image') await control('copy-image', { path: path.join(repo, 'src-tauri', 'icons', 'icon.png') });
      if (source === 'files') await control('copy-files', { path: filePath });
      if (source === 'delayed') {
        await control('copy-delayed');
        assert.equal((await receipt()).events.find(e => e.kind === 'test-copy-delayed').value, true);
      }
      const report = await deliver(b, 'insert', target);
      assert.equal(report.status, 'submitted', JSON.stringify(report));
      assert.equal((await receipt()).draft, '已有草稿：这是 B');
      assert.equal((await sent()).length, 0);
      assert.equal((await native(['-ClipboardExpected', '这是 B'])).clipboardMatches, true);
      assert.equal((await receipt()).events.filter(e => e.kind === 'render-request').length, 0, '不得读取原延迟格式');
    }
  });
  await test('单独图片和文件投递后保留最后写入的格式', async () => {
    const imageOnly = await savePhrase({ title: '仅图片', segments: [{ blocks: [images[0]] }] });
    const fileOnly = await savePhrase({ title: '仅文件', segments: [{ blocks: [{ kind: 'file', path: filePath, name: '发送验收.txt' }] }] });
    const imageReport = await deliver(imageOnly, 'insert', await prepare());
    assert.equal(imageReport.status, 'submitted', JSON.stringify(imageReport));
    assert.deepEqual((await receipt()).events.find(e => e.kind === 'paste-image').value, [256, 256]);
    const snapshot = await native(['-ClipboardImageSnapshot', path.join(root, 'last-written-image.png')]);
    assert.equal(snapshot.clipboardImageRecorded, true);
    assert.deepEqual(snapshot.clipboardImageSize, [256, 256]);
    assert.equal((await sent()).length, 0);
    const fileReport = await deliver(fileOnly, 'send', await prepare());
    assert.equal(fileReport.status, 'submitted', JSON.stringify(fileReport));
    assert.deepEqual((await receipt()).events.find(e => e.kind === 'paste-files').value, [filePath]);
    assert.equal((await native(['-ClipboardFileExpected', filePath])).clipboardFileMatches, true);
    assert.equal((await sent()).length, 1);
  });
  await test('混合内容按顺序逐段发送；图片和文件使用标准格式', async () => {
    const report = await deliver(mixed, 'send', await prepare()); assert.equal(report.status, 'submitted', JSON.stringify(report)); assert.equal(report.completedSegments, 2);
    const events = (await receipt()).events.filter(e => e.kind.startsWith('paste-') || e.kind === 'send');
    assert.deepEqual(events.map(e => e.kind), ['paste-text', 'paste-image', 'paste-text', 'paste-files', 'send', 'paste-text', 'send']);
    assert.deepEqual(events[1].value, [256, 256]); assert.deepEqual(events[3].value, [filePath]);
    assert.equal((await native(['-ClipboardExpected', '第二段'])).clipboardMatches, true);
  });
  await test('Ctrl+Enter 发送配置', async () => {
    await invoke('save_settings', { settings: { ...settings, enter: 'ctrl-enter' } });
    assert.equal((await deliver(b, 'send', await prepare('草稿：', true))).status, 'submitted');
    assert.equal((await sent())[0].value.key, 'Ctrl+Enter'); await invoke('save_settings', { settings });
  });
  await test('用户新复制中断，已执行段不重发，新剪贴板保留', async () => {
    const target = await prepare(); const task = deliver(multi, 'send', target);
    await poll(async () => (await sent()).length === 1, '第一段'); await control('copy', { text: '闪语验收新复制内容' });
    const report = await task; assert.equal(report.status, 'interrupted'); assert.equal(report.completedSegments, 1); assert.equal((await sent()).length, 1);
    assert.equal((await native(['-ClipboardExpected', '闪语验收新复制内容'])).clipboardMatches, true);
  });
  await test('输入焦点变化中断，剪贴板占用失败，取消与后端防重入', async () => {
    let target = await prepare(); let task = deliver(multi, 'send', target); await poll(async () => (await sent()).length === 1, '第一段'); await control('blur');
    let report = await task; assert.equal(report.completedSegments, 1); assert.match(report.error, /焦点/);
    target = await prepare(); await control('hold'); report = await deliver(b, 'send', target); assert.equal(report.status, 'failed'); assert.match(report.error, /占用/);
    await poll(async () => (await receipt()).events.some(e => e.kind === 'clipboard-released'), '释放剪贴板');
    target = await prepare(); task = deliver(multi, 'send', target); await assert.rejects(deliver(multi, 'send', target), /正在处理/);
    await poll(async () => (await sent()).length === 1, '第一段'); await invoke('cancel_delivery'); report = await task;
    assert.equal(report.status, 'interrupted'); assert.equal(report.completedSegments, 1);
  });
  await test('文件库原生图标、双击文件、失效保留和重新关联', async () => {
    await invoke('add_files', { categoryId: fileRoot, paths: [filePath] }); const linked = (await invoke('get_library')).files[0];
    await page.getByRole('button', { name: '文件', exact: true }).click(); await row(linked.id).locator('img').waitFor();
    await poll(async () => row(linked.id).locator('img').evaluate(img => img.naturalWidth > 0), '文件系统图标');
    await row(linked.id).click(); await page.screenshot({ path: path.join(root, 'files.png') });
    await prepare(); await row(linked.id).locator('.row-title').dblclick(); await poll(async () => (await receipt()).events.some(e => e.kind === 'paste-files'), '文件粘贴'); assert.equal((await sent()).length, 0);
    await fs.rename(filePath, `${filePath}.moved`); assert.equal((await invoke('get_library')).files[0].missing, true);
    const report = await deliver(linked.id, 'insert', await prepare(), 'file'); assert.equal(report.status, 'failed'); assert.match(report.error, /失效/);
    await invoke('relink_file', { id: linked.id, path: `${filePath}.moved`, categoryId: fileRoot }); assert.equal((await invoke('get_library')).files[0].missing, false);
    await invoke('delete_item', { id: linked.id, kind: 'file' }); await fs.access(`${filePath}.moved`); await fs.rename(`${filePath}.moved`, filePath);
    await invoke('add_files', { categoryId: fileRoot, paths: [filePath] }); await page.getByRole('button', { name: '话术', exact: true }).click();
  });
  await test('搜索附件名高亮、上下选择、Enter 插入和 Esc 关闭', async () => {
    await prepare(); const search = page.getByRole('textbox', { name: '搜索话术、文件' }); await search.fill('发送验收');
    await page.locator('.search-panel mark').first().waitFor(); assert.equal(await page.locator('.search-panel .content-row').count(), 2);
    assert.equal(await page.locator('.search-panel mark').count(), 2); await page.screenshot({ path: path.join(root, 'search.png') });
    await search.press('ArrowDown'); await search.press('Enter'); await poll(async () => (await receipt()).events.some(e => e.kind === 'paste-files'), '搜索 Enter 插入');
    assert.equal((await sent()).length, 0); await search.fill('不存在的关键词'); await page.locator('.no-results').waitFor(); await search.press('Escape');
    assert.equal(await page.locator('.search-panel').count(), 0);
  });
  await test('独立编辑窗口、四项工具栏、图片预览和文字限制', async () => {
    await row(mixed).click({ button: 'right' }); await page.screenshot({ path: path.join(root, 'context-menu.png') }); await page.getByRole('menuitem', { name: '编辑', exact: true }).click();
    await poll(async () => context.pages().some(p => p.url().includes('#editor')), '编辑窗口'); const editor = context.pages().find(p => p.url().includes('#editor'));
    await editor.locator('.editor-form').waitFor(); assert.deepEqual(await editor.locator('.editor-toolbar button').allTextContents(), ['日期', '图片', '文件', '添加分段']);
    await poll(async () => editor.locator('.media-block img').evaluate(img => img.naturalWidth > 0), '素材预览');
    await editor.screenshot({ path: path.join(root, 'editor.png') }); await editor.locator('#phrase-title').fill('字'.repeat(201)); await editor.getByRole('button', { name: '确定', exact: true }).click();
    await editor.getByRole('alert').waitFor(); assert.match(await editor.getByRole('alert').innerText(), /200/);
    await editor.locator('#phrase-title').fill('混合内容'); const text = editor.getByRole('textbox', { name: '第1段文字', exact: true }); await text.focus(); await text.evaluate(input => { const range = document.createRange(); range.setStart(input.firstChild, 2); range.collapse(true); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); input.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true })); });
    await editor.getByRole('button', { name: '日期', exact: true }).click(); assert.match(await text.innerText(), /\d{4}-\d{2}-\d{2}/);
    await editor.getByRole('button', { name: '添加分段', exact: true }).click(); await editor.getByRole('textbox', { name: '第2段文字', exact: true }).fill('新增段');
    await editor.getByRole('button', { name: '确定', exact: true }).click(); await poll(async () => !context.pages().includes(editor), '保存并关闭');
    assert.equal((await invoke('get_library')).phrases.find(p => p.id === mixed).segments.length, 3);
  });
  await test('设置八页、完整备份与 Excel、事务回滚和缓存边界', async () => {
    await page.getByRole('button', { name: '设置', exact: true }).click(); await poll(async () => context.pages().some(p => p.url().includes('#settings')), '设置窗口');
    const settingsPage = context.pages().find(p => p.url().includes('#settings')); await settingsPage.locator('.settings-layout').waitFor();
    for (const tab of ['常用设置', '界面设置', '吸附设置', '导入数据', '导出数据', '快捷键', '缓存管理', '关于']) { await settingsPage.getByRole('button', { name: tab, exact: true }).click(); assert.equal(await settingsPage.locator('.settings-content').isVisible(), true); }
    await settingsPage.screenshot({ path: path.join(root, 'settings.png') }); await settingsPage.getByRole('button', { name: '关闭窗口', exact: true }).click();
    const backupPath = path.join(root, '备份.zip'); const templatePath = path.join(root, '模板.xlsx');
    await transfer('export_data', { path: backupPath, kind: 'backup', categoryId: null }); await invoke('export_template', { path: templatePath });
    assert.equal((await transfer('preview_import', { path: templatePath })).phrases, 1);
    const before = await invoke('get_library'); const preview = await transfer('preview_import', { path: backupPath, merge: true, restoreSettings: true }); assert.equal(preview.images, 1);
    assert.equal(preview.commonPhrases, before.commonPhrases.length);
    await transfer('confirm_import', { previewId: preview.id }); const restored = await invoke('get_library');
    assert.equal(restored.phrases.length, before.phrases.length * 2); assert.equal(restored.categories.length, before.categories.length);
    assert.equal(restored.commonPhrases.length, before.commonPhrases.length * 2);
    const badPath = path.join(root, '无效备份.zip');
    await run('python', ['-c', "import sys,zipfile,json; z=zipfile.ZipFile(sys.argv[1]); data=json.loads(z.read('library.json')); data['library']['phrases'][0]['segments'][0]['blocks']=[{'kind':'text','text':'文'*5001}]; out=zipfile.ZipFile(sys.argv[2],'w'); [out.writestr(n,json.dumps(data,ensure_ascii=False).encode('utf8') if n=='library.json' else z.read(n)) for n in z.namelist()]; out.close()", backupPath, badPath], { windowsHide: true });
    const invalid = await transfer('preview_import', { path: badPath }); const imageCount = (await fs.readdir(path.join(root, 'data', 'images'))).length;
    await assert.rejects(transfer('confirm_import', { previewId: invalid.id }), /5000/);
    assert.equal((await invoke('get_library')).phrases.length, restored.phrases.length); assert.equal((await invoke('get_library')).categories.length, restored.categories.length);
    assert.equal((await fs.readdir(path.join(root, 'data', 'images'))).length, imageCount);
    await invoke('clear_cache', { kind: 'thumbnails' }); await invoke('clear_cache', { kind: 'temp' }); assert.equal(await invoke('cache_info'), 0);
    assert.equal((await fs.readdir(path.join(root, 'data', 'images'))).length, imageCount); await fs.access(filePath);
  });
  await test('全局快捷键显示隐藏与搜索定位', async () => {
    await page.getByRole('button', { name: '隐藏搜索框', exact: true }).click(); await poll(async () => !(await invoke('get_library')).settings.showSearch, '快捷键前隐藏搜索');
    await invoke('window_action', { action: 'hide' }); await native(['-Action', 'key', '-Key', '56', '-Alt']); await delay(200);
    assert.equal((await native(['-Handle', mainHandle])).visible, true); assert.equal(await page.locator('.search-bar input').evaluate(input => input === document.activeElement), true);
    assert.equal((await invoke('get_library')).settings.showSearch, true);
    await native(['-Action', 'key', '-Key', '57', '-Alt']); await delay(150); assert.equal((await native(['-Handle', mainHandle])).visible, false);
    await native(['-Action', 'key', '-Key', '57', '-Alt']); await delay(150); assert.equal((await native(['-Handle', mainHandle])).visible, true);
  });
  await test('吸附代表宿主：左右跟随、移动、最小化、关闭吸附和闲置保持完整宽度', async () => {
    const dockExe = path.join(root, 'ShanyuDockHost.exe'); const dockEvents = path.join(root, 'dock-events.json'); const dockControls = path.join(root, 'dock-control.json');
    await powershell(['-File', path.join(repo, 'tests', 'build-dock-host.ps1'), '-OutputPath', dockExe]);
    dockHost = spawn(dockExe, [dockEvents, dockControls], { windowsHide: true, stdio: 'ignore' }); const info = await read(dockEvents);
    await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: false } }); await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await delay(250);
    let dockRect = (await native(['-Handle', String(info.hwnd)])).frame; let main = await native(['-Handle', mainHandle]); assert.equal(main.frame.left, dockRect.right); assert.equal(main.frame.top, dockRect.top); assert.equal(main.frame.bottom, dockRect.bottom);
    await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: false, dockSide: 'left' } }); await delay(180); main = await native(['-Handle', mainHandle]); assert.equal(main.frame.right, dockRect.left);
    await native(['-Action', 'move', '-Handle', String(info.hwnd), '-X', '700', '-Y', '220', '-Width', '600', '-Height', '500']); await delay(180); main = await native(['-Handle', mainHandle]); dockRect = (await native(['-Handle', String(info.hwnd)])).frame; assert.equal(main.frame.right, dockRect.left); assert.equal(main.frame.top, dockRect.top); assert.equal(main.frame.bottom, dockRect.bottom);
    for (const [y, height] of [[320, 400], [100, 550]]) {
      await native(['-Action', 'move', '-Handle', String(info.hwnd), '-X', '700', '-Y', String(y), '-Width', '600', '-Height', String(height)]); await delay(180);
      main = await native(['-Handle', mainHandle]); dockRect = (await native(['-Handle', String(info.hwnd)])).frame; assert.equal(main.frame.top, dockRect.top); assert.equal(main.frame.bottom, dockRect.bottom);
    }
    await native(['-Action', 'minimize', '-Handle', String(info.hwnd)]); await delay(150); assert.equal((await native(['-Handle', mainHandle])).visible, false);
    await invoke('save_settings', { settings: { ...settings, dock: false } }); await delay(150); assert.equal((await native(['-Handle', mainHandle])).visible, true);
    await native(['-Action', 'restore', '-Handle', String(info.hwnd)]); await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: true } }); await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await delay(850);
    main = await native(['-Handle', mainHandle]); assert.ok(main.frame.right - main.frame.left >= 286);
    await control('close', {}, dockControls); await delay(200); main = await native(['-Handle', mainHandle]); assert.ok(main.rect.right - main.rect.left >= 300); assert.equal(main.visible, false);
    await invoke('save_settings', { settings });
  });
  await test('底部短语独立跟随、保留草稿插入发送、最小化禁用和关闭隐藏', async () => {
    const dockExe = path.join(root, 'ShanyuDockHost.exe'); const dockEvents = path.join(root, 'dock-events.json'); const dockControls = path.join(root, 'dock-control.json');
    await fs.rm(dockControls, { force: true });
    dockHost = spawn(dockExe, [dockEvents, dockControls], { windowsHide: true, stdio: 'ignore' });
    await poll(async () => (await read(dockEvents)).pid === dockHost.pid, '底部短语验收宿主'); const info = await read(dockEvents);
    const commonHandle = String((await native(['-ProcessId', String(app.pid), '-Title', '常用短语 · 闪语'])).handle);
    await invoke('save_settings', { settings: { ...settings, dock: false, showCommon: false, dockCommon: true } });
    await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus', {}, dockControls);
    await poll(async () => (await native(['-Handle', commonHandle])).visible, '底部横条显示');
    let state = await native(['-Handle', commonHandle]); let targetRect = (await native(['-Handle', String(info.hwnd)])).frame;
    assert.equal(state.foreground, info.hwnd, '横条显示不抢焦点'); assert.equal(state.frame.left, targetRect.left); assert.equal(state.frame.right, targetRect.right); assert.equal(state.frame.top, targetRect.bottom);
    assert.equal(await page.locator('.common-phrases').count(), 0, '主面板和底部横条分别控制');
    await native(['-Action', 'move', '-Handle', String(info.hwnd), '-X', '600', '-Y', '200', '-Width', '640', '-Height', '440']);
    targetRect = (await native(['-Handle', String(info.hwnd)])).frame;
    await poll(async () => (await native(['-Handle', commonHandle])).frame.left === targetRect.left, '底部横条跟随移动');
    state = await native(['-Handle', commonHandle]);
    assert.equal(state.frame.top, targetRect.bottom); assert.equal(state.frame.left, targetRect.left); assert.equal(state.frame.right, targetRect.right);
    await native(['-Handle', commonHandle, '-Screenshot', path.join(root, 'common-dock.png')]);
    await commonDock.getByRole('button', { name: '隐藏短语', exact: true }).click();
    await poll(async () => !(await native(['-Handle', commonHandle])).visible, '隐藏短语后整条原生横条隐藏');
    assert.equal((await invoke('get_library')).settings.dockCommon, false);
    assert.equal(await commonDock.getByRole('button', { name: '新增常用短语', exact: true }).count(), 0);
    await invoke('save_settings', { settings: { ...settings, dock: false, showCommon: false, dockCommon: true } });
    await poll(async () => (await native(['-Handle', commonHandle])).visible, '设置重新显示整条底部短语栏');
    const short = (await invoke('get_library')).commonPhrases.find(p => p.id === shortId);
    const prepareDock = async draft => {
      await control('reset', { draft }, dockControls); await control('copy', { text: clipboardMarker }, dockControls);
      await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus', {}, dockControls);
      const box = await common(shortId, commonDock).boundingBox();
      await native(['-Action', 'focus', '-Handle', commonHandle, '-X', String(Math.round(box.x + box.width / 2)), '-Y', String(Math.round(box.y + box.height / 2))]); await delay(150);
      assert.equal((await invoke('get_target', {}, commonDock)).hwnd, info.hwnd);
    };
    await prepareDock('底部草稿：'); await common(shortId, commonDock).dblclick(); await poll(async () => (await read(dockEvents)).draft === `底部草稿：${short.text}`, '底部双击插入');
    assert.equal((await read(dockEvents)).events.filter(e => e.kind === 'send').length, 0);
    await prepareDock('底部草稿：'); await common(shortId, commonDock).click({ button: 'right' }); await menuSelect(1, 'common-dock-menu.png');
    await poll(async () => (await read(dockEvents)).events.some(e => e.kind === 'send'), '底部菜单发送');
    assert.equal((await read(dockEvents)).events.find(e => e.kind === 'send').value.text, `底部草稿：${short.text}`);
    await native(['-Action', 'minimize', '-Handle', String(info.hwnd)]); await poll(async () => !(await native(['-Handle', commonHandle])).visible, '目标最小化隐藏横条');
    await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus', {}, dockControls); await poll(async () => (await native(['-Handle', commonHandle])).visible, '还原目标显示横条');
    await invoke('save_settings', { settings: { ...settings, dockCommon: false } }); await poll(async () => !(await native(['-Handle', commonHandle])).visible, '禁用底部吸附');
    await invoke('save_settings', { settings }); await poll(async () => (await native(['-Handle', commonHandle])).visible, '重新启用底部吸附');
    await native(['-Action', 'focus', '-Handle', String(hostInfo.hwnd)]); await poll(async () => !(await native(['-Handle', commonHandle])).visible, '切换其他程序隐藏横条');
    await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await poll(async () => (await native(['-Handle', commonHandle])).visible, '返回目标显示横条');
    await control('close', {}, dockControls); await poll(async () => !(await native(['-Handle', commonHandle])).visible, '关闭目标隐藏横条');
  });
} catch (error) {
  console.error(error); process.exitCode = 1;
} finally {
  if (dockHost) await control('close', {}, path.join(root, 'dock-control.json')).catch(() => {});
  if (hostInfo) {
    // 只认领隔离客户端当前拥有的序号，不通过正文猜测归属；宿主在锁内再次核对。
    const state = await native([]).catch(() => null);
    if (app && state?.clipboardOwnerPid === app.pid) await control('adopt', { sequence: state.clipboardSequence, ownerPid: app.pid }).catch(() => {});
    await control('close').catch(() => {});
  }
  if (page && !page.isClosed()) { try { await invoke('window_action', { action: 'exit' }); } catch {} }
  for (let i = 0; i < 60 && host?.exitCode === null; i++) await delay(40);
  if (app && app.exitCode === null) app.kill();
  if (host && host.exitCode === null) host.kill();
  if (dockHost && dockHost.exitCode === null) dockHost.kill();
  await fs.writeFile(path.join(root, 'report.json'), JSON.stringify({ date: new Date().toISOString(), scope: dockOnly ? 'dock-only' : 'full', results, limits: ['真实企业微信吸附待实机确认；同名验收进程只验证模块行为', 'Windows 10、多显示器与实际 150%/200% DPI、中文输入法仍需系统验收', '第三方软件粘贴支持与实际网络送达不由本宿主证明'] }, null, 2));
  console.log(`原生验收记录：${path.join(root, 'report.json')}`);
}
