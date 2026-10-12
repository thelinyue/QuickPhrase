import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 真实 WebView 和 Win32 窗口使用隔离数据库。宿主不访问剪贴板，发送回调仅记录身份。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'hover-docking-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port; await new Promise(resolve => server.close(resolve));
let app, host, browser, context, page, preview, mainHandle, barHandle, previewHandle, hostHandle, nonce = 0, lastNative;
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
const poll = async (check, label) => { for (let i = 0; i < 100; i++) { const value = await check(); if (value) return value; await delay(40); } throw new Error('等待超时：' + label); };
const read = async () => { try { lastNative = JSON.parse(await fs.readFile(output, 'utf8')); return lastNative; } catch { return null; } };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: [mainHandle, barHandle, previewHandle].filter(Boolean), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (error) { if (i >= 20 || !['EBUSY', 'EACCES', 'EPERM'].includes(error.code)) throw error; await delay(20); } }
  return poll(async () => { const state = await read(); return state?.nonce === request.nonce && state; }, '原生宿主响应');
};
const inspect = async () => { await control('inspect'); await delay(60); return poll(read, '读取原生窗口状态'); };
const windowOf = (state, handle) => state.windows.find(w => w.handle === handle);
const intersects = (a, b) => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
const assertDockOrder = state => {
  // Windows 可能在窗口之间保留辅助句柄，验证实际相对顺序而非必须紧邻。
  assert.ok(windowOf(state, hostHandle).zOrder < windowOf(state, mainHandle).zOrder, '目标先于侧栏显示');
  assert.ok(windowOf(state, mainHandle).zOrder < windowOf(state, barHandle).zOrder, '侧栏先于底栏显示');
};
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const check = async (name, fn) => { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); };
const row = id => page.locator('[data-row="' + id + '"]').first();
async function hover(locator, view = page, handle = mainHandle) {
  const box = await locator.boundingBox(), state = windowOf(await inspect(), handle), scale = state.dpi / 96;
  const x = box.x + Math.min(box.width - 8, 110), y = box.y + box.height / 2;
  await control('pointer', { x: Math.round(state.client.x + x * scale), y: Math.round(state.client.y + y * scale) });
  await view.mouse.move(x, y);
}
const popupVisible = async () => windowOf(await inspect(), previewHandle).visible;
async function focusHost() {
  await control('focus');
  if ((await inspect()).foreground !== hostHandle) {
    await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), '-Action', 'focus', '-Handle', String(hostHandle), '-X', '40', '-Y', '60'], { windowsHide: true });
  }
  await poll(async () => (await inspect()).foreground === hostHandle, '激活隔离吸附宿主');
}
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls], { windowsHide: true, stdio: 'ignore' });
  hostHandle = (await poll(read, '验收宿主')).hwnd;
  app = spawn(path.join(repo, 'src-tauri/target/debug/shanyu.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('隔离客户端提前退出'); return null; } }, '实际WebView');
  context = browser.contexts()[0]; context.setDefaultTimeout(6000);
  page = await poll(() => context.pages().find(p => !new URL(p.url()).hash), '主窗口页面');
  await page.locator('.main-window').waitFor();
  preview = await poll(() => context.pages().find(p => p.url().includes('#phrase-preview')), '预览窗口页面');
  await page.evaluate(() => {
    const original = window.fetch.bind(window); window.__hoverQA = []; window.__hoverEvents = [];
    window.fetch = (input, init) => { const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname === 'ipc.localhost' && url.pathname.includes('phrase_preview')) window.__hoverQA.push({ command: url.pathname, args: JSON.parse(init.body), time: performance.now() }); return original(input, init); };
    for (const type of ['pointermove', 'pointerdown', 'scroll', 'keydown']) window.addEventListener(type, event => {
      window.__hoverEvents.push({ type, x: event.clientX, y: event.clientY, key: event.key, target: event.target.className, time: performance.now() });
      if (window.__hoverEvents.length > 30) window.__hoverEvents.shift();
    }, true);
  });
  // 句柄只通过指定进程和固定标题读取，不查找或操作正式客户端。
  const nativeHandle = async title => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), '-ProcessId', String(app.pid), '-Title', title], { windowsHide: true, encoding: 'utf8' })).stdout).handle;
  mainHandle = await nativeHandle('闪语'); barHandle = await nativeHandle('常用短语 · 闪语'); previewHandle = await nativeHandle('话术预览 · 闪语');
  const initial = await invoke('get_library'); assert.equal(initial.dataDir, path.join(evidence, 'data'));
  // 点击分类的原生验收也需要真实分类，必须在几何专项开始前准备，避免点到新增按钮。
  const phraseRoot = initial.categories.find(category => category.kind === 'phrase' && !category.parentId)?.id
    ?? await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  await page.locator('.category-tabs button:not(.add-category)').first().waitFor();
  const settings = { ...initial.settings, startup: false, dock: false, autoHide: false, pinned: false, dockCommon: false, dockSearch: false, customHotkeys: false, searchHotkey: '8', windowHotkey: '9', commonHotkey: '0' };
  await poll(async () => { try { await invoke('save_settings', { settings }); return true; } catch (error) { if (!String(error).includes('服务尚未启动')) throw error; return false; } }, '原生设置服务');
  await check('吸附侧栏和底栏跟随目标层级，独立置顶设置不让它们覆盖任务栏', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    for (const pinned of [true, false]) {
      await invoke('save_settings', { settings: { ...settings, dock: true, pinned, dockCommon: true } });
      await control('move', { x: work.left + 600, y: work.top + 80, width: 640, height: work.bottom - work.top + 120 }); await focusHost(); await delay(180);
      const state = await inspect(), main = windowOf(state, mainHandle), bar = windowOf(state, barHandle), target = windowOf(state, hostHandle), taskbar = state.taskbar;
      snapshots.push({ name: 'target-layer-' + (pinned ? 'remembered-pin' : 'normal'), state });
      assert.equal(target.topmost, false);
      assert.equal(main.topmost, target.topmost, '吸附侧栏不能自行全局置顶');
      assert.equal(bar.topmost, target.topmost, '底栏与目标处于同一层级');
      assert.equal(main.frame.bottom, target.frame.bottom, '保留完整高度，允许任务栏遮挡');
      assert.equal(main.frame.left, target.frame.right, '侧栏固定贴在目标外侧');
      assert.equal(bar.frame.top, target.frame.bottom, '底栏不会向内回挪');
      assertDockOrder(state);
      assert.ok(taskbar.visible && taskbar.frame.bottom > taskbar.frame.top, '读取实际可见系统任务栏');
      const pointY = taskbar.frame.top + 4;
      assert.ok(pointY < main.frame.bottom && pointY < target.frame.bottom, '目标和侧栏都延伸到任务栏下面');
      for (const pointX of [target.frame.left + 40, main.frame.left + 12]) {
        const probed = await control('probe', { pointX, pointY });
        snapshots.push({ name: 'taskbar-covers-' + pointX, pointX, pointY, state: probed });
        assert.equal(probed.probedWindow, taskbar.handle, '任务栏对目标和侧栏保持相同遮挡关系');
      }
      await control('move', { x: work.left + 600, y: work.bottom - 540 + 4, width: 640, height: 540 }); await delay(180);
      const nearTaskbar = await inspect(), bottom = windowOf(nearTaskbar, barHandle);
      assert.equal(bottom.frame.top, windowOf(nearTaskbar, hostHandle).frame.bottom);
      const bottomProbe = await control('probe', { pointX: bottom.frame.left + 40, pointY });
      assert.equal(bottomProbe.probedWindow, taskbar.handle, '底栏进入任务栏范围时正常被遮挡');
      snapshots.push({ name: 'taskbar-covers-bottom-' + pinned, state: bottomProbe });
      await invoke('save_settings', { settings: { ...settings, dock: false, pinned, dockCommon: true } }); await delay(180);
      assert.equal(windowOf(await inspect(), mainHandle).topmost, pinned, '退出吸附后恢复用户独立窗口的置顶选择');
      assert.equal((await invoke('get_library')).settings.pinned, pinned, '自动跟随不改写置顶设置');
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    // 连续切换时界面线程等待后台设置响应，后台定位不能反向同步等待界面处理窗口消息。
    for (let i = 0; i < 12; i++) await Promise.all([
      invoke('save_settings', { settings: { ...settings, dock: i % 2 === 0, dockCommon: i % 3 !== 0 } }),
      invoke('get_library'),
    ]);
    await invoke('save_settings', { settings });
  });
  await check('普通或置顶目标外侧连续点击侧栏与底栏，短语可见且目标关系和输入焦点保留', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    await invoke('save_common_phrase', { phrase: { id: '', text: '底栏验证', position: 0 } });
    for (const pinned of [false, true]) {
      await invoke('save_settings', { settings: { ...settings, dock: true, pinned, dockCommon: true } });
      await control(pinned ? 'raise' : 'unpin');
      await control('move', { x: work.left + 360, y: work.top + 120, width: 640, height: 540 }); await focusHost(); await delay(180);
      await poll(async () => windowOf(await inspect(), barHandle).visible, '目标底部短语显示');
      const box = await page.locator('.category-tabs button:not(.add-category)').first().boundingBox(), main = windowOf(await inspect(), mainHandle), scale = main.dpi / 96;
      // 直接连续点击分类：既覆盖从聊天窗口进入侧栏，也覆盖前台句柄未变化的再次点击。
      for (let click = 0; click < 3; click++) {
        await control('click', { x: Math.round(main.client.x + (box.x + box.width / 2) * scale), y: Math.round(main.client.y + (box.y + box.height / 2) * scale) }); await delay(180);
        const state = await inspect(), bar = windowOf(state, barHandle), sidebar = windowOf(state, mainHandle);
        assert.equal(state.foreground, mainHandle, '实际点击后侧栏处于前台');
        assert.equal(bar.visible, true, '进入侧栏不能隐藏底部短语');
        assert.equal(sidebar.topmost, windowOf(state, hostHandle).topmost, '点击不改变目标层级');
        assert.equal(bar.topmost, sidebar.topmost, '底栏保留相同层级');
        assert.equal((await invoke('get_target')).hwnd, hostHandle, '侧栏操作保留聊天目标');
        const target = windowOf(state, hostHandle);
        assertDockOrder(state);
        assert.equal(sidebar.frame.left, target.frame.right); assert.equal(bar.frame.top, target.frame.bottom);
        assert.equal(intersects(sidebar.frame, target.frame), false); assert.equal(intersects(bar.frame, target.frame), false);
        const pointX = bar.frame.left + (bar.frame.right - bar.frame.left) / 2, pointY = bar.frame.top + 12;
        const probed = await control('probe', { pointX, pointY });
        snapshots.push({ name: 'sidebar-click-' + (pinned ? 'pinned' : 'normal') + '-' + click, pointX, pointY, state: probed });
        assert.equal(probed.probedWindow, barHandle, '点击侧栏后底栏实际可见');
        await control('click', { x: pointX, y: pointY }); await delay(180);
        await poll(async () => {
          const state = await inspect();
          return windowOf(state, hostHandle).zOrder < windowOf(state, mainHandle).zOrder && windowOf(state, mainHandle).zOrder < windowOf(state, barHandle).zOrder;
        }, '底栏点击后恢复目标、侧栏、底栏的顺序');
        const clickedBottom = await inspect();
        assert.equal(clickedBottom.foreground, barHandle, '跟随定位不夺取用户点击底栏后的输入焦点');
        assert.equal(windowOf(clickedBottom, barHandle).visible, true);
        assertDockOrder(clickedBottom);
        assert.equal((await invoke('get_target')).hwnd, hostHandle);
      }
    }
    await control('unpin');
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('侧栏超出屏幕顶部、任务栏和上下边缘时仍与目标等高，保留完整宽度', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    for (const side of ['right', 'left']) {
      await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side } }); await focusHost(); await delay(180);
      const fullWidth = windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left;
      for (const [name, y, height] of [['above', work.top - 120, 560], ['below', work.bottom - 160, 560], ['taller', work.top - 120, work.bottom - work.top + 240], ['outside-above', work.top - 800, 560], ['outside-below', work.bottom + 80, 560]]) {
        await control('move', { x: work.left + 600, y, width: 600, height }); await delay(180);
        const state = await inspect(), main = windowOf(state, mainHandle), target = windowOf(state, hostHandle);
        assert.equal(main.visible, true);
        assert.equal(main.frame.top, target.frame.top, side + '-' + name + ' 顶部对齐');
        assert.equal(main.frame.bottom, target.frame.bottom, side + '-' + name + ' 底部对齐');
        assert.equal(main.rect.right - main.rect.left, fullWidth, '屏幕裁切不能改变用户宽度');
        assert.equal(side === 'left' ? main.frame.right : main.frame.left, side === 'left' ? target.frame.left : target.frame.right);
        assert.equal(intersects(main.frame, target.frame), false, '纵向越界不向目标内部回挪');
        assert.equal(state.foreground, hostHandle, '尺寸跟随不抢焦点');
        snapshots.push({ name: 'full-size-' + side + '-' + name, main, target });
      }
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('底栏在目标横跨屏幕边缘或宽于工作区时保持目标全宽与完整高度', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    await invoke('save_settings', { settings: { ...settings, dockCommon: true, dockSearch: true } }); await focusHost();
    await poll(async () => windowOf(await inspect(), barHandle).visible, '显示底栏'); await delay(180);
    const bar = windowOf(await inspect(), barHandle), fullHeight = bar.frame.bottom - bar.frame.top;
    for (const [name, x, y, width] of [['left', work.left - 120, work.top + 120, 640], ['right', work.right - 200, work.top + 120, 640], ['wider', work.left - 120, work.top + 120, work.right - work.left + 240], ['bottom', work.left + 200, work.bottom - 160, 640]]) {
      await control('move', { x, y, width, height: 560 }); await delay(180);
      const state = await inspect(), current = windowOf(state, barHandle), target = windowOf(state, hostHandle);
      assert.equal(current.visible, true);
      assert.equal(current.frame.left, target.frame.left, name + ' 左端对齐');
      assert.equal(current.frame.right, target.frame.right, name + ' 右端对齐');
      assert.equal(current.frame.top, target.frame.bottom, name + ' 始终贴在目标下方');
      assert.equal(intersects(current.frame, target.frame), false, '底栏不能覆盖目标');
      assert.equal(current.frame.bottom - current.frame.top, fullHeight, '遮挡不能压缩底栏高度');
      assert.equal(state.foreground, hostHandle, '底栏跟随不抢焦点');
      snapshots.push({ name: 'full-size-bottom-' + name, bar: current, target });
    }
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  await check('四个方向连续移动和缩放时立即探测目标边缘，两处吸附面板不会覆盖目标内容', async () => {
    const original = windowOf(await inspect(), hostHandle).rect, work = windowOf(await inspect(), hostHandle).work;
    const moves = [[360, 120, 600, 480], [440, 120, 600, 480], [360, 120, 600, 480], [360, 200, 600, 480], [360, 120, 600, 480], [360, 120, 680, 560], [360, 120, 600, 480]];
    for (const side of ['left', 'right']) {
      for (const topmost of [false, true]) {
        await invoke('save_settings', { settings: { ...settings, dock: true, dockSide: side, dockCommon: true } });
        await control(topmost ? 'raise' : 'unpin'); await focusHost(); await delay(180);
        for (const [x, y, width, height] of moves) {
          const state = await control('move', { x: work.left + x, y: work.top + y, width, height, probeTarget: true });
          assert.equal(state.targetProbes.length, 4);
          for (const point of state.targetProbes) assert.equal(point.window, hostHandle, '移动后立即检查，目标内部边缘不能被旧面板位置覆盖');
          snapshots.push({ name: 'moving-' + side + '-' + topmost, state });
          await delay(100);
          const settled = await inspect(), main = windowOf(settled, mainHandle), bar = windowOf(settled, barHandle), target = windowOf(settled, hostHandle);
          assert.equal(side === 'left' ? main.frame.right : main.frame.left, side === 'left' ? target.frame.left : target.frame.right);
          assert.equal(bar.frame.top, target.frame.bottom);
          assertDockOrder(settled);
          assert.equal(intersects(main.frame, target.frame), false); assert.equal(intersects(bar.frame, target.frame), false);
          assert.equal(settled.foreground, hostHandle, '连续跟随不抢焦点');
        }
      }
    }
    await control('unpin');
    await invoke('save_settings', { settings: { ...settings, dockCommon: true } }); await delay(180);
    const onlyBottom = await inspect();
    assert.ok(windowOf(onlyBottom, hostHandle).zOrder < windowOf(onlyBottom, barHandle).zOrder, '没有侧栏时底栏位于目标后方');
    await control('move', { x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(180);
    await invoke('save_settings', { settings });
  });
  const images = await invoke('import_images', { paths: [path.join(repo, 'public/shanyu.png')] });
  const steps = '1、创建临时管理员账户，保留现有账户和数据。\n2、打开SSH服务，记录问题发生时间。\n3、提供识别码，保持网络连接。\n4、处理完成后删除临时账户并关闭SSH。\n';
  const savePhrase = (title, segments) => invoke('save_phrase', { phrase: { id: '', rootId: phraseRoot, groupId: null, title, color: '', background: '', shortcut: '', position: 0, segments }, afterId: null });
  const longId = await savePhrase('PRO-远程排查', [{ blocks: [{ kind: 'text', text: '您好，请按以下步骤准备远程排查：\n' }, ...images, { kind: 'text', text: steps.repeat(7) }, { kind: 'file', path: 'C:/仅作预览.txt', name: '排查说明.txt' }] }, { blocks: [{ kind: 'text', text: '第二段：问题处理完成后，请及时关闭临时访问。' }] }]);
  const shortId = await savePhrase('简短回复', [{ blocks: [{ kind: 'text', text: '您好，已收到您的反馈。\n我们会继续跟进。' }] }]);
  await row(shortId).waitFor(); await invoke('window_action', { action: 'show' });
  await control('move', { handle: mainHandle, x: 80, y: 120, width: 330, height: 600 }); await delay(160);
  await invoke('save_settings', { settings: { ...settings, pinned: true } }); await focusHost();
  await check('首次短文预览按客户区测量，正文没有空白膨胀或多余滚动条', async () => {
    await hover(row(shortId)); await poll(popupVisible, '首次短文预览');
    assert.ok(await preview.locator('.phrase-preview-body').evaluate(el => Math.abs(el.clientHeight - el.firstElementChild.scrollHeight) <= 2));
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭首次预览');
  });
  await check('快速掠过不弹出；停留后在鼠标右下显示轻量图文提示且不激活窗口', async () => {
    await hover(row(longId)); await delay(100); await control('pointer', { x: 40, y: 40 }); await page.mouse.move(-20, -20); await delay(450); assert.equal(await popupVisible(), false);
    await focusHost(); const foreground = (await inspect()).foreground;
    await hover(row(longId)); await poll(popupVisible, '长文预览');
    await preview.getByRole('region', { name: '话术正文预览' }).waitFor(); await preview.waitForFunction(() => [...document.images].every(img => img.naturalWidth > 0));
    const state = await inspect(), popup = windowOf(state, previewHandle), main = windowOf(state, mainHandle);
    assert.equal(state.foreground, foreground); assert.equal(popup.noActivate, true);
    const gap = Math.round(12 * popup.dpi / 96);
    assert.equal(popup.rect.left - state.cursor.x, gap, '按实际鼠标定位，而非整行右侧');
    assert.equal(popup.rect.top - state.cursor.y, gap);
    assert.equal(await preview.evaluate(() => innerWidth), 300);
    const appearance = await preview.locator('.phrase-preview-window').evaluate(el => ({ border: getComputedStyle(el).borderColor, header: getComputedStyle(el.querySelector('header')).backgroundColor, buttons: el.querySelectorAll('button').length, footers: el.querySelectorAll('footer').length }));
    assert.equal(appearance.border, 'rgb(23, 23, 23)'); assert.equal(appearance.header, 'rgba(0, 0, 0, 0)'); assert.equal(appearance.buttons, 0); assert.equal(appearance.footers, 0);
    assert.ok(popup.rect.right <= popup.work.right && popup.rect.bottom <= popup.work.bottom);
    assert.ok(popup.rect.bottom - popup.rect.top <= 480 * popup.dpi / 96 + 1);
    assert.equal(await preview.getByText('第 2 段', { exact: true }).count(), 1); assert.equal(await preview.getByText('排查说明.txt', { exact: true }).count(), 1);
    await preview.screenshot({ path: path.join(evidence, 'long-preview.png') }); snapshots.push({ name: 'long-preview', main, popup, foreground: state.foreground, foregroundPid: state.foregroundPid });
  });
  await check('鼠标进入浮层可滚动查看末尾；短文自适应高度，Esc关闭且不改变选择', async () => {
    await hover(preview.locator('.phrase-preview-body'), preview, previewHandle); await preview.mouse.wheel(0, 2000); await delay(800);
    assert.equal(await popupVisible(), true); assert.ok(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollTop > 0));
    await preview.screenshot({ path: path.join(evidence, 'long-preview-end.png') });
    const height = windowOf(await inspect(), previewHandle).rect.bottom - windowOf(await inspect(), previewHandle).rect.top;
    await hover(row(shortId)); await poll(async () => (await preview.locator('header').textContent()).includes('简短回复') && await popupVisible(), '短文预览');
    const popup = windowOf(await inspect(), previewHandle); assert.ok(popup.rect.bottom - popup.rect.top < height);
    assert.equal(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollHeight > el.clientHeight), false, '短文无需多余滚动条');
    assert.equal(await row(shortId).getAttribute('aria-selected'), 'false'); await preview.screenshot({ path: path.join(evidence, 'short-preview.png') });
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), 'Esc关闭');
    await hover(row(longId)); await poll(async () => (await preview.locator('header').textContent()).includes('PRO-远程排查') && await popupVisible(), '再次打开长文');
    assert.equal(await preview.locator('.phrase-preview-body').evaluate(el => el.scrollTop), 0, '切换话术从正文开头阅读');
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭长文');
  });
  await check('屏幕右下边缘仍从鼠标右下展开，不翻转或反向平移', async () => {
    const original = windowOf(await inspect(), mainHandle).rect;
    const work = windowOf(await inspect(), mainHandle).work;
    await control('move', { handle: mainHandle, x: work.right - 330, y: work.bottom - 400, width: 330, height: 400 }); await delay(160); await focusHost();
    await hover(row(longId)); await poll(popupVisible, '边缘预览');
    const state = await inspect(), popup = windowOf(state, previewHandle), gap = Math.round(12 * popup.dpi / 96);
    assert.equal(popup.rect.left - state.cursor.x, gap); assert.equal(popup.rect.top - state.cursor.y, gap);
    assert.ok(popup.rect.right > work.right && popup.rect.bottom > work.bottom, '保持固定方向，超出屏幕的部分由系统裁切');
    assert.equal(popup.noActivate, true); assert.equal(state.foreground, hostHandle);
    snapshots.push({ name: 'fixed-direction-at-edge', cursor: state.cursor, popup });
    await page.keyboard.press('Escape'); await poll(async () => !await popupVisible(), '关闭边缘预览');
    await control('move', { handle: mainHandle, x: original.left, y: original.top, width: original.right - original.left, height: original.bottom - original.top }); await delay(160);
  });
  await check('连续移动、缩放、拖动暂停和长时间闲置均保持完整宽度，旧自动隐藏设置不再收窄', async () => {
    await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: true } }); await focusHost(); await delay(200);
    assert.equal((await inspect()).foreground, hostHandle, '必须激活独立吸附宿主');
    const work = windowOf(await inspect(), hostHandle).work;
    await control('pointer', { x: work.left + 600, y: work.top + 200 });
    for (let i = 0; i < 12; i++) { await control('move', { x: work.left + 600 + i * 5, y: work.top + 120 + i * 3, width: 600, height: 500 + i }); await delay(i === 6 ? 1100 : 100); const main = windowOf(await inspect(), mainHandle); assert.ok(main.frame.right - main.frame.left >= 286 * main.dpi / 96); }
    const expanded = windowOf(await inspect(), mainHandle), fullWidth = expanded.rect.right - expanded.rect.left;
    await delay(3000); assert.equal(windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left, fullWidth);
    await control('move', { x: work.left + 680, y: work.top + 140, width: 650, height: 550 }); await delay(100);
    assert.equal(windowOf(await inspect(), mainHandle).rect.right - windowOf(await inspect(), mainHandle).rect.left, fullWidth);
    await delay(850);
    const restored = windowOf(await inspect(), mainHandle); assert.equal(restored.rect.right - restored.rect.left, fullWidth); assert.equal((await inspect()).foreground, hostHandle);
    snapshots.push({ name: 'drag-resize-restore', fullWidth, restored });
  });
  await check('左右屏幕边缘保持固定外侧和完整宽度，超出部分允许不可见且不覆盖目标', async () => {
    const work = windowOf(await inspect(), hostHandle).work;
    for (const side of ['right', 'left']) {
      await invoke('save_settings', { settings: { ...settings, dock: true, autoHide: false, dockSide: side } }); await focusHost();
      await control('move', { x: side === 'right' ? work.right - 670 : work.left + 30, y: work.top + 120, width: 640, height: 550 }); await delay(200);
      const state = await inspect(), main = windowOf(state, mainHandle), target = windowOf(state, hostHandle);
      assert.ok(main.frame.right - main.frame.left >= 286 * main.dpi / 96);
      assert.equal(side === 'right' ? main.frame.left : main.frame.right, side === 'right' ? target.frame.right : target.frame.left);
      assert.ok(side === 'right' ? main.frame.right > work.right : main.frame.left < work.left, '边缘空间不足时仍位于外侧');
      assert.equal(intersects(main.frame, target.frame), false);
      assert.ok(target.zOrder < main.zOrder);
      // 探测目标内部，外侧越界时不能通过回挪侧栏换取可见性。
      const point = { x: side === 'right' ? target.frame.right - 18 : target.frame.left + 18, y: target.frame.top + 200 };
      await control('pointer', point);
      const visible = await control('probe', { pointX: point.x, pointY: point.y }); assert.equal(visible.probedWindow, hostHandle, '贴边时目标内容不能被侧栏盖住');
      assert.ok([hostHandle, mainHandle].includes(visible.foreground));
      assert.equal(windowOf(visible, mainHandle).topmost, false, '普通目标贴边时侧栏仍在普通窗口层');
      assert.equal((await invoke('get_library')).settings.pinned, false, '跟随层级不能改写置顶设置');
      snapshots.push({ name: 'edge-' + side, main, target: windowOf(visible, hostHandle) }); await page.screenshot({ path: path.join(evidence, 'edge-' + side + '.png') });
    }
    await invoke('save_settings', { settings });
    await delay(150); assert.equal(windowOf(await inspect(), mainHandle).topmost, false, '关闭吸附恢复非置顶');
  });
} catch (error) {
  console.error(error); results.push({ name: 'failure', passed: false, error: String(error) }); process.exitCode = 1;
  snapshots.push({ name: 'failure-state', native: await inspect().catch(() => lastNative), requests: await page?.evaluate(() => window.__hoverQA).catch(() => null), events: await page?.evaluate(() => window.__hoverEvents).catch(() => null), previewText: await preview?.locator('body').innerText().catch(() => null) });
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (host && host.exitCode === null) await control('focus').then(() => control('close')).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  for (const process of [app, host]) if (process && process.exitCode === null) { await delay(150); if (process.exitCode === null) process.kill(); }
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ date: new Date().toISOString(), scope: 'full', results, snapshots, limits: ['实际企业微信、多显示器和150%/200%系统缩放未操作', '原生窗口与鼠标检查仅操作隔离宿主和隔离客户端，无剪贴板或聊天发送'] }, null, 2));
  console.log('悬停与吸附记录：' + path.join(evidence, 'report.json'));
}
