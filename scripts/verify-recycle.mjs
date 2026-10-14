import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 实际 Tauri、HTTP 和 SQLite 使用隔离数据；窗口保持隐藏，不操作聊天、剪贴板和实体键鼠。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'recycle-' + Date.now());
const source = process.env.SHANYU_TEAM_SERVER_EXE;
if (!source) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定 Hub 程序。');
await fs.mkdir(evidence, { recursive: true });
const serverExecutable = path.join(evidence, 'hub.exe'); await fs.copyFile(source, serverExecutable);
const port = async () => { const s = net.createServer(); await new Promise(resolve => s.listen(0, '127.0.0.1', resolve)); const result = s.address().port; await new Promise(resolve => s.close(resolve)); return result; };
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (read, accepts = value => !!value) => { const start = Date.now(); while (Date.now() - start < 15000) { try { const value = await read(); if (accepts(value)) return value; } catch {} await delay(80); } throw new Error('等待回收站验收状态超时'); };
const serverPort = await port(), debugPort = await port(), base = `http://127.0.0.1:${serverPort}`;
const initial = randomUUID(), password = randomUUID();
const hub = spawn(serverExecutable, [], { windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: path.join(evidence, 'server'), SHANYU_LISTEN: `127.0.0.1:${serverPort}`, SHANYU_ADMIN_USERNAME: 'recycle-qa', SHANYU_ADMIN_PASSWORD: initial } });
let app, browser, page, recycle, token;
let startupErrors = '';
const checks = [];
const invoke = (cmd, args = {}, target = page) => target.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const click = locator => locator.evaluate(el => el.click());
const check = async (name, run) => { await run(); checks.push(name); console.log('通过：' + name); };
const api = async (method, route, body) => { const response = await fetch(`${base}/api/${route}`, { method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'x-team-lifecycle': '1' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) }); assert.ok(response.ok, `接口失败：${response.status}`); return response.json(); };
const hideWindows = target => target.evaluate(() => {
  const original = window.fetch.bind(window);
  window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url);
    if (url.hostname !== 'ipc.localhost') return original(input, init);
    const command = decodeURIComponent(url.pathname.slice(1));
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(command)) return new Response('null', { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    if (command === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: false, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
    return original(input, init);
  };
});
try {
  await until(async () => (await fetch(base + '/health')).ok);
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, 'client'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + debugPort } });
  app.stderr.on('data', data => { startupErrors += data.toString(); });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`)); const context = browser.contexts()[0];
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor(); await hideWindows(page);
  await invoke('team_login', { server: base, username: 'recycle-qa', password: initial });
  await invoke('team_change_password', { request: { currentPassword: initial, password } });
  const login = await fetch(base + '/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'recycle-qa', password }) }); token = (await login.json()).token;
  const teamId = await invoke('team_admin', { action: 'create-team', request: { name: '测试小组' } }); await invoke('team_refresh');
  await until(() => invoke('get_team_view', { teamId }));
  const rootId = await invoke('save_category', { category: { id: '', name: '原分类', kind: 'phrase', parentId: null, position: 0, background: '' } });
  const external = path.join(evidence, '原附件.txt'); await fs.writeFile(external, '回收站源文件保留');
  const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
  const phraseId = await invoke('save_phrase', { phrase: { id: '', rootId, groupId: null, title: '欢迎测试', color: '', background: '#ffffcc', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '欢迎正文' }, image, { kind: 'file', path: external, name: '原附件.txt' }] }] } });
  await invoke('publish_private_to_team', { teamId, id: phraseId, kind: 'phrase' });
  const shared = await until(() => invoke('get_team_view', { teamId }), v => v.phrases.length === 1);
  await invoke('delete_team_content', { teamId, request: { kind: 'phrase', id: shared.phrases[0].contentRef.id, expectedVersion: shared.team.version, expectedLifecycleVersion: shared.team.lifecycleVersion } });
  await invoke('delete_item', { id: phraseId, kind: 'phrase', recycle: true });
  await click(page.getByRole('button', { name: '共享话术', exact: true })); await click(page.getByRole('menuitem', { name: /^测试小组/ }));
  await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '回收站', exact: true }));
  recycle = await until(() => context.pages().find(p => p.url().includes('#recycle-bin'))); await hideWindows(recycle);
  await recycle.getByText('类型：回收记录', { exact: true }).waitFor();
  await check('共享入口定位同一回收站，展示正文及素材名', async () => {
    assert.ok((await recycle.getByRole('button', { name: '共享话术', exact: true }).textContent()).includes('测试小组'));
    assert.ok((await recycle.locator('.recycle-detail').textContent()).includes('欢迎正文'));
    assert.ok((await recycle.locator('.recycle-detail').textContent()).includes('原附件.txt'));
    await recycle.screenshot({ path: path.join(evidence, 'shared.png') });
  });
  await check('同一窗口切换个人话术，实际图片预览和名称正常', async () => {
    await click(recycle.getByRole('button', { name: '个人话术', exact: true })); await recycle.getByLabel('回收话术标题').waitFor();
    assert.equal(await recycle.getByLabel('回收话术标题').inputValue(), '欢迎测试');
    await recycle.waitForFunction(() => document.querySelector('.recycle-content img')?.naturalWidth > 0);
    await recycle.screenshot({ path: path.join(evidence, 'private.png') });
  });
  await check('主菜单复用已有回收站时更新共享来源', async () => {
    await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '回收站', exact: true }));
    await until(() => recycle.getByRole('button', { name: '共享话术', exact: true }).textContent(), value => value.includes('测试小组'));
    await click(recycle.getByRole('button', { name: '共享话术', exact: true }));
    await recycle.getByRole('menu', { name: '选择共享话术' }).waitFor(); await recycle.screenshot({ path: path.join(evidence, 'sources.png') });
    await click(recycle.getByRole('menuitem', { name: /^测试小组/ }));
  });
  await check('共享全拼、首字母搜索与无结果态', async () => {
    for (const query of ['huanying', 'hycs', '不存在']) {
      await recycle.getByLabel('搜索回收站').fill(query); await recycle.locator('.recycle-search').evaluate(form => form.requestSubmit());
      await until(() => recycle.locator('.recycle-list [role="option"]').count(), n => n === (query === '不存在' ? 0 : 1));
    }
    await recycle.getByText('没有匹配的回收记录', { exact: true }).waitFor();
  });
  await check('清空包含搜索外记录，确认后的新记录保留且源文件保留', async () => {
    await click(recycle.getByRole('button', { name: '清空当前回收站', exact: true })); await recycle.locator('dialog').waitFor();
    assert.ok((await recycle.locator('dialog').textContent()).includes('1 条回收记录'));
    await click(recycle.locator('dialog').getByRole('button', { name: '取消', exact: true })); assert.equal((await api('GET', `teams/${teamId}/recycle`)).length, 1);
    await click(recycle.getByRole('button', { name: '清空当前回收站', exact: true })); await recycle.locator('dialog').waitFor();
    const snapshot = await api('GET', `teams/${teamId}/snapshot`), categoryId = randomUUID(), newId = randomUUID();
    const version = await api('PUT', `teams/${teamId}/snapshot`, { expectedVersion: snapshot.version, expectedLifecycleVersion: 1, categories: [...snapshot.categories, { id: categoryId, name: '新分类', parentId: null, position: 0, background: '' }], phrases: [{ id: newId, rootId: categoryId, groupId: null, title: '后来新增', color: '', background: '', position: 0, revision: 0, segments: [{ blocks: [{ kind: 'text', text: '新内容' }] }] }], newAssets: [] });
    await api('POST', `teams/${teamId}/recycle`, { kind: 'phrase', id: newId, expectedVersion: version.version, expectedLifecycleVersion: 1 });
    await recycle.locator('dialog form').evaluate(form => form.requestSubmit()); await until(() => recycle.locator('dialog').count(), count => count === 0);
    const records = await api('GET', `teams/${teamId}/recycle`); assert.equal(records.length, 1); assert.equal(records[0].label, '后来新增');
    assert.equal(await fs.readFile(external, 'utf8'), '回收站源文件保留');
  });
  await check('640×400窗口无横向溢出且来源和分页操作可见', async () => {
    // CSS 客户区检查，隐藏 WebView2 可直接使用视口，不替代实体 DPI 验收。
    await recycle.setViewportSize({ width: 640, height: 400 });
    const layout = await recycle.evaluate(() => ({ width: innerWidth, overflow: document.documentElement.scrollWidth > innerWidth, footer: document.querySelector('.recycle-pagination').getBoundingClientRect().bottom, height: innerHeight }));
    assert.equal(layout.overflow, false); assert.ok(layout.footer <= layout.height);
    await recycle.screenshot({ path: path.join(evidence, 'small.png') });
  });
  await check('40字自定义组名保持原样，窄窗入口省略且菜单可操作', async () => {
    const name = '甲乙丙丁戊己庚辛壬癸'.repeat(4);
    await invoke('team_admin', { action: 'update-team', teamId, request: { name, expectedLifecycleVersion: 1 } });
    await invoke('team_refresh');
    const button = recycle.getByRole('button', { name: '共享话术', exact: true });
    await until(() => button.textContent(), value => value.includes(name));
    assert.equal(await button.getAttribute('title'), name);
    assert.equal(await recycle.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await click(button); await recycle.getByRole('menu', { name: '选择共享话术' }).waitFor();
    const item = recycle.getByRole('menuitem', { name: new RegExp('^' + name) });
    assert.ok((await item.getAttribute('title')).startsWith(name));
    await recycle.screenshot({ path: path.join(evidence, 'long-name.png') });
    await click(item);
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks: checks.length, results: checks, limits: ['隔离数据、隐藏实际客户端', '未验证实体键鼠、系统DPI、安装升级和真实企业微信'] }, null, 2));
  console.log(JSON.stringify({ evidence, checks: checks.length }));
} catch (error) {
  if (startupErrors) await fs.writeFile(path.join(evidence, 'startup.log'), startupErrors);
  if (recycle && !recycle.isClosed()) await recycle.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error), stack: error.stack }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) app.kill();
  if (hub.exitCode === null) hub.kill();
}
