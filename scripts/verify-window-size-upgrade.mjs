import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 独立安装身份、目录及 Debug 数据根；模拟旧版缺少尺寸文件，不接触用户安装或数据。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'size-upgrade-' + Date.now());
const data = path.join(evidence, 'data'), installed = path.join(evidence, 'installed');
const sizeFile = path.join(data, 'window-size.json'), results = [];
const run = promisify(execFile), delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const quote = value => "'" + value.replaceAll("'", "''") + "'";
const ps = command => run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], { windowsHide: true, maxBuffer: 4 * 1024 * 1024 });
const poll = async (fn, name) => { for (let i = 0; i < 200; i++) { const value = await fn(); if (value) return value; await delay(50); } throw new Error('等待超时：' + name); };
let app, browser, page, handle;
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const saved = async () => JSON.parse((await fs.readFile(sizeFile, 'utf8')).replace(/^\uFEFF/, ''));
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true })).stdout);
const client = async () => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(evidence, 'client.ps1'), String(handle)], { windowsHide: true })).stdout);
const start = async () => {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(installed, 'QuickPhrase.exe'), ['--autostart'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { return null; } }, '客户端');
  page = await poll(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash), '主页面');
  await page.locator('.main-window').waitFor();
  const { settings } = await invoke('get_library');
  await poll(async () => {
    try { await invoke('save_settings', { settings: { ...settings, dock: false, dockCommon: false, dockSearch: false, edgeAutoHide: false, freeResize: true, tray: false } }); return true; }
    catch (error) { if (String(error).includes('Windows服务尚未启动')) return false; throw error; }
  }, '原生服务就绪');
  await invoke('window_action', { action: 'show' }); await delay(500);
  handle = (await native(['-ProcessId', String(app.pid), '-Title', '闪语'])).handle;
};
const stop = async () => {
  if (app?.exitCode === null) { await invoke('window_action', { action: 'exit' }); await poll(() => app.exitCode !== null, '退出'); }
  await browser?.close().catch(() => {});
};
const version = JSON.parse(await fs.readFile(path.join(repo, 'package.json'), 'utf8')).version;
const setup = path.join(evidence, `QuickPhrase-Setup-${version}-x64.exe`);
const install = async name => {
  const args = `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /DIR="${installed}" /LOG="${path.join(evidence, name + '.log')}"`;
  await ps(`$p=Start-Process -FilePath ${quote(setup)} -ArgumentList ${quote(args)} -Wait -PassThru -WindowStyle Hidden; if($p.ExitCode -ne 0){throw "安装失败：$($p.ExitCode)"}`);
};
try {
  await fs.mkdir(data, { recursive: true });
  await fs.writeFile(path.join(evidence, 'client.ps1'), `param([long]$WindowHandle)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class SizeQA {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left,top,right,bottom; }
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
'@
[SizeQA]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$r=New-Object SizeQA+Rect
[SizeQA]::GetClientRect([IntPtr]$WindowHandle,[ref]$r) | Out-Null
$dpi=[SizeQA]::GetDpiForWindow([IntPtr]$WindowHandle)
@{width=$r.right*96.0/$dpi;height=$r.bottom*96.0/$dpi} | ConvertTo-Json -Compress
`);
  const compiler = process.argv[2] || path.join(process.env.LOCALAPPDATA, 'Programs/Inno Setup 6/ISCC.exe');
  await run(compiler, ['/Q', '/DAppId={{76815277-47BD-4086-97AC-6E393D562C15}', `/DAppVersion=${version}`, `/DReleaseRoot=${evidence}`, `/DSourceDir=${path.join(repo, 'src-tauri/target/debug')}`, `/DWindowSizeDirectory=${data}`, `/DAppGroup=闪语尺寸验收-${path.basename(evidence)}`, `/DStartupValue=SizeQA-${path.basename(evidence)}`, `/DLegacyStartupValue=SizeQAOld-${path.basename(evidence)}`, path.join(repo, 'installer/QuickPhrase.iss')], { windowsHide: true, maxBuffer: 4 * 1024 * 1024 });
  await install('install'); await start();
  await native(['-Action', 'move', '-Handle', String(handle), '-X', '600', '-Y', '100', '-Width', '472', '-Height', '768']);
  const expected = await client(); await delay(600); assert.deepEqual(await saved(), expected);
  // 删除隔离记录模拟未实现持久化的旧版本；当前进程尺寸不变，不会再次落盘。
  await fs.unlink(sizeFile); await delay(300); await assert.rejects(fs.access(sizeFile));
  await install('upgrade'); await poll(() => app.exitCode !== null, '安装器关闭隔离旧进程'); await browser.close();
  assert.deepEqual(await saved(), expected);
  const log = await fs.readFile(path.join(evidence, 'upgrade.log'), 'utf8');
  assert.ok(log.includes('已记录升级前窗口大小。') && log.includes('已保留升级前窗口大小。'), '必须由安装器迁移旧尺寸');
  await start(); assert.deepEqual(await client(), expected); await stop();
  results.push('运行中旧客户端缺少尺寸记录：安装器捕获、关闭、升级、重启后实际客户区尺寸一致');
  const before = await fs.readFile(sizeFile);
  await install('upgrade-existing'); assert.deepEqual(await fs.readFile(sizeFile), before);
  await start(); assert.deepEqual(await client(), expected); await stop();
  results.push('已有本机尺寸记录：再次升级不覆盖，重启后大小一致');
} catch (error) { console.error(error); process.exitCode = 1; }
finally {
  await stop().catch(() => {}); if (app?.exitCode === null) app.kill();
  const uninstaller = path.join(installed, 'unins000.exe');
  if (await fs.access(uninstaller).then(() => true, () => false)) {
    try {
      await ps(`$p=Start-Process -FilePath ${quote(uninstaller)} -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden; if($p.ExitCode -ne 0){throw '卸载失败'}`);
      await fs.access(sizeFile); results.push('卸载保留本机尺寸记录');
    } catch (error) { console.error(error); process.exitCode = 1; }
  }
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ results, passed: !process.exitCode, limits: ['隔离 Debug 客户端删除尺寸文件模拟旧版；未替换用户安装，未验收真实历史二进制及多屏DPI升级'] }, null, 2));
  results.forEach(result => console.log('通过：' + result)); console.log('验收记录：' + evidence);
}
