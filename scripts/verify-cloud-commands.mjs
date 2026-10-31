import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 真实 WebView2、Rust IPC 与隔离 Hub；不触碰用户安装、聊天目标或系统剪贴板。
const repo = process.cwd(), evidence = path.join(repo, '.build', `cloud-commands-${Date.now()}`);
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请设置 SHANYU_TEAM_SERVER_EXE。');
await fs.mkdir(evidence, { recursive: true });
const executable = path.join(evidence, 'quickphrase-hub.exe'); await fs.copyFile(process.env.SHANYU_TEAM_SERVER_EXE, executable);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function port() { const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); const value = server.address().port; await new Promise(resolve => server.close(resolve)); return value; }
async function until(work) { const end = Date.now() + 25000; while (Date.now() < end) { try { const result = await work(); if (result) return result; } catch {} await delay(80); } throw new Error('等待闪云中心联调超时。'); }
const checks = [], check = async (name, work) => { await work(); checks.push(name); console.log('通过：' + name); };
const initial = randomUUID(), password = randomUUID(), workerPassword = randomUUID();
const base = `http://127.0.0.1:${await port()}`;
const server = spawn(executable, [], { windowsHide: true, stdio: 'ignore', env: { ...process.env, QUICKPHRASE_DATA_DIR: path.join(evidence, 'server'), QUICKPHRASE_LISTEN: new URL(base).host, QUICKPHRASE_ADMIN_USERNAME: 'qa-admin', QUICKPHRASE_ADMIN_PASSWORD: initial } });
let offline = false, holdDetail = false, releaseDetail, held = false, holdUpload = false, uploadHeld = false, releaseUpload, token, worker, app, browser, page, cloud, epoch;
const proxy = http.createServer((req, res) => {
  if (offline) { res.writeHead(503); res.end(); return; }
  if (holdUpload && req.method === 'PUT' && /\/api\/company\/files\/[^/]+\/content/.test(req.url)) {
    holdUpload = false; const chunks = []; req.on('data', chunk => chunks.push(chunk));
    req.on('end', () => { uploadHeld = true; releaseUpload = () => {
      uploadHeld = false; const upstream = http.request(base + req.url, { method: req.method, headers: { ...req.headers, host: new URL(base).host } }, response => { if (!res.destroyed) { res.writeHead(response.statusCode, response.headers); response.pipe(res); } else response.resume(); });
      upstream.on('error', () => res.destroy()); upstream.end(Buffer.concat(chunks));
    }; }); return;
  }
  const hold = holdDetail && req.method === 'GET' && /^\/api\/cloud\/commands\/[^/?]+/.test(req.url);
  if (hold) holdDetail = false;
  const upstream = http.request(base + req.url, { method: req.method, headers: { ...req.headers, host: new URL(base).host } }, response => {
    if (!hold) { res.writeHead(response.statusCode, response.headers); response.pipe(res); return; }
    const chunks = []; response.on('data', chunk => chunks.push(chunk)); response.on('end', () => { held = true; releaseDetail = () => { held = false; if (!res.destroyed) { res.writeHead(response.statusCode, response.headers); res.end(Buffer.concat(chunks)); } }; });
  });
  upstream.on('error', () => { if (!res.destroyed) { res.writeHead(503); res.end(); } }); req.pipe(upstream);
});
await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
const clientBase = `http://127.0.0.1:${proxy.address().port}`;
async function api(method, route, data) {
  const response = await fetch(`${base}/api/${route}`, { method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) });
  assert.ok(response.ok, `接口拒绝请求：${response.status}`); return response.json();
}
const invoke = (command, args = {}, view = page) => view.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
async function login(username, secret) { const status = await invoke('team_login', { server: clientBase, username, password: secret }); epoch = status.sessionEpoch; return status; }
async function resize(width, height) {
  const scale = await cloud.evaluate(() => devicePixelRatio), outer = await invoke('plugin:window|outer_size', { label: 'company-files' }), inner = await invoke('plugin:window|inner_size', { label: 'company-files' });
  const driver = spawn('powershell.exe', ['-NoProfile', '-File', 'tests/native-driver.ps1', '-Action', 'move', '-ProcessId', String(app.pid), '-Title', '闪云隔离验收:company-files', '-X', '-20000', '-Y', '-20000', '-Width', String(Math.round(width * scale) + outer.width - inner.width), '-Height', String(Math.round(height * scale) + outer.height - inner.height)], { cwd: repo, windowsHide: true, stdio: 'ignore' });
  assert.equal(await new Promise(resolve => driver.once('exit', resolve)), 0);
  await until(() => cloud.evaluate(({ width, height }) => innerWidth === width && innerHeight === height, { width, height }));
}
const query = { q: '', category: null, recycled: false, offset: 0, limit: 10 };
let command;
const body = '  curl -fsSL "https://example.test/install?x=1&y=2" | sh \\\n  --path="$HOME"\n';
try {
  await until(() => fetch(`${base}/health`).then(r => r.ok));
  token = (await api('POST', 'login', { username: 'qa-admin', password: initial })).token;
  await api('PUT', 'password', { currentPassword: initial, password });
  worker = await api('POST', 'users', { username: 'qa-worker', password: workerPassword });
  const adminToken = token; token = (await api('POST', 'login', { username: 'qa-worker', password: workerPassword })).token;
  await api('PUT', 'password', { currentPassword: workerPassword, password: workerPassword + '-changed' }); token = adminToken;
  const debug = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'client'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debug}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debug}`)); page = await until(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash));
  await page.locator('.main-window').waitFor(); await login('qa-admin', password);
  await invoke('plugin:webview|create_webview_window', { options: { label: 'company-files', url: 'index.html#company-files', title: '闪云隔离验收:company-files', width: 1000, height: 650, x: -20000, y: -20000, focus: false, skipTaskbar: true, decorations: false } });
  cloud = await until(() => browser.contexts()[0].pages().find(p => p.url().includes('#company-files')));
  await cloud.getByText('闪云中心', { exact: true }).waitFor();
  await check('真实窗口默认云文件，命令表单通过Rust保存原始正文', async () => {
    assert.equal(await cloud.getByRole('tab', { name: '云文件', exact: true }).getAttribute('aria-selected'), 'true');
    await cloud.getByRole('tab', { name: '云命令', exact: true }).click(); await cloud.getByRole('button', { name: '新建命令', exact: true }).click();
    await cloud.getByRole('textbox', { name: '名称', exact: true }).fill('Linux工具安装'); await cloud.getByLabel('分类', { exact: true }).fill('Linux');
    await cloud.getByRole('textbox', { name: '说明', exact: true }).fill('核对来源与参数\n仅文本示例，不执行'); await cloud.getByRole('textbox', { name: '命令正文', exact: true }).fill(body);
    await cloud.getByRole('button', { name: '保存命令', exact: true }).click(); await until(async () => await cloud.getByRole('dialog').count() === 0);
    command = (await api('GET', 'cloud/commands')).commands[0]; assert.equal((await api('GET', `cloud/commands/${command.id}`)).body, body);
    await until(async () => await cloud.locator('.command-code code').textContent() === body);
  });
  await check('切换云文件与云命令不停止在途上传，完成后文件目录仍正常刷新', async () => {
    const source = path.join(evidence,'模块切换上传.txt'); await fs.writeFile(source,'isolated-transfer-bytes'); holdUpload = true;
    const records = await invoke('enqueue_company_uploads',{ sessionEpoch: epoch,paths: [source] },cloud); await until(() => uploadHeld);
    await cloud.getByRole('tab',{ name: '云文件',exact: true }).click(); await cloud.getByRole('tab',{ name: '云命令',exact: true }).click(); releaseUpload();
    await until(async () => (await invoke('get_company_transfers',{ sessionEpoch: epoch })).some(task => task.assetId === records[0].assetId && task.status === '已完成'));
    assert.equal(await cloud.getByRole('tab',{ name: '云命令',exact: true }).getAttribute('aria-selected'),'true');
    await cloud.getByRole('tab',{ name: '云文件',exact: true }).click(); await cloud.getByText('模块切换上传.txt',{ exact: true }).waitFor();
    await cloud.getByRole('tab',{ name: '云命令',exact: true }).click();
  });
  await check('默认与最小窗口、七套主题、长正文和滚动表单布局', async () => {
    const longBody = body + 'echo ' + 'x'.repeat(5000) + '\n' + body.repeat(30);
    command = await api('PUT',`cloud/commands/${command.id}`,{ name: command.name,category: command.category,description: command.description,body: longBody,expectedRevision: command.revision });
    await until(async () => await cloud.locator('.command-code code').textContent() === longBody);
    assert.ok(await cloud.locator('.command-code pre').evaluate(el => el.scrollWidth > el.clientWidth));
    await resize(1000, 650);
    for (const theme of ['default','green','blue','black','pink','lavender','orange']) {
      await cloud.evaluate(theme => { document.documentElement.dataset.theme = theme; }, theme);
      const layout = await cloud.locator('.commands-workspace').evaluate(el => ({ columns: getComputedStyle(el).gridTemplateColumns.split(' ').length, overflow: document.documentElement.scrollWidth > innerWidth }));
      assert.equal(layout.columns,2); assert.equal(layout.overflow,false);
    }
    await cloud.evaluate(() => { document.documentElement.dataset.theme = 'default'; });
    await cloud.screenshot({ path: path.join(evidence,'commands-default.png') }); await resize(760,480);
    await cloud.locator('.command-list-row').first().click(); await cloud.getByRole('button',{ name: '返回列表',exact: true }).waitFor();
    assert.ok(await cloud.locator('.command-detail').isVisible()); assert.equal(await cloud.locator('.commands-list').isVisible(),false);
    await cloud.screenshot({ path: path.join(evidence,'commands-minimum.png') }); await cloud.getByRole('button',{ name: '返回列表',exact: true }).click();
    assert.ok(await cloud.locator('.commands-list').isVisible()); await resize(1000,650);
  });
  await check('独立权限对话框按行授权，维护包含使用', async () => {
    await cloud.getByRole('button',{ name: '权限管理',exact: true }).click(); const dialog = cloud.getByRole('dialog',{ name: '权限管理',exact: true });
    await dialog.getByRole('tab',{ name: '云命令',exact: true }).click(); const use = dialog.getByRole('checkbox',{ name: 'qa-worker的使用命令权限' });
    assert.equal(await use.isChecked(),false); await dialog.getByRole('checkbox',{ name: 'qa-worker的维护命令权限' }).check(); assert.ok(await use.isChecked());
    await dialog.getByRole('button',{ name: '保存qa-worker权限' }).click(); await dialog.getByText('qa-worker 的云命令权限已保存。').waitFor();
    const user = (await api('GET','users')).find(user => user.id === worker); assert.deepEqual(user.cloudCommandPermissions,{ canUse: true,canManage: true,version: 1 });
    assert.equal(user.companyFilePermissions.canUpload,false); await dialog.getByRole('button',{ name: '关闭权限管理' }).click();
    await login('qa-worker',workerPassword + '-changed'); await cloud.getByRole('tab',{ name: '云命令',exact: true }).click(); await cloud.getByRole('button',{ name: '编辑',exact: true }).waitFor();
  });
  await check('复制IPC在线核对修订，冲突或撤权时在剪贴板写入前拒绝', async () => {
    const updated = await api('PUT',`cloud/commands/${command.id}`,{ name: command.name,category: command.category,description: command.description,body: 'echo updated',expectedRevision: command.revision });
    await assert.rejects(invoke('copy_cloud_command',{ sessionEpoch: epoch,id: command.id,expectedRevision: command.revision },cloud),/409|更新/);
    command = updated;
    await api('PUT',`users/${worker}`,{ cloudCommandPermissions: { canUse: false,canManage: false,expectedVersion: 1 } });
    await invoke('team_refresh'); await until(async () => await cloud.getByRole('tab',{ name: '云命令',exact: true }).count() === 0);
    await assert.rejects(invoke('get_cloud_commands',{ sessionEpoch: epoch,query },cloud),/权限/);
    await api('PUT',`users/${worker}`,{ cloudCommandPermissions: { canUse: true,canManage: true,expectedVersion: 2 } }); await invoke('team_refresh');
    await cloud.getByRole('tab',{ name: '云命令',exact: true }).click(); await cloud.getByRole('button',{ name: '编辑',exact: true }).waitFor();
  });
  await check('并发编辑保留草稿，断网清目录禁保存，重连核权及退出清草稿', async () => {
    await cloud.getByRole('button',{ name: '编辑',exact: true }).click(); await cloud.getByRole('textbox',{ name: '名称',exact: true }).fill('保留中的草稿');
    await api('PUT',`cloud/commands/${command.id}`,{ name: command.name,category: command.category,description: command.description,body: 'echo newer',expectedRevision: command.revision });
    await cloud.getByRole('button',{ name: '保存命令',exact: true }).click(); await cloud.getByRole('dialog').getByText(/409/).waitFor();
    assert.equal(await cloud.getByRole('textbox',{ name: '名称',exact: true }).inputValue(),'保留中的草稿');
    offline = true; await assert.rejects(invoke('team_refresh')); await until(async () => await cloud.getByRole('button',{ name: '保存命令',exact: true }).isDisabled());
    assert.equal(await cloud.locator('.command-code code').count(),0); assert.equal(await cloud.locator('.command-list-row').count(),0);
    await resize(760,480); const bounds = await cloud.getByRole('button',{ name: '保存命令',exact: true }).boundingBox(); assert.ok(bounds.y >= 0 && bounds.y + bounds.height <= 480);
    assert.ok(await cloud.locator('.cloud-command-editor').evaluate(dialog => {
      const scroll = dialog.querySelector('.command-editor-scroll'), actions = dialog.querySelector('.dialog-actions');
      return scroll.scrollHeight > scroll.clientHeight && scroll.getBoundingClientRect().bottom <= actions.getBoundingClientRect().top && dialog.scrollHeight <= dialog.clientHeight + 1;
    }));
    await cloud.screenshot({ path: path.join(evidence,'commands-editor-offline-minimum.png') });
    offline = false; await invoke('team_refresh'); await until(async () => await cloud.getByRole('button',{ name: '保存命令',exact: true }).isEnabled());
    assert.equal(await cloud.getByRole('textbox',{ name: '名称',exact: true }).inputValue(),'保留中的草稿');
    await invoke('team_logout'); await until(async () => await cloud.getByRole('dialog').count() === 0);
  });
  await check('切号使在途命令详情失效，迟到响应不重新显示旧正文', async () => {
    await login('qa-worker',workerPassword + '-changed'); await delay(500);
    holdDetail = true; const pending = invoke('get_cloud_command',{ sessionEpoch: epoch,id: command.id },cloud).then(() => null,error => error);
    await until(() => held); await invoke('team_logout'); releaseDetail(); assert.ok(await pending);
    await until(async () => await cloud.locator('.command-code code').count() === 0); assert.equal(await cloud.getByRole('tab',{ name: '云命令',exact: true }).count(),0);
  });
  await fs.writeFile(path.join(evidence,'report.json'),JSON.stringify({ checks,limitations: ['未改写系统剪贴板：成功复制仅由前端及Rust测试和源码检查覆盖，真实剪贴板仍待人工验证', '七套主题为真实WebView2 CSS外观检查；未代替Windows 10、多屏DPI、安装升级或生产部署验收'] },null,2));
  console.log('报告：' + evidence);
} finally {
  offline = false; releaseDetail?.(); releaseUpload?.(); if (page) { await invoke('team_logout').catch(() => {}); await invoke('window_action',{ action: 'exit' }).catch(() => {}); }
  await browser?.close().catch(() => {}); app?.kill(); server.kill(); proxy.closeAllConnections(); await new Promise(resolve => proxy.close(resolve));
}
