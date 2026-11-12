import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 仅使用独立数据、临时 Hub 与原生输入宿主，核对真实下载和 Windows 投递，不接触真实聊天。
const repo = process.cwd(), evidence = path.join(repo, '.build', `cache-${Date.now()}`);
assert.ok(process.env.SHANYU_TEAM_SERVER_EXE, '请设置 SHANYU_TEAM_SERVER_EXE。');
await fs.mkdir(evidence, { recursive: true });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(work, accept = Boolean) {
  const end = Date.now() + 25000; let lastError;
  while (Date.now() < end) {
    try { const value = await work(); if (accept(value)) return value; } catch (error) { lastError = error; }
    await delay(80);
  }
  throw new Error('等待缓存验收超时。', { cause: lastError });
}
async function port() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const result = server.address().port; await new Promise(resolve => server.close(resolve)); return result;
}
const base = `http://127.0.0.1:${await port()}`, initial = randomUUID(), password = randomUUID();
const server = spawn(process.env.SHANYU_TEAM_SERVER_EXE, [], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: path.join(evidence, 'server'), SHANYU_LISTEN: new URL(base).host, SHANYU_ADMIN_USERNAME: 'qa-admin', SHANYU_ADMIN_PASSWORD: initial } });
let failDownloads = false, holdDownloads = false, downloads = 0, activeDownloads = 0, maxDownloads = 0;
const held = new Set();
const proxy = http.createServer((req, res) => {
  const asset = req.method === 'GET' && /\/(?:assets\/[^/?]+|company\/files\/[^/]+\/content)(?:\?|$)/.test(req.url);
  if (asset) {
    downloads++; activeDownloads++; maxDownloads = Math.max(maxDownloads, activeDownloads);
    let finished = false;
    const finish = () => { if (!finished) { finished = true; activeDownloads--; } };
    res.once('finish', finish); res.once('close', finish);
    if (failDownloads) { res.writeHead(503); res.end(); return; }
  }
  const upstream = http.request(base + req.url, { method: req.method, headers: { ...req.headers, host: new URL(base).host } }, response => {
    if (asset && holdDownloads) {
      const chunks = []; response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const release = () => { held.delete(release); if (!res.destroyed) { res.writeHead(response.statusCode, response.headers); res.end(Buffer.concat(chunks)); } };
        held.add(release); res.once('close', () => held.delete(release));
      });
    } else { res.writeHead(response.statusCode, response.headers); response.pipe(res); }
  });
  upstream.on('error', () => res.destroy()); req.pipe(upstream);
});
await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
const clientBase = `http://127.0.0.1:${proxy.address().port}`;
let app, browser, page, host, token, epoch, root, privateId, teamId, linkedId, grant;
const checks = [], run = promisify(execFile);
const check = async (name, work) => { await work(); checks.push(name); console.log('通过：' + name); };
const ps = args => run('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
const native = async args => JSON.parse((await ps(['-File', path.join(repo, 'tests/native-driver.ps1'), ...args])).stdout);
const invoke = (command, args = {}) => page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
const api = async (method, route, data) => {
  const team = /^teams\/([^/]+)/.exec(route)?.[1];
  const response = await fetch(`${base}/api/${route}`, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(team ? { 'x-team-lifecycle': '1' } : {}) }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) });
  assert.ok(response.ok, `${method} ${route}: ${response.status}`); return response.status === 204 ? null : response.json();
};
const eventsFile = path.join(evidence, 'host.json'), controlsFile = path.join(evidence, 'control.json');
const snapshot = async () => JSON.parse(await fs.readFile(eventsFile, 'utf8'));
async function control(command, fields = {}) {
  const text = JSON.stringify({ command, ...fields, nonce: randomUUID() });
  for (let attempt = 0; ; attempt++) {
    try { await fs.writeFile(controlsFile, text); break; }
    catch (error) { if (error.code !== 'EBUSY' || attempt >= 9) throw error; await delay(20); }
  }
  await delay(150);
}
async function focus() {
  await control('reset'); const info = await snapshot();
  await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus');
  await until(() => invoke('get_target'), target => target?.hwnd === info.hwnd && target?.focus === info.focus);
}
async function stopClient() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { app.kill(); await new Promise(resolve => app.once('exit', resolve)); }
}
async function startClient(device) {
  await stopClient(); const debug = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, device), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debug}` } });
  await until(async () => { browser = await chromium.connectOverCDP(`http://127.0.0.1:${debug}`); return true; });
  page = await until(async () => browser.contexts()[0].pages().find(view => !new URL(view.url()).hash));
  await page.locator('.main-window').waitFor();
  epoch = (await invoke('team_login', { server: clientBase, username: 'qa-admin', password })).sessionEpoch;
}
async function enableSync() {
  const preview = await invoke('preview_private_sync');
  await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } });
  await until(() => invoke('get_private_status'), value => value.enabled && value.pending === 0 && value.state === '已同步');
}
function draft(rootId, blocks) { return { id: '', rootId, groupId: null, title: '缓存验收', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks }] }; }
async function privateRow() {
  const library = await invoke('get_library'), phrase = library.phrases.find(value => value.id === privateId);
  return { phrase, key: `private:${library.libraryId}:phrase:${privateId}:${library.privateRevisions[`phrase:${privateId}`] || 0}`, library };
}
async function teamRow() { return (await invoke('get_team_view', { teamId })).phrases[0]; }
async function removeCache(file) {
  assert.ok(path.resolve(file).startsWith(path.resolve(evidence) + path.sep), '只能删除本次隔离数据内的缓存文件');
  await fs.rm(file, { force: true });
}
async function deliver(id, kind = 'phrase', mode = 'send') { return invoke('deliver_content', { id, kind, mode, target: await invoke('get_target') }); }
const outcome = promise => promise.then(report => ({ report }), error => ({ error: String(error) }));
function releaseDownloads() { holdDownloads = false; for (const release of [...held]) release(); }
function assertNoDelivery(info) { assert.ok(!info.events.some(event => event.kind.startsWith('paste-') || event.kind === 'send')); }
try {
  await until(async () => (await fetch(base + '/health')).ok);
  token = (await api('POST', 'login', { username: 'qa-admin', password: initial })).token;
  await api('PUT', 'password', { currentPassword: initial, password });
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await ps(['-File', path.join(repo, 'tests/build-dock-host.ps1'), '-OutputPath', fixture]);
  host = spawn(fixture, [eventsFile, controlsFile, 'WeWorkWindow'], { windowsHide: true, stdio: 'ignore' });
  await until(snapshot); const clipboardBefore = (await snapshot()).clipboardSequence;
  await startClient('source');
  root = await invoke('save_category', { category: { id: '', name: '缓存验收', kind: 'phrase', parentId: null, position: 0, background: '' } });
  const file = path.join(evidence, '缓存附件.txt'); await fs.writeFile(file, 'cache-qa-attachment');
  const upload = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: file });
  const cloud = await invoke('upload_company_file', { sessionEpoch: epoch, id: upload.id });
  await check('云文件目录更新后自动缓存，后台不触碰剪贴板或发送按键', async () => {
    await until(() => invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }), view => view.files.some(entry => entry.assetId === cloud.assetId && entry.cached));
    assertNoDelivery(await snapshot()); assert.equal((await snapshot()).clipboardSequence, clipboardBefore);
  });
  const library = await invoke('get_library');
  privateId = await invoke('save_company_private_phrase', { phrase: draft(root, [{ kind: 'text', text: '缓存正文' }, { kind: 'file', path: '', name: cloud.name }]), libraryId: library.libraryId, companyFiles: [{ segment: 0, block: 1, assetId: cloud.assetId, sessionEpoch: epoch }] });
  await enableSync();
  grant = await invoke('save_member_share', { sessionEpoch: epoch, request: { enabled: true, mode: 'all', rootIds: [], expectedVersion: 0 } });
  teamId = await api('POST', 'teams', { name: '自动缓存' }); await invoke('team_refresh');
  let view = await until(async () => invoke('get_team_view', { teamId }));
  await invoke('save_team_category', { teamId, expectedVersion: view.team.version, expectedLifecycleVersion: 1, category: { id: '', name: '资料', kind: 'phrase', parentId: null, position: 0, background: '' } });
  view = await invoke('get_team_view', { teamId });
  await invoke('save_team_phrase', { draft: { teamId, expectedVersion: view.team.version, expectedLifecycleVersion: 1, phrase: draft(view.categories[0].id, [{ kind: 'text', text: '共享正文' }, { kind: 'file', path: '', name: cloud.name }, { kind: 'file', path: file, name: '普通附件.txt' }]), companyFiles: [{ segment: 0, block: 1, assetId: cloud.assetId, sessionEpoch: epoch }] } });
  await check('共享话术发布后自动缓存普通附件', async () => { await until(teamRow, row => row.ready); });
  await startClient('recipient'); await enableSync();
  await check('第二设备同步后自动缓存个人、共享和云文件', async () => {
    await until(privateRow, value => value.library.privateReady[privateId]); await until(teamRow, row => row.ready);
    await until(() => invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }), value => value.files.every(entry => entry.cached));
    assert.equal(await fs.readFile((await privateRow()).phrase.segments[0].blocks[1].path, 'utf8'), 'cache-qa-attachment');
    assertNoDelivery(await snapshot()); assert.equal((await snapshot()).clipboardSequence, clipboardBefore); assert.ok(maxDownloads <= 2);
  });
  const category = await invoke('save_category', { category: { id: '', name: '云文件关联', kind: 'file', parentId: null, position: 0, background: '' } });
  await invoke('add_cloud_files', { sessionEpoch: epoch, categoryId: category, assetIds: [cloud.assetId] });
  linkedId = (await invoke('get_library')).files[0].id;
  for (const source of ['个人', '共享', '云文件']) {
    await check(`${source}缓存缺失时首次发送自动下载并仅提交一次`, async () => {
      await focus(); const own = await privateRow(), shared = await teamRow();
      await removeCache(source === '个人' ? own.phrase.segments[0].blocks[1].path : shared.segments[0].blocks[source === '共享' ? 2 : 1].path);
      const before = downloads; holdDownloads = true;
      const id = source === '个人' ? own.key : source === '共享' ? shared.id : linkedId, kind = source === '云文件' ? 'file' : 'phrase';
      const pending = outcome(deliver(id, kind)); await until(async () => held.size > 0); assertNoDelivery(await snapshot());
      assert.match((await outcome(deliver(id, kind))).error, /上一次操作/); releaseDownloads();
      const result = await pending; assert.equal(result.error, undefined); assert.equal(result.report.status, 'submitted');
      const info = await until(snapshot, value => value.events.some(event => event.kind === 'send'));
      assert.equal(info.events.filter(event => event.kind === 'send').length, 1);
      assert.equal(info.events.filter(event => event.kind === 'paste-files').length, source === '共享' ? 2 : 1);
      assert.equal(downloads, before + 1, '同一缺失附件只下载一次');
    });
  }
  await check('下载失败时整条话术不投递，文字和剪贴板均保持', async () => {
    await focus(); const own = await privateRow(); await removeCache(own.phrase.segments[0].blocks[1].path);
    failDownloads = true; const sequence = (await snapshot()).clipboardSequence, result = await outcome(deliver(own.key)); failDownloads = false;
    assert.ok(result.error); assertNoDelivery(await snapshot()); assert.equal((await snapshot()).clipboardSequence, sequence);
  });
  for (const change of ['切换输入区', '新复制', '修改话术']) {
    await check(`下载期间${change}停止本次投递`, async () => {
      await focus(); const own = await privateRow(); await removeCache(own.phrase.segments[0].blocks[1].path); holdDownloads = true;
      const pending = outcome(deliver(own.key)); await until(async () => held.size > 0);
      if (change === '切换输入区') await control('blur');
      if (change === '新复制') await control('copy', { text: 'cache-qa-new-copy' });
      if (change === '修改话术') await invoke('save_phrase', { phrase: { ...own.phrase, title: '更新后的话术' }, libraryId: own.library.libraryId, expectedRevision: own.library.privateRevisions[`phrase:${privateId}`] });
      if (change !== '修改话术') assert.match((await pending).error, change === '新复制' ? /新的复制/ : /变化/);
      releaseDownloads(); if (change === '修改话术') assert.match((await pending).error, /更新|变化/);
      await delay(250); assertNoDelivery(await snapshot());
    });
  }
  await check('下载期间退出账号停止投递，迟到响应不写入旧缓存', async () => {
    await focus(); const own = await privateRow(), file = own.phrase.segments[0].blocks[1].path;
    await removeCache(file); holdDownloads = true;
    const pending = outcome(deliver(own.key)); await until(async () => held.size > 0);
    await invoke('team_logout'); assert.ok((await pending).error); releaseDownloads();
    await delay(250); assertNoDelivery(await snapshot()); assert.equal(await fs.access(file).then(() => true, () => false), false);
  });
  await check('成员话术同步后自动缓存，缺失时首次插入自动补齐', async () => {
    const workerInitial = randomUUID(); await api('POST', 'users', { username: 'qa-worker', password: workerInitial });
    epoch = (await invoke('team_login', { server: clientBase, username: 'qa-worker', password: workerInitial })).sessionEpoch;
    await invoke('team_change_password', { request: { currentPassword: workerInitial, password: randomUUID() } });
    const overview = await invoke('get_member_overview', { sessionEpoch: epoch });
    await invoke('set_member_subscription', { sessionEpoch: epoch, request: { grantId: grant.id, expectedVersion: overview.subscription.version } });
    const member = await until(() => invoke('get_member_view'), value => value.phrases[0]?.ready);
    await focus(); await removeCache(member.phrases[0].segments[0].blocks[1].path);
    const report = await deliver(member.phrases[0].id, 'phrase', 'insert'); assert.equal(report.status, 'submitted'); assert.equal(report.completedBlocks, 2);
    const info = await snapshot(); assert.equal(info.events.filter(event => event.kind === 'paste-files').length, 1); assert.ok(!info.events.some(event => event.kind === 'send'));
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, downloads, maxDownloads, isolatedHost: true, realWeCom: false }, null, 2));
  console.log(`缓存验收 ${checks.length} 项通过：${evidence}`);
} catch (error) {
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error), downloads, maxDownloads }, null, 2)); throw error;
} finally {
  releaseDownloads();
  if (host?.exitCode === null) {
    const clipboard = await native([]).catch(() => null);
    if (clipboard?.clipboardOwnerPid === app?.pid) await control('adopt', { ownerPid: app.pid, sequence: clipboard.clipboardSequence }).catch(() => {});
    await control('close').catch(() => {}); if (host.exitCode === null) host.kill();
  }
  await stopClient(); server.kill(); proxy.closeAllConnections(); await new Promise(resolve => proxy.close(resolve));
}
