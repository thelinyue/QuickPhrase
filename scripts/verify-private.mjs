import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 两个隔离设备轮流运行真实 Windows 客户端，通过真实 IPC、HTTP、SSE 和凭据管理器联调。
// 不触发聊天发送、系统剪贴板或实体键鼠，不在报告中保存密码、会话和话术正文。
const repo = process.cwd(), evidence = path.join(repo, '.build', `private-${Date.now()}`);
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定服务端程序。');
await fs.mkdir(evidence, { recursive: true });
const serverExe = path.join(evidence, 'shanyu-team-server.exe');
await fs.copyFile(path.resolve(process.env.SHANYU_TEAM_SERVER_EXE), serverExe);
const serverData = path.join(evidence, 'server'), checks = [];
const initial = randomUUID(), password = randomUUID(), otherPassword = randomUUID(), nextOtherPassword = randomUUID();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const port = async () => { const s = net.createServer(); await new Promise(r => s.listen(0, '127.0.0.1', r)); const p = s.address().port; await new Promise(r => s.close(r)); return p; };
const apiPort = await port(), base = `http://127.0.0.1:${apiPort}`;
const until = async (work, accept = v => !!v, timeout = 15000) => { const start = Date.now(); while (Date.now() - start < timeout) { try { const v = await work(); if (accept(v)) return v; } catch {} await delay(60); } throw new Error('等待联调结果超时'); };
const check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
const startServer = () => spawn(serverExe, [], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: serverData, SHANYU_LISTEN: `127.0.0.1:${apiPort}`, SHANYU_ADMIN_USERNAME: 'qa-admin', SHANYU_ADMIN_PASSWORD: initial } });
let service = startServer(), app, browser, page, context, token;
const api = async (method, route, data) => { const r = await fetch(`${base}/api/${route}`, { method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) }); assert.ok(r.ok, `接口返回 ${r.status}`); return r.json(); };
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
async function hideWindows(view) {
  await view.evaluate(() => { const original = window.fetch.bind(window); window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const cmd = decodeURIComponent(url.pathname.slice(1));
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(cmd)) return new Response('null', { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    if (cmd === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: true, center: false, x: -20000, y: -20000, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
    return original(input, init);
  }; });
}
async function accountSettings() {
  await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
  const view = await until(() => context.pages().find(p => !p.isClosed() && p.url().includes('#settings'))); await view.locator('.settings-sidebar').waitFor(); await hideWindows(view);
  await view.getByRole('button', { name: '账号与同步', exact: true }).evaluate(el => el.click()); return view;
}
async function stopClient() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { app.kill(); await until(() => app.exitCode !== null || app.signalCode !== null); }
  page = undefined; browser = undefined;
}
async function startClient(device) {
  await stopClient(); const debug = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, device), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debug}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debug}`)); context = browser.contexts()[0];
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor(); await hideWindows(page);
  await page.getByRole('status').evaluateAll(nodes => nodes.forEach(node => node.click()));
}
const synced = () => until(() => invoke('get_private_status'), s => s.enabled && s.pending === 0 && s.conflicts === 0 && s.state === '已同步');
const firstEnable = async () => { const preview = await invoke('preview_private_sync'); await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } }); return preview; };
const savePhrase = async (phrase, library) => invoke('save_phrase', { phrase, libraryId: library.libraryId, expectedRevision: library.privateRevisions[`phrase:${phrase.id}`] });
let root, phraseId, commonId, ownerLibrary, oldEdit, external;
try {
  await until(async () => (await fetch(base + '/health')).ok);
  await startClient('device-a');
  await check('默认关闭；首次合并保留主键、原本机库及独立文件库', async () => {
    assert.equal((await invoke('get_private_status')).enabled, false);
    root = await invoke('save_category', { category: { id: '', name: '私人验收', kind: 'phrase', parentId: null, position: 0, background: '' } });
    // 在客户端停止时将隔离库改成早期默认分类编号，覆盖真实旧库首次同步的400回归。
    await stopClient(); const database = path.join(evidence, 'device-a', 'shanyu.sqlite3');
    assert.ok(path.resolve(database).startsWith(path.resolve(evidence) + path.sep));
    const fixture = spawn('python', ['-c', 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); r=c.execute("UPDATE categories SET id=? WHERE id=?", ("common",sys.argv[2])); assert r.rowcount==1; c.commit(); c.close()', database, root], { windowsHide: true, stdio: 'ignore' });
    assert.equal(await new Promise(resolve => fixture.once('exit', resolve)), 0); root = 'common'; await startClient('device-a');
    assert.ok((await invoke('get_library')).categories.some(c => c.id === root));
    const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    external = path.join(evidence, '原附件.txt'); await fs.writeFile(external, 'private-qa-attachment');
    phraseId = await invoke('save_phrase', { phrase: { id: '', rootId: root, groupId: null, title: '同步验收', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '隔离验收内容' }, image, { kind: 'file', path: external, name: '原附件.txt' }] }] } });
    commonId = await invoke('save_common_phrase', { phrase: { id: '', text: '私人短语', position: 0 } });
    const fileCategory = await invoke('save_category', { category: { id: '', name: '本机文件', kind: 'file', parentId: null, position: 0, background: '' } });
    await invoke('add_files', { categoryId: fileCategory, paths: [external] });
    await invoke('team_login', { server: base, username: 'qa-admin', password: initial });
    await invoke('team_change_password', { request: { currentPassword: initial, password } });
    token = (await api('POST', 'login', { username: 'qa-admin', password })).token;
    await api('POST', 'users', { username: '0002', password: otherPassword });
    const settings = await accountSettings(); await settings.getByRole('button', { name: '开启同步', exact: true }).evaluate(el => el.click());
    await settings.getByRole('dialog', { name: '首次合并', exact: true }).waitFor(); await until(() => settings.getByRole('button', { name: '确认同步', exact: true }).isEnabled());
    assert.equal(await settings.locator('dialog').evaluate(el => getComputedStyle(el).outlineStyle), 'none'); await settings.screenshot({ path: path.join(evidence, 'private-first-merge.png') });
    await settings.getByRole('button', { name: '确认同步', exact: true }).evaluate(el => el.click()); await synced();
    const library = await invoke('get_library'); ownerLibrary = library.libraryId;
    assert.equal(library.phrases[0].id, phraseId); assert.ok(library.files.length === 1); assert.equal(library.commonPhrases[0].id, commonId);
    const remote = await api('GET', 'me/private/snapshot'); assert.ok(remote.records.some(r => r.kind === 'phrase' && r.id === phraseId)); assert.ok(remote.records.some(r => r.kind === 'category' && r.id === root)); assert.ok(!remote.records.some(r => r.kind === 'category' && r.id === fileCategory));
    await invoke('use_local_private_library'); assert.equal((await invoke('get_library')).phrases[0].id, phraseId);
    await invoke('team_login', { server: base, username: 'qa-admin', password }); await synced();
    const owned = (await invoke('get_library')).phrases[0].segments[0].blocks[2].path; assert.notEqual(owned, external); assert.equal(path.basename(owned), '原附件.txt');
  });
  await startClient('device-b');
  await check('第二设备接入保留相同 ID；图片后台缓存，附件按需下载', async () => {
    await invoke('team_login', { server: base, username: 'qa-admin', password });
    await firstEnable(); await synced();
    const library = await invoke('get_library'); assert.equal(library.phrases[0].id, phraseId); assert.equal(library.phrases[0].rootId, root); assert.ok(library.categories.some(c => c.id === root)); assert.equal(library.files.length, 0); assert.equal(library.privateReady[phraseId], false);
    await invoke('download_private_materials', { ids: [phraseId], attachments: true });
    const ready = await until(() => invoke('get_library'), l => l.privateReady[phraseId]);
    const file = ready.phrases[0].segments[0].blocks[2].path; assert.equal(await fs.readFile(file, 'utf8'), 'private-qa-attachment'); assert.notEqual(file, external);
    oldEdit = { phrase: ready.phrases[0], libraryId: ready.libraryId, revision: ready.privateRevisions[`phrase:${phraseId}`] };
  });
  await check('退出后个人库可离线增删改；改动不会上传其他账号', async () => {
    await invoke('team_logout'); const library = await invoke('get_library'); assert.equal(library.libraryId, oldEdit.libraryId); assert.equal(library.privateReady[phraseId], true);
    await savePhrase({ ...library.phrases[0], title: '离线草稿' }, library);
    await invoke('save_common_phrase', { phrase: { id: '', text: '离线新增', position: 0 }, libraryId: library.libraryId });
    await invoke('delete_item', { kind: 'common', id: commonId, libraryId: library.libraryId });
    assert.ok((await invoke('get_private_status')).pending > 0);
  });
  await startClient('device-a');
  await check('另一设备在线更新内容和发送偏好', async () => {
    await until(() => invoke('get_team_status'), s => !!s.user && !s.offline); await synced();
    const library = await invoke('get_library'); await savePhrase({ ...library.phrases[0], title: '远端新版' }, library);
    await invoke('save_common_phrase', { phrase: { id: '', text: '无关内容', position: 0 }, libraryId: library.libraryId });
    await invoke('save_settings', { libraryId: library.libraryId, settings: { ...library.settings, enter: 'ctrl-enter', theme: 'blue' } }); await synced();
  });
  await startClient('device-b');
  await check('冲突保留双方；无关内容继续同步；发送偏好等待本机确认', async () => {
    await invoke('team_login', { server: base, username: 'qa-admin', password });
    await until(() => invoke('get_private_status'), s => s.conflicts > 0 && s.settingsPending > 0);
    const library = await invoke('get_library'); assert.equal(library.phrases[0].title, '离线草稿'); assert.equal(library.settings.enter, 'enter'); assert.equal(library.settings.theme, 'blue'); assert.ok(library.commonPhrases.some(c => c.text === '无关内容'));
    await until(() => api('GET', 'me/private/snapshot'), r => r.records.some(r => r.kind === 'common' && r.value?.text === '离线新增'));
    await assert.rejects(invoke('save_phrase', { phrase: { ...oldEdit.phrase, title: '旧编辑窗口' }, libraryId: oldEdit.libraryId, expectedRevision: oldEdit.revision }));
    const settings = await accountSettings(); await settings.getByRole('button', { name: /^处理冲突/ }).evaluate(el => el.click());
    await settings.getByRole('dialog', { name: '处理冲突', exact: true }).waitFor(); await settings.screenshot({ path: path.join(evidence, 'private-conflicts.png') });
    const footer = await settings.locator('dialog .dialog-actions').boundingBox(); assert.ok(footer.y + footer.height <= 620);
    await settings.getByRole('button', { name: '关闭', exact: true }).evaluate(el => el.click());
    const conflict = (await invoke('get_private_conflicts')).find(c => c.id === phraseId); assert.ok(conflict);
    await invoke('resolve_private_conflict', { request: { kind: conflict.kind, id: conflict.id, choice: 'both', serverVersion: conflict.serverVersion, localRevision: conflict.localRevision, libraryId: library.libraryId } }); await synced();
    const merged = await invoke('get_library'); assert.equal(merged.phrases.length, 2); assert.ok(merged.phrases.some(p => p.id === phraseId && p.title === '远端新版')); assert.ok(merged.phrases.some(p => p.id !== phraseId && p.title === '离线草稿'));
    await settings.getByRole('button', { name: /^设置待确认/ }).evaluate(el => el.click()); await settings.getByRole('dialog', { name: '应用设置', exact: true }).waitFor();
    await settings.screenshot({ path: path.join(evidence, 'private-settings-confirm.png') }); await settings.getByRole('button', { name: '稍后', exact: true }).evaluate(el => el.click());
    assert.equal((await invoke('get_library')).settings.enter, 'enter');
    await invoke('apply_private_settings', { libraryId: merged.libraryId }); assert.equal((await invoke('get_library')).settings.enter, 'ctrl-enter');
    await page.screenshot({ path: path.join(evidence, 'private-synced.png') });
  });
  await check('账号切换隔离，旧编辑提交拒绝，原账号重新登录恢复个人库', async () => {
    const before = await invoke('get_library');
    await invoke('team_login', { server: base, username: '0002', password: otherPassword });
    await invoke('team_change_password', { request: { currentPassword: otherPassword, password: nextOtherPassword } });
    assert.equal((await invoke('get_library')).phrases.length, 0); assert.notEqual((await invoke('get_library')).libraryId, before.libraryId);
    await assert.rejects(invoke('save_phrase', { phrase: before.phrases[0], libraryId: before.libraryId }));
    await firstEnable(); await synced(); assert.equal((await invoke('get_library')).phrases.length, 0);
    await invoke('team_login', { server: base, username: 'qa-admin', password }); await synced(); assert.equal((await invoke('get_library')).libraryId, before.libraryId);
  });
  await check('恢复工具拒绝运行中操作；恢复后保留离线改动并重新预览', async () => {
    const runRestore = () => new Promise(resolve => { const child = spawn(serverExe, ['--prepare-restore'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: serverData } }); child.on('exit', code => resolve(code)); });
    assert.notEqual(await runRestore(), 0);
    await invoke('pause_private_sync'); const library = await invoke('get_library');
    await invoke('save_common_phrase', { phrase: { id: '', text: '恢复前草稿', position: 0 }, libraryId: library.libraryId });
    service.kill(); await until(() => service.exitCode !== null || service.signalCode !== null);
    assert.equal(await runRestore(), 0); service = startServer(); await until(async () => (await fetch(base + '/health')).ok);
    await invoke('team_login', { server: base, username: 'qa-admin', password });
    const preview = await invoke('preview_private_sync'); assert.equal(preview.restore, true); assert.ok((await invoke('get_library')).commonPhrases.some(c => c.text === '恢复前草稿'));
    await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } }); await synced();
    token = (await api('POST', 'login', { username: 'qa-admin', password })).token;
    assert.ok((await api('GET', 'me/private/snapshot')).records.some(r => r.value?.text === '恢复前草稿'));
    await invoke('backup_private_server', { path: path.join(evidence, 'personal.zip') });
    const backupLibrary = await invoke('get_library'), backupAccount = await invoke('get_team_status');
    const previewBackup = await invoke('preview_import', { path: path.join(evidence, 'personal.zip'), target: { source: 'private', libraryId: backupLibrary.libraryId, sessionEpoch: backupAccount.sessionEpoch }, merge: false, restoreSettings: false, operationId: randomUUID() }); assert.equal(previewBackup.phrases, 2); assert.ok(previewBackup.images >= 1);
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, clientVersion: '0.2.0', limits: ['真实 WebView2、Windows 凭据、SQLite、HTTP/SSE；两个设备轮流运行', '未触发企业微信发送、系统剪贴板或实体输入', '回环网络不能替代局域网、代理、DPI和安装升级验收'] }, null, 2));
  console.log('证据：' + evidence);
} catch (error) {
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('team_logout').catch(() => {});
  await stopClient(); if (service.exitCode === null) service.kill();
}
