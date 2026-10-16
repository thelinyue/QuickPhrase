import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { chromium } from 'playwright';

// 真实 WebView2/IPC/签名/进程交接；签名夹具只启动写标记的测试程序，不执行产品安装器。
const repo = process.cwd(), root = path.join(repo, '.build', 'updater-' + Date.now());
const data = path.join(root, 'data'), fixture = path.join(data, 'update-fixture'), marker = path.join(root, 'installer-started.txt');
await fs.mkdir(fixture, { recursive: true });
const checks = [], delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let app, browser, main, settings, context, debugPort;
const executablePath = path.join(root, "QuickPhrase.exe");
await fs.copyFile(path.join(repo, "src-tauri/target/debug/QuickPhrase.exe"), executablePath);
const invoke = (command, args = {}, page = main) => page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
const click = locator => locator.evaluate(el => el.click());
async function until(fn, ms = 8000) { const start = Date.now(); while (Date.now() - start < ms) { const value = await fn(); if (value) return value; await delay(100); } throw new Error('等待验收状态超时'); }
async function check(label, fn) { await fn(); checks.push(label); console.log('通过：' + label); }
async function window(label, route) {
  await invoke('plugin:webview|create_webview_window', { options: { label, url: `index.html#${route}`, title: '更新隔离验收', width: 594, height: 620, visible: false, focus: false, skipTaskbar: true, decorations: false } });
  const page = await until(() => context.pages().find(p => p.url().includes('#' + route)));
  await page.waitForFunction(() => !!window.__TAURI_INTERNALS__); return page;
}
async function start() {
  if (!debugPort) { const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); debugPort = server.address().port; await new Promise(resolve => server.close(resolve)); }
  const port = debugPort;
  const env = { ...process.env, SHANYU_TEST_DATA_DIR: data, QUICKPHRASE_QA_INSTALL_MARKER: marker, WEBVIEW2_USER_DATA_FOLDER: path.join(root, 'webview-profile'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port };
  delete env.QUICKPHRASE_UPDATE_PRIVATE_KEY; delete env.QUICKPHRASE_UPDATE_KEY_PASSWORD;
  app = spawn(executablePath, ['--autostart'], { cwd: repo, env, windowsHide: true, stdio: 'ignore' });
  await fs.appendFile(path.join(root, 'processes.txt'), String(app.pid) + '\n');
  browser = await until(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port, { timeout: 1000 }); } catch { assert.equal(app.exitCode, null); return null; } }, 15000);
  context = browser.contexts()[0]; context.setDefaultTimeout(8000);
  main = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await main.locator('.main-window').waitFor();
  settings = await window('settings', 'settings?tab=%E5%85%B3%E4%BA%8E'); await settings.getByLabel('下载线路').waitFor();
}
async function stop() {
  if (app?.exitCode === null) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { await delay(1000); if (app.exitCode === null) app.kill(); await until(() => app.exitCode !== null, 10000); }
  await delay(500);
}
const installerName = 'QuickPhrase-Setup-9.9.9-x64.exe';
async function signedFixture(executable) {
  await fs.writeFile(path.join(fixture, installerName), executable);
  const manifest = Buffer.from(JSON.stringify({ version: '9.9.9', rid: 'win-x64', releaseChannel: 'stable', artifacts: [installerName], notes: '隔离更新验收\n保留本机数据；不执行真实产品安装器。' }));
  await fs.writeFile(path.join(fixture, 'release-manifest.json'), manifest);
  const hash = bytes => createHash('sha256').update(bytes).digest('hex');
  const sums = path.join(fixture, 'SHA256SUMS.txt');
  await fs.writeFile(sums, `${hash(executable)}  ${installerName}\n${hash(manifest)}  release-manifest.json\n`);
  assert.ok(process.env.QUICKPHRASE_UPDATE_PRIVATE_KEY && process.env.QUICKPHRASE_UPDATE_KEY_PASSWORD, '需通过环境变量提供签名密钥，禁止使用无签名夹具');
  const result = spawnSync(path.join(repo, 'src-tauri/target/debug/examples/update-sign.exe'), ['sign', sums, path.join(repo, 'src-tauri/update.pub')], { env: process.env, windowsHide: true, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
}
try {
  await start();
  await check('四条线路、默认自动检查、本机配置保存及无效地址拒绝', async () => {
    const status = await invoke('get_update_status'); assert.equal(status.config.automatic, true); assert.equal(status.config.route, 'default');
    assert.equal(await settings.getByLabel('下载线路').locator('option').count(), 4);
    await assert.rejects(invoke('save_update_config', { config: { automatic: true, route: 'custom', customProxy: 'http://example.com/' } }), /HTTPS/);
    await invoke('save_update_config', { config: { automatic: false, route: 'custom', customProxy: 'https://gh-proxy.com' } });
    const saved = await invoke('get_update_status'); assert.equal(saved.config.customProxy, 'https://gh-proxy.com/');
    await until(async () => (await settings.getByLabel('下载线路').inputValue()) === 'custom');
    await settings.screenshot({ path: path.join(root, 'settings-custom.png') });
    await invoke('plugin:window|close', {}, settings).catch(e => { if (!/closed/.test(String(e))) throw e; });
    await until(() => settings.isClosed()); settings = await window('settings', 'settings?tab=%E5%85%B3%E4%BA%8E');
    await until(async () => (await settings.getByLabel('下载线路').inputValue()) === 'custom');
  });
  await check('配置跨进程持久化，后台未在关闭自动检查后启动', async () => {
    await stop(); await start(); const status = await invoke('get_update_status');
    assert.equal(status.config.route, 'custom'); assert.equal(status.config.automatic, false); assert.equal(status.phase, 'idle');
  });
  await signedFixture(Buffer.from('not a PE executable'));
  await check('损坏签名拒绝加载，正确签名通过并通知主窗口', async () => {
    const file = path.join(fixture, 'SHA256SUMS.txt.minisig'), signature = await fs.readFile(file);
    await fs.writeFile(file, 'invalid signature'); await assert.rejects(invoke('qa_load_update'), /签名/);
    await fs.writeFile(file, signature); const status = await invoke('qa_load_update'); assert.equal(status.phase, 'ready');
    await settings.getByRole('button', { name: '退出并安装', exact: true }).waitFor();
    await main.getByRole('button', { name: /最新版本 v9.9.9/ }).waitFor();
    await settings.screenshot({ path: path.join(root, 'settings-ready.png') });
  });
  await check('编辑窗口阻止安装；关闭后启动失败保留应用并释放操作互斥', async () => {
    const editor = await window('editor', 'editor');
    await assert.rejects(invoke('install_update'), /关闭编辑/);
    assert.equal((await invoke('get_update_status')).manualInstallAvailable, false);
    await invoke('ensure_update_idle'); await invoke('plugin:window|close', {}, editor).catch(e => { if (!/closed/.test(String(e))) throw e; }); await until(() => editor.isClosed());
    await assert.rejects(invoke('install_update'), /无法启动安装器/); assert.equal(app.exitCode, null);
    await invoke('ensure_update_idle'); const failed = await invoke('get_update_status'); assert.equal(failed.phase, 'ready'); assert.equal(failed.manualInstallAvailable, true);
    await settings.getByRole('button', { name: '打开下载目录', exact: true }).waitFor();
    await invoke('open_update_download_directory');
  });
  await check('免安装转安装说明、稍后和取消确认不启动安装器', async () => {
    await click(settings.getByRole('button', { name: '稍后', exact: true })); assert.equal(app.exitCode, null);
    await click(settings.getByRole('button', { name: '退出并安装', exact: true }));
    const dialog = settings.getByRole('dialog'); await dialog.waitFor(); assert.match(await dialog.textContent(), /免安装版将转为安装版/);
    await settings.screenshot({ path: path.join(root, 'install-confirm.png') });
    await click(dialog.getByRole('button', { name: '取消', exact: true })); assert.equal(app.exitCode, null);
  });
  await check('真实启动已验证的隔离程序后退出，数据目录仍然保留', async () => {
    const source = path.join(root, 'installer-fixture.cs'), executable = path.join(root, 'installer-fixture.exe');
    await fs.writeFile(source, 'using System; using System.IO; using System.Reflection; class InstallerFixture { static void Main() { File.WriteAllText(Environment.GetEnvironmentVariable("QUICKPHRASE_QA_INSTALL_MARKER"), Assembly.GetExecutingAssembly().Location); } }');
    const csc = path.join(process.env.WINDIR, 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
    const built = spawnSync(csc, ['/nologo', '/target:winexe', '/out:' + executable, source], { windowsHide: true, encoding: 'utf8' }); assert.equal(built.status, 0, built.stdout + built.stderr);
    await signedFixture(await fs.readFile(executable)); await invoke('qa_load_update');
    await invoke('install_update').catch(e => { if (!/closed|disposed|destroyed|Target/.test(String(e))) throw e; });
    await until(() => app.exitCode !== null); await until(async () => { try { return await fs.readFile(marker, 'utf8'); } catch { return null; } });
    await fs.access(path.join(data, 'shanyu.sqlite3'));
    const installedPath = await fs.readFile(marker, 'utf8'), temporary = path.dirname(installedPath);
    assert.equal(path.basename(installedPath), installerName); assert.equal(path.dirname(temporary).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase()); assert.ok(path.basename(temporary).startsWith('QuickPhrase-update-qa-'));
    await delay(300); await fs.rm(temporary, { recursive: true });
  });
} finally {
  await stop(); await fs.writeFile(path.join(root, 'report.json'), JSON.stringify({ checks, limits: ['成功交接使用签名的无界面测试程序；真实 Inno 安装向导及用户实际升级仍需另行验收。', '未操作真实聊天、剪贴板或用户安装。'], screenshots: ['settings-custom.png', 'settings-ready.png', 'install-confirm.png'] }, null, 2));
  console.log('更新验收记录：' + root);
}
