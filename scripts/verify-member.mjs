import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 隔离数据下运行 Windows IPC、凭据、HTTP、SSE 与 WebView2；仅向隔离宿主触发候选，缺失素材必须在操作剪贴板前拒绝。
const repo = process.cwd(), evidence = path.join(repo, '.build', `member-${Date.now()}`);
// 微软拼音需要当前交互桌面的 TSF 服务；此选项仍只输入隔离宿主的固定测试内容。
const verifyIme = process.argv.includes('--ime');
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定 Hub 程序。');
await fs.mkdir(evidence, { recursive: true });
const serverExe = path.join(evidence, 'quickphrase-hub.exe');
await fs.copyFile(path.resolve(process.env.SHANYU_TEAM_SERVER_EXE), serverExe);
const port = async () => { const server = net.createServer(); await new Promise(r => server.listen(0, '127.0.0.1', r)); const value = server.address().port; await new Promise(r => server.close(r)); return value; };
const delay = ms => new Promise(r => setTimeout(r, ms));
const until = async (work, accept = v => !!v, timeout = 18000) => { const start = Date.now(); while (Date.now() - start < timeout) { try { const value = await work(); if (accept(value)) return value; } catch {} await delay(80); } throw new Error('等待成员联调结果超时'); };
const apiPort = await port(), base = `http://127.0.0.1:${apiPort}`;
const initial = randomUUID(), password = randomUUID(), tokens = {}, users = {};
let service, app, browser, context, page;
const checks = [], faults = [];
const check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
const startServer = () => spawn(serverExe, [], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, QUICKPHRASE_DATA_DIR: path.join(evidence, 'server'), QUICKPHRASE_LISTEN: `127.0.0.1:${apiPort}`, QUICKPHRASE_ADMIN_USERNAME: 'qa-admin', QUICKPHRASE_ADMIN_PASSWORD: initial } });
const api = async (who, method, route, body) => { const response = await fetch(`${base}/api/${route}`, { method, headers: { ...(tokens[who] ? { Authorization: 'Bearer ' + tokens[who] } : {}), 'Content-Type': 'application/json' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) }); assert.ok(response.ok, `接口返回 ${response.status}`); return response.json(); };
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
// 重启回归必须直接按系统键；读取偏好字符串不能证明绑定已进入原生监听表。
async function memberCandidate(expected = true, letters = false, invalidate) {
  const run = promisify(execFile), events = path.join(evidence, 'restart-host.json'), controls = path.join(evidence, 'restart-control.json');
  const ps = args => run('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
  const native = async args => JSON.parse((await ps(['-File', path.join(repo, 'tests/native-driver.ps1'), ...args])).stdout);
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  if (!await fs.access(fixture).then(() => true, () => false)) await ps(['-File', path.join(repo, 'tests/build-dock-host.ps1'), '-OutputPath', fixture]);
  await fs.rm(events, { force: true });
  const ime = verifyIme && letters;
  const host = spawn(fixture, [events, controls, 'WeWorkWindow', ...(ime ? ['ime'] : [])], { windowsHide: true, stdio: 'ignore' });
  const control = async command => { await fs.writeFile(controls, JSON.stringify({ command, nonce: Date.now() })); await delay(180); };
  const imeSamples = [];
  const imeState = async label => {
    await control('ime-status');
    const value = JSON.parse(await fs.readFile(events, 'utf8')); imeSamples.push({ label, ...value });
    await fs.writeFile(path.join(evidence, 'member-ime.json'), JSON.stringify(imeSamples, null, 2));
    return value.events.filter(e => e.kind === 'ime-status').at(-1).value;
  };
  try {
    const info = await until(async () => JSON.parse(await fs.readFile(events, 'utf8')));
    for (let attempt = 0; attempt < 3; attempt++) {
      await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus'); await delay(350);
      if ((await native(['-Handle', String(info.hwnd)])).foreground === info.hwnd) break;
    }
    assert.equal((await native(['-Handle', String(info.hwnd)])).foreground, info.hwnd);
    if (ime) { await control('focus'); await control('ime-on'); }
    await native(letters ? ['-Action', 'key', '-Keys', '89,67'] : ['-Action', 'key', '-Key', '122', '-Ctrl', '-Shift']);
    if (!expected) { await delay(250); assert.equal(await invoke('get_shortcut_candidates'), null); return; }
    const payload = await until(() => invoke('get_shortcut_candidates'), Boolean, 6000);
    const members = await invoke('get_member_view');
    assert.deepEqual(payload.phrases.map(p => p.id), [members.phrases[0].id]);
    assert.equal(payload.memberEpoch, members.sessionEpoch);
    assert.equal(payload.target.hwnd, info.hwnd);
    const window = await native(['-ProcessId', String(app.pid), '-Title', '快捷键候选 · 闪语']);
    assert.equal(window.visible, true); assert.equal(window.foreground, info.hwnd);
    if (ime) {
      const state = await imeState('重启后未修改设置直接Y+C');
      assert.ok(state.open && (state.conversion & 1) && state.compositionBytes >= 4 && state.focused);
      await native(['-Handle', String(info.hwnd), '-Screenshot', path.join(evidence, 'member-ime-candidates.png')]);
    }
    if (invalidate) {
      await invalidate();
      await until(() => invoke('get_shortcut_candidates'), value => value === null);
    } else await native(['-Action', 'key', '-Key', '27']);
    if (ime) {
      for (const enabled of [false, true]) {
        await native(['-Action', 'key', '-Key', '27']);
        const settings = (await invoke('get_library')).settings;
        await invoke('save_settings', { settings: { ...settings, customHotkeys: enabled } });
        await native(['-Action', 'key', '-Key', '89']);
        const state = await imeState(`自定义快捷键${enabled ? '开启' : '关闭'}单按Y`);
        assert.ok(state.compositionBytes > 0 && state.focused);
        assert.equal(await invoke('get_shortcut_candidates'), null);
      }
      const log = await fs.readFile(path.join(evidence, 'recipient-a/shanyu.log'), 'utf8');
      const triggers = log.split('\n').filter(line => /隔离按键：code=(89|67)，/.test(line));
      assert.ok(triggers.length >= 8 && triggers.every(line => line.includes('吞键=false')));
      assert.ok(imeSamples.every(sample => !sample.events.some(e => e.kind === 'send' || e.kind.startsWith('paste-'))));
    } else assert.equal((await until(async () => JSON.parse(await fs.readFile(events, 'utf8')))).events.length, 0);
  } finally { host.kill(); }
}
async function hook(view) {
  view.on('pageerror', error => faults.push(String(error)));
  await view.evaluate(() => { const original = window.fetch.bind(window); window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const command = decodeURIComponent(url.pathname.slice(1));
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(command)) return new Response('null', { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    if (command === 'plugin:dialog|message') { const args = JSON.parse(init.body); return new Response(JSON.stringify(args.buttons?.OkCancelCustom?.[0] || 'Ok'), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } }); }
    if (command === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: true, x: -20000, y: -20000, center: false, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
    return original(input, init);
  }; });
}
async function stopClient() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { app.kill(); await until(() => app.exitCode !== null || app.signalCode !== null); }
  page = undefined; browser = undefined;
}
async function startClient(device) {
  await stopClient(); const debugPort = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, device), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`)); context = browser.contexts()[0];
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor(); await hook(page);
  await page.getByRole('status').evaluateAll(nodes => nodes.forEach(node => node.click()));
}
async function settings(alias = '账号与共享') {
  await invoke('plugin:webview|create_webview_window', { options: { label: 'settings', title: '设置 · 闪语', url: 'index.html#settings?' + new URLSearchParams({ tab: alias }), width: 594, height: 620, decorations: false } });
  const view = await until(() => context.pages().find(p => !p.isClosed() && p.url().includes('#settings')));
  await hook(view); await view.getByRole('heading', { name: '账号与共享', exact: true }).waitFor(); return view;
}
const click = locator => locator.evaluate(el => el.click());
// 关闭原生窗口而非仅销毁调试目标，否则管理窗口仍存在，会按设计暂停业务快捷键。
const closeView = async view => { await invoke('plugin:window|close', {}, view).catch(error => { if (!view.isClosed()) throw error; }); await until(() => view.isClosed()); };
const memberView = accept => until(() => invoke('get_member_view'), accept);
const synced = () => until(() => invoke('get_private_status'), s => s.enabled && s.state === '已同步' && s.pending === 0 && s.lastSuccessfulSyncAt > 0);
const login = username => invoke('team_login', { server: base, username, password });
const category = (name, parentId = null) => invoke('save_category', { category: { id: '', name, kind: 'phrase', parentId, position: 0, background: '' } });
let sourceRoot, secretRoot, group, sourcePhrase, grant, ownId, oldKey, savedPath;
async function editSource(change) {
  const snapshot = await api('source', 'GET', 'me/private/snapshot'); const record = snapshot.records.find(r => r.kind === 'phrase' && r.id === sourcePhrase);
  const value = change(structuredClone(record.value));
  await api('source', 'POST', 'me/private/mutations', { generation: snapshot.generation, requestId: randomUUID(), changes: [{ kind: 'phrase', id: sourcePhrase, expectedVersion: record.version, value }], newAssets: [] });
}
try {
  service = startServer(); await until(async () => (await fetch(base + '/health')).ok);
  const admin = await api('admin', 'POST', 'login', { username: 'qa-admin', password: initial }); tokens.admin = admin.token;
  await api('admin', 'PUT', 'password', { currentPassword: initial, password });
  for (const [key, username] of [['source', '001'], ['recipient', '002']]) {
    users[key] = await api('admin', 'POST', 'users', { username, password });
    const session = await api(key, 'POST', 'login', { username, password }); tokens[key] = session.token;
    await api(key, 'PUT', 'password', { currentPassword: password, password });
  }
  await startClient('source'); await login('001');
  await check('来源本人同步图文附件，开放共享先选择范围再生效', async () => {
    sourceRoot = await category('常用'); group = await category('二级分类', sourceRoot); secretRoot = await category('不分享');
    const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    const external = path.join(evidence, '原附件.txt'); await fs.writeFile(external, 'member-attachment');
    sourcePhrase = await invoke('save_phrase', { phrase: { id: '', rootId: sourceRoot, groupId: group, title: '成员验收话术', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '成员联调内容' }, image, { kind: 'file', path: external, name: '原附件.txt' }] }] } });
    await invoke('save_common_phrase', { phrase: { id: '', text: '私人短语', position: 0 } });
    const preview = await invoke('preview_private_sync'); await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } }); await synced();
    const account = await invoke('get_team_status'); assert.equal(account.memberPhraseSync, true);
    const view = await settings('账号与同步');
    await until(() => view.getByRole('button', { name: '开启共享', exact: true }).isEnabled()); await click(view.getByRole('button', { name: '开启共享', exact: true }));
    assert.equal(await view.getByRole('radio', { name: '全部分类（包含今后新增分类）' }).isChecked(), true);
    const radios = view.getByRole('radio');
    assert.ok(await radios.evaluateAll(nodes => nodes.every(el => el.getBoundingClientRect().width === 15 && el.getBoundingClientRect().height === 15)));
    assert.ok(await view.locator('.member-root-choices').evaluate(el => el.scrollWidth <= el.clientWidth));
    await view.getByRole('radio', { name: '指定分类', exact: true }).check();
    const root = view.getByRole('checkbox', { name: '常用', exact: true }); await root.waitFor(); assert.equal(await root.isChecked(), false);
    assert.equal(await view.getByRole('checkbox').count(), 2);
    const choice = await root.evaluate(el => { const label = el.closest('label').getBoundingClientRect(), box = el.getBoundingClientRect(); return { direction: getComputedStyle(el.closest('label')).flexDirection, labelLeft: label.left, boxLeft: box.left, labelHeight: label.height }; });
    assert.equal(choice.direction, 'row'); assert.ok(choice.boxLeft - choice.labelLeft < 2); assert.ok(choice.labelHeight < 36);
    await root.check(); assert.equal(await view.getByRole('button', { name: '确认共享', exact: true }).isEnabled(), true);
    await view.screenshot({ path: path.join(evidence, 'share-scope.png') }); await click(view.getByRole('button', { name: '确认共享', exact: true })); await view.locator('dialog').waitFor({ state: 'hidden' });
    const overview = await invoke('get_member_overview', { sessionEpoch: account.sessionEpoch }); grant = overview.sharing; assert.deepEqual(grant.rootIds, [sourceRoot]);
    await closeView(view);
  });
  await startClient('recipient-a'); await login('002');
  await check('接收方不启用本人同步即可使用，分类同名隔离、两级只读且个人库不混入缓存', async () => {
    const root = await category('常用'); ownId = await invoke('save_phrase', { phrase: { id: '', rootId: root, groupId: null, title: '本人保留', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '本人内容' }] }] } });
    assert.equal((await invoke('get_private_status')).enabled, false);
    const view = await settings('共享话术'); await until(() => view.getByRole('button', { name: '选择成员', exact: true }).isEnabled());
    await click(view.getByRole('button', { name: '选择成员', exact: true })); await view.locator('dialog').getByRole('combobox').selectOption(grant.id); await click(view.getByRole('button', { name: '使用话术', exact: true })); await view.locator('dialog').waitFor({ state: 'hidden' });
    const member = await memberView(v => v.phrases.length === 1); oldKey = member.phrases[0].id;
    assert.equal(member.categories.length, 2); assert.ok(member.categories.every(c => c.readOnly)); assert.ok(!member.phrases[0].shortcut);
    const categories = page.locator('.category-tabs button:not(.add-category)'); await until(() => categories.count(), n => n === 2); assert.equal(await categories.nth(0).innerText(), '常用'); assert.match(await categories.nth(1).innerText(), /001.*只读/);
    await click(categories.nth(1)); await page.getByRole('button', { name: '二级分类', exact: true }).waitFor(); assert.equal(await page.locator('.group-edit').count(), 0);
    await page.screenshot({ path: path.join(evidence, 'mixed-readonly.png') });
    await page.locator(`[data-row="${oldKey}"]`).dispatchEvent('contextmenu', { clientX: 40, clientY: 160 });
    assert.equal(await page.getByRole('menuitem', { name: '编辑', exact: true }).count(), 0); assert.equal(await page.getByRole('menuitem', { name: '复制到共享话术', exact: true }).count(), 0);
    await page.screenshot({ path: path.join(evidence, 'readonly-actions.png') }); await page.keyboard.press('Escape');
    const own = await invoke('get_library'); assert.equal(own.phrases.length, 1); assert.equal(own.phrases[0].id, ownId); assert.equal(own.categories.length, 1);
    await assert.rejects(invoke('save_category', { category: { ...member.categories[0], name: '禁止修改' } }));
    await assert.rejects(invoke('save_phrase', { phrase: { ...member.phrases[0], title: '禁止修改' } }));
    const syncLabel = await view.locator('.private-sync-settings .sync-row > strong').evaluate(el => { const range = document.createRange(); range.selectNodeContents(el); return { wrap: getComputedStyle(el).whiteSpace, lines: range.getClientRects().length }; });
    assert.equal(syncLabel.wrap, 'nowrap'); assert.equal(syncLabel.lines, 1);
    await view.screenshot({ path: path.join(evidence, 'account-sharing.png') }); await closeView(view);
  });
  await check('分享话术个人快捷键独立保存，同键候选可选且缺失素材整条拒绝', async () => {
    const epoch = (await invoke('get_team_status')).sessionEpoch;
    const bind = shortcut => invoke('save_member_shortcut', { id: oldKey, shortcut, sessionEpoch: epoch });
    await assert.rejects(invoke('save_member_shortcut', { id: oldKey, shortcut: 'Ctrl+K', sessionEpoch: epoch - 1 }));
    await bind('Control+K');
    assert.equal((await invoke('get_member_view')).phrases[0].shortcut, 'Ctrl+K');
    await assert.rejects(invoke('validate_phrase_shortcut', { id: ownId, shortcut: 'Ctrl+K+Y' }));
    const own = (await invoke('get_library')).phrases.find(p => p.id === ownId);
    await assert.rejects(invoke('save_phrase', { phrase: { ...own, shortcut: 'Ctrl+K+Y' } }));
    await bind(''); assert.equal((await invoke('get_member_view')).phrases[0].shortcut, '');
    await bind('Ctrl+Shift+F11');
    await invoke('save_phrase', { phrase: { ...own, shortcut: 'Ctrl+Shift+F11' } });
    const settings = (await invoke('get_library')).settings;
    await assert.rejects(invoke('save_settings', { settings: { ...settings, sequenceHotkeys: true, sequenceBindings: ['Ctrl+Shift+F11', ...settings.sequenceBindings.slice(1)] } }));
    await invoke('save_settings', { settings: { ...settings, dock: false, dockCommon: false, edgeAutoHide: false, customHotkeys: true, globalPhraseHotkeys: true, sequenceHotkeys: false } });
    const run = promisify(execFile), events = path.join(evidence, 'shortcut-host.json'), controls = path.join(evidence, 'shortcut-control.json');
    const ps = args => run('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
    const native = async args => JSON.parse((await ps(['-File', path.join(repo, 'tests/native-driver.ps1'), ...args])).stdout);
    const fixture = path.join(evidence, 'ShanyuDockHost.exe');
    await ps(['-File', path.join(repo, 'tests/build-dock-host.ps1'), '-OutputPath', fixture]);
    const host = spawn(fixture, [events, controls], { windowsHide: true, stdio: 'ignore' });
    try {
      const info = await until(async () => JSON.parse(await fs.readFile(events, 'utf8')));
      await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await delay(350);
      await native(['-Action', 'key', '-Key', '122', '-Ctrl', '-Shift']);
      const payload = await until(() => invoke('get_shortcut_candidates'));
      assert.equal(payload.memberEpoch, epoch);
      const ownLibrary = await invoke('get_library');
      const ownKey = `private:${ownLibrary.libraryId}:phrase:${ownId}:${ownLibrary.privateRevisions[`phrase:${ownId}`]}`;
      assert.deepEqual(payload.phrases.map(p => p.id).sort(), [ownKey, oldKey].sort());
      const index = payload.phrases.findIndex(p => p.id === oldKey); assert.ok(index >= 0);
      await assert.rejects(invoke('submit_shortcut_candidate', { index, mode: 'insert' }), /素材未缓存/);
      assert.equal((await until(async () => JSON.parse(await fs.readFile(events, 'utf8')))).events.length, 0, '缺失素材不得部分投递');
      await native(['-Action', 'key', '-Key', '122', '-Ctrl', '-Shift']); await until(() => invoke('get_shortcut_candidates'));
      await bind(''); assert.equal(await invoke('get_shortcut_candidates'), null);
      await bind('Ctrl+Shift+F11');
    } finally { host.kill(); }
    await invoke('save_phrase', { phrase: { ...own, shortcut: '' } });
    const source = await api('source', 'GET', 'me/private/snapshot');
    assert.equal(source.records.find(r => r.kind === 'phrase' && r.id === sourcePhrase).value.shortcut, '');
  });
  await check('本人同步未开启，成员内容未变化，联网重启无需重新设置即可实际唤起候选', async () => {
    await startClient('recipient-a');
    await memberView(v => v.state === '已同步' && v.phrases[0]?.shortcut === 'Ctrl+Shift+F11');
    assert.equal((await invoke('get_private_status')).enabled, false);
    await memberCandidate(true, false, () => invoke('team_logout')); await memberCandidate(false);
    await login('002'); await memberView(v => v.phrases[0]?.shortcut === 'Ctrl+Shift+F11'); await memberCandidate();
  });
  await check(verifyIme ? '成员 Y+C 重启后候选与微软组字共存，开启和关闭快捷键单按 Y 都正常组字' : '成员 Y+C 保存后重启，无需重新设置即可唤起候选且原始字母保留', async () => {
    await invoke('save_member_shortcut', { id: oldKey, shortcut: 'Y+C', sessionEpoch: (await invoke('get_team_status')).sessionEpoch });
    const settings = (await invoke('get_library')).settings;
    await invoke('save_settings', { settings: { ...settings, globalPhraseHotkeys: false } });
    await startClient('recipient-a'); await memberView(v => v.phrases[0]?.shortcut === 'Y+C');
    await memberCandidate(true, true);
    const host = JSON.parse(await fs.readFile(path.join(evidence, 'restart-host.json'), 'utf8'));
    if (!verifyIme) { assert.ok(host.draft.includes('yc')); assert.equal(host.draft.replace('yc', ''), '已有草稿：'); }
    await invoke('save_member_shortcut', { id: oldKey, shortcut: 'Ctrl+Shift+F11', sessionEpoch: (await invoke('get_team_status')).sessionEpoch });
  });
  await check('图片自动缓存、附件按需下载，主搜索与全部搜索都保留成员来源', async () => {
    // 新增重启／注销回归后默认回到本人分类；图片预加载只针对当前可见分类。
    await click(page.locator('.category-tabs button').filter({ hasText: /001.*只读/ }));
    await memberView(v => v.phrases[0]?.segments[0].blocks.find(b => b.kind === 'image')?.path);
    const before = await invoke('get_member_view'); assert.equal(before.phrases[0].ready, false);
    await invoke('download_member_materials', { ids: [oldKey], attachments: true }); const ready = await memberView(v => v.phrases[0]?.ready);
    savedPath = ready.phrases[0].segments[0].blocks.find(b => b.kind === 'file').path; assert.equal(await fs.readFile(savedPath, 'utf8'), 'member-attachment');
    for (const scope of ['private', 'all']) { const hits = await invoke('search_scoped_content', { query: '成员验收', scope }); assert.equal(hits.length, 1); assert.equal(hits[0].phrase.contentRef.source, 'member'); assert.match(hits[0].sourceLabel, /001/); }
    await until(() => invoke('get_member_view'), v => v.phrases[0].ready); assert.equal((await invoke('get_library')).phrases.length, 1);
  });
  await startClient('recipient-b'); await login('002');
  await check('同账号第二设备读取使用选择，停止会清除缓存并同步回第一设备', async () => {
    const member = await memberView(v => v.phrases.length === 1); const account = await invoke('get_team_status');
    assert.equal(member.phrases[0].shortcut, '', '个人绑定不应从其他设备同步');
    assert.equal((await api('source', 'GET', 'me/phrase-grants')).users.length, 1);
    await invoke('set_member_subscription', { sessionEpoch: account.sessionEpoch, request: { grantId: null, expectedVersion: member.subscription.version } }); await memberView(v => !v.subscription.grantId && !v.phrases.length);
    assert.equal((await api('source', 'GET', 'me/phrase-grants')).users.length, 0);
    await startClient('recipient-a'); await memberView(v => !v.subscription.grantId && !v.phrases.length);
    const nextAccount = await invoke('get_team_status'), overview = await invoke('get_member_overview', { sessionEpoch: nextAccount.sessionEpoch });
    await invoke('set_member_subscription', { sessionEpoch: nextAccount.sessionEpoch, request: { grantId: grant.id, expectedVersion: overview.subscription.version } });
    const next = await memberView(v => v.phrases.length === 1); oldKey = next.phrases[0].id;
    assert.equal(next.phrases[0].shortcut, 'Ctrl+Shift+F11');
    await invoke('download_member_materials', { ids: [oldKey], attachments: true });
  });
  await check('共享范围和人数同一行，名单排序滚动、Esc关闭及焦点恢复；暂停与离线状态明确', async () => {
    await startClient('source');
    const extra = [];
    for (let n = 3; n <= 14; n++) {
      const username = `m${String(n).padStart(3, '0')}`;
      await api('admin', 'POST', 'users', { username, displayName: n === 3 ? '测试成员' : '', password });
      tokens[username] = (await api(username, 'POST', 'login', { username, password })).token;
      await api(username, 'PUT', 'password', { currentPassword: password, password });
      await api(username, 'PUT', 'me/phrase-subscription', { grantId: grant.id, expectedVersion: 0 }); extra.push(username);
    }
    const view = await settings();
    const count = view.getByRole('button', { name: '13 人使用', exact: true }); await count.waitFor();
    assert.match(await count.locator('..').innerText(), /已选 1 个分类.*13 人使用/s);
    const summaryLayout = () => count.evaluate(el => { const range = el.parentElement.firstElementChild.getBoundingClientRect(); const count = el.getBoundingClientRect(); return { delta: Math.abs(range.top - count.top), left: count.left, right: range.right }; });
    assert.ok((await summaryLayout()).delta < 3);
    assert.ok(await view.locator('.account-user').evaluate(el => { const name = el.querySelector('.account-user-name').getBoundingClientRect(), role = el.querySelector('small').getBoundingClientRect(); return Math.abs(name.top-role.top) < 4 && name.right <= role.left; }));
    const cdp = await context.newCDPSession(view);
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 480, height: 620, deviceScaleFactor: 1, mobile: false });
    assert.ok((await summaryLayout()).delta < 3);
    assert.ok(await view.locator('.account-user').evaluate(el => { const name = el.querySelector('.account-user-name').getBoundingClientRect(), role = el.querySelector('small').getBoundingClientRect(); return Math.abs(name.top-role.top) < 4 && name.right <= role.left; }));
    assert.ok(await view.locator('.sync-sharing-settings').evaluate(el => el.scrollWidth <= el.clientWidth));
    await view.screenshot({ path: path.join(evidence, 'narrow-sharing.png') });
    await cdp.send('Emulation.clearDeviceMetricsOverride'); await cdp.detach();

    assert.equal(await view.getByRole('button', { name: '重新连接', exact: true }).count(), 0);
    assert.equal(await view.locator('.sync-sharing-settings').count(), 1);
    assert.equal(await view.locator('.sync-sharing-settings .sync-row').count(), 3);
    assert.ok(await view.locator('.shared-library-settings .sync-row').evaluate(el => { const label = el.querySelector('strong').getBoundingClientRect(), button = el.querySelector('button').getBoundingClientRect(); return Math.abs(label.top + label.height / 2 - button.top - button.height / 2) < 3 && el.scrollWidth <= el.clientWidth; }));
    assert.ok(await view.locator('.sync-sharing-settings').evaluate(el => el.scrollWidth <= el.clientWidth));
    await count.focus(); await click(count);
    const dialog = view.getByRole('dialog', { name: '使用成员（13）' }); await dialog.waitFor();
    await until(() => dialog.locator('tbody tr').count(), n => n === 13);
    const names = await dialog.locator('tbody td:first-child').allTextContents();
    assert.equal(names[0], '002'); assert.equal(names[1], '测试成员（m003）'); assert.equal(names.at(-1), 'm014');
    assert.ok((await dialog.locator('tbody td:last-child').allTextContents()).every(text => text === '常用'));
    await dialog.getByRole('columnheader', { name: '使用分类', exact: true }).waitFor();
    assert.ok(await dialog.locator('.account-panel-body').evaluate(el => el.scrollHeight > el.clientHeight));
    assert.equal(await dialog.locator('.account-panel-body').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 255)');
    assert.ok(await dialog.evaluate(el => el.querySelector('.account-panel-body').getBoundingClientRect().bottom < el.querySelector('.dialog-actions').getBoundingClientRect().top));
    await view.screenshot({ path: path.join(evidence, 'usage-members.png') });
    await view.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' });
    assert.equal(await count.evaluate(el => document.activeElement === el), true);
    await click(view.getByRole('button', { name: '暂停同步', exact: true }));
    await view.getByText('同步已暂停，共享保留最后同步内容。').waitFor();
    await view.screenshot({ path: path.join(evidence, 'unified-sharing.png') });
    service.kill(); await until(() => service.exitCode !== null || service.signalCode !== null);
    await assert.rejects(invoke('team_refresh')); await click(count);
    await dialog.getByText('离线数据', { exact: true }).waitFor();
    await view.screenshot({ path: path.join(evidence, 'offline-users.png') });
    await click(dialog.getByRole('button', { name: '关闭', exact: true }));
    await view.getByRole('button', { name: '重新连接', exact: true }).waitFor();
    service = startServer(); await until(async () => (await fetch(base + '/health')).ok);
    // 后台可能已自动重连并撤下按钮；在同一浏览器任务内查找和点击，避免等待已消失的入口。
    await view.evaluate(() => Array.from(document.querySelectorAll('button')).find(button => button.textContent === '重新连接')?.click());
    await view.getByRole('button', { name: '重新连接', exact: true }).waitFor({ state: 'hidden' });
    for (const username of extra) await api(username, 'PUT', 'me/phrase-subscription', { grantId: null, expectedVersion: 1 });
    await until(async () => (await api('source', 'GET', 'me/phrase-grants')).users.length, n => n === 1);
    await closeView(view); await startClient('recipient-a'); await memberView(v => v.phrases[0]?.ready);
  });
  await check('断网与离线重启保留完整缓存，联网后恢复授权核验', async () => {
    service.kill(); await until(() => service.exitCode !== null || service.signalCode !== null);
    await assert.rejects(invoke('team_refresh')); assert.equal((await invoke('get_team_status')).offline, true);
    await startClient('recipient-a'); const cached = await memberView(v => v.state === '离线' && v.phrases[0]?.ready); oldKey = cached.phrases[0].id; assert.equal(cached.phrases[0].shortcut, 'Ctrl+Shift+F11');
    await memberCandidate();
    service = startServer(); await until(async () => (await fetch(base + '/health')).ok); await invoke('team_refresh');
    await memberView(v => v.state === '已同步' && v.phrases.length === 1);
    await memberCandidate();
  });
  await check('来源编辑经 SSE 更新，旧引用拒绝；移出范围撤下正文及素材', async () => {
    await editSource(value => ({ ...value, title: '来源已更新' })); const edited = await memberView(v => v.phrases[0]?.title === '来源已更新');
    assert.notEqual(edited.phrases[0].id, oldKey); assert.equal(edited.phrases[0].shortcut, 'Ctrl+Shift+F11');
    await assert.rejects(invoke('save_member_shortcut', { id: oldKey, shortcut: '', sessionEpoch: (await invoke('get_team_status')).sessionEpoch }));
    await assert.rejects(invoke('show_phrase_preview', { id: oldKey, anchor: { left: 0, top: 0, right: 50, bottom: 50 } }));
    await editSource(value => ({ ...value, rootId: secretRoot, groupId: null })); await memberView(v => v.phrases.length === 0);
    await memberCandidate(false);
    const oldPath = edited.phrases[0].segments[0].blocks.find(b => b.kind === 'file').path; await assert.rejects(fs.access(oldPath));
    await editSource(value => ({ ...value, rootId: sourceRoot, groupId: group })); const restored = await memberView(v => v.phrases.length === 1); oldKey = restored.phrases[0].id;
  });
  await check('来源停止分享立即撤下缓存，重新分享不自动恢复使用，退出清理成员缓存', async () => {
    await memberCandidate(true, false, async () => {
      await api('source', 'PUT', `me/phrase-sharing`, { enabled: false, mode: "selected", rootIds: [], expectedVersion: grant.version });
      await memberView(v => !v.phrases.length && !v.subscription.grantId);
    });
    await memberCandidate(false);
    await assert.rejects(invoke('download_member_materials', { ids: [oldKey], attachments: true }));
    const overview = await api('source', 'GET', 'me/phrase-grants'); grant = overview.sharing;
    await api('source', 'PUT', `me/phrase-sharing`, { enabled: true, mode: "selected", rootIds: [sourceRoot], expectedVersion: grant.version });
    const after = await invoke('get_member_view'); assert.equal(after.subscription.grantId, null); assert.equal(after.phrases.length, 0);
    assert.equal((await invoke('get_library')).phrases[0].id, ownId);
  });
  await check('修改连接预填当前值，认证失败保留原连接；切换账号隔离本人内容', async () => {
    const view = await settings();
    const before = await invoke('get_team_status');
    const trigger = view.getByRole('button', { name: '修改连接', exact: true }); await click(trigger);
    const dialog = view.getByRole('dialog', { name: '修改连接', exact: true }); await dialog.waitFor();
    assert.equal(await dialog.getByLabel('服务器地址', { exact: true }).inputValue(), base);
    assert.equal(await dialog.getByLabel('用户名', { exact: true }).inputValue(), '002');
    assert.equal(await dialog.getByLabel('密码', { exact: true }).inputValue(), '');
    await view.screenshot({ path: path.join(evidence, 'modify-connection.png') });
    await dialog.getByLabel('服务器地址', { exact: true }).fill(base + '/invalid');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true })); await dialog.getByRole('alert').waitFor();
    const failed = await invoke('get_team_status'); assert.equal(failed.sessionEpoch, before.sessionEpoch); assert.equal(failed.user.id, before.user.id);
    await dialog.getByLabel('服务器地址', { exact: true }).fill(base);
    await dialog.getByLabel('用户名', { exact: true }).fill('001');
    await dialog.getByLabel('密码', { exact: true }).fill('incorrect-password');
    await click(dialog.getByRole('button', { name: '连接', exact: true }));
    await until(() => dialog.getByRole('alert').innerText(), text => text.includes('用户名') && text.includes('密码'));
    assert.equal((await invoke('get_team_status')).sessionEpoch, before.sessionEpoch);
    await dialog.getByLabel('密码', { exact: true }).fill('');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true }));
    await dialog.waitFor({ state: 'hidden' }); await until(() => invoke('get_team_status'), value => value.user?.username === '001');
    assert.ok(!(await invoke('get_library')).phrases.some(p => p.id === ownId));
    await click(trigger); await dialog.waitFor(); await dialog.getByLabel('用户名', { exact: true }).fill('002');
    assert.equal(await dialog.getByLabel('密码', { exact: true }).inputValue(), '');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true })); await dialog.waitFor({ state: 'hidden' });
    await until(() => invoke('get_library'), value => value.phrases.some(p => p.id === ownId));
    await closeView(view); await invoke('team_logout'); assert.equal((await invoke('get_member_view')).phrases.length, 0);
  });
  assert.deepEqual(faults, []);
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, clientVersion: await invoke('plugin:app|version'), limits: ['真实 Windows WebView2、IPC、SQLite、凭据和回环 HTTP/SSE；接收方两个隔离设备轮流运行', '快捷键通过系统按键进入隔离宿主，缺失素材拒绝投递；未操作真实企业微信或剪贴板；局域网、代理、DPI和安装升级未验证'] }, null, 2));
  console.log('证据：' + evidence);
} catch (error) {
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await context?.pages().find(p => !p.isClosed() && p.url().includes('#settings'))?.screenshot({ path: path.join(evidence, 'failure-settings.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('team_logout').catch(() => {});
  await stopClient(); if (service?.exitCode === null) service.kill();
}
