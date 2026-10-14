import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 使用复制的 Debug 程序和独立数据，验证真实 IPC、SQLite、跨卷重启及素材协议；不操作剪贴板或聊天。
const repo = process.cwd();
const evidence = path.join(repo, '.build', `data-directory-${Date.now()}`);
const source = path.join(evidence, '原数据 空格');
const crossVolume = path.join(os.tmpdir(), `QuickPhrase-data-${randomUUID()} 中文`);
const finalDirectory = path.join(evidence, '新数据 空格');
const executable = path.join(evidence, 'QuickPhrase.exe');
await fs.mkdir(evidence, { recursive: true });
await fs.copyFile(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), executable);
await fs.mkdir(crossVolume); await fs.mkdir(finalDirectory);
const listener = net.createServer();
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
const debugPort = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (work, accept = value => !!value, timeout = 30000) => {
  const start = Date.now(); let last;
  while (Date.now() - start < timeout) {
    try { const value = await work(); if (accept(value)) return value; } catch (error) { last = error; }
    await delay(100);
  }
  throw new Error(`等待目录验收结果超时${last ? `：${last}` : ''}`);
};
const checks = [];
const check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
let browser, context, page, processHandle;
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
function launch() {
  processHandle = spawn(executable, ['--autostart'], {
    cwd: repo, windowsHide: true, stdio: 'ignore', env: {
      ...process.env, SHANYU_TEST_DATA_DIR: source,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort}`,
    },
  });
}
async function connect(expected) {
  await browser?.close().catch(() => {});
  await until(async () => {
    // 重启时旧 WebView 端点可能短暂存活；限制单次连接等待，留出时间重连新实例。
    const candidate = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`, { timeout: 2000 });
    const ctx = candidate.contexts()[0];
    const main = ctx.pages().find(view => !new URL(view.url()).hash);
    if (!main) { await candidate.close(); return false; }
    try {
      await main.locator('.main-window').waitFor({ timeout: 2000 });
      const info = await invoke('get_data_directory', {}, main);
      if (expected && !samePath(info.path, expected)) { await candidate.close(); return false; }
      browser = candidate; context = ctx; page = main; return true;
    } catch { await candidate.close(); return false; }
  });
}
async function createWindow(label, route) {
  await invoke('plugin:webview|create_webview_window', { options: {
    label, url: `index.html#${route}`, title: '目录隔离验收', width: 594, height: 620,
    decorations: false, visible: false, focus: false, skipTaskbar: true, generalAutofillEnabled: false,
  } });
  return until(() => context.pages().find(view => !view.isClosed() && view.url().includes(`#${route}`)));
}
async function directorySettings() {
  let view = context.pages().find(view => !view.isClosed() && view.url().includes('#settings'));
  if (!view) view = await createWindow('settings', 'settings?tab=%E5%85%B3%E4%BA%8E');
  await view.locator('.settings-sidebar').waitFor();
  await view.getByRole('button', { name: '关于', exact: true }).click();
  await until(async () => view.getByRole('button', { name: '更换目录', exact: true }).isEnabled());
  return view;
}
async function choose(view, selected) {
  // 只替换系统选择器返回值；确认、迁移、重启和磁盘操作仍全部由正式业务接口执行。
  await view.evaluate(selected => {
    window.__qaDirectoryChoice = selected;
    if (window.__qaDirectoryFetch) return;
    window.__qaDirectoryFetch = true;
    const original = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input.url);
      if (url.hostname === 'ipc.localhost' && decodeURIComponent(url.pathname.slice(1)) === 'plugin:dialog|open') {
        return new Response(JSON.stringify(window.__qaDirectoryChoice), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
      }
      return original(input, init);
    };
  }, selected);
  await view.getByRole('button', { name: '更换目录', exact: true }).click();
}
let identity, phraseId, categoryId, imagePath, external;
try {
  launch(); await connect(source);
  await check('关于页显示根目录，选择及确认取消不修改定位', async () => {
    const view = await directorySettings();
    assert.ok(samePath(await view.locator('.setting-block > .data-path').innerText(), source));
    await choose(view, null);
    await until(() => view.getByRole('button', { name: '更换目录', exact: true }).isEnabled());
    assert.equal(await view.getByRole('dialog').count(), 0);
    await choose(view, crossVolume);
    await view.getByRole('dialog').waitFor();
    await view.getByRole('button', { name: '取消', exact: true }).click();
    assert.ok(samePath((await invoke('get_data_directory')).path, source));
  });
  await check('非空目录、原目录和编辑窗口由真实后端拒绝', async () => {
    await fs.writeFile(path.join(finalDirectory, '已有文件.txt'), 'keep');
    await assert.rejects(invoke('change_data_directory', { path: finalDirectory }), /空文件夹/);
    await assert.rejects(invoke('change_data_directory', { path: source }), /相同|包含/);
    const editor = await createWindow('editor', 'editor');
    await assert.rejects(invoke('change_data_directory', { path: crossVolume }), /编辑/);
    await invoke('plugin:window|close', { label: 'editor' }, editor).catch(error => {
      if (!/closed|destroyed/i.test(String(error))) throw error;
    });
    await until(() => editor.isClosed());
    await fs.unlink(path.join(finalDirectory, '已有文件.txt'));
  });
  await check('准备真实图片话术、外部文件关联及保留文件', async () => {
    const library = await invoke('get_library'); identity = library.libraryId;
    categoryId = await invoke('save_category', { libraryId: identity, category: { id: '', name: '目录验收', kind: 'phrase', parentId: null, position: 0, background: '' } });
    const [image] = await invoke('import_images', { libraryId: identity, paths: [path.join(repo, 'src-tauri/icons/icon.png')] }); imagePath = image.path;
    external = path.join(evidence, '外部源文件.txt'); await fs.writeFile(external, 'external-source');
    phraseId = await invoke('save_phrase', { libraryId: identity, phrase: {
      id: '', rootId: categoryId, groupId: null, title: '目录迁移验收', color: '', background: '', shortcut: '', position: 0,
      segments: [{ blocks: [{ kind: 'text', text: imagePath }, image, { kind: 'file', path: external, name: '附件' }] }],
    } });
    const fileCategory = await invoke('save_category', { category: { id: '', name: '文件关联', kind: 'file', parentId: null, position: 0, background: '' } });
    await invoke('add_files', { categoryId: fileCategory, paths: [external] });
    await fs.writeFile(path.join(source, '用户保留.txt'), 'unknown-file');
  });
  await check('界面确认触发跨卷完整迁移、真实自动重启及关于页结果', async () => {
    const view = await directorySettings(); await choose(view, crossVolume);
    await view.getByRole('dialog').waitFor();
    await view.screenshot({ path: path.join(evidence, 'confirmation.png') });
    await view.getByRole('button', { name: '迁移并重启', exact: true }).click().catch(error => {
      if (!/closed|disconnected|destroyed/i.test(String(error))) throw error;
    });
    await connect(crossVolume);
    const about = await directorySettings();
    await about.getByText('目录已更换，原数据已清理。', { exact: true }).waitFor();
    const library = await invoke('get_library'); assert.equal(library.libraryId, identity);
    const blocks = library.phrases.find(phrase => phrase.id === phraseId).segments[0].blocks;
    assert.equal(blocks[0].text, imagePath);
    assert.ok(samePath(blocks[1].path, path.join(crossVolume, 'images', path.basename(imagePath))));
    assert.equal(blocks[2].path, external); assert.equal(library.files[0].path, external);
    assert.ok(await page.evaluate(async filePath => {
      const image = new Image(); image.src = window.__TAURI_INTERNALS__.convertFileSrc(filePath, 'asset'); await image.decode(); return image.naturalWidth > 0;
    }, blocks[1].path));
    assert.equal(await fs.readFile(external, 'utf8'), 'external-source');
    assert.equal(await fs.readFile(path.join(source, '用户保留.txt'), 'utf8'), 'unknown-file');
    await assert.rejects(fs.access(path.join(source, 'shanyu.sqlite3')));
    await fs.access(path.join(source, 'data-directory.json'));
    await about.screenshot({ path: path.join(evidence, 'about.png') });
  });
  await check('再次更换目录继续保留身份与新写入内容，默认定位文件持续生效', async () => {
    await invoke('save_common_phrase', { libraryId: identity, phrase: { id: '', text: '迁移后保存', position: 0 } });
    await invoke('change_data_directory', { path: finalDirectory }).catch(error => {
      if (!/closed|disconnected|destroyed/i.test(String(error))) throw error;
    });
    await connect(finalDirectory);
    const library = await invoke('get_library'); assert.equal(library.libraryId, identity);
    assert.ok(library.commonPhrases.some(phrase => phrase.text === '迁移后保存'));
    const blocks = library.phrases.find(phrase => phrase.id === phraseId).segments[0].blocks;
    assert.ok(samePath(blocks[1].path, path.join(finalDirectory, 'images', path.basename(imagePath))));
    assert.equal(await fs.readFile(external, 'utf8'), 'external-source');
    await assert.rejects(fs.access(path.join(crossVolume, 'shanyu.sqlite3')));
    await invoke('window_action', { action: 'exit' }).catch(error => {
      if (!/closed|disconnected|destroyed/i.test(String(error))) throw error;
    });
    await browser.close(); browser = undefined;
    await until(async () => { try { await fetch(`http://127.0.0.1:${debugPort}/json/version`); return false; } catch { return true; } });
    launch(); await connect(finalDirectory);
    assert.equal((await invoke('get_library')).libraryId, identity);
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, source, crossVolume, finalDirectory,
    crossedVolume: path.parse(source).root.toLowerCase() !== path.parse(crossVolume).root.toLowerCase(),
    limits: ['真实 Windows Debug 客户端、WebView2、SQLite 和自动重启；系统目录选择器只替换返回值', '未操作真实聊天发送、剪贴板、实体键鼠、安装升级或系统 DPI', '空间耗尽和 ACL 权限拒绝未做实机注入'] }, null, 2));
  console.log('证据：' + evidence);
} catch (error) {
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2));
  throw error;
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  // 只处理本脚本直接创建的进程，自动重启后的实例通过 IPC 退出；不终止其他客户端。
  if (processHandle?.exitCode === null) processHandle.kill();
  // 若重启恰好关闭 CDP 连接，按本次复制程序的完整路径清理自己的重启子进程。
  const cleanup = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    "Get-CimInstance Win32_Process -Filter \"Name = 'QuickPhrase.exe'\" | Where-Object { $_.ExecutablePath -eq $env:QUICKPHRASE_DIRECTORY_QA_EXE } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }"],
  { windowsHide: true, stdio: 'ignore', env: { ...process.env, QUICKPHRASE_DIRECTORY_QA_EXE: executable } });
  await new Promise(resolve => cleanup.once('exit', resolve));
}
