import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 专用宿主只被隔离 Debug 客户端识别；同一宿主改名后模拟其他程序，不碰正式客户端和聊天输入。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'background-following-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const executable = path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe');
const dataDir = path.join(evidence, 'data');
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
const neutralOutput = path.join(evidence, 'neutral.json'), neutralControls = path.join(evidence, 'neutral-control.json');
let app, browser, page, host, neutral, hostHandle, neutralHandle, handles = {}, nonce = 0, appEnv, launchOwner;
const poll = async (check, label) => { for (let i = 0; i < 100; i++) { const value = await check(); if (value) return value; await delay(40); } throw new Error('等待超时：' + label); };
const read = async filename => { try { return JSON.parse(await fs.readFile(filename, 'utf8')); } catch { return null; } };
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: [neutralHandle, ...Object.values(handles)].filter(Boolean), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (error) { if (i >= 20 || !['EBUSY', 'EACCES', 'EPERM'].includes(error.code)) throw error; await delay(20); } }
  return poll(async () => { const state = await read(output); return state?.nonce === request.nonce && state; }, '隔离宿主响应');
};
const inspect = async () => { await control('inspect'); await delay(100); return control('inspect'); };
const windowOf = (state, name) => state.windows.find(w => w.handle === handles[name]);
const focus = async handle => {
  await control('focus', { handle });
  if ((await inspect()).foreground !== handle) await native(['-Action', 'focus', '-Handle', String(handle), '-X', '40', '-Y', '60']);
  await poll(async () => (await inspect()).foreground === handle, '激活指定隔离宿主');
  launchOwner = handle;
};
async function authorizeLaunch(processId) {
  if (launchOwner === hostHandle) await control('allow-foreground', { processId });
  else if (launchOwner === neutralHandle) {
    const request = { command: 'allow-foreground', processId, nonce: ++nonce };
    await fs.writeFile(neutralControls, JSON.stringify(request)); await poll(async () => (await read(neutralOutput))?.nonce === request.nonce, '前台独立宿主授权隔离启动');
  }
}
const check = async (name, fn) => {
  try { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); }
  catch (error) { results.push({ name, passed: false, error: String(error) }); throw error; }
};
async function stopApp() {
  if (app?.exitCode === null) {
    await invoke('window_action', { action: 'exit' }).catch(() => {});
    await poll(() => app.exitCode !== null, '隔离客户端退出');
  }
  await browser?.close().catch(() => {}); browser = null; handles = {};
}
async function startApp(args) {
  const fresh = await fs.access(path.join(dataDir, 'shanyu.sqlite3')).then(() => false, () => true);
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  appEnv = { ...process.env, SHANYU_TEST_DATA_DIR: dataDir, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port };
  app = spawn(executable, args, { cwd: repo, windowsHide: true, stdio: 'ignore', env: appEnv });
  if (!args.includes('--autostart')) await authorizeLaunch(app.pid);
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); return null; } }, '客户端 WebView');
  const context = browser.contexts()[0]; context.setDefaultTimeout(5000);
  page = await poll(() => context.pages().find(p => !new URL(p.url()).hash), '主窗口页面');
  await page.locator('.main-window').waitFor();
  for (const [name, title] of [['main', '闪语'], ['bar', '常用短语 · 闪语'], ['preview', '话术预览 · 闪语']]) handles[name] = (await native(['-ProcessId', String(app.pid), '-Title', title])).handle;
  const library = await invoke('get_library'); assert.equal(library.dataDir, dataDir);
  if (fresh && !args.includes('--autostart')) {
    await check('新建本机库首次手动启动自动打开独立指引，展示后才保存状态', async () => {
      const guide = await poll(() => context.pages().find(p => p.url().includes('#usage-guide')), '首次使用指引');
      await guide.locator('.usage-guide-window').waitFor(); assert.equal(await guide.locator('.guide-steps li').count(), 3);
      handles.guide = (await native(['-ProcessId', String(app.pid), '-Title', '使用指引 · 闪语'])).handle; await focus(handles.guide);
      await poll(async () => !(await invoke('get_usage_guide_state')).pending, '已实际展示指引');
      await guide.screenshot({ path: path.join(evidence, 'usage-guide.png') });
      await guide.getByRole('button', { name: '开始使用', exact: true }).click(); await poll(() => guide.isClosed(), '开始使用关闭指引');
      delete handles.guide;
      await poll(async () => (await inspect()).foreground === handles.main, '开始使用恢复主窗口');
    });
  }
  await delay(150); return library.settings;
}
async function duplicate(args) {
  if (!args.includes('--autostart')) await authorizeLaunch(app.pid);
  const child = spawn(executable, args, { cwd: repo, windowsHide: true, stdio: 'ignore', env: appEnv });
  try { await poll(() => child.exitCode !== null, '重复启动退出'); assert.equal(child.exitCode, 0); }
  finally { if (child.exitCode === null) child.kill(); }
}
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe'), other = path.join(evidence, 'ShanyuIndependentHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  await fs.copyFile(fixture, other);
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' });
  hostHandle = (await poll(() => read(output), '吸附宿主')).hwnd;
  neutral = spawn(other, [neutralOutput, neutralControls], { windowsHide: true, stdio: 'ignore' });
  neutralHandle = (await poll(() => read(neutralOutput), '独立宿主')).hwnd;

  for (const args of [[], ['--autostart']]) {
    const name = args.length ? '开机启动' : '普通启动';
    await focus(hostHandle);
    let settings = await startApp(args);
    await check(name + (args.length ? '保持后台跟随且不抢焦点' : '主动显示并激活主窗口'), async () => {
      const state = await inspect(); assert.equal(windowOf(state, 'main').visible, true);
      assert.equal(state.foreground, args.length ? hostHandle : handles.main);
    });
    await focus(hostHandle);
    await check(name + '随目标完整显示，不抢焦点且三个常驻窗口均不进入任务栏', async () => {
      const state = await poll(async () => { const state = await inspect(); return windowOf(state, 'main').visible && windowOf(state, 'bar').visible && state; }, '窗口类型确认后自动跟随');
      assert.match(state.windows[0].className, /^WindowsForms10\.WeWorkWindow\.app\./);
      assert.equal(windowOf(state, 'main').visible, true); assert.equal(windowOf(state, 'bar').visible, true);
      assert.equal(windowOf(state, 'main').frame.left, state.windows[0].frame.right);
      assert.equal(windowOf(state, 'bar').frame.top, state.windows[0].frame.bottom);
      assert.ok(state.windows[0].zOrder < windowOf(state, 'main').zOrder); assert.ok(windowOf(state, 'main').zOrder < windowOf(state, 'bar').zOrder);
      assert.equal(state.foreground, hostHandle);
      assert.ok(windowOf(state, 'main').frame.right - windowOf(state, 'main').frame.left >= 286);
      for (const key of ['main', 'bar', 'preview']) { assert.equal(windowOf(state, key).toolWindow, true); assert.equal(windowOf(state, key).appWindow, false); }
      snapshots.push({ name: name + '-active', state });
    });
    await check(name + '主窗口内通讯录、搜索及无可用输入框页面持续吸附，不要求输入焦点', async () => {
      await control('focus-list');
      const unfocused = await inspect(); assert.equal(unfocused.inputFocused, false);
      assert.equal(windowOf(unfocused, 'main').visible, true); assert.equal(windowOf(unfocused, 'bar').visible, true);
      for (const pageState of ['none', 'contacts', 'search', 'hidden', 'disabled', 'readonly', 'password']) {
        await control('page', { page: pageState });
        // 跨过原检测结论的 750ms 生命周期，确保不是暂时沿用旧结果。
        await delay(800);
        const state = await inspect();
        assert.equal(windowOf(state, 'main').visible, true, pageState + ' 页面保留侧栏');
        assert.equal(windowOf(state, 'bar').visible, true, pageState + ' 页面保留底栏');
        assert.equal(state.foreground, hostHandle, '检测不能改变目标焦点');
        snapshots.push({ name: name + '-page-' + pageState, state });
      }
      await control('page', { page: 'chat' });
    });
    await check(name + '同进程登录、视频、搜索结果和未知窗口类型不吸附', async () => {
      for (const className of ['WeChatLogin', 'VideoPlayerWnd', 'SearchResultWindow2', 'WeWorkWindowExtra']) {
        const rejectedHandle = (await control('viewer', { className })).viewer;
        await focus(rejectedHandle);
        const state = await poll(async () => { const state = await inspect(); return !windowOf(state, 'main').visible && !windowOf(state, 'bar').visible && state; }, className + ' 类型隐藏两处面板');
        assert.equal(state.foregroundPid, host.pid);
        assert.ok(state.windows.find(window => window.handle === rejectedHandle).className.startsWith('WindowsForms10.' + className + '.app.'));
        snapshots.push({ name: name + '-rejected-' + className, state });
        await focus(hostHandle);
        await poll(async () => { const state = await inspect(); return windowOf(state, 'main').visible && windowOf(state, 'bar').visible; }, '返回主窗口恢复吸附');
      }
    });
    await check(name + '同进程图片窗口不吸附，独立聊天窗口和返回聊天时恢复', async () => {
      const viewerHandle = (await control('viewer', { className: 'ImagePreview' })).viewer;
      await focus(viewerHandle);
      const hidden = await poll(async () => { const state = await inspect(); return !windowOf(state, 'main').visible && !windowOf(state, 'bar').visible && state; }, '图片页面不借用后台聊天目标');
      assert.equal(hidden.foregroundPid, host.pid); snapshots.push({ name: name + '-viewer', state: hidden });
      await invoke('window_action', { action: 'show' });
      await poll(async () => windowOf(await inspect(), 'main').visible, '图片页仍可独立唤起闪语');
      assert.equal(windowOf(await inspect(), 'bar').visible, false);
      for (const className of ['WwStandaloneConversationWnd', 'CRMCustomerStandaloneWindow']) {
        const secondHandle = (await control('second-chat', { className })).secondChat;
        await focus(secondHandle);
        const second = await poll(async () => { const state = await inspect(); const target = state.windows.find(w => w.handle === secondHandle); return windowOf(state, 'bar').visible && windowOf(state, 'main').frame.left === target.frame.right && state; }, '独立聊天窗口类型确认后跟随新窗口');
        assert.ok(second.windows.find(window => window.handle === secondHandle).className.startsWith('WindowsForms10.' + className + '.app.'));
        assert.equal(windowOf(second, 'bar').frame.top, second.windows.find(w => w.handle === secondHandle).frame.bottom);
        await focus(handles.main); assert.equal(windowOf(await inspect(), 'bar').visible, true, '使用闪语保留已确认的独立聊天关系');
        snapshots.push({ name: name + '-standalone-' + className, state: second });
      }
      await focus(viewerHandle);
      await poll(async () => !windowOf(await inspect(), 'main').visible && !windowOf(await inspect(), 'bar').visible, '再次查看图片隐藏');
      await focus(hostHandle);
      await poll(async () => { const state = await inspect(); return windowOf(state, 'main').visible && windowOf(state, 'bar').visible && windowOf(state, 'main').frame.left === state.windows[0].frame.right; }, '返回原聊天窗口恢复');
    });
    await check(name + '多窗口返回目标时侧栏同步层级、不被普通窗口盖住且不改写置顶设置', async () => {
      await focus(hostHandle); let state = await inspect(); const main = windowOf(state, 'main');
      const x = main.frame.left + 100, y = main.frame.top + 100;
      await control('move', { handle: neutralHandle, x: main.frame.left - 20, y: main.frame.top, width: 600, height: 500 });
      await focus(neutralHandle);
      snapshots.push({ name: name + '-covered', state: await inspect() });
      await focus(hostHandle);
      state = await control('probe', { pointX: x, pointY: y });
      snapshots.push({ name: name + '-reactivated', state });
      assert.equal(state.probedWindow, handles.main); assert.equal(state.foreground, hostHandle); assert.equal(windowOf(state, 'main').topmost, false);
      assert.ok(state.windows[0].zOrder < windowOf(state, 'main').zOrder); assert.ok(windowOf(state, 'main').zOrder < windowOf(state, 'bar').zOrder);
      await control('raise'); await delay(150); let raised = await inspect(); assert.equal(windowOf(raised, 'main').topmost, true, '目标置顶时侧栏进入相同层级'); assert.equal(windowOf(raised, 'bar').topmost, true, '目标置顶时底栏进入相同层级');
      await control('unpin'); await delay(150); let lowered = await inspect(); assert.equal(windowOf(lowered, 'main').topmost, false); assert.equal(windowOf(lowered, 'bar').topmost, false, '目标取消置顶时底栏同步回到普通层级');
      assert.equal((await invoke('get_library')).settings.pinned, false);
      snapshots.push({ name: name + '-window-order', state });
      await control('move', { handle: neutralHandle, x: 700, y: 120, width: 600, height: 540 });
    });
    await check(name + '重复手动启动恢复隐藏及最小化，自启重复启动保留手动隐藏', async () => {
      await duplicate([]); await poll(async () => (await inspect()).foreground === handles.main, '重复手动启动激活主窗口');
      await invoke('window_action', { action: 'hide' }); await poll(async () => !windowOf(await inspect(), 'main').visible, '手动隐藏');
      await focus(hostHandle); await duplicate(['--autostart']);
      let state = await inspect(); assert.equal(windowOf(state, 'main').visible, false); assert.equal(windowOf(state, 'bar').visible, false); assert.equal(state.foreground, hostHandle);
      await duplicate([]); await poll(async () => windowOf(await inspect(), 'main').visible && (await inspect()).foreground === handles.main, '重复运行解除手动隐藏');
      settings = { ...settings, tray: false }; await invoke('save_settings', { settings }); await invoke('window_action', { action: 'minimize' });
      await poll(async () => windowOf(await inspect(), 'main').iconic, '重复启动前最小化'); await duplicate([]);
      await poll(async () => { const state = await inspect(); return windowOf(state, 'main').visible && !windowOf(state, 'main').iconic && state.foreground === handles.main; }, '重复启动还原最小化窗口');
      settings = { ...settings, tray: true }; await invoke('save_settings', { settings });
    });
    await stopApp(); await focus(neutralHandle); settings = await startApp(args);
    await check(name + '无目标及关闭吸附时仍遵守手动显示／自启后台规则，重启不再自动打开指引', async () => {
      let state = await inspect(); assert.equal(windowOf(state, 'main').visible, !args.length); assert.equal(windowOf(state, 'bar').visible, false); assert.equal(state.foreground, args.length ? neutralHandle : handles.main);
      assert.equal(browser.contexts()[0].pages().filter(p => p.url().includes('#usage-guide')).length, 0);
      settings = { ...settings, dock: false }; await invoke('save_settings', { settings });
      await stopApp(); await focus(neutralHandle); settings = await startApp(args);
      state = await inspect(); assert.equal(windowOf(state, 'main').visible, !args.length); assert.equal(state.foreground, args.length ? neutralHandle : handles.main);
      settings = { ...settings, dock: true }; await invoke('save_settings', { settings });
    });
    await check(name + '无目标手动独立唤起，返回目标后恢复跟随并延续编辑窗口的关系', async () => {
      await invoke('window_action', { action: 'show' }); await poll(async () => windowOf(await inspect(), 'main').visible, '独立显示');
      await focus(neutralHandle); assert.equal(windowOf(await inspect(), 'main').visible, true);
      await focus(hostHandle); const state = await inspect(); assert.equal(windowOf(state, 'main').frame.left, state.windows[0].frame.right);
      await page.getByRole('button', { name: '设置', exact: true }).click();
      const settingsPage = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置窗口');
      await settingsPage.locator('.settings-window').waitFor();
      handles.settings = (await native(['-ProcessId', String(app.pid), '-Title', '设置 · 闪语'])).handle;
      await focus(handles.settings);
      assert.equal(windowOf(await inspect(), 'main').visible, true);
      assert.equal(windowOf(await inspect(), 'settings').toolWindow, false, '主动打开的设置保留普通任务栏窗口');
      await settingsPage.getByRole('button', { name: '关闭窗口', exact: true }).click();
      delete handles.settings;
    });
    await check(name + '指引可关闭重开，最小尺寸可滚动，前台暂停业务快捷键且切回目标可练习', async () => {
      const originalSettings = settings, library = await invoke('get_library');
      const root = await invoke('save_category', { libraryId: library.libraryId, category: { id: '', name: '指引验收', kind: 'phrase', parentId: null, position: 0, background: '' } });
      await invoke('save_phrase', { libraryId: library.libraryId, phrase: { id: '', rootId: root, groupId: null, title: '指引候选验收', color: '', background: '', shortcut: 'Ctrl+F10', position: 0, segments: [{ blocks: [{ kind: 'text', text: '只打开候选，不投递' }] }] } });
      settings = { ...settings, customHotkeys: true, globalPhraseHotkeys: false }; await invoke('save_settings', { settings });
      await focus(hostHandle); await focus(handles.main);
      await page.getByRole('button', { name: '主菜单', exact: true }).click(); await page.getByRole('menuitem', { name: '使用指引', exact: true }).click();
      let guide = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#usage-guide')), '菜单重开指引'); await guide.locator('.usage-guide-window').waitFor();
      handles.guide = (await native(['-ProcessId', String(app.pid), '-Title', '使用指引 · 闪语'])).handle;
      handles.candidates = (await native(['-ProcessId', String(app.pid), '-Title', '快捷键候选 · 闪语'])).handle;
      const inner = await guide.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|inner_size', { label: 'usage-guide' })), outer = await guide.evaluate(() => window.__TAURI_INTERNALS__.invoke('plugin:window|outer_size', { label: 'usage-guide' })), scale = await guide.evaluate(() => devicePixelRatio);
      await native(['-Action', 'move', '-Handle', String(handles.guide), '-X', '100', '-Y', '100', '-Width', String(620 * scale + outer.width - inner.width), '-Height', String(440 * scale + outer.height - inner.height)]);
      await poll(() => guide.evaluate(() => innerWidth === 620 && innerHeight === 440), '指引最小尺寸');
      assert.ok(await guide.locator('.guide-content').evaluate(el => el.scrollHeight > el.clientHeight)); assert.ok(await guide.locator('.guide-footer').evaluate(el => el.getBoundingClientRect().bottom <= innerHeight));
      await guide.screenshot({ path: path.join(evidence, `usage-guide-minimum-${name}.png`) });
      await focus(handles.guide); await native(['-Action', 'key', '-Key', '121', '-Ctrl']); await delay(200); assert.equal(windowOf(await inspect(), 'candidates').visible, false);
      await focus(hostHandle); await native(['-Action', 'key', '-Key', '121', '-Ctrl']); await poll(async () => windowOf(await inspect(), 'candidates').visible, '指引打开时目标窗口仍可触发候选');
      await native(['-Action', 'key', '-Key', '27']); await poll(async () => !windowOf(await inspect(), 'candidates').visible, '取消候选，不执行投递');
      await guide.keyboard.press('Escape'); await poll(() => guide.isClosed(), 'Esc关闭指引'); delete handles.guide;
      await focus(handles.main); await page.getByRole('button', { name: '主菜单', exact: true }).click(); await page.getByRole('menuitem', { name: '使用指引', exact: true }).click();
      guide = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#usage-guide')), '再次打开指引'); await guide.getByRole('button', { name: '开始使用', exact: true }).click(); await poll(() => guide.isClosed(), '再次开始使用');
      await poll(async () => (await inspect()).foreground === handles.main, '开始使用回到主窗口');
      settings = originalSettings; await invoke('save_settings', { settings }); await invoke('delete_category', { id: root }); delete handles.candidates;
    });
    await check(name + '目标最小化、切换其他程序及还原同步隐藏和显示，手动隐藏保持有效', async () => {
      await focus(hostHandle); await control('minimize');
      await poll(async () => !windowOf(await inspect(), 'main').visible && !windowOf(await inspect(), 'bar').visible, '目标最小化隐藏');
      await control('restore'); await focus(hostHandle);
      assert.equal(windowOf(await inspect(), 'main').visible, true);
      await focus(neutralHandle); assert.equal(windowOf(await inspect(), 'main').visible, false);
      await focus(hostHandle); assert.equal(windowOf(await inspect(), 'main').visible, true);
      await invoke('window_action', { action: 'hide' }); await delay(100);
      await focus(neutralHandle); await focus(hostHandle); assert.equal(windowOf(await inspect(), 'main').visible, false);
      await invoke('window_action', { action: 'show' }); await poll(async () => windowOf(await inspect(), 'main').visible, '重新唤起');
    });
    await check(name + '快捷键恢复非托盘最小化，关闭吸附恢复独立尺寸且旧设置兼容保留', async () => {
      settings = { ...settings, tray: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0', autoHide: true, hideDelay: 1000 };
      await invoke('save_settings', { settings }); await invoke('window_action', { action: 'minimize' });
      await poll(async () => windowOf(await inspect(), 'main').iconic, '非托盘最小化');
      await native(['-Action', 'key', '-Key', '57', '-Alt']);
      await poll(async () => { const main = windowOf(await inspect(), 'main'); return main.visible && !main.iconic; }, '快捷键还原');
      settings = { ...settings, tray: true, dock: false }; await invoke('save_settings', { settings });
      const main = windowOf(await inspect(), 'main'); assert.equal(main.visible, true); assert.ok(main.rect.bottom - main.rect.top >= 700);
      const saved = (await invoke('get_library')).settings; assert.equal(saved.autoHide, true); assert.equal(saved.hideDelay, 1000);
      for (const freeResize of [false, true]) {
        settings = { ...settings, freeResize }; await invoke('save_settings', { settings });
        const main = windowOf(await inspect(), 'main'); assert.equal(main.toolWindow, true); assert.equal(main.appWindow, false);
      }
      settings = { ...settings, dock: true }; await invoke('save_settings', { settings });
    });
    await stopApp();
  }
  await check('已有数据库升级不自动打开指引，首次状态与内容无关', async () => {
    await run('python', ['-c', "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute(\"DELETE FROM sync_meta WHERE key='usageGuideShown'\"); c.commit(); c.close()", path.join(dataDir, 'shanyu.sqlite3')], { windowsHide: true });
    await focus(neutralHandle); await startApp([]); assert.equal((await invoke('get_usage_guide_state')).pending, false);
    assert.equal(browser.contexts()[0].pages().filter(p => p.url().includes('#usage-guide')).length, 0); await stopApp();
  });
} catch (error) {
  console.error(error);
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  snapshots.push({ name: 'failure', state: await inspect().catch(() => null) }); process.exitCode = 1;
} finally {
  await stopApp().catch(() => {});
  for (const child of [app, host, neutral]) if (child && child.exitCode === null) child.kill();
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ date: new Date().toISOString(), results, snapshots, limits: ['只操作隔离客户端与原生宿主，不操作系统剪贴板或聊天发送', '真实企业微信及多显示器DPI另需实机验收'] }, null, 2));
  console.log('验收记录：' + evidence);
}
