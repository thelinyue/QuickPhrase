import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 使用真实 Windows WebView2、Rust IPC 和独立 Hub；代理仅制造断线、旧响应和回执丢失。
// 所有数据和账号均为临时夹具，不触发聊天投递、系统剪贴板或实体键鼠。
const repo = process.cwd(), evidence = path.join(repo, '.build', `transfer-${Date.now()}`);
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定服务端程序。');
await fs.mkdir(evidence, { recursive: true });
const serverExe = path.join(evidence, 'shanyu-team-server.exe');
await fs.copyFile(path.resolve(process.env.SHANYU_TEAM_SERVER_EXE), serverExe);
const delay = ms => new Promise(r => setTimeout(r, ms));
const port = async () => { const s = net.createServer(); await new Promise(r => s.listen(0, '127.0.0.1', r)); const p = s.address().port; await new Promise(r => s.close(r)); return p; };
const until = async (work, accept = v => !!v, timeout = 15000) => { const start = Date.now(); while (Date.now() - start < timeout) { try { const v = await work(); if (accept(v)) return v; } catch {} await delay(60); } throw new Error('等待导入导出联调结果超时'); };
const checks = [], check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
const apiPort = await port(), base = `http://127.0.0.1:${apiPort}`;
const initial = randomUUID(), password = randomUUID(), memberInitial = randomUUID(), memberPassword = randomUUID();
const service = spawn(serverExe, [], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: path.join(evidence, 'server'), SHANYU_LISTEN: `127.0.0.1:${apiPort}`, SHANYU_ADMIN_USERNAME: 'qa-admin', SHANYU_ADMIN_PASSWORD: initial } });
let token, app, browser, context, page, device, team, member;
const lifecycle = new Map(), fault = { offline: false, blockSnapshots: false, losePublish: false, delayAsset: false }, sockets = new Set();
let publishCount = 0, delayedAsset, releaseAsset;
const proxy = http.createServer((req, res) => {
  if (fault.offline || (fault.blockSnapshots && req.method === 'GET' && /\/teams\/[^/]+\/snapshot$/.test(req.url))) { res.writeHead(503); res.end(); return; }
  const lost = fault.losePublish && req.method === 'PUT' && /\/teams\/[^/]+\/snapshot$/.test(req.url);
  if (req.method === 'PUT' && /\/teams\/[^/]+\/snapshot$/.test(req.url)) publishCount++;
  if (lost) fault.losePublish = false;
  const held = fault.delayAsset && req.method === 'GET' && /\/teams\/[^/]+\/assets\//.test(req.url);
  if (held) fault.delayAsset = false;
  const upstream = http.request(base + req.url, { method: req.method, headers: { ...req.headers, host: `127.0.0.1:${apiPort}` } }, response => {
    if (lost || held) {
      const chunks = []; response.on('data', c => chunks.push(c));
      response.on('end', () => {
        if (lost) { res.destroy(); return; }
        delayedAsset = true;
        releaseAsset = () => { if (!res.destroyed) { res.writeHead(response.statusCode, response.headers); res.end(Buffer.concat(chunks)); } releaseAsset = undefined; };
      });
    } else { res.writeHead(response.statusCode, response.headers); response.pipe(res); }
  });
  upstream.on('error', () => { if (!res.headersSent && !res.destroyed) res.writeHead(503); res.end(); });
  res.on('close', () => { if (!res.writableFinished) upstream.destroy(); }); req.pipe(upstream);
});
proxy.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
await new Promise(r => proxy.listen(0, '127.0.0.1', r));
const clientBase = `http://127.0.0.1:${proxy.address().port}`;
const api = async (method, route, data) => { const group = /^teams\/([^/]+)\//.exec(route)?.[1]; const r = await fetch(`${base}/api/${route}`, { method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...(group ? { 'x-team-lifecycle': String(lifecycle.get(group) || 1) } : {}) }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) }); assert.ok(r.ok, `服务接口返回 ${r.status}`); return r.json(); };
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const target = async (group = '') => { const account = await invoke('get_team_status'); if (!group) return { source: 'private', libraryId: (await invoke('get_library')).libraryId, sessionEpoch: account.sessionEpoch }; const view = await invoke('get_team_view', { teamId: group }); return { source: 'team', teamId: group, version: view.team.version, lifecycleVersion: view.team.lifecycleVersion, sessionEpoch: account.sessionEpoch }; };
const transfer = async (cmd, args = {}, scope) => invoke(cmd, { merge: false, restoreSettings: false, operationId: randomUUID(), ...args, target: scope || await target() });
const category = (name, kind = 'phrase', parentId = null) => invoke('save_category', { category: { id: '', name, kind, parentId, position: 0, background: '' } });
const phrase = (rootId, title, blocks, groupId = null) => invoke('save_phrase', { phrase: { id: '', rootId, groupId, title, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks }] } });
const members = async role => { const current = await api('GET', `teams/${team}/members`); await api('PUT', `teams/${team}/members`, { expectedMembershipVersion: current.membershipVersion, expectedLifecycleVersion: current.lifecycleVersion, members: role ? [{ userId: member, teamId: team, role }] : [] }); };
const runPython = (code, args) => new Promise((resolve, reject) => { const p = spawn('python', ['-c', code, ...args], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, PYTHONIOENCODING: 'utf-8' } }); let output = '', error = ''; p.stdout.on('data', b => output += b); p.stderr.on('data', b => error += b); p.on('error', reject); p.on('exit', c => c === 0 ? resolve(output) : reject(new Error(error || 'ZIP夹具处理失败'))); });
const metadata = async file => JSON.parse(await runPython("import sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); print(z.read('library.json').decode('utf8'))", [file]));
const patchPackage = (source, dest, changes) => runPython("import sys,zipfile,json; z=zipfile.ZipFile(sys.argv[1]); data=json.loads(z.read('library.json')); exec(sys.argv[3]); out=zipfile.ZipFile(sys.argv[2],'w'); [out.writestr(n,json.dumps(data,ensure_ascii=False).encode('utf8') if n=='library.json' else z.read(n)) for n in z.namelist()]; out.close()", [source, dest, changes]);
async function hook(view) {
  await view.evaluate(() => {
    if (window.__transferQA) return;
    window.__transferQA = { openPath: null, savePath: null };
    const original = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
      const cmd = decodeURIComponent(url.pathname.slice(1)), reply = value => new Response(JSON.stringify(value), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
      if (['plugin:window|show', 'plugin:window|set_focus'].includes(cmd)) return reply(null);
      if (cmd === 'plugin:dialog|open') return reply(window.__transferQA.openPath);
      if (cmd === 'plugin:dialog|save') return reply(window.__transferQA.savePath);
      if (cmd === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: false, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
      return original(input, init);
    };
  });
}
async function stopClient() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { app.kill(); await until(() => app.exitCode !== null || app.signalCode !== null); }
  page = undefined; browser = undefined;
}
async function startClient(name) {
  await stopClient(); device = name; const debug = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, name), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debug}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debug}`)); context = browser.contexts()[0]; context.setDefaultTimeout(7000);
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor(); await hook(page);
  await page.getByRole('status').evaluateAll(nodes => nodes.forEach(node => node.click()));
}
async function settings(tab) {
  await invoke('plugin:webview|create_webview_window', { options: { label: 'settings', url: `index.html#settings?tab=${encodeURIComponent(tab)}`, title: '设置', width: 760, height: 650, visible: false, focus: false, skipTaskbar: true } });
  const view = await until(() => context.pages().find(p => p.url().includes('#settings'))); await hook(view); await view.locator('.settings-layout').waitFor(); return view;
}
async function checkFormAlignment(view) {
  const rows = await view.locator('.transfer-row').evaluateAll(labels => labels.map(label => {
    const text = label.querySelector('span').getBoundingClientRect(), field = label.querySelector('select').getBoundingClientRect();
    return { left: field.left, width: field.width, height: field.height, gap: field.left - text.right, centered: Math.abs((text.top + text.bottom - field.top - field.bottom) / 2) };
  }));
  assert.ok(rows.length > 0);
  for (const row of rows) {
    assert.ok(row.width >= 180 && row.width <= 310);
    assert.equal(row.height, 32); assert.ok(row.gap >= 16 && row.centered < 1);
    assert.equal(row.left, rows[0].left); assert.equal(row.width, rows[0].width);
  }
}
const enable = async () => { const preview = await invoke('preview_private_sync'); await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } }); };
const synced = () => until(() => invoke('get_private_status'), s => s.enabled && s.pending === 0 && s.conflicts === 0 && s.state === '已同步');
const full = path.join(evidence, 'local-full.zip'), pack = path.join(evidence, 'phrases.zip'), accountPackage = path.join(evidence, 'account.zip'), groupPackage = path.join(evidence, 'team.zip');
let privateRoot, originalFile, accountLibrary, previewId;
try {
  await until(async () => (await fetch(base + '/health')).ok); await startClient('local');
  await check('本机完整备份包含实际图文附件、回收、短语、设备设置和独立文件关联', async () => {
    privateRoot = await category('导入验收'); const sub = await category('欢迎', 'phrase', privateRoot), other = await category('其他');
    const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    originalFile = path.join(evidence, 'external.txt'); await fs.writeFile(originalFile, 'actual-test-attachment');
    await phrase(privateRoot, '图文附件', [{ kind: 'text', text: '隔离验收文字' }, image, { kind: 'file', path: originalFile, name: 'external.txt' }], sub);
    await phrase(other, '另一分类', [{ kind: 'text', text: '另一条' }]);
    const removed = await phrase(privateRoot, '回收内容', [{ kind: 'text', text: '回收文字' }]); await invoke('delete_item', { kind: 'phrase', id: removed, recycle: true });
    await invoke('save_common_phrase', { phrase: { id: '', text: '常用短语', position: 0 } });
    const fileRoot = await category('文件关联', 'file'); await invoke('add_files', { categoryId: fileRoot, paths: [originalFile] });
    const library = await invoke('get_library');
    // 主WebView可早于Windows后台服务加载，先等待本次隔离设置成功应用。
    await until(async () => { await invoke('save_settings', { settings: { ...library.settings, theme: 'green', dock: false, pinned: false } }); return true; });
    await transfer('export_data', { path: full, kind: 'backup', categoryId: null });
    const data = await metadata(full); assert.equal(data.version, 2); assert.equal(data.settingsScope, 'device'); assert.equal(data.library.phrases.length, 2); assert.equal(data.recycleBin.length, 1);
    assert.equal(data.library.files[0].path, originalFile); assert.equal(data.assets.length, 2); assert.equal(data.library.settings.theme, 'green');
  });
  await check('分类话术包不含回收和设置，Excel只保留文字', async () => {
    await transfer('export_data', { path: pack, kind: 'backup', categoryId: privateRoot }); const data = await metadata(pack);
    assert.equal(data.library.phrases.length, 1); assert.equal(data.library.categories.length, 2); assert.equal(data.recycleBin.length, 0); assert.equal(data.library.commonPhrases.length, 0); assert.deepEqual(data.library.settings, {});
    const excel = path.join(evidence, 'text.xlsx'); await transfer('export_data', { path: excel, kind: 'excel', categoryId: privateRoot });
    const preview = await transfer('preview_import', { path: excel }); assert.equal(preview.phrases, 1); assert.equal(preview.images, 0); assert.equal(preview.attachments, 0); await transfer('discard_import', { previewId: preview.id });
  });
  await check('固定预览跨重启保留，库更新拒绝旧确认，重新核对后只导入一份', async () => {
    const preview = await transfer('preview_import', { path: full }); previewId = preview.id;
    const before = await invoke('get_library'); await startClient('local'); const recovered = await transfer('get_import_pending'); assert.equal(recovered.id, preview.id);
    await invoke('save_common_phrase', { phrase: { id: '', text: '期间新增', position: 0 } }); await assert.rejects(transfer('confirm_import', { previewId }), /已更新/);
    const refreshed = await transfer('refresh_import_preview', { previewId }); assert.equal(refreshed.id, previewId);
    const result = await transfer('confirm_import', { previewId }); assert.equal(result.state, 'completed');
    const after = await invoke('get_library'); assert.equal(after.phrases.length, before.phrases.length * 2); assert.equal(after.files.length, before.files.length);
    const imported = after.phrases.find(p => !before.phrases.some(old => old.id === p.id) && p.title === '图文附件'); const file = imported.segments[0].blocks.find(b => b.kind === 'file');
    assert.notEqual(file.path, originalFile); assert.equal(await fs.readFile(file.path, 'utf8'), 'actual-test-attachment');
    assert.equal((await invoke('get_recycle_bin')).length, 2);
    await assert.rejects(transfer('confirm_import', { previewId })); assert.equal((await invoke('get_library')).phrases.length, after.phrases.length);
  });
  await check('旧ZIP可预览；非法路径、缺失素材及无效正文整批拒绝', async () => {
    const legacy = path.join(evidence, 'legacy.zip');
    await runPython("import sys,zipfile,json; z=zipfile.ZipFile(sys.argv[1]); data=json.loads(z.read('library.json')); out=zipfile.ZipFile(sys.argv[2],'w'); images={}; [(images.setdefault(b['path'],'images/'+b['path'].split('/')[-1]+'.png') if b['kind']=='image' else None) for p in data['library']['phrases'] for s in p['segments'] for b in s['blocks']]; [out.writestr(v,z.read(k)) for k,v in images.items()]; [(b.update(path=images[b['path']]) if b['kind']=='image' else b.update(path=sys.argv[3]) if b['kind']=='file' else None) for p in data['library']['phrases'] for s in p['segments'] for b in s['blocks']]; out.writestr('library.json',json.dumps({'format':'shanyu-backup','version':1,'library':data['library'],'recycleBin':[]},ensure_ascii=False)); out.close()", [pack, legacy, originalFile]);
    const old = await transfer('preview_import', { path: legacy }); assert.equal(old.images, 1); assert.equal(old.attachments, 1); await transfer('discard_import', { previewId: old.id });
    const unsafe = path.join(evidence, 'unsafe.zip'); await fs.copyFile(pack, unsafe); await runPython("import sys,zipfile; z=zipfile.ZipFile(sys.argv[1],'a'); z.writestr('../outside.txt','bad'); z.close()", [unsafe]);
    await assert.rejects(transfer('preview_import', { path: unsafe }), /不安全/);
    const incomplete = path.join(evidence, 'incomplete.zip'); await runPython("import sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); out=zipfile.ZipFile(sys.argv[2],'w'); out.writestr('library.json',z.read('library.json')); out.close()", [pack, incomplete]);
    await assert.rejects(transfer('preview_import', { path: incomplete }), /缺失/);
    const invalid = path.join(evidence, 'invalid.zip'); await patchPackage(pack, invalid, "data['library']['phrases'][0]['segments'][0]['blocks'][0]['text']='文'*5001");
    const library = await invoke('get_library'), bad = await transfer('preview_import', { path: invalid }); await assert.rejects(transfer('confirm_import', { previewId: bad.id }), /5000/);
    assert.equal((await invoke('get_library')).phrases.length, library.phrases.length); await transfer('discard_import', { previewId: bad.id });
  });
  await startClient('account');
  await check('统一设置入口默认私人，导入选项关闭，移除个人备份按钮', async () => {
    await invoke('team_login', { server: clientBase, username: 'qa-admin', password: initial }); await invoke('team_change_password', { request: { currentPassword: initial, password } });
    token = (await api('POST', 'login', { username: 'qa-admin', password })).token;
    const root = await category('账号内容'); const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    await phrase(root, '账号原稿', [{ kind: 'text', text: '账号文字' }, image, { kind: 'file', path: originalFile, name: 'external.txt' }]);
    await enable(); await synced(); accountLibrary = (await invoke('get_library')).libraryId;
    team = await invoke('team_admin', { action: 'create-team', request: { name: '导入导出组' } }); member = await invoke('team_admin', { action: 'create-user', request: { username: '0002', password: memberInitial } }); await members('editor');
    const view = await settings('导入数据'); assert.equal(await view.getByLabel('目标库').inputValue(), 'private'); assert.equal(await view.getByLabel('合并同级唯一同名分类').isChecked(), false); assert.equal(await view.getByLabel('恢复个人设置').isChecked(), false);
    await checkFormAlignment(view); await view.screenshot({ path: path.join(evidence, 'private-import.png') });
    await view.getByRole('button', { name: '账号与共享', exact: true }).click(); assert.equal(await view.getByRole('button', { name: '个人备份', exact: true }).count(), 0);
    await view.getByRole('button', { name: '导出数据', exact: true }).click(); await checkFormAlignment(view); await view.screenshot({ path: path.join(evidence, 'private-export.png') }); await view.getByRole('button', { name: '关闭窗口', exact: true }).click();
  });
  await check('旧完整备份分类导入，个人包恢复设置不阻塞正文', async () => {
    const personalPreview = await transfer('preview_import', { path: full });
    assert.equal(personalPreview.files, 0); assert.ok(personalPreview.notes.some(n => n.includes('分别提交')));
    await transfer('discard_import', { previewId: personalPreview.id });
    const deviceTarget = { ...await target(), devicePart: true }, beforeDevice = await invoke('get_library');
    const devicePreview = await transfer('preview_import', { path: full }, deviceTarget);
    assert.equal(devicePreview.phrases, 0); assert.equal(devicePreview.files, 1); assert.equal(devicePreview.commonPhrases, 0);
    await transfer('confirm_import', { previewId: devicePreview.id }, deviceTarget);
    const afterDevice = await invoke('get_library'); assert.equal(afterDevice.libraryId, beforeDevice.libraryId);
    assert.equal(afterDevice.phrases.length, beforeDevice.phrases.length); assert.equal(afterDevice.files.length, beforeDevice.files.length + 1);
    assert.equal(afterDevice.settings.theme, beforeDevice.settings.theme);
    await assert.rejects(transfer('confirm_import', { previewId: devicePreview.id }, deviceTarget));
    const personal = path.join(evidence, 'personal.zip'), configured = path.join(evidence, 'personal-configured.zip'); await invoke('backup_private_server', { path: personal });
    await runPython("import sys,zipfile,json; z=zipfile.ZipFile(sys.argv[1]); data=json.loads(z.read('private.json')); [(r.update(value='ctrl-enter') if r['kind']=='setting' and r['id']=='enter' else r.update(value='blue') if r['kind']=='setting' and r['id']=='theme' else None) for r in data['records']]; out=zipfile.ZipFile(sys.argv[2],'w'); [out.writestr(n,json.dumps(data,ensure_ascii=False).encode('utf8') if n=='private.json' else z.read(n)) for n in z.namelist()]; out.close()", [personal, configured]);
    await invoke('pause_private_sync');
    const importView = await settings('导入数据'); await importView.evaluate(file => { window.__transferQA.openPath = file; }, configured);
    await importView.getByRole('button', { name: '选择文件', exact: true }).click(); await importView.getByLabel('恢复个人设置').check();
    await importView.getByRole('button', { name: '预览导入', exact: true }).click(); await importView.getByRole('dialog').waitFor();
    await importView.screenshot({ path: path.join(evidence, 'private-import-preview.png') });
    await importView.getByRole('button', { name: '确认导入', exact: true }).click(); await importView.getByText('导入完成', { exact: true }).waitFor(); await importView.getByRole('button', { name: '关闭窗口', exact: true }).click();
    const library = await invoke('get_library'); assert.equal(library.phrases.length, 2); assert.equal(library.settings.enter, 'enter'); assert.equal(library.settings.theme, 'blue'); assert.ok((await invoke('get_private_settings')).some(s => s.key === 'enter' && s.next === 'ctrl-enter'));
  });
  await check('账号导出本机待同步正文与待确认配置，仅含便携设置', async () => {
    let library = await invoke('get_library'); const p = library.phrases[0]; await invoke('save_phrase', { libraryId: library.libraryId, expectedRevision: library.privateRevisions[`phrase:${p.id}`], phrase: { ...p, title: '本机待同步稿' } });
    assert.ok((await invoke('get_private_status')).pending > 0); await transfer('export_data', { path: accountPackage, kind: 'backup', categoryId: null });
    const data = await metadata(accountPackage); assert.equal(data.settingsScope, 'portable'); assert.ok(data.library.phrases.some(p => p.title === '本机待同步稿')); assert.equal(data.library.settings.enter, 'ctrl-enter'); assert.equal(data.library.settings.startup, undefined); assert.equal(data.library.settings.dock, undefined); assert.deepEqual(data.library.files, []);
    assert.ok(!(await api('GET', 'me/private/snapshot')).records.some(r => r.value?.title === '本机待同步稿'));
  });
  await check('共享话术导入忽略私人项目，复用事务追加且实际附件在线补齐导出', async () => {
    await invoke('team_refresh'); await until(() => invoke('get_team_view', { teamId: team }));
    const scope = await target(team), preview = await transfer('preview_import', { path: full }, scope); assert.equal(preview.files, 0); assert.equal(preview.commonPhrases, 0); assert.equal(preview.recycled, 0); assert.ok(preview.notes.some(n => n.startsWith('不导入：')));
    const result = await transfer('confirm_import', { previewId: preview.id }, scope); assert.equal(result.state, 'completed');
    const before = await api('GET', `teams/${team}/snapshot`); assert.equal(before.phrases.length, 2);
    await transfer('export_data', { path: groupPackage, kind: 'backup', categoryId: null }, await target(team));
    const data = await metadata(groupPackage); assert.equal(data.source, 'team'); assert.deepEqual(data.library.settings, {}); assert.equal(data.recycleBin.length, 0); assert.equal(data.library.phrases.length, 2); assert.equal(data.assets.length, 2);
  });
  await check('提交回执丢失保留批次，重启先核对，不重复发布', async () => {
    const scope = await target(team), preview = await transfer('preview_import', { path: pack }, scope); const writes = publishCount;
    fault.losePublish = true; fault.blockSnapshots = true;
    const result = await transfer('confirm_import', { previewId: preview.id }, scope); assert.equal(result.state, 'pending'); assert.equal(publishCount, writes + 1);
    await startClient('account'); await until(() => invoke('get_team_status'), s => !!s.user); await invoke('team_refresh');
    const pending = await transfer('get_import_pending', {}, await target(team)); assert.equal(pending.id, preview.id); assert.equal(pending.state, 'pending'); assert.equal(publishCount, writes + 1);
    fault.blockSnapshots = false;
    const checked = await transfer('check_import_result', { previewId: preview.id }, await target(team)); assert.equal(checked.state, 'completed'); assert.equal(publishCount, writes + 1); assert.equal((await api('GET', `teams/${team}/snapshot`)).phrases.length, 3);
  });
  await check('共享话术并发更新保留原预览编号，重新核对后追加不覆盖新内容', async () => {
    await until(() => invoke('get_team_view', { teamId: team }), v => v.phrases.length === 3);
    const scope = await target(team), preview = await transfer('preview_import', { path: pack }, scope); const raw = await api('GET', `teams/${team}/snapshot`);
    raw.phrases.push({ id: randomUUID(), rootId: raw.categories[0].id, groupId: null, title: '期间新增', color: '', background: '', position: 99, revision: 0, segments: [{ blocks: [{ kind: 'text', text: '并发新增' }] }] });
    await api('PUT', `teams/${team}/snapshot`, { expectedVersion: raw.version, expectedLifecycleVersion: raw.lifecycleVersion, categories: raw.categories, phrases: raw.phrases, newAssets: [] });
    await until(() => invoke('get_team_view', { teamId: team }), v => v.phrases.length === 4); await assert.rejects(transfer('confirm_import', { previewId: preview.id }, scope), /已更新/);
    const renewed = await transfer('refresh_import_preview', { previewId: preview.id }, await target(team)); assert.equal(renewed.id, preview.id); await transfer('confirm_import', { previewId: preview.id }, renewed.target);
    const current = await api('GET', `teams/${team}/snapshot`); assert.equal(current.phrases.length, 5); assert.ok(current.phrases.some(p => p.title === '期间新增'));
  });
  await check('离线重启可导出完整共享话术缓存，缺素材则保留原成品', async () => {
    await transfer('export_data', { path: groupPackage, kind: 'backup', categoryId: null }, await target(team)); fault.offline = true; for (const s of sockets) s.destroy(); await assert.rejects(invoke('team_refresh'));
    await startClient('account'); const offline = await invoke('get_team_view', { teamId: team }); assert.equal(offline.offline, true);
    const output = path.join(evidence, 'offline.zip'); await transfer('export_data', { path: output, kind: 'backup', categoryId: null }, await target(team)); assert.equal((await metadata(output)).library.phrases.length, 5);
    const file = offline.phrases.flatMap(p => p.segments).flatMap(s => s.blocks).find(b => b.kind === 'file'); await fs.unlink(file.path);
    const old = await fs.readFile(output); await assert.rejects(transfer('export_data', { path: output, kind: 'backup', categoryId: null }, await target(team)), /未缓存/); assert.deepEqual(await fs.readFile(output), old);
    fault.offline = false; await invoke('team_refresh'); await transfer('export_data', { path: output, kind: 'backup', categoryId: null }, await target(team));
  });
  await check('退出期间旧下载取消，取消导出不覆盖旧文件，共享话术缓存及批次不能复活', async () => {
    const current = await invoke('get_team_view', { teamId: team }); const file = current.phrases.flatMap(p => p.segments).flatMap(s => s.blocks).find(b => b.kind === 'file'); await fs.unlink(file.path);
    const output = path.join(evidence, 'cancel.zip'); await fs.writeFile(output, 'existing-backup');
    fault.delayAsset = true; delayedAsset = false;
    const scope = await target(team), operationId = randomUUID();
    const exporting = invoke('export_data', { path: output, kind: 'backup', categoryId: null, target: scope, operationId }).then(() => ({ ok: true }), error => ({ ok: false, error: String(error) }));
    await until(() => delayedAsset); await invoke('cancel_data_transfer', { operationId }); const canceled = await exporting; assert.equal(canceled.ok, false); releaseAsset?.(); assert.equal(await fs.readFile(output, 'utf8'), 'existing-backup');
    fault.delayAsset = true; delayedAsset = false;
    const exportingAgain = transfer('export_data', { path: output, kind: 'backup', categoryId: null }, scope).then(() => true, () => false);
    await until(() => delayedAsset); await invoke('team_logout'); releaseAsset?.(); assert.equal(await exportingAgain, false); await delay(150); assert.equal(await fs.readFile(output, 'utf8'), 'existing-backup');
    assert.equal((await invoke('get_team_status')).teams.length, 0); await assert.rejects(invoke('get_team_view', { teamId: team }));
    await invoke('team_login', { server: clientBase, username: 'qa-admin', password });
  });
  await check('切号拒绝旧私人预览；新会话主动恢复沿用批次，旧确认仍被拒绝', async () => {
    const scope = await target(), preview = await transfer('preview_import', { path: pack }, scope); await invoke('team_logout'); await assert.rejects(transfer('confirm_import', { previewId: preview.id }, scope), /会话/);
    await invoke('team_login', { server: clientBase, username: '0002', password: memberInitial }); await invoke('team_change_password', { request: { currentPassword: memberInitial, password: memberPassword } }); await enable(); await synced();
    assert.notEqual((await invoke('get_library')).libraryId, accountLibrary); await assert.rejects(transfer('confirm_import', { previewId: preview.id }, await target()), /会话|个人库|目标库/);
    await invoke('team_login', { server: clientBase, username: 'qa-admin', password }); const current = await target(); assert.equal(current.libraryId, accountLibrary); const renewed = await transfer('get_import_pending', {}, current); assert.equal(renewed.id, preview.id);
    await assert.rejects(transfer('confirm_import', { previewId: preview.id }, scope), /会话/); await transfer('discard_import', { previewId: preview.id }, current);
  });
  await check('查看者只读导出，降权、快速删除共享话术恢复和撤权使旧导入失效', async () => {
    await invoke('team_login', { server: clientBase, username: '0002', password: memberPassword }); await invoke('team_refresh'); await until(() => invoke('get_team_view', { teamId: team }));
    const scope = await target(team), preview = await transfer('preview_import', { path: pack }, scope); await members('member'); await invoke('team_refresh'); await assert.rejects(transfer('confirm_import', { previewId: preview.id }, scope), /权限/);
    await transfer('export_data', { path: path.join(evidence, 'member.zip'), kind: 'backup', categoryId: null }, await target(team)); await assert.rejects(transfer('preview_import', { path: pack }, await target(team)), /权限/);
    await members('editor'); await invoke('team_refresh'); const editable = await target(team), another = await transfer('preview_import', { path: pack }, editable);
    await api('POST', `teams/${team}/delete`, { expectedLifecycleVersion: 1 }); await api('POST', `deleted-teams/${team}/restore`, { expectedLifecycleVersion: 2 }); lifecycle.set(team, 3); await invoke('team_refresh');
    await assert.rejects(transfer('confirm_import', { previewId: another.id }, editable));
    await until(() => invoke('get_team_view', { teamId: team }), v => v.team.lifecycleVersion === 3); const next = await transfer('preview_import', { path: pack }, await target(team));
    await members(null); await invoke('team_refresh'); await assert.rejects(transfer('confirm_import', { previewId: next.id }, next.target));
    await assert.rejects(fs.access(path.join(evidence, device, 'transfer', next.id))); assert.equal(await fs.readFile(originalFile, 'utf8'), 'actual-test-attachment');
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ clientVersion: await invoke('plugin:app|version'), checks, limits: ['使用真实 Windows WebView2、Rust IPC、SQLite、HTTP/SSE和隔离Hub', '系统选择文件对话框仅替换选择结果；其余业务接口真实执行', '断线和回执丢失由回环代理构造，不能替代真实局域网与代理部署', '未触发企业微信发送、剪贴板、实体键鼠、DPI及安装升级验收'] }, null, 2));
  console.log('证据：' + evidence);
} catch (error) {
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2)); throw error;
} finally {
  releaseAsset?.(); if (page && !page.isClosed()) await invoke('team_logout').catch(() => {});
  await stopClient(); if (service.exitCode === null) service.kill(); for (const s of sockets) s.destroy(); await new Promise(r => proxy.close(r));
}
