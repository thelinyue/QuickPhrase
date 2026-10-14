import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 真实 WebView2、Rust IPC、SQLite 和 Hub 联调。代理只制造回执丢失；不使用聊天、剪贴板或实体键鼠。
const repo = process.cwd(), evidence = path.join(repo, '.build', `company-${Date.now()}`);
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请设置 SHANYU_TEAM_SERVER_EXE。');
await fs.mkdir(evidence, { recursive: true });
const executable = path.join(evidence, 'shanyu-team-server.exe'); await fs.copyFile(process.env.SHANYU_TEAM_SERVER_EXE, executable);
const delay = ms => new Promise(r => setTimeout(r, ms));
const port = async () => { const s = net.createServer(); await new Promise(r => s.listen(0, '127.0.0.1', r)); const p = s.address().port; await new Promise(r => s.close(r)); return p; };
const until = async work => { const end = Date.now() + 20000; while (Date.now() < end) { try { const result = await work(); if (result) return result; } catch {} await delay(80); } throw new Error('等待云文件联调超时。'); };
const checks = [], check = async (name, work) => { await work(); checks.push(name); console.log('通过：' + name); };
const initial = randomUUID(), password = randomUUID(), workerInitial = randomUUID(), workerPassword = randomUUID();
const base = `http://127.0.0.1:${await port()}`;
const server = spawn(executable, [], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: path.join(evidence, 'server'), SHANYU_LISTEN: new URL(base).host, SHANYU_ADMIN_USERNAME: 'qa-admin', SHANYU_ADMIN_PASSWORD: initial } });
let loseUpload = false, uploadCount = 0, downloadCount = 0, holdDownload = false, heldDownload = false, releaseDownload, holdUpload = false, heldUpload = false, releaseUpload, uploadSettled, token, workerToken, app, browser, page, cloud, editor, epoch, libraryId, root, asset, teams = [];
const proxy = http.createServer((req, res) => {
  const lost = loseUpload && req.method === 'PUT' && /\/company\/files\/[^/]+\/content/.test(req.url);
  if (req.method === 'PUT' && /\/company\/files\/[^/]+\/content/.test(req.url)) ++uploadCount;
  if (req.method === 'GET' && /\/company\/files\/[^/]+\/content/.test(req.url)) ++downloadCount;
  const held = holdDownload && req.method === 'GET' && /\/company\/files\/[^/]+\/content/.test(req.url);
  const pausedUpload = holdUpload && req.method === 'PUT' && /\/company\/files\/[^/]+\/content/.test(req.url);
  if (pausedUpload) { holdUpload = false; uploadSettled = null; }
  if (held) holdDownload = false;
  if (lost) loseUpload = false;
  const upstream = http.request(base + req.url, { method: req.method, headers: { ...req.headers, host: new URL(base).host } }, response => {
    if (pausedUpload) uploadSettled = response.statusCode;
    if (lost) { response.resume(); response.on('end', () => res.destroy()); return; }
    if (held) {
      const chunks = []; response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => { heldDownload = true; releaseDownload = () => { if (!res.destroyed) { res.writeHead(response.statusCode, response.headers); res.end(Buffer.concat(chunks)); } releaseDownload = undefined; heldDownload = false; }; });
      return;
    }
    res.writeHead(response.statusCode, response.headers); response.pipe(res);
  }); upstream.on('error', () => { if (!res.headersSent && !res.destroyed) { res.writeHead(503); res.end(); } else res.destroy(); });
  if (pausedUpload) {
    // 服务端收到首字节后暂存剩余请求体，覆盖已开始、未登记时的授权变化。
    const chunks = []; let first = true;
    req.on('data', chunk => { if (first) { upstream.write(chunk.subarray(0, 1)); chunks.push(chunk.subarray(1)); first = false; } else chunks.push(chunk); });
    req.on('end', () => { heldUpload = true; releaseUpload = () => { upstream.end(Buffer.concat(chunks)); releaseUpload = undefined; heldUpload = false; }; });
  } else req.pipe(upstream);
});
await new Promise(r => proxy.listen(0, '127.0.0.1', r)); const clientBase = `http://127.0.0.1:${proxy.address().port}`;
const api = async (method, route, data, auth = token) => {
  const group = /^teams\/([^/]+)/.exec(route)?.[1]; const response = await fetch(`${base}/api/${route}`, { method, headers: { Authorization: `Bearer ${auth}`, 'Content-Type': 'application/json', ...(group ? { 'x-team-lifecycle': '1' } : {}) }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) });
  assert.ok(response.ok, `接口拒绝请求：${response.status}`); return response.status === 204 ? null : response.json();
};
const invoke = async (command, args = {}, view = page) => view.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
const draft = (title, rootId, blocks = [{ kind: 'file', path: '', name: '价格表.xlsx' }]) => ({ id: '', rootId, groupId: null, title, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks }] });
const selections = () => [{ segment: 0, block: 0, assetId: asset.assetId, sessionEpoch: epoch }];
async function window(label, route) {
  await invoke('plugin:webview|create_webview_window', { options: { label, url: `index.html#${route}`, title: `闪语隔离验收:${label}`, width: 1000, height: 650, x: -20000, y: -20000, visible: true, focus: false, skipTaskbar: true, decorations: false } });
  return until(() => browser.contexts()[0].pages().find(p => p.url().includes(`#${route}`)));
}
async function python(code, args) {
  return new Promise((resolve, reject) => { const process = spawn('python', ['-c', code, ...args], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...globalThis.process.env, PYTHONIOENCODING: 'utf-8' } }); let output = ''; process.stdout.on('data', b => output += b); process.on('error', reject); process.on('exit', c => c === 0 ? resolve(output) : reject(new Error('ZIP验收失败。'))); });
}
async function resizeCloud(width, height) {
  // 只调整本次隔离进程的屏幕外窗口，复用原生验收驱动，不扩大产品前端权限。
  const inner = await invoke('plugin:window|inner_size', { label: 'company-files' }, cloud), outer = await invoke('plugin:window|outer_size', { label: 'company-files' }, cloud), scale = await cloud.evaluate(() => devicePixelRatio);
  const driver = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tests/native-driver.ps1', '-Action', 'move', '-ProcessId', String(app.pid), '-Title', '闪语隔离验收:company-files', '-X', '-20000', '-Y', '-20000', '-Width', String(Math.round(width * scale) + outer.width - inner.width), '-Height', String(Math.round(height * scale) + outer.height - inner.height)], { cwd: repo, windowsHide: true, stdio: 'ignore' });
  assert.equal(await new Promise(resolve => driver.once('exit', resolve)), 0);
  await until(() => cloud.evaluate(({ width, height }) => innerWidth === width && innerHeight === height, { width, height }));
}
async function verifyProgressLayout() {
  // 按实际文字矩形核对百分比未换行、未挤出单元格，防止 100／% 的布局回归。
  const layout = await cloud.locator('.company-progress').evaluateAll(elements => elements.map(el => {
    const bar = el.querySelector('progress').getBoundingClientRect(), span = el.querySelector('span'), percent = span.getBoundingClientRect(), bounds = el.getBoundingClientRect();
    const range = document.createRange(); range.selectNodeContents(span);
    // React 的数字与百分号是两个文本节点，同一行也有多个矩形，因此比较纵向坐标。
    return { text: span.textContent, lines: new Set([...range.getClientRects()].map(rect => Math.round(rect.top))).size, ordered: bar.right <= percent.left && percent.right <= bounds.right + 1 };
  }));
  assert.ok(layout.length > 0 && layout.every(item => item.lines === 1 && item.ordered), JSON.stringify(layout));
}
async function startClient() {
  const debug = await port(); app = spawn(path.join(repo, 'src-tauri/target/debug/shanyu.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'client'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debug}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debug}`)); page = await until(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor();
}
try {
  await until(() => fetch(`${base}/health`).then(r => r.ok)); const session = await api('POST', 'login', { username: 'qa-admin', password: initial }); token = session.token; await api('PUT', 'password', { currentPassword: initial, password });
  const worker = await api('POST', 'users', { username: 'qa-worker', password: workerInitial });
  const memberSession = await api('POST', 'login', { username: 'qa-worker', password: workerInitial }); workerToken = memberSession.token; await api('PUT', 'password', { currentPassword: workerInitial, password: workerPassword }, workerToken);
  await startClient();
  await invoke('team_login', { server: clientBase, username: 'qa-admin', password }); epoch = (await invoke('get_team_status')).sessionEpoch;
  libraryId = (await invoke('get_library')).libraryId; root = await invoke('save_category', { category: { id: '', name: '资料', kind: 'phrase', parentId: null, position: 0, background: '' }, libraryId });
  const source = path.join(evidence, '价格表.xlsx'), bytes = Buffer.from('实际附件内容\n中文文件名\n'); await fs.writeFile(source, bytes);
  let task;
  await check('上传回执丢失后复用固定任务和固化源副本，不重复传输', async () => {
    task = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: source }); await fs.writeFile(source, '外部文件后来修改'); loseUpload = true;
    await assert.rejects(invoke('upload_company_file', { sessionEpoch: epoch, id: task.id })); asset = await invoke('upload_company_file', { sessionEpoch: epoch, id: task.id }); assert.equal(uploadCount, 1);
    const response = await fetch(`${base}/api/company/files/${asset.assetId}/content`, { headers: { Authorization: `Bearer ${token}` } }); assert.deepEqual(Buffer.from(await response.arrayBuffer()), bytes);
    assert.equal((await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' })).uploads.length, 0);
  });
  await check('云文件管理真实窗口显示目录、权限和中文名称', async () => {
    cloud = await window('company-files', 'company-files'); await cloud.getByText('价格表.xlsx', { exact: true }).waitFor();
    assert.deepEqual(await cloud.locator('.company-toolbar button').allTextContents(), ['刷新', '下载管理', '上传管理', '上传新文件']);
    await cloud.locator('.company-file-name img').waitFor(); assert.ok(await cloud.locator('.company-file-name img').evaluate(img => img.complete && img.naturalWidth > 0));
    assert.equal(await cloud.getByRole('combobox', { name: '每页条数' }).inputValue(), '10'); await cloud.getByRole('button', { name: '最小化窗口' }).waitFor(); await cloud.getByRole('button', { name: '最大化或还原窗口' }).waitFor();
    assert.ok(await cloud.locator('tbody button').evaluateAll(buttons => buttons.every(b => !b.textContent && b.title && b.getAttribute('aria-label'))));
    const more = cloud.getByRole('button', { name: '更多', exact: true }); await more.click(); await cloud.getByRole('menuitem', { name: '另存为', exact: true }).waitFor(); await cloud.keyboard.press('Escape'); assert.ok(await more.evaluate(el => el === document.activeElement));
  });
  await check('真实目录分页全选、时间排序、搜索与最小窗口保持可操作', async () => {
    const temporary = [];
    for (let i = 1; i <= 22; ++i) {
      const id = randomUUID(), extension = ['pdf', 'docx', 'xlsx', 'zip'][i % 4]; temporary.push(id);
      const response = await fetch(`${base}/api/company/files/${id}/content?${new URLSearchParams({ name: `公司资料${String(i).padStart(2, '0')}.${extension}`, size: '3' })}`, { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: 'abc' }); assert.ok(response.ok);
    }
    await cloud.getByRole('button', { name: '刷新', exact: true }).click(); await cloud.getByText('共3页 23条').waitFor();
    assert.equal(await cloud.locator('tbody tr').count(), 10); await cloud.getByRole('checkbox', { name: '选择当前页全部文件' }).check(); assert.equal(await cloud.locator('tbody input:checked').count(), 10); await cloud.getByText('已选 10 项', { exact: true }).waitFor();
    await cloud.getByRole('button', { name: '下一页' }).click(); assert.equal(await cloud.locator('tbody input:checked').count(), 0);
    await cloud.getByRole('textbox', { name: '跳转页码' }).fill('3'); await cloud.getByRole('button', { name: '跳转' }).click(); assert.equal(await cloud.locator('tbody tr').count(), 3);
    await cloud.getByRole('button', { name: '上传时间倒序' }).click(); await cloud.getByRole('button', { name: '上传时间正序' }).waitFor();
    await cloud.getByRole('textbox', { name: '搜索云文件' }).fill('价格表'); await until(async () => await cloud.locator('tbody tr').count() === 1); await cloud.getByRole('textbox', { name: '搜索云文件' }).fill('');
    await cloud.getByRole('button', { name: '上传时间正序' }).click(); await cloud.getByText('共3页 23条').waitFor();
    await cloud.screenshot({ path: path.join(evidence, 'company-manager.png') });
    await resizeCloud(760, 480);
    assert.ok(await cloud.locator('.company-pagination').evaluate(el => { const b = el.getBoundingClientRect(); return b.bottom <= innerHeight && b.right <= innerWidth; }));
    assert.equal(await cloud.locator('.company-sidebar').evaluate(el => el.getBoundingClientRect().width), 190);
    await cloud.getByRole('checkbox', { name: '选择当前页全部文件' }).check(); const batchButton = cloud.getByRole('button', { name: '批量操作', exact: true }), beforeScroll = await batchButton.boundingBox();
    assert.ok(await batchButton.evaluate(el => { const b = el.getBoundingClientRect(), list = document.querySelector('.company-list').getBoundingClientRect(); return !el.closest('table') && b.left >= list.left && b.right <= innerWidth && b.bottom <= list.top; }));
    await cloud.locator('.company-list').evaluate(el => { el.scrollLeft = el.scrollWidth; }); assert.deepEqual(await batchButton.boundingBox(), beforeScroll);
    await batchButton.click(); assert.ok(await cloud.getByRole('menu').evaluate(el => { const b = el.getBoundingClientRect(); return b.left >= 0 && b.right <= innerWidth && b.bottom <= innerHeight; }));
    await cloud.screenshot({ path: path.join(evidence, 'company-batch-menu.png') }); await cloud.keyboard.press('Escape'); assert.ok(await batchButton.evaluate(el => el === document.activeElement));
    await cloud.locator('.company-list').evaluate(el => { el.scrollLeft = 0; }); await cloud.screenshot({ path: path.join(evidence, 'company-minimum.png') });
    await resizeCloud(1000, 650);
    for (const id of temporary) { await api('POST', `company/files/${id}/recycle`); const file = await api('GET', `company/files/${id}`); await api('DELETE', `company/files/${id}`, { confirmName: file.name }); }
    await cloud.getByRole('button', { name: '刷新', exact: true }).click(); await cloud.getByText('共1页 1条').waitFor();
  });
  await check('权限页逐行保存、草稿冲突与固定管理员权限通过真实IPC和SSE', async () => {
    await cloud.getByRole('button', { name: '云文件权限管理', exact: true }).click(); await cloud.getByRole('checkbox', { name: 'qa-worker的上传权限' }).waitFor();
    assert.ok(await cloud.getByRole('checkbox', { name: 'qa-admin的上传权限' }).isDisabled()); await cloud.getByRole('checkbox', { name: 'qa-worker的上传权限' }).check();
    await cloud.getByRole('button', { name: '保存', exact: true }).click(); await cloud.getByText('qa-worker 的云文件权限已保存。').waitFor();
    let user = (await api('GET', 'users')).find(u => u.id === worker); assert.equal(user.companyFilePermissions.canUpload, true);
    await cloud.getByRole('checkbox', { name: 'qa-worker的删除权限' }).check();
    await api('PUT', `users/${worker}`, { companyFilePermissions: { canUpload: true, canRecycle: false, canRestore: true, expectedVersion: user.companyFilePermissions.version } });
    await cloud.getByText('权限已变更，草稿已保留').waitFor(); assert.ok(await cloud.getByRole('checkbox', { name: 'qa-worker的删除权限' }).isChecked()); assert.ok(await cloud.getByRole('button', { name: '保存', exact: true }).isDisabled());
    assert.ok(await cloud.locator('tbody tr').evaluateAll(rows => rows.every(row => !row.lastElementChild.textContent.trim())));
    await cloud.getByRole('img', { name: '固定全部权限', exact: true }).waitFor();
    await cloud.screenshot({ path: path.join(evidence, 'company-permissions.png') }); await cloud.getByRole('button', { name: '采用最新', exact: true }).click();
    user = (await api('GET', 'users')).find(u => u.id === worker); await api('PUT', `users/${worker}`, { companyFilePermissions: { canUpload: false, canRecycle: false, canRestore: false, expectedVersion: user.companyFilePermissions.version } });
    await until(async () => !await cloud.getByRole('checkbox', { name: 'qa-worker的上传权限' }).isChecked()); await cloud.getByRole('button', { name: '云文件管理', exact: true }).click();
  });
  let first, second;
  await check('我的话术生成独立附件，同一本机个人库复用副本', async () => {
    first = await invoke('save_company_private_phrase', { phrase: draft('第一份', root), afterId: null, expectedRevision: null, libraryId, companyFiles: selections() });
    second = await invoke('save_company_private_phrase', { phrase: draft('第二份', root), afterId: null, expectedRevision: null, libraryId, companyFiles: selections() });
    const library = await invoke('get_library'), a = library.phrases.find(p => p.id === first).segments[0].blocks[0].path, b = library.phrases.find(p => p.id === second).segments[0].blocks[0].path;
    assert.equal(a, b); assert.equal(path.basename(a), '价格表.xlsx'); assert.deepEqual(await fs.readFile(a), bytes);
  });
  await check('真实编辑器选择云文件并保存，附件位置和ID不丢失', async () => {
    editor = await window('editor', `editor?categoryId=${root}&libraryId=${encodeURIComponent(libraryId)}`);
    await editor.getByLabel('话术标题（可不填）').fill('编辑器生成');
    await editor.getByRole('button', { name: '文件', exact: true }).click(); await editor.getByRole('button', { name: '云文件', exact: true }).click();
    const picker = editor.getByRole('dialog', { name: '选择云文件' }); await picker.getByRole('checkbox').check(); await picker.getByRole('button', { name: '添加文件' }).click();
    await editor.getByRole('button', { name: '删除附件 价格表.xlsx' }).waitFor(); await editor.screenshot({ path: path.join(evidence, 'company-editor.png') });
    await editor.getByRole('button', { name: '确定', exact: true }).click();
    const library = await until(async () => { const library = await invoke('get_library'); return library.phrases.some(p => p.title === '编辑器生成') && library; });
    assert.deepEqual(await fs.readFile(library.phrases.find(p => p.title === '编辑器生成').segments[0].blocks.find(b => b.kind === 'file').path), bytes);
  });
  await check('两共享库直接引用同一云文件本体，下载路径保留实际文件名', async () => {
    for (const name of ['销售', '售后']) teams.push(await api('POST', 'teams', { name })); await invoke('team_refresh');
    for (const teamId of teams) {
      await until(() => invoke('get_team_view', { teamId })); let view = await invoke('get_team_view', { teamId });
      await invoke('save_team_category', { teamId, expectedVersion: view.team.version, expectedLifecycleVersion: 1, category: { id: '', name: '资料', kind: 'phrase', parentId: null, position: 0, background: '' } }); view = await invoke('get_team_view', { teamId });
      await invoke('save_team_phrase', { draft: { teamId, expectedVersion: view.team.version, expectedLifecycleVersion: 1, phrase: draft('公司资料', view.categories[0].id), companyFiles: selections() } });
      const snapshot = await api('GET', `teams/${teamId}/snapshot`); assert.equal(snapshot.phrases[0].segments[0].blocks[0].assetId, asset.assetId); assert.deepEqual(snapshot.companyAssetIds, [asset.assetId]);
      view = await invoke('get_team_view', { teamId }); await invoke('prepare_team_phrase', { id: view.phrases[0].id }); view = await invoke('get_team_view', { teamId }); assert.equal(path.basename(view.phrases[0].segments[0].blocks[0].path), '价格表.xlsx');
    }
  });
  await check('共享库ZIP包含实际公司附件，普通导入成为目标私人附件', async () => {
    const teamId = teams[0], view = await invoke('get_team_view', { teamId }), target = { source: 'team', teamId, version: view.team.version, lifecycleVersion: 1, sessionEpoch: epoch }, zip = path.join(evidence, '公司话术.zip');
    await invoke('export_data', { path: zip, kind: 'backup', categoryId: null, operationId: randomUUID(), target });
    const result = JSON.parse(await python("import sys,zipfile,json; z=zipfile.ZipFile(sys.argv[1]); d=json.loads(z.read('library.json')); print(json.dumps({'files':z.namelist(),'library':d}))", [zip])); assert.ok(result.files.some(name => name !== 'library.json'));
    const privateTarget = { source: 'private', libraryId, sessionEpoch: epoch }; const preview = await invoke('preview_import', { path: zip, merge: false, restoreSettings: false, operationId: randomUUID(), target: privateTarget });
    await invoke('confirm_import', { previewId: preview.id, operationId: randomUUID(), target: privateTarget });
    const imported = (await invoke('get_library')).phrases.find(p => p.title === '公司资料'); assert.ok(imported); assert.deepEqual(await fs.readFile(imported.segments[0].blocks[0].path), bytes);
  });
  await check('回收保留原话术，禁止新增引用，并阻止有引用的永久删除', async () => {
    await invoke('manage_company_file', { sessionEpoch: epoch, id: asset.assetId, operation: 'recycle', confirmName: '' });
    await assert.rejects(invoke('save_company_private_phrase', { phrase: draft('禁止新增', root), afterId: null, expectedRevision: null, libraryId, companyFiles: selections() }), /回收/);
    await assert.rejects(invoke('manage_company_file', { sessionEpoch: epoch, id: asset.assetId, operation: 'purge', confirmName: asset.name }), /引用/);
    for (const teamId of teams) { const view = await invoke('get_team_view', { teamId }); const response = await fetch(`${base}/api/teams/${teamId}/assets/${asset.assetId}`, { headers: { Authorization: `Bearer ${token}`, 'x-team-lifecycle': '1' } }); assert.deepEqual(Buffer.from(await response.arrayBuffer()), bytes); assert.ok(view.phrases[0].ready); }
  });
  await check('真实回收站保留系统图标、两种用户名和时间，批量恢复及逐文件永久删除', async () => {
    await cloud.getByRole('button', { name: '云文件回收站', exact: true }).click(); const row = cloud.locator('tbody tr').filter({ hasText: asset.name }); await row.waitFor();
    assert.equal(await row.getByText('qa-admin', { exact: true }).count(), 2); await row.locator('img').waitFor();
    assert.ok(await cloud.locator('.company-recycle-table').evaluate(el => el.getBoundingClientRect().right <= el.parentElement.getBoundingClientRect().right));
    await cloud.screenshot({ path: path.join(evidence, 'company-recycle.png') });
    await row.getByRole('button', { name: '恢复', exact: true }).click(); await cloud.getByRole('dialog', { name: '恢复云文件' }).getByRole('button', { name: '确定', exact: true }).click(); await until(async () => !await row.count());
    await cloud.getByRole('button', { name: '云文件管理', exact: true }).click();
    const unreferenced = [];
    for (const name of ['批量资料.pdf', '批量资料.docx']) {
      const id = randomUUID(); unreferenced.push({ id, name }); const response = await fetch(`${base}/api/company/files/${id}/content?${new URLSearchParams({ name, size: '3' })}`, { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: 'abc' }); assert.ok(response.ok);
    }
    await cloud.getByRole('button', { name: '刷新', exact: true }).click();
    for (const file of unreferenced) await cloud.getByRole('checkbox', { name: `选择 ${file.name}`, exact: true }).check();
    await cloud.getByRole('button', { name: '批量操作' }).click(); await cloud.getByRole('menuitem', { name: '批量删除' }).click();
    const confirmation = cloud.getByRole('dialog', { name: '批量删除云文件' }); for (const file of unreferenced) await confirmation.getByText(file.name, { exact: true }).waitFor();
    await confirmation.getByRole('button', { name: '确定', exact: true }).click(); await cloud.getByText(/成功 2 个，失败 0 个/).waitFor();
    await cloud.getByRole('button', { name: '云文件回收站', exact: true }).click(); await cloud.getByRole('checkbox', { name: '选择当前页全部文件' }).check(); await cloud.getByRole('button', { name: '批量操作' }).click(); await cloud.getByRole('menuitem', { name: '批量恢复' }).click();
    await cloud.getByRole('dialog', { name: '批量恢复云文件' }).getByRole('button', { name: '确定', exact: true }).click(); await cloud.getByText('回收站为空').waitFor();
    for (const file of unreferenced) {
      await api('POST', `company/files/${file.id}/recycle`); await cloud.getByRole('button', { name: '刷新', exact: true }).click();
      const item = cloud.locator('tbody tr').filter({ hasText: file.name }); await item.getByRole('button', { name: '永久删除', exact: true }).click();
      const dialog = cloud.getByRole('dialog', { name: '永久删除云文件' }); await dialog.getByRole('textbox', { name: '输入完整文件名' }).fill(file.name); await dialog.getByRole('button', { name: '确定', exact: true }).click(); await until(async () => !await item.count());
    }
    await cloud.getByRole('button', { name: '云文件管理', exact: true }).click();
  });
  await check('无共享库用户可用云文件目录，管理权限由服务端拒绝', async () => {
    await invoke('manage_company_file', { sessionEpoch: epoch, id: asset.assetId, operation: 'restore', confirmName: '' }); await invoke('team_logout');
    const employee = await invoke('team_login', { server: clientBase, username: 'qa-worker', password: workerPassword }); epoch = employee.sessionEpoch; assert.equal(employee.teams.length, 0); assert.ok(employee.companyFiles);
    const catalog = await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }); assert.equal(catalog.files.length, 1); await invoke('download_company_file', { sessionEpoch: epoch, id: asset.assetId });
    await assert.rejects(invoke('manage_company_file', { sessionEpoch: epoch, id: asset.assetId, operation: 'recycle', confirmName: '' }), /权限/);
    assert.deepEqual(await fs.readFile((await invoke('get_library')).phrases.find(p => p.id === first).segments[0].blocks[0].path), bytes);
    assert.ok(worker);
  });
  await check('按用户名撤权使在途上传失效，重新授权后旧任务不能登记；查询不触发上传', async () => {
    const update = async (canUpload, canRestore) => { const user = (await api('GET', 'users')).find(u => u.id === worker); await api('PUT', `users/${worker}`, { companyFilePermissions: { canUpload, canRecycle: false, canRestore, expectedVersion: user.companyFilePermissions.version } }); return until(async () => { const status = await invoke('get_team_status'); return status.user?.companyFilePermissions.version > user.companyFilePermissions.version && status; }); };
    await update(true, false); const source = path.join(evidence, '用户授权资料.pdf'); await fs.writeFile(source, '授权期间的受控源副本');
    const task = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: source });
    holdUpload = true; const pending = invoke('upload_company_file', { sessionEpoch: epoch, id: task.id }).then(() => null, e => e);
    await until(() => heldUpload); await until(async () => (await fs.readdir(path.join(evidence, 'server', 'temp'))).length > 0);
    await update(false, false); assert.match(String(await pending), /授权|停止/);
    const uploads = uploadCount; await assert.rejects(invoke('query_company_upload', { sessionEpoch: epoch, id: task.id }), /未登记/); assert.equal(uploadCount, uploads);
    await cloud.getByRole('button', { name: '上传管理', exact: true }).click(); await cloud.getByText(task.name, { exact: true }).waitFor(); assert.equal(await cloud.getByRole('button', { name: '继续原任务', exact: true }).count(), 0);
    await update(true, false); releaseUpload?.(); await until(() => uploadSettled); assert.equal(uploadSettled, 409);
    assert.equal((await fetch(`${base}/api/company/files/${task.id}`, { headers: { Authorization: `Bearer ${workerToken}` } })).status, 404);
    const result = await invoke('upload_company_file', { sessionEpoch: epoch, id: task.id }); assert.equal(result.assetId, task.id); assert.equal(uploadCount, uploads + 1);
    await api('POST', `company/files/${task.id}/recycle`); await api('DELETE', `company/files/${task.id}`, { confirmName: task.name });
  });
  await check('调整恢复权限不打断上传，重开窗口可观察运行任务，清空保留待核对源副本', async () => {
    const source = path.join(evidence, '用户传输资料.docx'); await fs.writeFile(source, '运行中的任务'); const task = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: source });
    const staged = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: source });
    // 提前接住异步拒绝，布局断言失败后的清理不能用取消错误盖住原始失败。
    holdUpload = true; const pending = invoke('upload_company_file', { sessionEpoch: epoch, id: task.id }).then(result => ({ result }), error => ({ error })); await until(() => heldUpload);
    const user = (await api('GET', 'users')).find(u => u.id === worker), oldVersion = user.companyFilePermissions.uploadVersion;
    await api('PUT', `users/${worker}`, { companyFilePermissions: { canUpload: true, canRecycle: false, canRestore: true, expectedVersion: user.companyFilePermissions.version } });
    await until(async () => (await invoke('get_team_status')).user?.companyFilePermissions.canRestore); assert.equal((await invoke('get_team_status')).user.companyFilePermissions.uploadVersion, oldVersion);
    await invoke('plugin:window|close', {}, cloud); await until(() => cloud.isClosed()); cloud = await window('company-files', 'company-files'); await cloud.getByRole('button', { name: '上传管理', exact: true }).click();
    await cloud.getByRole('button', { name: '停止', exact: true }).waitFor(); await cloud.getByText('待核对', { exact: true }).waitFor();
    await cloud.getByRole('button', { name: '清空', exact: true }).click(); await cloud.getByRole('button', { name: '停止', exact: true }).waitFor();
    await verifyProgressLayout(); await cloud.screenshot({ path: path.join(evidence, 'company-upload.png') });
    await resizeCloud(760, 480); await verifyProgressLayout(); await cloud.screenshot({ path: path.join(evidence, 'company-upload-minimum.png') }); await resizeCloud(1000, 650);
    releaseUpload?.(); const settled = await pending; assert.ok(settled.result, String(settled.error)); assert.equal(settled.result.assetId, task.id);
    assert.ok((await invoke('get_company_transfers', { sessionEpoch: epoch })).some(r => r.assetId === staged.id && r.status === '待核对'));
    await invoke('upload_company_file', { sessionEpoch: epoch, id: staged.id });
    for (const id of [task.id, staged.id]) { await api('POST', `company/files/${id}/recycle`); await api('DELETE', `company/files/${id}`, { confirmName: task.name }); }
    const current = (await api('GET', 'users')).find(u => u.id === worker); await api('PUT', `users/${worker}`, { companyFilePermissions: { canUpload: false, canRecycle: false, canRestore: false, expectedVersion: current.companyFilePermissions.version } });
    await until(async () => !(await invoke('get_team_status')).user?.companyFilePermissions.canUpload);
    await cloud.getByRole('button', { name: '返回', exact: true }).click(); await cloud.getByRole('button', { name: '下载管理', exact: true }).click(); await cloud.getByText('已完成', { exact: true }).waitFor(); await verifyProgressLayout(); await cloud.screenshot({ path: path.join(evidence, 'company-download.png') }); await cloud.getByRole('button', { name: '返回', exact: true }).click();
  });
  await check('重新登录后共享库按需下载，两共享库使用同一云文件缓存', async () => {
    await invoke('team_logout'); const admin = await invoke('team_login', { server: clientBase, username: 'qa-admin', password }); epoch = admin.sessionEpoch;
    assert.equal((await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' })).files[0].cached, false);
    const downloads = downloadCount;
    await invoke('save_company_private_phrase', { phrase: draft('退出后复用', root), afterId: null, expectedRevision: null, libraryId, companyFiles: selections() });
    assert.equal(downloadCount, downloads, '已有独立个人副本时无需重新下载本体');
    const paths = [];
    for (const teamId of teams) {
      let view = await until(() => invoke('get_team_view', { teamId })); assert.equal(view.phrases[0].ready, false);
      await invoke('prepare_team_phrase', { id: view.phrases[0].id }); view = await invoke('get_team_view', { teamId }); paths.push(view.phrases[0].segments[0].blocks[0].path);
      assert.equal(view.phrases[0].ready, true); assert.deepEqual(await fs.readFile(paths.at(-1)), bytes);
      if (paths.length === 1) break;
    }
    const other = await invoke('get_team_view', { teamId: teams[1] }); assert.equal(other.phrases[0].ready, true); paths.push(other.phrases[0].segments[0].blocks[0].path); assert.equal(paths[0], paths[1]);
    assert.ok((await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' })).files[0].cached);
  });
  let restarted;
  await check('重启后发现受控上传源副本，继续原ID且不自动上传', async () => {
    const source = path.join(evidence, '重启资料.pdf'); await fs.writeFile(source, '重启前固化的附件');
    const task = await invoke('prepare_company_upload', { sessionEpoch: epoch, path: source }); const uploads = uploadCount;
    await fs.writeFile(source, '外部源后来改动');
    await invoke('window_action', { action: 'exit' }).catch(() => {}); await browser.close().catch(() => {}); if (app.exitCode === null && app.signalCode === null) app.kill(); await until(() => app.exitCode !== null || app.signalCode !== null);
    await startClient(); const status = await until(async () => { const status = await invoke('get_team_status'); return status.user?.username === 'qa-admin' && !status.offline && status; }); epoch = status.sessionEpoch;
    const catalog = await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }); assert.ok(catalog.uploads.some(upload => upload.id === task.id)); assert.equal(uploadCount, uploads);
    restarted = await invoke('upload_company_file', { sessionEpoch: epoch, id: task.id }); assert.equal(restarted.assetId, task.id); assert.equal(uploadCount, uploads + 1);
    const response = await fetch(`${base}/api/company/files/${task.id}/content`, { headers: { Authorization: `Bearer ${token}` } }); assert.equal(await response.text(), '重启前固化的附件');
  });
  await check('下载等待响应时可取消，旧结果不会登记为完整缓存', async () => {
    holdDownload = true; const pending = invoke('download_company_file', { sessionEpoch: epoch, id: restarted.assetId }).then(() => null, error => error);
    await until(() => heldDownload); await invoke('cancel_company_transfer', { sessionEpoch: epoch, id: restarted.assetId });
    const error = await Promise.race([pending, delay(5000).then(() => { throw new Error('取消等待响应的下载超时。'); })]); assert.match(String(error), /取消/); releaseDownload?.();
    const catalog = await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }); assert.equal(catalog.files.find(file => file.assetId === restarted.assetId).cached, false);
  });
  await check('切换账号使旧下载失效，迟到响应不能写入新账号缓存', async () => {
    holdDownload = true; const pending = invoke('download_company_file', { sessionEpoch: epoch, id: restarted.assetId }).then(() => null, error => error);
    await until(() => heldDownload); await invoke('team_logout'); const employee = await invoke('team_login', { server: clientBase, username: 'qa-worker', password: workerPassword }); epoch = employee.sessionEpoch;
    assert.ok(await pending); releaseDownload?.();
    const catalog = await invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' }); assert.equal(catalog.files.find(file => file.assetId === restarted.assetId).cached, false);
  });
  let accountCopy;
  await check('开启个人同步后保存独立附件，公司原件永久删除不影响本机或私人云副本', async () => {
    const preview = await invoke('preview_private_sync'); await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } });
    await until(async () => { const status = await invoke('get_private_status'); return status.enabled && status.pending === 0 && status.state === '已同步'; });
    const library = await invoke('get_library'); const selected = [{ segment: 0, block: 0, assetId: restarted.assetId, sessionEpoch: epoch }];
    const id = await invoke('save_company_private_phrase', { phrase: draft('独立个人同步附件', library.categories.find(c => c.kind === 'phrase' && !c.parentId).id), afterId: null, expectedRevision: null, libraryId: library.libraryId, companyFiles: selected });
    await until(async () => { const status = await invoke('get_private_status'); return status.pending === 0 && status.state === '已同步'; });
    accountCopy = (await invoke('get_library')).phrases.find(p => p.id === id).segments[0].blocks[0].path;
    const snapshot = await api('GET', 'me/private/snapshot', undefined, workerToken), record = snapshot.records.find(r => r.kind === 'phrase' && r.id === id); assert.ok(record, '个人同步已登记独立话术'); const privateAsset = record.value.segments[0].blocks[0].assetId;
    assert.notEqual(privateAsset, restarted.assetId); assert.equal(path.basename(accountCopy), '重启资料.pdf');
    await api('POST', `company/files/${restarted.assetId}/recycle`); await api('DELETE', `company/files/${restarted.assetId}`, { confirmName: restarted.name });
    assert.equal(await fs.readFile(accountCopy, 'utf8'), '重启前固化的附件');
    const response = await fetch(`${base}/api/me/private/assets/${privateAsset}`, { headers: { Authorization: `Bearer ${workerToken}` } }); assert.equal(response.status, 200); assert.equal(await response.text(), '重启前固化的附件');
  });
  await check('用户被停用后在途下载失效，私人独立附件仍保留', async () => {
    holdDownload = true; const pending = invoke('download_company_file', { sessionEpoch: epoch, id: asset.assetId }).then(() => null, error => error);
    await until(() => heldDownload); await api('PUT', `users/${worker}`, { disabled: true });
    await until(async () => !(await invoke('get_team_status')).user); assert.ok(await pending); releaseDownload?.();
    await assert.rejects(invoke('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' })); assert.equal(await fs.readFile(accountCopy, 'utf8'), '重启前固化的附件');
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, limitations: ['真实企业微信接收、安装升级、系统DPI、旧共享库客户端及局域网／HTTPS代理仍须单独验收；容器使用独立脚本验证'] }, null, 2)); console.log('报告：' + evidence);
} finally {
  await invoke('team_logout').catch(() => {}); await invoke('window_action', { action: 'exit' }).catch(() => {}); await browser?.close().catch(() => {}); app?.kill(); server.kill(); proxy.closeAllConnections(); await new Promise(r => proxy.close(r));
}
