import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { chromium } from 'playwright';

// 必须先通过关闭自定义快捷键的微软拼音组字对照；普通文本输入不能算作 IME 验收。
// 所有输入均为隔离宿主内的固定测试内容，不读取真实聊天窗口或组字正文。
const repo = process.cwd(), evidence = path.join(repo, '.build', `ime-${Date.now()}`);
const events = path.join(evidence, 'events.json'), controls = path.join(evidence, 'control.json');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms)), run = promisify(execFile);
const ps = args => run('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', ...args], { windowsHide: true, encoding: 'utf8' });
const native = async args => JSON.parse((await ps(['-File', path.join(repo, 'tests/native-driver.ps1'), ...args])).stdout);
const until = async (work, accept = Boolean) => { for (let i = 0; i < 100; i++) { const value = await work().catch(() => null); if (accept(value)) return value; await delay(80); } throw Error('等待微软拼音验收结果超时'); };
const receipt = () => fs.readFile(events, 'utf8').then(JSON.parse);
const control = async (command, fields = {}) => { await fs.writeFile(controls, JSON.stringify({ command, ...fields, nonce: Date.now() })); await delay(180); };
let host, app, browser, page, info;
const checks = [], samples = [];
const invoke = async (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const settings = async patch => { const library = await invoke('get_library'); await invoke('save_settings', { settings: { ...library.settings, ...patch } }); };
const key = (code, extra = []) => native(['-Action', 'key', '-Key', String(code), ...extra]);
const status = async label => { await control('ime-status'); const value = await receipt(); samples.push({ label, ...value }); await fs.writeFile(path.join(evidence, 'samples.json'), JSON.stringify(samples, null, 2)); return value.events.filter(e => e.kind === 'ime-status').at(-1)?.value; };
const check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
const focus = async () => { await native(['-Action', 'focus', '-Handle', String(info.hwnd)]); await control('focus'); assert.equal((await native(['-Handle', String(info.hwnd)])).foreground, info.hwnd); };
const prepare = async enabled => { await focus(); await key(27); await key(27); await settings({ customHotkeys: enabled }); await control('reset', { draft: '已有草稿：' }); await control('ime-on'); const state = await status('准备中文模式'); assert.equal(state?.open, true); assert.equal(state.conversion & 1, 1); };
try {
  await fs.mkdir(evidence, { recursive: true });
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await ps(['-File', path.join(repo, 'tests/build-dock-host.ps1'), '-OutputPath', fixture]);
  host = spawn(fixture, [events, controls, 'WeWorkWindow', 'ime'], { windowsHide: true, stdio: 'ignore' }); info = await until(receipt);
  const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve)); const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_USER_DATA_FOLDER: path.join(evidence, 'webview'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${port}`));
  page = await until(async () => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor();
  await until(async () => { await settings({ dock: false, dockCommon: false, edgeAutoHide: false, sequenceHotkeys: false, customHotkeys: false, globalPhraseHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' }); return true; });
  const rootId = await invoke('save_category', { category: { id: '', name: '输入法验收', kind: 'phrase', parentId: null, position: 0, background: '' } });
  await invoke('save_phrase', { phrase: { id: '', title: '测试话术', rootId, groupId: null, shortcut: 'Y+C', color: '', background: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '测试话术' }] }] } });
  await invoke('save_phrase', { phrase: { id: '', title: '数字触发话术', rootId, groupId: null, shortcut: '1+1', color: '', background: '', position: 1, segments: [{ blocks: [{ kind: 'text', text: '数字触发话术' }] }] } });
  await check('关闭自定义快捷键：微软拼音单按 Y 产生真实组字消息', async () => {
    await prepare(false); await key(89); const state = await status('关闭快捷键单按Y');
    await native(['-Handle', String(info.hwnd), '-Screenshot', path.join(evidence, 'ime-disabled-y.png')]);
    assert.ok(state.compositionBytes > 0, '关闭快捷键时也没有微软拼音组字，当前环境不能作为输入法验收');
    assert.ok((await receipt()).events.some(e => e.kind === 'ime-message' && e.value.message === 0x10f));
  });
  await check('开启自定义快捷键：单按 Y 不吞按下松开且正常组字', async () => {
    await prepare(true); await key(89); const state = await status('开启快捷键单按Y');
    assert.ok(state.compositionBytes > 0); assert.equal(await invoke('get_shortcut_candidates'), null);
  });
  await check('Y+C 后组字保留、候选可见、目标焦点不变，继续输入不丢字', async () => {
    await prepare(true); await native(['-Action', 'key', '-Keys', '89,67']);
    await until(() => invoke('get_shortcut_candidates'));
    const state = await status('完成Y+C'); assert.ok(state.compositionBytes >= 4); assert.equal(state.focused, true);
    const candidate = await native(['-ProcessId', String(app.pid), '-Title', '快捷键候选 · 闪语', '-Screenshot', path.join(evidence, 'ime-with-candidate.png')]);
    assert.equal(candidate.visible, true); assert.equal(candidate.foreground, info.hwnd);
    await native(['-Handle', String(info.hwnd), '-Screenshot', path.join(evidence, 'ime-host-with-candidate.png')]);
    await key(65); assert.ok((await status('继续输入A')).compositionBytes > state.compositionBytes);
    const log = await fs.readFile(path.join(evidence, 'data/shanyu.log'), 'utf8');
    for (const code of [89, 67]) for (const down of [true, false]) assert.ok(log.includes(`code=${code}，down=${down}，吞键=false`));
    assert.ok(!/隔离按键：code=(89|67)，down=(true|false)，吞键=true/.test(log));
  });
  await check('中文模式数字双键长按不会误选或重复投递', async () => {
    await prepare(true); await native(['-Action', 'key', '-Keys', '49,49', '-Repeats', '8', '-HoldMilliseconds', '180']);
    await until(() => invoke('get_shortcut_candidates'));
    const value = await receipt(); assert.equal(value.events.filter(e => e.kind === 'paste-text' || e.kind === 'send').length, 0);
    await status('数字双键长按');
  });
  // 独立对照：关闭快捷键后在新测试草稿中按标准 Ctrl+V，不重试任何产品投递。
  await prepare(false); await control('copy', { text: '标准粘贴对照' });
  await native(['-Action', 'key', '-Keys', '89,67']); await key(86, ['-Ctrl']);
  await status('组字期间标准扫描码Ctrl+V对照');
  await check('选择话术只投递一次且不主动清理触发内容', async () => {
    await prepare(true); await native(['-Action', 'key', '-Keys', '89,67']); await until(() => invoke('get_shortcut_candidates'));
    await key(49, ['-Repeats', '8', '-HoldMilliseconds', '180']);
    const result = await until(receipt, r => r?.events.some(e => e.kind === 'paste-text'));
    assert.equal(result.events.filter(e => e.kind === 'paste-text').length, 1);
    assert.equal(result.events.filter(e => e.kind === 'send').length, 0);
    const state = await status('选话术后'); assert.ok(state.compositionBytes >= 4 || result.draft.includes('yc'), '触发组字应保留或由输入法自身处理，不主动清理');
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, limits: ['真实微软拼音组件与隔离 WinForms 宿主，输入为标记 SendInput；实体键盘和真实企业微信仍需复核'] }, null, 2));
} catch (error) {
  await status('失败状态').catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2)); throw error;
} finally {
  const log = await fs.readFile(path.join(evidence, 'data/shanyu.log'), 'utf8').catch(() => '');
  const triggerEvents = log.split('\n').filter(line => /隔离按键：code=(89|67)，/.test(line));
  await fs.writeFile(path.join(evidence, 'trigger-events.json'), JSON.stringify({ events: triggerEvents, allPassedThrough: triggerEvents.length > 0 && triggerEvents.every(line => line.includes('吞键=false')) }, null, 2));
  // 产品保留最后投递剪贴板；测试仅认领本次隔离客户端写入的序号再由宿主恢复。
  if (app && host?.exitCode === null) { const clipboard = await native([]).catch(() => null); if (clipboard) await control('adopt', { ownerPid: app.pid, sequence: clipboard.clipboardSequence }).catch(() => {}); }
  await invoke('window_action', { action: 'exit' }).catch(() => {}); await browser?.close().catch(() => {});
  if (app?.exitCode === null) app.kill(); await control('close').catch(() => {}); if (host?.exitCode === null) host.kill(); console.log('证据：' + evidence);
}
