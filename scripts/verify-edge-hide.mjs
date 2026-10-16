import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

// 仅使用隔离 Debug 客户端和宿主；真实拖动、悬停，不触碰聊天内容、剪贴板或用户配置。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'edge-hide-' + Date.now());
await fs.mkdir(evidence, { recursive: true });
const run = promisify(execFile), results = [], snapshots = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = path.join(evidence, 'native.json'), controls = path.join(evidence, 'control.json');
let app, host, browser, page, handles = {}, nonce = 0, crossMonitorVerified = false;
const read = async () => { try { return JSON.parse(await fs.readFile(output, 'utf8')); } catch { return null; } };
const poll = async (fn, label) => { for (let i = 0; i < 160; i++) { const value = await fn(); if (value) return value; await delay(35); } throw new Error('等待超时：' + label); };
const control = async (command, args = {}) => {
  const request = { command, nonce: ++nonce, handles: Object.values(handles), ...args };
  const temporary = controls + '.tmp'; await fs.writeFile(temporary, JSON.stringify(request));
  for (let i = 0; ; i++) { try { await fs.rename(temporary, controls); break; } catch (e) { if (i > 20 || !['EACCES', 'EPERM', 'EBUSY'].includes(e.code)) throw e; await delay(20); } }
  return poll(async () => { const s = await read(); if (s?.nonce === request.nonce && ['pointer', 'drag'].includes(command)) assert.equal(s.pointerError, 0, '系统鼠标操作失败'); return s?.nonce === request.nonce && s; }, command);
};
const inspect = () => control('inspect');
const main = state => state.windows.find(w => w.handle === handles.main);
const invoke = (cmd, args = {}) => page.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const native = async args => JSON.parse((await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/native-driver.ps1'), ...args], { windowsHide: true, encoding: 'utf8' })).stdout);
const filter = process.argv.find(arg => arg.startsWith('--filter='))?.slice(9);
const check = async (name, fn) => { if (filter && !name.includes(filter)) return; try { await fn(); results.push({ name, passed: true }); console.log('通过：' + name); } catch (e) { results.push({ name, passed: false, error: String(e) }); throw e; } };
const save = async patch => { const { settings } = await invoke('get_library'); await invoke('save_settings', { settings: { ...settings, ...patch } }); };
const away = async () => { const w = main(await inspect()).work; await control('pointer', { x: Math.round((w.left + w.right) / 2), y: w.top + 30 }); };
const atEdge = async side => {
  const m = main(await inspect());
  await control('pointer', { x: side === 'right' ? m.work.right - 1 : m.work.left + 1, y: m.rect.top + 10 });
};
const hidden = async side => poll(async () => {
  const m = main(await inspect()); const strip = Math.round(2 * m.dpi / 96);
  return Math.abs((side === 'right' ? m.work.right - m.clip.left : m.clip.right - m.work.left) - strip) <= 1 && m;
}, side + '收起');
const expanded = async side => poll(async () => {
  const m = main(await inspect()); return m.visible && !m.iconic && !m.clipped && Math.abs(side === 'right' ? m.frame.right - m.work.right : m.frame.left - m.work.left) <= 1 && m;
}, side + '展开');
const drag = async (side, overshoot = 0) => {
  // 外部点击用例结束后宿主持有前台；模拟用户主动唤起时 Shell 授予的激活权限。
  await control('allow-foreground', { processId: app.pid });
  await invoke('window_action', { action: 'show' }); await delay(250);
  await control('raise', { handle: handles.main });
  await control('focus', { handle: handles.main });
  const m = main(await inspect()), box = await page.locator('.main-titlebar .titlebar-spacer').boundingBox(), scale = m.dpi / 96;
  const fromX = Math.round(m.client.x + (box.x + box.width / 2) * scale), fromY = Math.round(m.client.y + (box.y + box.height / 2) * scale);
  const dx = side === 'right' ? m.work.right - m.frame.right + overshoot : m.work.left - m.frame.left - overshoot;
  await control('pointer', { x: fromX, y: fromY });
  assert.equal((await inspect()).windowAtCursor, handles.main, '拖动起点必须命中隔离主窗口');
  await control('drag', { handle: handles.main, fromX, fromY, x: fromX + dx, y: fromY });
  snapshots.push({ name: 'drag-' + side, overshoot, state: await inspect() });
  await delay(180); await away();
};
async function startApp() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'data'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port } });
  browser = await poll(async () => { try { return await chromium.connectOverCDP('http://127.0.0.1:' + port); } catch { if (app.exitCode !== null) throw new Error('客户端提前退出'); return null; } }, 'WebView');
  page = await poll(() => browser.contexts()[0].pages().find(p => !new URL(p.url()).hash), '主页面'); await page.locator('.main-window').waitFor();
  handles.main = (await native(['-ProcessId', String(app.pid), '-Title', '闪语'])).handle;
  await check('首次显示全程位于右侧，不先显示左侧再跳转', async () => {
    await control('trace-start', { handle: handles.main });
    await invoke('window_action', { action: 'show' }); await delay(600);
    const s = await control('trace-stop'), m = main(s);
    const visible = s.trace.filter(sample => sample.visible);
    snapshots.push({ name: 'first-show', trace: s.trace });
    assert.ok(visible.length > 0);
    for (const sample of visible) assert.ok(Math.abs(m.work.right - sample.rect.right - 12 * m.dpi / 96) <= 1, '首次可见帧必须已经定位到右侧');
  });
}
try {
  const fixture = path.join(evidence, 'ShanyuDockHost.exe');
  await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'tests/build-hover-dock-host.ps1'), '-OutputPath', fixture], { windowsHide: true });
  host = spawn(fixture, [output, controls, '--dpi-unaware-target'], { windowsHide: true, stdio: 'ignore' }); await poll(read, '宿主');
  // 默认尺寸只在尚未吸附时核对；先最小化宿主，避免自动跟随已更新初始宽高。
  await control('minimize');
  await startApp();
  await save({ dock: false, dockCommon: false, dockSearch: false, pinned: true }); await invoke('window_action', { action: 'show' }); await delay(300);
  await check('默认可见尺寸306×720DIP，首次独立显示保留12DIP边距', async () => {
    const { settings } = await invoke('get_library'); assert.equal(settings.edgeAutoHide, true); assert.equal(settings.edgeRevealDelay, 500);
    const m = main(await inspect()); snapshots.push({ name: 'initial', m }); assert.ok(m.work.right - m.frame.right >= 10 * m.dpi / 96);
    assert.ok(Math.abs((m.frame.right - m.frame.left) * 96 / m.dpi - 306) <= 1);
    assert.ok(Math.abs((m.frame.bottom - m.frame.top) * 96 / m.dpi - 720) <= 1);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(evidence, 'default-size.png') });
  });
  await check('标题栏拖划和全选不产生文字高亮，搜索输入仍可正常选择', async () => {
    const bar = page.locator('.main-titlebar'), box = await bar.boundingBox();
    await page.mouse.move(box.x + 140, box.y + 20); await page.mouse.down();
    await page.mouse.move(box.x + 10, box.y + 20, { steps: 12 }); await page.mouse.up();
    assert.equal(await page.evaluate(() => window.getSelection().toString()), '');
    const selection = await bar.evaluate(el => {
      const selection = window.getSelection(), range = document.createRange();
      range.selectNodeContents(el); selection.removeAllRanges(); selection.addRange(range);
      const text = selection.toString(); selection.removeAllRanges(); return text;
    });
    assert.equal(selection, '');
    const input = page.getByRole('textbox', { name: '搜索话术、本地文件', exact: true });
    await input.fill('选择验收');
    assert.equal(await input.evaluate(el => { el.select(); return el.value.slice(el.selectionStart, el.selectionEnd); }), '选择验收');
    await input.fill(''); await page.keyboard.press('Escape');
    await page.screenshot({ path: path.join(evidence, 'titlebar-no-selection.png') });
  });
  const monitors = (await inspect()).monitors;
  if (monitors.length < 2) {
    results.push({ name: '跨显示器拖动保持逻辑尺寸', skipped: true, reason: '当前仅检测到一台显示器' });
    console.log('跳过：跨显示器拖动（当前仅一台显示器）');
  } else {
    await check('跨显示器往返拖动保持逻辑尺寸，像素尺寸随DPI变化', async () => {
      await save({ edgeAutoHide: false });
      const original = main(await inspect());
      const targets = [...monitors.filter(m => m.work.left !== original.work.left || m.work.top !== original.work.top), { work: original.work }];
      for (const target of targets) {
        const before = main(await inspect()), box = await page.locator('.main-titlebar .titlebar-spacer').boundingBox();
        const fromX = Math.round(before.client.x + (box.x + box.width / 2) * before.dpi / 96);
        const fromY = Math.round(before.client.y + (box.y + box.height / 2) * before.dpi / 96);
        await control('pointer', { x: fromX, y: fromY });
        assert.equal((await inspect()).windowAtCursor, handles.main);
        await control('drag', { handle: handles.main, fromX, fromY, x: Math.round((target.work.left + target.work.right) / 2), y: target.work.top + 60 });
        const after = await poll(async () => { const m = main(await inspect()); return m.work.left === target.work.left && m.work.top === target.work.top && Math.abs((m.frame.right - m.frame.left) * 96 / m.dpi - 306) <= 2 && Math.abs((m.frame.bottom - m.frame.top) * 96 / m.dpi - 720) <= 2 && m; }, '跨屏尺寸稳定');
        snapshots.push({ name: 'cross-monitor-size', before, after });
      }
      crossMonitorVerified = true;
      await save({ edgeAutoHide: true });
    });
  }
  await control('restore');
  await check('吸附禁止拖动和调整高度，只允许调整宽度，取消后恢复自由调整', async () => {
    for (const dockSide of ['right', 'left']) {
      await save({ dock: true, edgeAutoHide: false, freeResize: true, dockSide });
      await control('click', { x: 750, y: 200 });
      await poll(async () => { const s = await inspect(), m = main(s); return (dockSide === 'right' ? m.frame.left === s.windows[0].frame.right : m.frame.right === s.windows[0].frame.left) && m.frame.top === s.windows[0].frame.top; }, '吸附就绪');
      const attempt = async (name, from, delta, mode) => {
        const before = main(await inspect());
        await control('trace-start', { handle: handles.main });
        await control('drag', { handle: handles.main, fromX: from.x, fromY: from.y, x: from.x + delta.x, y: from.y + delta.y });
        await delay(180); const s = await control('trace-stop'), after = main(s);
        snapshots.push({ name, trace: s.trace, before, after });
        for (const sample of s.trace.filter(v => v.visible)) {
          if (mode === 'locked') assert.deepEqual(sample.rect, before.rect, name + '不能发生瞬时移动再复位');
          if (mode === 'width') { assert.equal(sample.rect.top, before.rect.top); assert.equal(sample.rect.bottom, before.rect.bottom); }
        }
        if (mode === 'width') assert.ok(after.rect.right - after.rect.left > before.rect.right - before.rect.left + 30, '横向调整宽度必须生效');
        return after;
      };
      for (const selector of ['.main-titlebar .titlebar-spacer', '.main-footer .footer-spacer']) {
        const m = main(await inspect()), b = await page.locator(selector).boundingBox();
        await attempt(selector, { x: Math.round(m.client.x + (b.x + b.width / 2) * m.dpi / 96), y: Math.round(m.client.y + (b.y + b.height / 2) * m.dpi / 96) }, { x: -80, y: 60 }, 'locked');
      }
      let m = main(await inspect());
      await attempt('上边不能调整高度', { x: (m.rect.left + m.rect.right) / 2, y: m.rect.top + 2 }, { x: 0, y: -60 }, 'locked');
      m = main(await inspect());
      await attempt('下边不能调整高度', { x: (m.rect.left + m.rect.right) / 2, y: m.rect.bottom - 2 }, { x: 0, y: 60 }, 'locked');
      m = main(await inspect());
      await attempt('外侧边调整宽度', { x: dockSide === 'right' ? m.rect.right - 2 : m.rect.left + 2, y: (m.rect.top + m.rect.bottom) / 2 }, { x: dockSide === 'right' ? 80 : -80, y: 0 }, 'width');
      m = main(await inspect());
      await attempt('外侧下角只调整宽度', { x: dockSide === 'right' ? m.rect.right - 2 : m.rect.left + 2, y: m.rect.bottom - 2 }, { x: dockSide === 'right' ? 60 : -60, y: 60 }, 'width');
      await save({ dock: false }); await delay(250);
      m = main(await inspect()); const b = await page.locator('.main-titlebar .titlebar-spacer').boundingBox();
      const moved = await attempt('取消吸附允许拖动', { x: Math.round(m.client.x + (b.x + b.width / 2) * m.dpi / 96), y: Math.round(m.client.y + (b.y + b.height / 2) * m.dpi / 96) }, { x: -80, y: 40 }, 'free');
      assert.ok(Math.abs(moved.rect.left - m.rect.left) >= 60);
      m = main(await inspect());
      const resized = await attempt('取消吸附允许调整高度', { x: (m.rect.left + m.rect.right) / 2, y: m.rect.bottom - 2 }, { x: 0, y: 60 }, 'free');
      assert.ok(resized.rect.bottom - resized.rect.top > m.rect.bottom - m.rect.top + 30);
    }
  });
  for (const pinned of [false, true]) {
    await check((pinned ? '开启' : '关闭') + '置顶设置时，左右边缘只悬停即可浮出且不抢焦点，退出贴边恢复设置', async () => {
      await save({ pinned, dock: false, edgeAutoHide: true });
      for (const side of ['right', 'left']) {
        for (const wait of [0, 500, 1000]) {
          await control('restore'); await save({ edgeRevealDelay: wait });
          await drag(side); await hidden(side);
          // 普通宿主覆盖展开区域；贴边期间临时置顶，但不改写用户的置顶设置。
          await control('maximize'); await away(); const h = await hidden(side);
          // 真正点击隔离宿主，避免仅调用 SetForegroundWindow 被系统前台锁拒绝。
          await control('click', { x: Math.round((h.work.left + h.work.right) / 2), y: h.work.top + 60 });
          await poll(async () => { const s = await inspect(); return s.foreground === s.hwnd; }, '激活遮挡宿主');
          const before = await inspect();
          assert.equal(before.foreground, before.hwnd); assert.equal(main(before).topmost, true);
          await atEdge(side); const m = await expanded(side);
          const after = await control('inspect', { pointX: m.client.x + 60, pointY: m.client.y + 100 });
          snapshots.push({ name: 'hover-over-foreground', pinned, side, wait, before, after });
          assert.equal(after.probedWindow, handles.main, '悬停展开的内容必须实际位于外部窗口前，不能只移动到其背后');
          assert.equal(after.foreground, before.hwnd, '悬停不能抢走外部输入焦点');
          assert.equal(main(after).topmost, true, '贴边展开面板应像客服宝一样浮在其他软件前');
          assert.equal((await invoke('get_library')).settings.pinned, pinned, '贴边不得改写用户置顶设置');
          await away(); await hidden(side);
          await save({ edgeAutoHide: false }); await expanded(side);
          await poll(async () => main(await inspect()).topmost === pinned, '退出贴边恢复原置顶状态');
          await save({ edgeAutoHide: true });
        }
      }
    });
  }
  await control('restore'); await save({ pinned: true, edgeRevealDelay: 500 });
  await check('最大化软件上方悬停唤出后连续移动到面板各处保持展开', async () => {
    await save({ edgeAutoHide: true, dock: false, pinned: false, edgeRevealDelay: 500 });
    for (const side of ['right', 'left']) {
      await control('restore'); await drag(side); await hidden(side);
      await control('maximize');
      const h = main(await inspect());
      await control('click', { x: Math.round((h.work.left + h.work.right) / 2), y: h.work.top + 60 });
      await poll(async () => { const s = await inspect(); return s.foreground === s.hwnd; }, '最大化宿主持有前台');
      for (let cycle = 0; cycle < 3; cycle++) {
        await atEdge(side); const e = await expanded(side);
        const frames = [];
        snapshots.push({ name: 'maximized-movement-' + side, cycle, expanded: e, frames });
        for (const [fx, fy] of [[0.5, 0.1], [0.03, 0.5], [0.97, 0.5], [0.5, 0.95], [0.03, 0.05], [0.97, 0.05], [0.5, 0.5]]) {
          const x = Math.round(e.client.x + e.clientRect.right * fx), y = Math.round(e.client.y + e.clientRect.bottom * fy);
          await control('pointer', { x, y });
          const until = Date.now() + 250;
          do {
            const s = await inspect(); frames.push(s);
            assert.deepEqual(s.cursor, { x, y }, '鼠标被外部操作移动，当前采样无效');
            assert.deepEqual(main(s).rect, e.rect, '鼠标在面板内移动不能收起');
            assert.equal(s.windowAtCursor, handles.main, '展开面板应实际覆盖最大化软件');
            assert.equal(s.foreground, s.hwnd, '悬停不得抢走最大化软件的焦点');
          } while (Date.now() < until);
        }
        await away(); await hidden(side);
      }
    }
    await control('restore'); await save({ pinned: true });
  });
  await check('最大化软件上方收起途中移回窗口立即展开且持续停留不收回', async () => {
    await save({ edgeAutoHide: true, dock: false, pinned: false });
    for (const side of ['right', 'left']) {
      for (const wait of [0, 500, 1000]) {
        await save({ edgeRevealDelay: wait });
        await control('restore'); await drag(side); const h = await hidden(side);
        await control('maximize');
        const awayX = Math.round((h.work.left + h.work.right) / 2), awayY = h.work.top + 60;
        await control('click', { x: awayX, y: awayY });
        await atEdge(side); const e = await expanded(side);
        const x = side === 'right' ? e.work.right - 12 : e.work.left + 12, y = e.rect.top + 130;
        const returned = await control('leave-reenter', { handle: handles.main, awayX, awayY, x, y });
        assert.equal(returned.pointerError, 0, '必须在开始收起后成功移回');
        const first = returned.trace[0].rect;
        assert.notEqual(first.left, e.rect.left, '必须已开始收起');
        assert.notEqual(first.left, h.rect.left, '必须尚未完全收起');
        await delay(400);
        const s = await control('trace-stop');
        snapshots.push({ name: 'reenter-' + side, wait, expanded: e, trace: s.trace });
        assert.deepEqual(s.cursor, { x, y }, '鼠标被外部操作移动，当前采样无效');
        assert.deepEqual(main(s).rect, e.rect, '移回窗口后不应等待边条延时');
        // 允许已经排队的一帧结束，但收起不得越过位于窗口内的鼠标。
        for (const sample of s.trace) assert.ok(side === 'right' ? sample.rect.left < x : sample.rect.right > x, '收起动画不能从鼠标下方缩走');
        for (const dx of [-3, 0, 3, 0]) {
          await control('pointer', { x: x + dx, y: y + 10 }); await delay(200);
          assert.deepEqual(main(await inspect()).rect, e.rect, '持续停留及移动不能收回');
        }
        assert.equal(s.foreground, s.hwnd, '不抢走最大化宿主焦点');
        await away(); await hidden(side);
      }
    }
    await control('restore'); await save({ pinned: true, edgeRevealDelay: 500 });
  });
  await check('最大化软件上方展开后移入原生缩放边框仍保持展开', async () => {
    await save({ edgeAutoHide: true, dock: false, pinned: false, edgeRevealDelay: 500 });
    for (const side of ['right', 'left']) {
      await control('restore'); await drag(side); await hidden(side); await control('maximize');
      const h = main(await inspect());
      await control('click', { x: Math.round((h.work.left + h.work.right) / 2), y: h.work.top + 60 });
      await atEdge(side); const e = await expanded(side); await delay(100);
      for (const [x, y] of [
        [side === 'right' ? e.rect.left + 3 : e.rect.right - 3, e.rect.top + 180],
        [Math.round((e.client.x + e.frame.right) / 2), e.rect.bottom - 3],
      ]) {
        const probe = await control('inspect', { pointX: x, pointY: y });
        snapshots.push({ name: 'resize-border-probe-' + side, probe });
        assert.equal(probe.probedWindow, handles.main, '移动前边框应属于主窗口');
        await control('pointer', { x, y });
        const frames = []; snapshots.push({ name: 'resize-border-' + side, expanded: e, frames });
        for (let n = 0; n < 25; n++) {
          const s = await inspect(); frames.push(s);
          assert.deepEqual(s.cursor, { x, y }, '鼠标被外部操作移动，当前采样无效');
          assert.equal(s.windowAtCursor, handles.main, '原生缩放边框应实际属于闪语');
          assert.deepEqual(main(s).rect, e.rect, '指针仍命中闪语原生边框时不能收回');
        }
      }
      await away(); await hidden(side);
    }
    await control('restore'); await save({ pinned: true });
  });
  for (const side of ['right', 'left']) {
    await check(side + '真实拖动后仅留2DIP，短暂经过取消，连续悬停展开且宽度不变', async () => {
      const width = main(await inspect()).rect.right - main(await inspect()).rect.left;
      await drag(side); const h = await hidden(side); assert.equal(h.rect.right - h.rect.left, width);
      const foreground = (await inspect()).foreground;
      await atEdge(side); await delay(200); await away(); await delay(550); await hidden(side);
      await atEdge(side); await delay(200); const early = main(await inspect()); assert.deepEqual(early.rect, h.rect);
      const e = await expanded(side); assert.equal(e.rect.right - e.rect.left, width); assert.equal((await inspect()).foreground, foreground);
      snapshots.push({ name: side, hidden: h, expanded: e }); await away(); await hidden(side);
    });
    await check(side + '展开过程中快速移入话术区不会反向收回，停留和小幅移动保持展开', async () => {
      await drag(side); const h = await hidden(side);
      await atEdge(side);
      const opening = await poll(async () => { const m = main(await inspect()); return m.rect.left !== h.rect.left && m; }, '开始展开');
      const x = side === 'right' ? h.work.right - (h.clientRect.right - h.clientRect.left) + 24 : h.work.left + (h.clientRect.right - h.clientRect.left) - 24;
      const y = h.rect.top + 130;
      assert.ok(x < opening.clip.left || x >= opening.clip.right, '移入点应领先于当前展开动画');
      await control('pointer', { x, y });
      const frames = []; let previous = opening.rect.left;
      for (let n = 0; n < 20; n++) {
        const m = main(await inspect()); frames.push(m);
        assert.ok(side === 'right' ? m.rect.left <= previous : m.rect.left >= previous, '进入即将展开的话术区域不能让动画反向收回');
        previous = m.rect.left;
      }
      const e = await expanded(side);
      for (const dx of [-6, 0, 6, 0]) { await control('pointer', { x: x + dx, y: y + 6 }); await delay(250); assert.deepEqual(main(await inspect()).rect, e.rect); }
      snapshots.push({ name: 'hover-entry-' + side, hidden: h, opening, expanded: e, frames });
      await away(); await hidden(side);
    });
    await check(side + '收起后点击外部输入框、右键及长按均不短暂展开', async () => {
      await drag(side);
      const h = await hidden(side), work = h.work;
      await control('move', { x: Math.round((work.left + work.right) / 2) - 240, y: work.top + 100, width: 480, height: 400 });
      await control('focus');
      const target = (await inspect()).windows[0];
      await native(['-Action', 'focus', '-Handle', String(target.handle)]);
      const x = target.client.x + 80, y = target.client.y + target.clientRect.bottom - 40;
      for (const right of [false, true]) {
        for (const duration of [180, 650]) {
          await control('pointer', { x, y });
          assert.equal((await inspect()).windowAtCursor, target.handle, '点击必须命中隔离宿主');
          const frames = [];
          try {
            frames.push(await control('mouse-down', { right }));
            const end = Date.now() + duration;
            while (Date.now() < end) frames.push(await inspect());
          } finally { frames.push(await control('mouse-up')); }
          const end = Date.now() + 300;
          while (Date.now() < end) frames.push(await inspect());
          snapshots.push({ name: 'external-click-' + side, right, duration, frames });
          assert.ok(frames.length >= 6, '必须连续采样按下和松开过程');
          for (const frame of frames) {
            assert.deepEqual(main(frame).rect, h.rect, '外部点击期间主窗口不得移动');
            assert.deepEqual(main(frame).clip, h.clip, '外部点击期间边条不得展开');
            // Windows 菜单切换时前台句柄可能短暂为空，但不能激活闪语或其他窗口。
            assert.ok(frame.foreground === 0 || frame.foreground === target.handle, '闪语不得抢走输入焦点');
          }
          assert.equal(frames.at(-1).foreground, target.handle, '点击结束后仍应由宿主持有焦点');
          if (!right) assert.equal(frames.at(-1).inputFocused, true, '实际点击应聚焦宿主输入框');
          // 关闭宿主输入框的右键菜单，再继续下一次真实点击。
          await control('click', { x: x - 20, y: y - 20 }); await delay(250); await hidden(side);
        }
      }
    });
  }
  await check('左右拖过屏幕边缘13及40像素仍收起，展开后可见边框对齐工作区', async () => {
    for (const side of ['right', 'left']) {
      for (const overshoot of [13, 40]) {
        await drag(side, overshoot); const h = await hidden(side);
        await atEdge(side); const e = await expanded(side);
        assert.equal(e.rect.right - e.rect.left, h.rect.right - h.rect.left);
        snapshots.push({ name: 'overshoot-' + side, overshoot, hidden: h, expanded: e });
        await away(); await hidden(side);
      }
    }
  });
  await check('0和1秒等待均生效，显式唤起及输入期间保持展开', async () => {
    await drag('left'); await hidden('left');
    await save({ edgeRevealDelay: 1000 }); await atEdge('left'); await delay(650); await hidden('left'); await expanded('left');
    await away(); await hidden('left'); await save({ edgeRevealDelay: 0 }); await atEdge('left'); await expanded('left');
    await away(); await hidden('left'); await invoke('window_action', { action: 'show' }); await expanded('left');
    await delay(400); await expanded('left');
    await page.locator('.search-bar input').focus(); await atEdge('left'); await away(); await delay(400); await expanded('left');
    await page.locator('.search-bar input').evaluate(el => el.blur()); await hidden('left');
  });
  await check('关闭立即展开并可自由移动，设置页默认及窄窗口无横向溢出', async () => {
    await save({ edgeAutoHide: false }); await expanded('left'); await delay(400); await expanded('left');
    await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
    const settingsPage = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置窗口');
    await settingsPage.getByText('自动适配 Windows 缩放比例').waitFor();
    assert.equal(await settingsPage.getByRole('radio', { name: '等0.5秒' }).isDisabled(), true);
    await settingsPage.getByRole('checkbox', { name: '悬浮时贴边自动隐藏' }).evaluate(el => el.click());
    await poll(async () => !(await settingsPage.getByRole('radio', { name: '等0.5秒' }).isDisabled()), '界面开启贴边隐藏');
    await settingsPage.getByRole('radio', { name: '等0.5秒' }).evaluate(el => el.click());
    await poll(async () => (await invoke('get_library')).settings.edgeRevealDelay === 500, '保存展开等待时间');
    for (const width of [594, 480]) {
      await settingsPage.setViewportSize({ width, height: 620 });
      assert.equal(await settingsPage.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await settingsPage.screenshot({ path: path.join(evidence, 'settings-' + width + '.png') });
    }
    await settingsPage.getByRole('button', { name: '关闭窗口' }).evaluate(el => el.click()); await poll(() => settingsPage.isClosed(), '关闭设置');
  });
  await check('设置打开及设置输入期间左右贴边仍收起，设置窗口位置和焦点不变', async () => {
    for (const side of ['left', 'right']) {
      await save({ edgeAutoHide: true, edgeRevealDelay: 0 }); await drag(side); await hidden(side);
      await atEdge(side); await expanded(side);
      await page.getByRole('button', { name: '设置', exact: true }).evaluate(el => el.click());
      const settingsPage = await poll(() => browser.contexts()[0].pages().find(p => p.url().includes('#settings')), '设置窗口');
      const settingsWindow = await native(['-ProcessId', String(app.pid), '-Title', '设置 · 闪语']);
      await native(['-Action', 'focus', '-Handle', String(settingsWindow.handle)]);
      await away(); await hidden(side);
      const before = await native(['-Handle', String(settingsWindow.handle)]);
      // 用设置中已有输入控件验证产品的真实焦点上报，不伪造原生输入标志。
      const input = settingsPage.locator('input:not([type=checkbox]):not([type=radio]):not([type=hidden])').first();
      await input.focus(); await input.fill('250');
      await atEdge(side); await expanded(side); await away(); await hidden(side);
      const after = await native(['-Handle', String(settingsWindow.handle)]);
      assert.deepEqual(after.rect, before.rect); assert.equal((await inspect()).foreground, settingsWindow.handle);
      await settingsPage.getByRole('button', { name: '关闭窗口' }).evaluate(el => el.click()); await poll(() => settingsPage.isClosed(), '关闭设置');
    }
  });
  await check('手动隐藏与非托盘最小化清除边条，重新显示恢复完整尺寸', async () => {
    await save({ edgeAutoHide: true, edgeRevealDelay: 500, tray: false }); await drag('right'); await hidden('right');
    await invoke('window_action', { action: 'hide' }); await poll(async () => !main(await inspect()).visible, '手动隐藏');
    await atEdge('right'); await delay(700); assert.equal(main(await inspect()).visible, false);
    await invoke('window_action', { action: 'show' }); await expanded('right');
    await drag('right'); await hidden('right'); await invoke('window_action', { action: 'minimize' });
    await poll(async () => main(await inspect()).iconic, '最小化'); await delay(250);
    await invoke('window_action', { action: 'show' }); await expanded('right'); assert.equal(main(await inspect()).iconic, false);
  });
  await check('贴边收起后窗口与搜索快捷键仍能主动唤起', async () => {
    // 使用隔离快捷键，避免触发用户正在运行的正式客户端。
    await save({ searchHotkey: '8', windowHotkey: '9', commonHotkey: '0', customHotkeys: false });
    for (const key of ['57', '56']) {
      await drag('right'); await hidden('right'); await control('focus');
      // 窗口快捷键沿用显示／隐藏切换语义；贴边仍是可见窗口，第一次会手动隐藏。
      if (key === '57') {
        await native(['-Action', 'key', '-Key', key, '-Alt']);
        await poll(async () => !main(await inspect()).visible, '快捷键手动隐藏');
      }
      await native(['-Action', 'key', '-Key', key, '-Alt']); await expanded('right');
      await poll(async () => (await inspect()).foreground === handles.main, '快捷键激活主窗口');
      if (key === '56') assert.equal(await page.locator('.search-bar input').evaluate(el => el === document.activeElement), true);
    }
    await page.locator('.search-bar input').evaluate(el => el.blur());
  });
  await check('贴边收起后切换聊天吸附恢复完整侧栏，退出吸附恢复正常独立宽度', async () => {
    await drag('right'); const h = await hidden('right'); await save({ dock: true });
    await control('focus'); await poll(async () => { const s = await inspect(), m = main(s); return m.visible && m.frame.left === s.windows[0].frame.right && m.frame.top === s.windows[0].frame.top; }, '吸附');
    const m = main(await inspect()); assert.equal(m.rect.right - m.rect.left, h.rect.right - h.rect.left);
    await save({ dock: false }); await delay(250); const restored = main(await inspect()); assert.ok(restored.frame.right <= restored.work.right + 1);
  });
  await check('取消左右吸附保留最新宽高，目标调整高度及重复切换不恢复旧尺寸', async () => {
    await save({ dock: false, edgeAutoHide: false });
    const size = m => [m.rect.right - m.rect.left, m.rect.bottom - m.rect.top];
    for (const dockSide of ['left', 'right']) {
      for (const height of [540, 820]) {
        await control('move', { handle: handles.main, x: 1400, y: 90, width: height === 540 ? 360 : 420, height: 700 });
        await control('move', { x: 600, y: 90, width: 640, height });
        await save({ dock: true, dockSide }); await control('click', { x: 650, y: 160 });
        await poll(async () => { const s = await inspect(); return s.foreground === s.windows[0].handle; }, '隔离目标取得前台');
        const docked = await poll(async () => {
          const s = await inspect(), m = main(s), target = s.windows[0];
          return m.visible && m.frame.top === target.frame.top && m.frame.bottom === target.frame.bottom
            && (dockSide === 'left' ? m.frame.right === target.frame.left : m.frame.left === target.frame.right) && m;
        }, '目标调整后的吸附尺寸');
        await save({ dock: false });
        await delay(300);
        const detached = main(await inspect());
        assert.deepEqual(size(detached), size(docked), '取消吸附必须保留最新宽高');
        await delay(300); assert.deepEqual(size(main(await inspect())), size(docked), '后续刷新不回退旧尺寸');
      }
    }
  });
  await check('解除吸附保持当前屏幕并保留尺寸，旧位置越界不把窗口送出屏幕', async () => {
    const source = monitors[0].work;
    // 单屏也覆盖失效的屏外旧位置；有多屏时再真实跨屏跟随，不能将单屏结果称作跨屏验收。
    const targets = monitors.length > 1 ? monitors.slice(1).map(m => m.work) : [source];
    for (const work of targets) {
      for (const dockSide of ['left', 'right']) {
        await save({ dock: false, edgeAutoHide: false, pinned: true });
        const originalX = monitors.length > 1 ? source.left + 60 : source.left - 900;
        await control('move', { handle: handles.main, x: originalX, y: source.top + 80, width: 360, height: 600 });
        await delay(200);
        const original = main(await inspect());
        await control('move', { x: Math.round((work.left + work.right) / 2) - 200, y: work.top + 100, width: 400, height: 540 });
        const target = (await inspect()).windows[0];
        await save({ dock: true, dockSide });
        await control('raise', { handle: target.handle });
        await control('click', { x: target.client.x + 60, y: target.client.y + 80 });
        await poll(async () => {
          const s = await inspect(), m = main(s), host = s.windows[0];
          return m.visible && m.frame.top === host.frame.top && m.frame.bottom === host.frame.bottom
            && (dockSide === 'left' ? m.frame.right === host.frame.left : m.frame.left === host.frame.right);
        }, '当前屏幕吸附稳定');
        await delay(200); const docked = main(await inspect());
        assert.deepEqual(docked.work, work, '必须先在目标屏幕完成吸附');
        await control('trace-start', { handle: handles.main });
        await save({ dock: false }); await delay(600);
        const state = await control('trace-stop'), detached = main(state);
        snapshots.push({ name: 'undock-monitor', dockSide, original, docked, detached, trace: state.trace });
        assert.deepEqual(detached.work, work, '解除吸附不能跳回旧显示器');
        assert.equal(detached.dpi, docked.dpi, '留在当前屏幕不能套用旧屏幕DPI');
        assert.ok(state.trace.length > 0);
        for (const sample of state.trace.filter(s => s.visible)) {
          assert.ok(sample.rect.left >= work.left && sample.rect.right <= work.right, '不能短暂跳回旧位置再恢复');
          assert.ok(sample.rect.top >= work.top && sample.rect.bottom <= work.bottom);
          assert.equal(sample.rect.right - sample.rect.left, docked.rect.right - docked.rect.left);
          assert.equal(sample.rect.bottom - sample.rect.top, docked.rect.bottom - docked.rect.top);
        }
        if (monitors.length > 1) assert.deepEqual(detached.rect, docked.rect, '跨屏旧位置失效后保留当前可见位置');
        await control('unpin', { handle: target.handle });
      }
    }
  });
  await check('重启保留窗口大小，贴边隐藏和最小化不覆盖记录', async () => {
    const sizeFile = path.join(evidence, 'data', 'window-size.json');
    const clientSize = async () => { const m = main(await inspect()); return { width: m.clientRect.right * 96 / m.dpi, height: m.clientRect.bottom * 96 / m.dpi }; };
    const savedSize = async () => JSON.parse(await fs.readFile(sizeFile, 'utf8'));
    const restart = async expected => {
      await invoke('window_action', { action: 'exit' }); await poll(() => app.exitCode !== null, '退出保存');
      await browser.close(); await control('minimize'); await startApp();
      await invoke('window_action', { action: 'show' }); await delay(350);
      assert.deepEqual(await clientSize(), expected, '重启后的逻辑客户区宽高必须一致');
      assert.deepEqual(await savedSize(), expected, '尺寸文件不能被启动默认大小覆盖');
      await control('restore');
    };
    await save({ dock: false, edgeAutoHide: false, freeResize: true, tray: false });
    await control('move', { handle: handles.main, x: 1300, y: 100, width: 460, height: 760 });
    const expected = await clientSize();
    await poll(async () => JSON.stringify(await savedSize().catch(() => null)) === JSON.stringify(expected), '本机尺寸落盘');
    await restart(expected);
    await invoke('window_action', { action: 'minimize' }); await delay(120); await restart(expected);
    await save({ edgeAutoHide: true }); await drag('right'); await hidden('right');
    await delay(350); assert.deepEqual(await savedSize(), expected, '隐藏边条不改写宽高');
    await restart(expected);
    await save({ edgeAutoHide: false });
    await control('move', { handle: handles.main, x: 1300, y: 100, width: 500, height: 790 });
    const immediate = await clientSize(); await restart(immediate);
    // 吸附产生的新高度同样是最新窗口大小；下次独立启动不能回到吸附前尺寸。
    await control('move', { x: 600, y: 90, width: 640, height: 650 });
    await save({ dock: true, dockSide: 'right' }); await control('click', { x: 650, y: 160 });
    await poll(async () => { const s = await inspect(), m = main(s); return m.frame.top === s.windows[0].frame.top && m.frame.bottom === s.windows[0].frame.bottom; }, '吸附最新高度');
    const docked = await clientSize(); await delay(350); await restart(docked);
    await save({ dock: false }); await control('minimize');
    const m = main(await inspect());
    await control('move', { handle: handles.main, x: m.work.left + 100, y: m.work.top, width: 500, height: m.work.bottom - m.work.top + 120 });
    const oversized = await clientSize(); await restart(oversized);
  });

} catch (error) {
  console.error(error); process.exitCode = 1;
  snapshots.push({ name: 'failure', state: await inspect().catch(() => null) });
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
} finally {
  if (host?.exitCode === null) await control('mouse-up').catch(() => {});
  await invoke('window_action', { action: 'exit' }).catch(() => {});
  if (app) await poll(() => app.exitCode !== null, '退出').catch(() => {});
  await browser?.close().catch(() => {});
  if (host?.exitCode === null) await control('close').catch(() => {});
  for (const child of [app, host]) if (child?.exitCode === null) child.kill();
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ results, snapshots, limits: [crossMonitorVerified ? '跨屏仅覆盖当前连接的显示器与系统缩放比例' : '真实多显示器与150%/200%系统DPI未实测；几何单测覆盖对应比例', '真实企业微信及发送另行验收'] }, null, 2));
  console.log('验收记录：' + evidence);
}
