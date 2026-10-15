import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 真实 Rust HTTP/SSE、Windows 凭据管理器和 WebView2，全部使用隔离数据。
// 不触发聊天发送、系统剪贴板、实体键鼠；不把登录参数或令牌写入报告。
const repo = process.cwd(), evidence = path.join(repo, '.build', 'teams-' + Date.now());
// 服务端独立维护；调用者明确提供产物，不隐式依赖相邻仓库或旧目录。
const serverSource = process.env.SHANYU_TEAM_SERVER_EXE;
if (!serverSource) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定服务端程序路径。');
const clientData = path.join(evidence, 'client'), serverData = path.join(evidence, 'server');
await fs.mkdir(evidence, { recursive: true });
const serverExecutable = path.join(evidence, 'shanyu-team-server.exe');
await fs.copyFile(path.resolve(serverSource), serverExecutable);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const port = async () => { const s = net.createServer(); await new Promise(resolve => s.listen(0, '127.0.0.1', resolve)); const p = s.address().port; await new Promise(resolve => s.close(resolve)); return p; };
const apiPort = await port(), debugPort = await port(), base = 'http://127.0.0.1:' + apiPort;
const initialPassword = randomUUID(), password = randomUUID(), employeePassword = randomUUID(), recipientPassword = randomUUID(), employeeNewPassword = randomUUID();
const startServer = () => spawn(serverExecutable, [], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_DATA_DIR: serverData, SHANYU_LISTEN: '127.0.0.1:' + apiPort, SHANYU_ADMIN_USERNAME: 'qa-admin', SHANYU_ADMIN_PASSWORD: initialPassword } });
let service = startServer(), app, browser, page, context;
const results = [], pages = new Set(), lifecycles = new Map(); let token;
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
const check = async (label, work) => { await work(); results.push(label); console.log('通过：' + label); };
const until = async (work, test = v => !!v, limit = 10000) => { const start = Date.now(); while (Date.now() - start < limit) { try { const value = await work(); if (test(value)) return value; } catch {} await delay(50); } throw new Error('等待测试结果超时'); };
const api = async (method, route, body) => { const team = /^teams\/([^/]+)\//.exec(route)?.[1]; const response = await fetch(base + '/api/' + route, { method, headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', ...(team ? { 'x-team-lifecycle': String(lifecycles.get(team) || 1) } : {}) }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) }); assert.ok(response.ok, '服务接口返回 ' + response.status); return response.json(); };
const publish = (team, snapshot) => api('PUT', `teams/${team}/snapshot`, { expectedVersion: snapshot.version, expectedLifecycleVersion: snapshot.lifecycleVersion, categories: snapshot.categories, phrases: snapshot.phrases, newAssets: [] });
const setMembers = async (team, members) => { const current = await api('GET', `teams/${team}/members`); return api('PUT', `teams/${team}/members`, { expectedMembershipVersion: current.membershipVersion, expectedLifecycleVersion: current.lifecycleVersion, members }); };
const click = locator => locator.evaluate(el => el.click());
const view = async route => { const found = await until(() => context.pages().find(p => !p.isClosed() && p.url().includes('#' + route))); pages.add(found); return found; };
async function hideDynamicWindows(target) {
  await target.evaluate(() => { const original = window.fetch.bind(window); window.__qaConfirmations = []; window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const cmd = decodeURIComponent(url.pathname.slice(1));
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(cmd)) return new Response('null', { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    // 只模拟系统确认按钮；表单、真实管理请求、数据库与缓存照常执行。
    if (cmd === 'plugin:dialog|message') { const args = JSON.parse(init.body); window.__qaConfirmations.push({ title: args.title, message: args.message }); const cancel = window.__qaCancelNext; window.__qaCancelNext = false; return new Response(JSON.stringify(args.buttons?.OkCancelCustom?.[cancel ? 1 : 0] || (cancel ? 'Cancel' : 'Ok')), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } }); }
    // 暂停一次管理写入以检查提交中的关闭保护，随后继续调用真实接口。
    if (cmd === 'team_admin' && window.__qaPauseAction === JSON.parse(init.body).action) { window.__qaPauseAction = ''; await new Promise(resolve => { window.__qaResume = resolve; }); }
    if (cmd === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: true, center: false, x: -20000, y: -20000, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
    return original(input, init);
  }; });
}
// 隐藏创建会在 Windows 上保留标题栏高度；移到屏幕外正常创建，验证真实客户区且不抢焦点。
async function resizeManagement(target, title, width, height) {
  const inner = await invoke('plugin:window|inner_size', { label: title === '账号管理' ? 'user-management' : 'team-management' }, target);
  const outer = await invoke('plugin:window|outer_size', { label: title === '账号管理' ? 'user-management' : 'team-management' }, target);
  const scale = await target.evaluate(() => devicePixelRatio);
  const driver = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tests/native-driver.ps1', '-Action', 'move', '-ProcessId', String(app.pid), '-Title', title + ' · 闪语', '-X', '-20000', '-Y', '-20000', '-Width', String(Math.round(width * scale) + outer.width - inner.width), '-Height', String(Math.round(height * scale) + outer.height - inner.height)], { cwd: repo, windowsHide: true, stdio: 'ignore' });
  const code = await new Promise(resolve => driver.once('exit', resolve)); assert.equal(code, 0);
  await until(() => target.evaluate(() => ({ width: innerWidth, height: innerHeight })), size => size.width === width && size.height === height);
}
async function startClient() {
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: clientData, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + debugPort } });
  browser = await until(() => chromium.connectOverCDP('http://127.0.0.1:' + debugPort)); context = browser.contexts()[0];
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash));
  await page.locator('.main-window').waitFor(); await page.getByRole('status').evaluateAll(nodes => nodes.forEach(el => el.click())); pages.add(page); await hideDynamicWindows(page);
}
try {
  await until(async () => (await fetch(base + '/health')).ok);
  await startClient();
  let teamA, teamB, privateId, employeePrivateId, shared, externalAsset, employee, recipient, ownTeam, ownSnapshot, managementView, userView;
  await check('HTTP登录、环境变量管理员首次改密和字符串账号管理', async () => {
    await click(page.getByRole('button', { name: '设置', exact: true })); const accountView = await view('settings'); await accountView.locator('.settings-sidebar').waitFor(); await hideDynamicWindows(accountView); await click(accountView.getByRole('button', { name: '账号与共享', exact: true }));
    await accountView.getByLabel('服务器地址', { exact: true }).fill(base); await accountView.getByLabel('用户名', { exact: true }).fill('qa-admin');
    await accountView.screenshot({ path: path.join(evidence, 'account-login.png') });
    await accountView.getByLabel('密码', { exact: true }).fill(initialPassword); await accountView.locator('.team-connection').evaluate(el => el.requestSubmit());
    await accountView.getByRole('dialog', { name: '修改初始密码', exact: true }).waitFor();
    const status = await invoke('get_team_status'); assert.ok(status.user.mustChangePassword); assert.equal(status.teams.length, 0); assert.ok(await accountView.getByRole('button', { name: '管理话术', exact: true }).isDisabled());
    await accountView.screenshot({ path: path.join(evidence, 'first-password.png') });
    await accountView.getByLabel('当前密码', { exact: true }).fill(initialPassword); await accountView.getByLabel('新密码', { exact: true }).fill(password); await accountView.getByLabel('确认新密码', { exact: true }).fill(password); await accountView.locator('dialog form').evaluate(el => el.requestSubmit());
    await until(() => invoke('get_team_status'), value => value.user && !value.user.mustChangePassword);
    await until(() => accountView.locator('dialog').count(), value => value === 0);
    const response = await fetch(base + '/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'qa-admin', password }) }); token = (await response.json()).token;
    teamA = await invoke('team_admin', { action: 'create-team', request: { name: '售前小组' } }); teamB = await invoke('team_admin', { action: 'create-team', request: { name: '售后小组' } });
    employee = await invoke('team_admin', { action: 'create-user', request: { username: '0001', password: employeePassword } });
    await setMembers(teamA, [{ userId: employee, teamId: teamA, role: 'member' }]); await invoke('team_refresh');
    assert.equal((await invoke('team_admin', { action: 'users' })).find(u => u.id === employee).username, '0001');
    await until(() => invoke('get_team_view', { teamId: teamA }));
  });
  await check('用户名和共享话术窗口右上角关闭实际销毁，可重新打开', async () => {
    for (const [label, title, width, height] of [['user-management', '账号管理', 620, 620], ['team-management', '共享话术管理', 680, 600]]) {
      await invoke('plugin:webview|create_webview_window', { options: { label, title: title + ' · 闪语', url: 'index.html#' + label, width, height, decorations: false } });
      const target = await view(label); await target.locator(label === 'user-management' ? '.user-card' : '.management-groups').first().waitFor();
      const failures = []; target.on('pageerror', error => { if (String(error).includes('destroy')) failures.push(String(error)); });
      await target.getByRole('button', { name: '关闭窗口', exact: true }).click({ timeout: 4000 });
      await until(() => target.isClosed()).catch(() => { throw new Error(title + '关闭失败：' + failures.join('；')); });
      await invoke('plugin:webview|create_webview_window', { options: { label, title: title + ' · 闪语', url: 'index.html#' + label, width, height, decorations: false } });
      const reopened = await view(label); await reopened.locator(label === 'user-management' ? '.user-card' : '.management-groups').first().waitFor(); await hideDynamicWindows(reopened);
      await click(reopened.getByRole('button', { name: label === 'user-management' ? '新建账号' : '新建共享话术', exact: true }));
      const input = reopened.locator('dialog input').first(); await input.fill('窗口关闭验收'); await input.focus();
      assert.equal(await input.evaluate(el => getComputedStyle(el).outlineStyle), 'none');
      const closeBox = await reopened.locator('.dialog-window-close').boundingBox(); assert.ok(Math.abs(closeBox.x + closeBox.width - (width - 4)) < 1 && Math.abs(closeBox.y - 4) < 1);
      await reopened.screenshot({ path: path.join(evidence, label + '-dialog-close.png') });
      await reopened.locator('.dialog-window-close').click({ timeout: 4000 }); await until(() => reopened.isClosed());
    }
  });
  await check('私人分类发布到共享话术只复制指定话术和实际图文附件', async () => {
    const rootId = await invoke('save_category', { category: { id: '', name: '私人QA', kind: 'phrase', parentId: null, position: 0, background: '' } });
    const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    externalAsset = path.join(evidence, '外部文件.txt'); await fs.writeFile(externalAsset, '共享话术附件QA');
    privateId = await invoke('save_phrase', { phrase: { id: '', rootId, groupId: null, title: 'QA图文附件', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: 'QA正文' }, image, { kind: 'file', path: externalAsset, name: '外部文件.txt' }] }] } });
    await invoke('publish_private_to_team', { teamId: teamA, id: rootId, kind: 'category' });
    shared = await until(() => invoke('get_team_view', { teamId: teamA }), v => v.phrases.length === 1); assert.equal(shared.phrases[0].shortcut, '');
    const remote = await api('GET', `teams/${teamA}/snapshot`); assert.equal(remote.phrases[0].segments[0].blocks[1].kind, 'image'); assert.ok(remote.phrases[0].segments[0].blocks[2].assetId);
    assert.equal(remote.settings, undefined); assert.equal(remote.commonPhrases, undefined);
  });
  await check('共享话术入口在私人入口旁；主窗口展示当前组及窄窗布局', async () => {
    await click(page.getByRole('button', { name: '共享话术', exact: true })); await click(page.getByRole('menuitem', { name: /^售前小组/ }));
    await page.getByRole('button', { name: '共享话术', exact: true }).getByText('共享话术：售前小组', { exact: true }).waitFor(); await page.getByText('QA图文附件', { exact: true }).waitFor();
    assert.ok(await page.getByRole('button', { name: '个人话术', exact: true }).count());
    const geometry = await page.locator('.scope-bar').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth })); assert.ok(geometry.scroll <= geometry.width + 1);
    await page.getByRole('status').evaluateAll(nodes => nodes.forEach(el => el.click()));
    await page.screenshot({ path: path.join(evidence, 'team-entry.png') });
  });
  await check('入口交换保持当前共享话术及选中话术，窄窗下拉不越界', async () => {
    const original = await invoke('get_library'); const currentRow = page.locator('.content-row').filter({ hasText: 'QA图文附件' }); await click(currentRow);
    const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    await invoke('save_settings', { settings: { ...original.settings, teamsFirst: true }, libraryId: original.libraryId });
    await page.waitForFunction(() => document.querySelector('.scope-bar').firstElementChild.classList.contains('team-selector'));
    assert.equal(await page.locator('.content-row.selected').filter({ hasText: 'QA图文附件' }).count(), 1);
    assert.ok(await page.getByRole('button', { name: '共享话术', exact: true }).getByText('共享话术：售前小组', { exact: true }).count());
    await page.setViewportSize({ width: 300, height: 400 });
    const geometry = await page.locator('.scope-bar').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth })); assert.ok(geometry.scroll <= geometry.width + 1);
    await click(page.getByRole('button', { name: '共享话术', exact: true })); const dropdown = await page.getByRole('menu', { name: '选择共享话术', exact: true }).boundingBox();
    assert.ok(dropdown.x >= 0 && dropdown.x + dropdown.width <= 300); assert.ok(dropdown.width < 200, '短库名的下拉应随内容收紧'); await page.screenshot({ path: path.join(evidence, 'team-entry-reordered.png') });
    await page.keyboard.press('Escape'); await page.setViewportSize(viewport); await invoke('save_settings', { settings: original.settings, libraryId: original.libraryId });
    await page.waitForFunction(() => document.querySelector('.scope-bar').firstElementChild.textContent === '个人话术');
  });
  await check('完整下载、素材协议显示和复制个人库后独立保留真实附件', async () => {
    await invoke('prepare_team_phrase', { id: shared.phrases[0].id }); shared = await invoke('get_team_view', { teamId: teamA }); assert.equal(shared.phrases[0].ready, true);
    const imagePath = shared.phrases[0].segments[0].blocks[1].path;
    const imageReady = await page.evaluate(path => new Promise(resolve => { const image = new Image(); image.onload = () => resolve(true); image.onerror = () => resolve(false); image.src = 'http://asset.localhost/' + encodeURIComponent(path); }), imagePath); assert.ok(imageReady);
    await invoke('copy_team_to_private', { id: shared.phrases[0].id });
    const copy = (await invoke('get_library')).phrases.find(p => p.id !== privateId); assert.ok(copy); assert.notEqual(copy.segments[0].blocks[2].path, externalAsset);
    assert.equal(await fs.readFile(copy.segments[0].blocks[2].path, 'utf8'), '共享话术附件QA');
  });
  await check('SSE真实网络更新、编辑目标标识和编辑冲突保留草稿', async () => {
    const remote = await api('GET', `teams/${teamA}/snapshot`); remote.phrases[0].title = 'QA同步更新';
    const start = Date.now(); await publish(teamA, remote);
    await until(() => invoke('get_team_view', { teamId: teamA }), v => v.phrases[0].title === 'QA同步更新', 5000); assert.ok(Date.now() - start < 5000);
    shared = await invoke('get_team_view', { teamId: teamA });
    await page.locator('[data-row]').filter({ hasText: 'QA同步更新' }).dispatchEvent('contextmenu'); await click(page.getByRole('menuitem', { name: '编辑', exact: true }));
    const editor = await view('editor'); await editor.locator('.editor-form').waitFor(); await hideDynamicWindows(editor);
    assert.ok(await editor.getByText('共享话术 · 售前小组', { exact: true }).count()); await editor.locator('#phrase-title').fill('QA保留草稿');
    const remote2 = await api('GET', `teams/${teamA}/snapshot`); remote2.phrases[0].title = 'QA其他成员保存'; await publish(teamA, remote2);
    await until(() => invoke('get_team_view', { teamId: teamA }), v => v.phrases[0].title === 'QA其他成员保存');
    await editor.locator('form').evaluate(el => el.requestSubmit()); await editor.locator('.validation-error').waitFor(); assert.equal(await editor.locator('#phrase-title').inputValue(), 'QA保留草稿');
    await editor.screenshot({ path: path.join(evidence, 'team-editor-conflict.png') }); await invoke('plugin:window|close', { label: 'editor' }, editor).catch(() => {});
  });
  await check('普通用户名仅访问所属库；断网及离线重启可用完整缓存，重连撤权立即清理', async () => {
    await invoke('team_logout'); await invoke('team_login', { server: base, username: '0001', password: employeePassword });
    await invoke('team_change_password', { request: { currentPassword: employeePassword, password: employeeNewPassword } });
    assert.equal((await invoke('get_library')).phrases.length, 0, '新账号不带入上一账号尚未同步的内容');
    await until(() => invoke('get_team_view', { teamId: teamA }), v => v.phrases.length === 1);
    const status = await invoke('get_team_status'); assert.equal(status.teams.length, 1); assert.equal(status.teams[0].editable, false);
    assert.ok((await invoke('search_scoped_content', { query: 'QA', scope: 'all' })).some(h => h.teamId === teamA && h.sourceLabel === '售前小组'));
    await assert.rejects(invoke('get_team_view', { teamId: teamB }));
    const current = await invoke('get_team_view', { teamId: teamA }); await invoke('prepare_team_phrase', { id: current.phrases[0].id });
    await invoke('copy_team_to_private', { id: current.phrases[0].id });
    const employeeLibrary = await invoke('get_library'); employeePrivateId = employeeLibrary.phrases[0].id;
    await invoke('save_phrase', { libraryId: employeeLibrary.libraryId, expectedRevision: employeeLibrary.privateRevisions[`phrase:${employeePrivateId}`], phrase: { ...employeeLibrary.phrases[0], title: '员工个人副本' } });
    const lastTeamSync = (await invoke('get_team_status')).teamSyncTimes[teamA]; assert.ok(lastTeamSync > 0);
    service.kill(); await until(() => Promise.resolve(service.exitCode !== null || service.signalCode !== null));
    await assert.rejects(invoke('team_refresh')); const offline = await invoke('get_team_view', { teamId: teamA }); assert.ok(offline.offline); assert.ok(offline.phrases[0].ready);
    await click(page.getByRole('button', { name: '共享话术', exact: true }));
    assert.ok(await page.getByRole('menuitem', { name: '共享话术管理', exact: true }).isDisabled());
    assert.ok(await page.getByRole('menuitem', { name: /^售前小组/ }).isEnabled());
    await click(page.getByRole('menuitem', { name: /^售前小组/ }));
    await invoke('window_action', { action: 'exit' }); await browser.close();
    await until(() => app.exitCode !== null || app.signalCode !== null); pages.clear();
    await startClient();
    const restarted = await invoke('get_team_view', { teamId: teamA });
    assert.ok(restarted.offline); assert.ok(restarted.phrases[0].ready);
    assert.equal((await invoke('get_team_status')).teamSyncTimes[teamA], lastTeamSync);
    assert.equal(restarted.phrases[0].title, offline.phrases[0].title);
    assert.equal(await fs.readFile(restarted.phrases[0].segments[0].blocks[2].path, 'utf8'), '共享话术附件QA');
    assert.ok((await invoke('search_scoped_content', { query: 'QA', scope: 'all' })).some(h => h.teamId === teamA));
    await assert.rejects(invoke('team_admin', { action: 'create-team', request: { name: '离线创建共享话术' } }));
    service = startServer(); await until(async () => (await fetch(base + '/health')).ok); await invoke('team_refresh');
    await click(page.getByRole('button', { name: '共享话术', exact: true })); await click(page.getByRole('menuitem', { name: /^售前小组/ }));
    await setMembers(teamA, []); await until(() => invoke('get_team_status'), s => s.teams.length === 0, 5000);
    assert.equal(await page.getByText('QA其他成员保存', { exact: true }).count(), 0); await assert.rejects(invoke('get_team_view', { teamId: teamA }));
    const privateCopy = (await invoke('get_library')).phrases.find(p => p.id !== privateId); assert.equal(await fs.readFile(privateCopy.segments[0].blocks[2].path, 'utf8'), '共享话术附件QA'); assert.equal(await fs.readFile(externalAsset, 'utf8'), '共享话术附件QA');
  });
  await check('默认不授权创建共享话术，授权后用户原子创建并成为管理员', async () => {
    await click(page.getByRole('button', { name: '共享话术', exact: true })); await click(page.getByRole('menuitem', { name: '共享话术管理', exact: true }));
    managementView = await view('team-management'); await managementView.locator('.team-management').waitFor(); await hideDynamicWindows(managementView);
    assert.equal(await managementView.getByRole('button', { name: '新建共享话术', exact: true }).count(), 0);
    assert.equal(await managementView.getByRole('button', { name: '账号管理', exact: true }).count(), 0);
    await api('PUT', `users/${employee}`, { canCreateTeams: true });
    await until(() => invoke('get_team_status'), s => s.user.canCreateTeams);
    await click(page.getByRole('button', { name: '共享话术', exact: true })); await until(() => page.getByRole('menuitem', { name: '新建共享话术', exact: true }).count());
    assert.ok((await page.locator('.team-dropdown').boundingBox()).width <= 250);
    await page.screenshot({ path: path.join(evidence, 'team-dropdown.png') });
    await click(page.getByRole('menuitem', { name: '新建共享话术', exact: true })); await managementView.locator('dialog input[name="name"]').fill('员工自建组');
    await managementView.locator('dialog form').evaluate(el => el.requestSubmit());
    const status = await until(() => invoke('get_team_status'), s => s.teams.some(t => t.name === '员工自建组'));
    ownTeam = status.teams.find(t => t.name === '员工自建组').teamId;
    assert.equal(status.teams.find(t => t.teamId === ownTeam).role, 'manager');
    const members = await api('GET', `teams/${ownTeam}/members`); assert.equal(members.members.length, 1); assert.equal(members.members[0].role, 'manager');
    await until(() => managementView.getByRole('button', { name: '退出共享话术', exact: true }).isDisabled());
    await managementView.screenshot({ path: path.join(evidence, 'team-manager.png') });
  });
  await check('精确用户名添加成员，草稿保存后生效；库内角色与创建共享话术权限独立', async () => {
    recipient = await api('POST', 'users', { username: '0002', displayName: '客服乙', password: recipientPassword });
    await click(managementView.getByRole('button', { name: '添加成员', exact: true }));
    await managementView.getByLabel('用户名', { exact: true }).fill('0002'); await managementView.locator('dialog form').evaluate(el => el.requestSubmit());
    await managementView.getByRole('combobox', { name: '0002的共享话术角色' }).waitFor();
    assert.equal((await api('GET', `teams/${ownTeam}/members`)).members.length, 1);
    await managementView.getByRole('combobox', { name: '0002的共享话术角色' }).selectOption('editor');
    await click(managementView.getByRole('button', { name: '保存', exact: true }));
    await until(() => api('GET', `teams/${ownTeam}/members`), s => s.members.some(m => m.userId === recipient && m.role === 'editor'));
    await invoke('publish_private_to_team', { teamId: ownTeam, id: employeePrivateId, kind: 'phrase' });
    ownSnapshot = await until(() => invoke('get_team_view', { teamId: ownTeam }), v => v.phrases.length === 1);
    await api('PUT', `users/${employee}`, { canCreateTeams: false }); await until(() => invoke('get_team_status'), s => !s.user.canCreateTeams);
    assert.equal((await invoke('get_team_status')).teams.find(t => t.teamId === ownTeam).role, 'manager');
    assert.equal(await managementView.getByRole('button', { name: '新建共享话术', exact: true }).count(), 0);
  });
  await check('管理员移交确认显示组和接收人，移交后撤下编辑与管理入口', async () => {
    await until(() => managementView.getByRole('button', { name: '移交管理员', exact: true }).isEnabled());
    await click(managementView.getByRole('button', { name: '移交管理员', exact: true }));
    assert.ok(await managementView.getByRole('heading', { name: '移交管理员 · 员工自建组', exact: true }).count());
    assert.ok(await managementView.getByRole('option', { name: '0002 · 客服乙', exact: true }).count());
    assert.ok(await managementView.getByText('移交后，你将成为查看者。', { exact: true }).count());
    await managementView.locator('dialog form').evaluate(el => el.requestSubmit());
    await until(() => invoke('get_team_status'), s => s.teams.find(t => t.teamId === ownTeam)?.role === 'member');
    await until(() => managementView.getByRole('button', { name: '删除整组共享话术', exact: true }).count(), n => n === 0);
    assert.equal(await managementView.getByRole('button', { name: '移交管理员', exact: true }).count(), 0);
    await until(() => managementView.getByRole('button', { name: '退出共享话术', exact: true }).isEnabled());
    await managementView.screenshot({ path: path.join(evidence, 'team-after-transfer.png') });
  });
  await check('成员主动退出后撤下内容与搜索、清理本库缓存且保留个人副本', async () => {
    const current = await invoke('get_team_view', { teamId: ownTeam }); await invoke('prepare_team_phrase', { id: current.phrases[0].id });
    assert.ok((await fs.readdir(path.join(clientData, 'teams'), { recursive: true })).some(p => p.includes(ownTeam)));
    await click(managementView.getByRole('button', { name: '退出共享话术', exact: true }));
    await until(() => invoke('get_team_status'), s => !s.teams.some(t => t.teamId === ownTeam));
    await assert.rejects(invoke('get_team_view', { teamId: ownTeam }));
    assert.ok(!(await invoke('search_scoped_content', { query: 'QA', scope: 'all' })).some(h => h.teamId === ownTeam));
    assert.ok(!(await fs.readdir(path.join(clientData, 'teams'), { recursive: true })).some(p => p.includes(ownTeam)));
    assert.ok((await invoke('get_library')).phrases.some(p => p.id === employeePrivateId)); assert.equal(await fs.readFile(externalAsset, 'utf8'), '共享话术附件QA');
    assert.ok(await managementView.evaluate(() => window.__qaConfirmations.some(v => v.title === '退出共享话术' && v.message.includes('员工自建组'))));
    const members = await api('GET', `teams/${ownTeam}/members`); assert.equal(members.members.length, 1); assert.equal(members.members[0].userId, recipient); assert.equal(members.members[0].role, 'manager');
  });
  await check('接收人获得本库管理权，可整库删除，成员与素材完整保留', async () => {
    await invoke('team_logout'); await invoke('team_login', { server: base, username: '0002', password: recipientPassword });
    await invoke('team_change_password', { request: { currentPassword: recipientPassword, password: randomUUID() } });
    const current = await until(() => invoke('get_team_view', { teamId: ownTeam }), v => v.phrases.length === 1);
    assert.equal(current.team.role, 'manager'); assert.equal((await invoke('get_team_status')).user.canCreateTeams, false);
    await until(() => managementView.getByRole('button', { name: '删除整组共享话术', exact: true }).isEnabled());
    await click(managementView.getByRole('button', { name: '删除整组共享话术', exact: true }));
    await until(() => invoke('get_team_status'), s => s.teams.length === 0);
    const removed = (await api('GET', 'deleted-teams')).find(t => t.teamId === ownTeam); assert.equal(removed.memberCount, 1); assert.equal(removed.phraseCount, 1); assert.equal(removed.lifecycleVersion, 2);
    await assert.rejects(invoke('get_team_view', { teamId: ownTeam }));
  });
  await check('服务管理员整库恢复，旧生命周期拒绝，永久删除需完整库名', async () => {
    await invoke('team_logout'); await invoke('team_login', { server: base, username: 'qa-admin', password });
    await until(() => managementView.getByRole('button', { name: '已删除共享话术', exact: true }).count()); await click(managementView.getByRole('button', { name: '已删除共享话术', exact: true }));
    await managementView.getByText('员工自建组', { exact: true }).waitFor(); await managementView.screenshot({ path: path.join(evidence, 'deleted-teams.png') });
    await click(managementView.getByRole('button', { name: '恢复整组共享话术', exact: true }));
    await until(() => invoke('get_team_status'), s => s.teams.some(t => t.teamId === ownTeam && t.lifecycleVersion === 3)); lifecycles.set(ownTeam, 3);
    const restored = await api('GET', `teams/${ownTeam}/snapshot`); assert.equal(restored.phrases.length, 1);
    const members = await api('GET', `teams/${ownTeam}/members`); assert.equal(members.members[0].userId, recipient); assert.equal(members.members[0].role, 'manager');
    const old = await fetch(base + `/api/teams/${ownTeam}/snapshot`, { headers: { Authorization: 'Bearer ' + token, 'x-team-lifecycle': '1' } }); assert.equal(old.status, 409);
    const materials = restored.phrases[0].segments.flatMap(s => s.blocks).filter(b => b.kind !== 'text').map(b => b.assetId);
    for (const id of materials) assert.ok((await fs.stat(path.join(serverData, 'assets', id))).isFile());
    await invoke('team_admin', { action: 'delete-team', teamId: ownTeam, request: { expectedLifecycleVersion: 3 } });
    await click(managementView.getByRole('button', { name: '刷新', exact: true })); await managementView.getByText('员工自建组', { exact: true }).waitFor();
    await click(managementView.getByRole('button', { name: '永久删除整组共享话术', exact: true })); await managementView.locator('dialog input[name="confirmName"]').fill('错误库名'); await managementView.locator('dialog form').evaluate(el => el.requestSubmit());
    await managementView.locator('dialog .validation-error').waitFor(); assert.ok((await api('GET', 'deleted-teams')).some(t => t.teamId === ownTeam));
    await managementView.locator('dialog input[name="confirmName"]').fill('员工自建组'); await managementView.locator('dialog form').evaluate(el => el.requestSubmit());
    await until(() => api('GET', 'deleted-teams'), teams => !teams.some(t => t.teamId === ownTeam));
    for (const id of materials) await assert.rejects(fs.stat(path.join(serverData, 'assets', id)));
    assert.ok(!(await api('GET', 'asset-cleanup')).some(t => t.teamId === ownTeam));
  });
  await check('用户名与共享话术双窗口并存、分别复用，资料卡与创建共享话术授权', async () => {
    await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '账号管理', exact: true }));
    userView = await view('user-management'); await userView.locator('.user-management').waitFor(); await hideDynamicWindows(userView);
    assert.deepEqual(await userView.evaluate(() => ({ width: innerWidth, height: innerHeight })), { width: 620, height: 620 });
    assert.deepEqual(await managementView.evaluate(() => ({ width: innerWidth, height: innerHeight })), { width: 680, height: 600 });
    await click(userView.getByRole('button', { name: '新建账号', exact: true }));
    await userView.getByLabel('用户名', { exact: true }).fill('0003'); await userView.getByLabel('昵称（可选）', { exact: true }).fill('客服丙'); await userView.getByLabel('联系方式（可选）', { exact: true }).fill('分机003');
    await click(userView.getByRole('button', { name: '生成密码', exact: true }));
    const generatedInput = userView.getByLabel('初始密码', { exact: true }); const generated = await generatedInput.inputValue(); assert.ok(/^[A-Za-z0-9_-]{8}$/.test(generated)); assert.equal(await generatedInput.getAttribute('type'), 'text');
    assert.equal(await generatedInput.evaluate(el => getComputedStyle(el).outlineStyle), 'none'); assert.equal(await generatedInput.evaluate(el => getComputedStyle(el).boxShadow), 'none');
    await click(userView.getByRole('button', { name: '隐藏初始密码', exact: true })); assert.ok((await generatedInput.inputValue()) === generated); assert.equal(await generatedInput.getAttribute('type'), 'password');
    await userView.screenshot({ path: path.join(evidence, 'new-user.png') });
    await userView.evaluate(() => { window.__qaPauseAction = 'create-user'; }); await userView.locator('dialog form').evaluate(el => el.requestSubmit());
    await until(() => userView.evaluate(() => !!window.__qaResume)); assert.ok(await userView.locator('.dialog-window-close').isDisabled());
    await invoke('plugin:window|close', { label: 'user-management' }, userView); await delay(100); assert.equal(userView.isClosed(), false); await userView.evaluate(() => { window.__qaResume(); delete window.__qaResume; });
    const permission = userView.getByRole('switch', { name: '0003的创建共享话术权限', exact: true }); await permission.waitFor(); assert.equal(await permission.isChecked(), false);
    await click(permission); await until(() => api('GET', 'users'), users => users.some(u => u.username === '0003' && u.canCreateTeams)); await until(() => permission.isChecked());
    await userView.getByRole('searchbox', { name: '搜索用户名', exact: true }).fill('客服丙'); assert.ok(await userView.getByRole('button', { name: '修改0003的联系方式', exact: true }).count());
    await userView.screenshot({ path: path.join(evidence, 'employee-management.png') });
    for (const menu of ['共享话术管理', '账号管理']) { await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: menu, exact: true })); }
    await managementView.getByRole('searchbox', { name: '搜索共享话术', exact: true }).waitFor();
    for (const route of ['team-management', 'user-management']) assert.equal(context.pages().filter(p => !p.isClosed() && p.url().includes('#' + route)).length, 1);
    await managementView.screenshot({ path: path.join(evidence, 'group-list.png') });
    for (const [target, label, width] of [[userView, 'user-management', 480], [managementView, 'team-management', 560]]) {
      await resizeManagement(target, label === 'user-management' ? '账号管理' : '共享话术管理', width, 440);
      await until(() => target.evaluate(() => innerWidth), value => value === width);
      const box = await target.locator(label === 'user-management' ? '.user-pagination' : '.management-pagination').boundingBox(); assert.ok(box.y >= 0 && box.y + box.height <= 440);
      assert.ok(await target.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await target.screenshot({ path: path.join(evidence, label + '-minimum.png') });
      await resizeManagement(target, label === 'user-management' ? '账号管理' : '共享话术管理', label === 'user-management' ? 620 : 680, label === 'user-management' ? 620 : 600);
    }
  });
  await check('账号紧凑三行、资料明确保存及真实服务校验失败保留草稿', async () => {
    await userView.getByRole('searchbox').fill('');
    await until(() => userView.locator('.user-card').count(), n => n >= 3);
    assert.ok(await userView.locator('.user-card').evaluateAll(cards => cards.filter(card => { const r = card.getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; }).length >= 3));
    await userView.screenshot({ path: path.join(evidence, 'accounts-default.png') });
    await click(userView.getByRole('button', { name: '修改0003的昵称', exact: true }));
    const nickname = userView.getByRole('textbox', { name: '0003的昵称', exact: true });
    await nickname.fill('客服丙更新'); await userView.getByRole('searchbox').focus();
    assert.equal((await api('GET', 'users')).find(u => u.username === '0003').displayName, '客服丙');
    await userView.screenshot({ path: path.join(evidence, 'accounts-edit.png') });
    await click(userView.getByRole('button', { name: '保存昵称', exact: true }));
    await until(() => api('GET', 'users'), users => users.find(u => u.username === '0003').displayName === '客服丙更新');
    await until(() => nickname.count(), n => n === 0);
    assert.equal((await api('GET', 'users')).find(u => u.username === '0003').contact, '分机003');
    await click(userView.getByRole('button', { name: '修改0003的联系方式', exact: true }));
    const contact = userView.getByRole('textbox', { name: '0003的联系方式', exact: true });
    await contact.fill('校验' + String.fromCharCode(1)); await click(userView.getByRole('button', { name: '保存联系方式', exact: true }));
    await userView.getByRole('alert').waitFor(); assert.equal(await contact.inputValue(), '校验' + String.fromCharCode(1));
    assert.equal((await api('GET', 'users')).find(u => u.username === '0003').contact, '分机003');
    await userView.screenshot({ path: path.join(evidence, 'accounts-edit-error.png') });
    await resizeManagement(userView, '账号管理', 480, 440);
    await contact.scrollIntoViewIfNeeded(); await userView.screenshot({ path: path.join(evidence, 'accounts-minimum-edit-error.png') });
    assert.ok(await userView.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await click(userView.getByRole('button', { name: '取消修改联系方式', exact: true }));
    await click(userView.getByRole('button', { name: '关闭提示', exact: true }));
    await userView.locator('.user-cards').evaluate(el => { el.scrollTop = 0; });
    await userView.screenshot({ path: path.join(evidence, 'accounts-minimum.png') });
    await resizeManagement(userView, '账号管理', 620, 620);
  });
  await check('20条分页、长用户名、成员全草稿保存和旧服务人数兼容', async () => {
    const additions = [];
    for (let i = 0; i < 21; i++) additions.push({ id: await api('POST', 'users', { username: i === 20 ? '0000000000000000000000000000000024' : `UI${String(i + 4).padStart(4, '0')}`, displayName: i === 20 ? '多库成员与较长昵称展示检查' : `客服${i + 4}`, contact: i === 20 ? '较长联系方式'.repeat(10) : '', password: randomUUID() }) });
    await invoke('team_refresh');
    await userView.getByRole('searchbox', { name: '搜索用户名', exact: true }).fill('');
    await until(() => userView.locator('.user-card').count(), value => value === 20);
    assert.ok(await userView.getByRole('button', { name: '下一页', exact: true }).isEnabled()); await click(userView.getByRole('button', { name: '下一页', exact: true }));
    await until(() => userView.locator('.user-card').count(), value => value > 0 && value < 20); await userView.screenshot({ path: path.join(evidence, 'employee-page-two.png') });
    await userView.getByRole('searchbox', { name: '搜索用户名', exact: true }).fill('0000000000000000000000000000000024'); await userView.getByText('0000000000000000000000000000000024', { exact: true }).waitFor(); await userView.screenshot({ path: path.join(evidence, 'employee-long-fields.png') });
    await setMembers(teamB, additions.map(u => ({ userId: u.id, teamId: teamB, role: 'member' }))); await invoke('team_refresh');
    await setMembers(teamA, [{ userId: additions.at(-1).id, teamId: teamA, role: 'editor' }]); await invoke('team_refresh');
    await until(() => userView.locator('.user-membership-more').count(), value => value === 1);
    await click(userView.locator('.user-membership-more'));
    await userView.getByRole('menu').waitFor();
    for (const name of ['售前小组 · 编辑者', '售后小组 · 查看者']) assert.equal(await userView.locator('.membership-link, [role="menuitem"]').filter({ hasText: name.split(' · ')[0] }).count(), 1);
    await userView.screenshot({ path: path.join(evidence, 'employee-membership-popup.png') });
    await userView.locator('[role="menuitem"]').first().press('Escape');
    await userView.screenshot({ path: path.join(evidence, 'employee-long-fields.png') });
    await resizeManagement(userView, '账号管理', 480, 440);
    assert.ok(await userView.locator('.user-card-heading, .user-card-details, .user-card-groups').evaluateAll(rows => rows.every(row => row.scrollWidth <= row.clientWidth)));
    await click(userView.locator('.user-membership-more')); await userView.getByRole('menu').waitFor();
    const popupBox = await userView.getByRole('menu').boundingBox(); assert.ok(popupBox.x >= 0 && popupBox.y >= 0 && popupBox.x + popupBox.width <= 480 && popupBox.y + popupBox.height <= 440);
    await userView.screenshot({ path: path.join(evidence, 'accounts-minimum-memberships.png') });
    await userView.getByRole('menuitem').first().press('Escape');
    await resizeManagement(userView, '账号管理', 620, 620);
    const group = managementView.locator('.management-groups tbody tr').filter({ hasText: '售后小组' }); await until(() => group.locator('td').nth(1).textContent(), value => value === '21');
    await click(group.getByRole('button', { name: '管理成员', exact: true }));
    await managementView.getByRole('combobox', { name: 'UI0004的共享话术角色', exact: true }).selectOption('editor');
    await click(managementView.getByRole('button', { name: '下一页', exact: true })); await managementView.getByRole('combobox', { name: 'UI0023的共享话术角色', exact: true }).selectOption('editor');
    await managementView.getByRole('searchbox', { name: '搜索成员', exact: true }).fill('UI0005'); await click(managementView.getByRole('button', { name: '移出', exact: true }));
    assert.equal((await api('GET', `teams/${teamB}/members`)).members.length, 21); await click(managementView.getByRole('button', { name: '保存', exact: true }));
    const saved = await until(() => api('GET', `teams/${teamB}/members`), value => value.members.length === 20); assert.equal(saved.members.filter(m => m.role === 'editor').length, 2); assert.ok(!saved.members.some(m => m.username === 'UI0005'));
    await managementView.getByRole('searchbox', { name: '搜索成员', exact: true }).fill(''); await managementView.screenshot({ path: path.join(evidence, 'group-members.png') });
    const saveBox = await managementView.locator('.management-save').boundingBox(); assert.ok(saveBox.y + saveBox.height <= 600);
    await click(managementView.getByRole('button', { name: '返回', exact: true }));
    // 仅在 WebView 的授权事件中省略人数，模拟旧服务；不改服务器数据或权限。
    await managementView.evaluate(async () => { const status = await window.__TAURI_INTERNALS__.invoke('get_team_status'); status.teams = status.teams.map(({ memberCount, ...team }) => team); await window.__TAURI_INTERNALS__.invoke('plugin:event|emit_to', { target: { kind: 'WebviewWindow', label: 'team-management' }, event: 'teams-changed', payload: status }); });
    await until(() => managementView.locator('.management-groups tbody tr td:nth-child(2)').allTextContents(), values => values.length && values.every(value => value === '—'));
    await invoke('team_refresh');
  });
  await check('成员草稿关闭取消后保留，确认放弃后销毁并可重新打开', async () => {
    const group = managementView.locator('.management-groups tbody tr').filter({ hasText: '售后小组' }); await click(group.getByRole('button', { name: '管理成员', exact: true }));
    const role = managementView.getByRole('combobox', { name: 'UI0004的共享话术角色', exact: true }); await role.selectOption('member');
    await managementView.evaluate(() => { window.__qaCancelNext = true; }); const before = await managementView.evaluate(() => window.__qaConfirmations.length);
    await managementView.getByRole('button', { name: '关闭窗口', exact: true }).click(); await until(() => managementView.evaluate(() => window.__qaConfirmations.length), n => n > before);
    await delay(100); assert.equal(managementView.isClosed(), false); assert.equal(await role.inputValue(), 'member');
    assert.equal((await api('GET', `teams/${teamB}/members`)).members.find(m => m.username === 'UI0004').role, 'editor');
    await managementView.getByRole('button', { name: '关闭窗口', exact: true }).click(); await until(() => managementView.isClosed());
    await click(page.getByRole('button', { name: '主菜单', exact: true })); await click(page.getByRole('menuitem', { name: '共享话术管理', exact: true }));
    managementView = await view('team-management'); await managementView.locator('.management-groups').waitFor(); await hideDynamicWindows(managementView);
  });
  await check('设置保留连接与管理入口；退出清理凭据与缓存', async () => {
    await click(page.getByRole('button', { name: '设置', exact: true })); const settings = await view('settings'); await settings.locator('.settings-sidebar').waitFor(); await hideDynamicWindows(settings); await click(settings.getByRole('button', { name: '账号与共享', exact: true }));
    await settings.locator('.team-settings').waitFor(); await settings.screenshot({ path: path.join(evidence, 'team-settings.png') });
    const identityLayout = await settings.locator('.account-user').evaluate(el => { const name = el.querySelector('.account-user-name').getBoundingClientRect(), role = el.querySelector('small').getBoundingClientRect(); return { delta: Math.abs(name.top - role.top), overlap: name.right > role.left, title: el.querySelector('.account-user-name').title }; });
    assert.ok(identityLayout.delta < 4); assert.equal(identityLayout.overlap, false); assert.ok(identityLayout.title);
    assert.ok(await settings.locator('.shared-library-settings .sync-row').evaluate(el => { const label = el.querySelector('strong').getBoundingClientRect(), button = el.querySelector('button').getBoundingClientRect(); return Math.abs(label.top + label.height / 2 - button.top - button.height / 2) < 3 && el.scrollWidth <= el.clientWidth; }));
    const content = settings.locator('.settings-content');
    const layout = await content.evaluate(el => ({ height: el.clientHeight, scroll: el.scrollHeight, width: el.clientWidth, scrollWidth: el.scrollWidth, overflowY: getComputedStyle(el).overflowY }));
    assert.ok(layout.scroll >= layout.height); assert.ok(['auto', 'scroll'].includes(layout.overflowY)); assert.ok(layout.scrollWidth <= layout.width + 1);
    // 账号页增加成员跟随与分享后允许纵向滚动，底部管理入口和顶部账号操作都必须可达。
    for (const name of ['管理话术', '账号管理', '退出登录']) {
      const button = settings.getByRole('button', { name, exact: true }); await button.scrollIntoViewIfNeeded();
      const area = await content.boundingBox(), box = await button.boundingBox(); assert.ok(box.y >= area.y && box.y + box.height <= area.y + area.height + 1);
    }
    for (const [target, name, width, height] of [[settings, 'account', 594, 620], [userView, 'users', 620, 620], [managementView, 'groups', 680, 600]]) {
      const cdp = await context.newCDPSession(target);
      for (const scale of [1.5, 2]) {
        await cdp.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: scale, mobile: false });
        assert.ok(await target.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await target.screenshot({ path: path.join(evidence, name + '-render-' + scale * 100 + '.png') });
      }
      await cdp.send('Emulation.clearDeviceMetricsOverride'); await cdp.detach();
    }
    await invoke('team_logout'); assert.equal((await invoke('get_team_status')).user, null);
    await until(() => userView.locator('.user-card').count(), value => value === 0); await until(() => managementView.locator('.management-groups').count(), value => value === 0);
    const directories = await fs.readdir(path.join(clientData, 'teams'), { withFileTypes: true }); assert.equal(directories.filter(d => d.isDirectory() && d.name !== 'temp').length, 0);
  });
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ date: new Date().toISOString(), checks: results, limits: ['真实客户端及HTTP/SSE，使用隔离数据和凭据；未进行聊天发送或剪贴板操作', '管理窗移到屏幕外正常创建，实际原生默认／最小客户区；系统确认按钮已模拟确认', '100%为本机真实DPI；150%/200%为WebView渲染倍率，不替代系统DPI，未覆盖真实局域网、HTTPS代理或安装升级'] }, null, 2));
  console.log(JSON.stringify({ evidence, checks: results.length }));
} catch (error) {
  for (const target of context?.pages() || []) if (!target.isClosed() && target.url().includes('#user-management')) await target.screenshot({ path: path.join(evidence, 'accounts-failure.png') }).catch(() => {});
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks: results, error: String(error) }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) { await invoke('team_logout').catch(() => {}); await invoke('window_action', { action: 'exit' }).catch(() => {}); }
  await browser?.close().catch(() => {}); if (app?.exitCode === null) app.kill(); if (service.exitCode === null) service.kill();
}
