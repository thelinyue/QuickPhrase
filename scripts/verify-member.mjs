import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';

// 隔离数据下运行真实 Windows IPC、凭据、HTTP、SSE 与 WebView2；不发送聊天或操作系统剪贴板。
const repo = process.cwd(), evidence = path.join(repo, '.build', `member-${Date.now()}`);
if (!process.env.SHANYU_TEAM_SERVER_EXE) throw new Error('请通过 SHANYU_TEAM_SERVER_EXE 指定 Hub 程序。');
await fs.mkdir(evidence, { recursive: true });
const serverExe = path.join(evidence, 'quickphrase-hub.exe');
await fs.copyFile(path.resolve(process.env.SHANYU_TEAM_SERVER_EXE), serverExe);
const port = async () => { const server = net.createServer(); await new Promise(r => server.listen(0, '127.0.0.1', r)); const value = server.address().port; await new Promise(r => server.close(r)); return value; };
const delay = ms => new Promise(r => setTimeout(r, ms));
const until = async (work, accept = v => !!v, timeout = 18000) => { const start = Date.now(); while (Date.now() - start < timeout) { try { const value = await work(); if (accept(value)) return value; } catch {} await delay(80); } throw new Error('等待成员联调结果超时'); };
const apiPort = await port(), base = `http://127.0.0.1:${apiPort}`;
const initial = randomUUID(), password = randomUUID(), tokens = {}, users = {};
let service, app, browser, context, page;
const checks = [], faults = [];
const check = async (label, work) => { await work(); checks.push(label); console.log('通过：' + label); };
const startServer = () => spawn(serverExe, [], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, QUICKPHRASE_DATA_DIR: path.join(evidence, 'server'), QUICKPHRASE_LISTEN: `127.0.0.1:${apiPort}`, QUICKPHRASE_ADMIN_USERNAME: 'qa-admin', QUICKPHRASE_ADMIN_PASSWORD: initial } });
const api = async (who, method, route, body) => { const response = await fetch(`${base}/api/${route}`, { method, headers: { ...(tokens[who] ? { Authorization: 'Bearer ' + tokens[who] } : {}), 'Content-Type': 'application/json' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) }); assert.ok(response.ok, `接口返回 ${response.status}`); return response.json(); };
const invoke = (cmd, args = {}, view = page) => view.evaluate(({ cmd, args }) => window.__TAURI_INTERNALS__.invoke(cmd, args), { cmd, args });
async function hook(view) {
  view.on('pageerror', error => faults.push(String(error)));
  await view.evaluate(() => { const original = window.fetch.bind(window); window.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input.url); if (url.hostname !== 'ipc.localhost') return original(input, init);
    const command = decodeURIComponent(url.pathname.slice(1));
    if (['plugin:window|show', 'plugin:window|set_focus'].includes(command)) return new Response('null', { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } });
    if (command === 'plugin:dialog|message') { const args = JSON.parse(init.body); return new Response(JSON.stringify(args.buttons?.OkCancelCustom?.[0] || 'Ok'), { headers: { 'Tauri-Response': 'ok', 'Content-Type': 'application/json' } }); }
    if (command === 'plugin:webview|create_webview_window') { const args = JSON.parse(init.body); args.options = { ...args.options, visible: true, x: -20000, y: -20000, center: false, focus: false, skipTaskbar: true }; return original(input, { ...init, body: JSON.stringify(args) }); }
    return original(input, init);
  }; });
}
async function stopClient() {
  if (page && !page.isClosed()) await invoke('window_action', { action: 'exit' }).catch(() => {});
  await browser?.close().catch(() => {});
  if (app?.exitCode === null) { app.kill(); await until(() => app.exitCode !== null || app.signalCode !== null); }
  page = undefined; browser = undefined;
}
async function startClient(device) {
  await stopClient(); const debugPort = await port();
  app = spawn(path.join(repo, 'src-tauri/target/debug/QuickPhrase.exe'), ['--autostart'], { cwd: repo, windowsHide: true, stdio: 'ignore', env: { ...process.env, SHANYU_TEST_DATA_DIR: path.join(evidence, device), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort}` } });
  browser = await until(() => chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`)); context = browser.contexts()[0];
  page = await until(() => context.pages().find(p => !new URL(p.url()).hash)); await page.locator('.main-window').waitFor(); await hook(page);
  await page.getByRole('status').evaluateAll(nodes => nodes.forEach(node => node.click()));
}
async function settings(alias = '账号与共享') {
  await invoke('plugin:webview|create_webview_window', { options: { label: 'settings', title: '设置 · 闪语', url: 'index.html#settings?' + new URLSearchParams({ tab: alias }), width: 594, height: 620, decorations: false } });
  const view = await until(() => context.pages().find(p => !p.isClosed() && p.url().includes('#settings')));
  await hook(view); await view.getByRole('heading', { name: '账号与共享', exact: true }).waitFor(); return view;
}
const click = locator => locator.evaluate(el => el.click());
const memberView = accept => until(() => invoke('get_member_view'), accept);
const synced = () => until(() => invoke('get_private_status'), s => s.enabled && s.state === '已同步' && s.pending === 0 && s.lastSuccessfulSyncAt > 0);
const login = username => invoke('team_login', { server: base, username, password });
const category = (name, parentId = null) => invoke('save_category', { category: { id: '', name, kind: 'phrase', parentId, position: 0, background: '' } });
let sourceRoot, secretRoot, group, sourcePhrase, grant, ownId, oldKey, savedPath;
async function editSource(change) {
  const snapshot = await api('source', 'GET', 'me/private/snapshot'); const record = snapshot.records.find(r => r.kind === 'phrase' && r.id === sourcePhrase);
  const value = change(structuredClone(record.value));
  await api('source', 'POST', 'me/private/mutations', { generation: snapshot.generation, requestId: randomUUID(), changes: [{ kind: 'phrase', id: sourcePhrase, expectedVersion: record.version, value }], newAssets: [] });
}
try {
  service = startServer(); await until(async () => (await fetch(base + '/health')).ok);
  const admin = await api('admin', 'POST', 'login', { username: 'qa-admin', password: initial }); tokens.admin = admin.token;
  await api('admin', 'PUT', 'password', { currentPassword: initial, password });
  for (const [key, username] of [['source', '001'], ['recipient', '002']]) {
    users[key] = await api('admin', 'POST', 'users', { username, password });
    const session = await api(key, 'POST', 'login', { username, password }); tokens[key] = session.token;
    await api(key, 'PUT', 'password', { currentPassword: password, password });
  }
  await startClient('source'); await login('001');
  await check('来源本人同步图文附件，开放共享先选择范围再生效', async () => {
    sourceRoot = await category('常用'); group = await category('二级分类', sourceRoot); secretRoot = await category('不分享');
    const [image] = await invoke('import_images', { paths: [path.join(repo, 'src-tauri/icons/icon.png')] });
    const external = path.join(evidence, '原附件.txt'); await fs.writeFile(external, 'member-attachment');
    sourcePhrase = await invoke('save_phrase', { phrase: { id: '', rootId: sourceRoot, groupId: group, title: '成员验收话术', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '成员联调内容' }, image, { kind: 'file', path: external, name: '原附件.txt' }] }] } });
    await invoke('save_common_phrase', { phrase: { id: '', text: '私人短语', position: 0 } });
    const preview = await invoke('preview_private_sync'); await invoke('confirm_private_sync', { request: { previewId: preview.id, choices: {} } }); await synced();
    const account = await invoke('get_team_status'); assert.equal(account.memberPhraseSync, true);
    const view = await settings('账号与同步');
    await until(() => view.getByRole('button', { name: '开启共享', exact: true }).isEnabled()); await click(view.getByRole('button', { name: '开启共享', exact: true }));
    assert.equal(await view.getByRole('radio', { name: '全部分类（包含今后新增分类）' }).isChecked(), true);
    const radios = view.getByRole('radio');
    assert.ok(await radios.evaluateAll(nodes => nodes.every(el => el.getBoundingClientRect().width === 15 && el.getBoundingClientRect().height === 15)));
    assert.ok(await view.locator('.member-root-choices').evaluate(el => el.scrollWidth <= el.clientWidth));
    await view.getByRole('radio', { name: '指定分类', exact: true }).check();
    const root = view.getByRole('checkbox', { name: '常用', exact: true }); await root.waitFor(); assert.equal(await root.isChecked(), false);
    assert.equal(await view.getByRole('checkbox').count(), 2);
    const choice = await root.evaluate(el => { const label = el.closest('label').getBoundingClientRect(), box = el.getBoundingClientRect(); return { direction: getComputedStyle(el.closest('label')).flexDirection, labelLeft: label.left, boxLeft: box.left, labelHeight: label.height }; });
    assert.equal(choice.direction, 'row'); assert.ok(choice.boxLeft - choice.labelLeft < 2); assert.ok(choice.labelHeight < 36);
    await root.check(); assert.equal(await view.getByRole('button', { name: '确认共享', exact: true }).isEnabled(), true);
    await view.screenshot({ path: path.join(evidence, 'share-scope.png') }); await click(view.getByRole('button', { name: '确认共享', exact: true })); await view.locator('dialog').waitFor({ state: 'hidden' });
    const overview = await invoke('get_member_overview', { sessionEpoch: account.sessionEpoch }); grant = overview.sharing; assert.deepEqual(grant.rootIds, [sourceRoot]);
    await view.close();
  });
  await startClient('recipient-a'); await login('002');
  await check('接收方不启用本人同步即可使用，分类同名隔离、两级只读且个人库不混入缓存', async () => {
    const root = await category('常用'); ownId = await invoke('save_phrase', { phrase: { id: '', rootId: root, groupId: null, title: '本人保留', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '本人内容' }] }] } });
    assert.equal((await invoke('get_private_status')).enabled, false);
    const view = await settings('共享话术'); await until(() => view.getByRole('button', { name: '选择成员', exact: true }).isEnabled());
    await click(view.getByRole('button', { name: '选择成员', exact: true })); await view.getByRole('combobox').selectOption(grant.id); await click(view.getByRole('button', { name: '使用话术', exact: true })); await view.locator('dialog').waitFor({ state: 'hidden' });
    const member = await memberView(v => v.phrases.length === 1); oldKey = member.phrases[0].id;
    assert.equal(member.categories.length, 2); assert.ok(member.categories.every(c => c.readOnly)); assert.ok(!member.phrases[0].shortcut);
    const categories = page.locator('.category-tabs button:not(.add-category)'); await until(() => categories.count(), n => n === 2); assert.equal(await categories.nth(0).innerText(), '常用'); assert.match(await categories.nth(1).innerText(), /001.*只读/);
    await click(categories.nth(1)); await page.getByRole('button', { name: '二级分类', exact: true }).waitFor(); assert.equal(await page.locator('.group-edit').count(), 0);
    await page.screenshot({ path: path.join(evidence, 'mixed-readonly.png') });
    await page.locator(`[data-row="${oldKey}"]`).dispatchEvent('contextmenu', { clientX: 40, clientY: 160 });
    assert.equal(await page.getByRole('menuitem', { name: '编辑', exact: true }).count(), 0); assert.equal(await page.getByRole('menuitem', { name: '复制到共享话术', exact: true }).count(), 0);
    await page.screenshot({ path: path.join(evidence, 'readonly-actions.png') }); await page.keyboard.press('Escape');
    const own = await invoke('get_library'); assert.equal(own.phrases.length, 1); assert.equal(own.phrases[0].id, ownId); assert.equal(own.categories.length, 1);
    await assert.rejects(invoke('save_category', { category: { ...member.categories[0], name: '禁止修改' } }));
    await assert.rejects(invoke('save_phrase', { phrase: { ...member.phrases[0], title: '禁止修改' } }));
    const syncLabel = await view.locator('.private-sync-settings .sync-row > strong').evaluate(el => { const range = document.createRange(); range.selectNodeContents(el); return { wrap: getComputedStyle(el).whiteSpace, lines: range.getClientRects().length }; });
    assert.equal(syncLabel.wrap, 'nowrap'); assert.equal(syncLabel.lines, 1);
    await view.screenshot({ path: path.join(evidence, 'account-sharing.png') }); await view.close();
  });
  await check('图片自动缓存、附件按需下载，主搜索与全部搜索都保留成员来源', async () => {
    await memberView(v => v.phrases[0]?.segments[0].blocks.find(b => b.kind === 'image')?.path);
    const before = await invoke('get_member_view'); assert.equal(before.phrases[0].ready, false);
    await invoke('download_member_materials', { ids: [oldKey], attachments: true }); const ready = await memberView(v => v.phrases[0]?.ready);
    savedPath = ready.phrases[0].segments[0].blocks.find(b => b.kind === 'file').path; assert.equal(await fs.readFile(savedPath, 'utf8'), 'member-attachment');
    for (const scope of ['private', 'all']) { const hits = await invoke('search_scoped_content', { query: '成员验收', scope }); assert.equal(hits.length, 1); assert.equal(hits[0].phrase.contentRef.source, 'member'); assert.match(hits[0].sourceLabel, /001/); }
    await until(() => invoke('get_member_view'), v => v.phrases[0].ready); assert.equal((await invoke('get_library')).phrases.length, 1);
  });
  await startClient('recipient-b'); await login('002');
  await check('同账号第二设备读取使用选择，停止会清除缓存并同步回第一设备', async () => {
    const member = await memberView(v => v.phrases.length === 1); const account = await invoke('get_team_status');
    assert.equal((await api('source', 'GET', 'me/phrase-grants')).users.length, 1);
    await invoke('set_member_subscription', { sessionEpoch: account.sessionEpoch, request: { grantId: null, expectedVersion: member.subscription.version } }); await memberView(v => !v.subscription.grantId && !v.phrases.length);
    assert.equal((await api('source', 'GET', 'me/phrase-grants')).users.length, 0);
    await startClient('recipient-a'); await memberView(v => !v.subscription.grantId && !v.phrases.length);
    const nextAccount = await invoke('get_team_status'), overview = await invoke('get_member_overview', { sessionEpoch: nextAccount.sessionEpoch });
    await invoke('set_member_subscription', { sessionEpoch: nextAccount.sessionEpoch, request: { grantId: grant.id, expectedVersion: overview.subscription.version } });
    const next = await memberView(v => v.phrases.length === 1); oldKey = next.phrases[0].id;
    await invoke('download_member_materials', { ids: [oldKey], attachments: true });
  });
  await check('共享范围和人数同一行，名单排序滚动、Esc关闭及焦点恢复；暂停与离线状态明确', async () => {
    await startClient('source');
    const extra = [];
    for (let n = 3; n <= 14; n++) {
      const username = `m${String(n).padStart(3, '0')}`;
      await api('admin', 'POST', 'users', { username, displayName: n === 3 ? '测试成员' : '', password });
      tokens[username] = (await api(username, 'POST', 'login', { username, password })).token;
      await api(username, 'PUT', 'password', { currentPassword: password, password });
      await api(username, 'PUT', 'me/phrase-subscription', { grantId: grant.id, expectedVersion: 0 }); extra.push(username);
    }
    const view = await settings();
    const count = view.getByRole('button', { name: '13 人使用', exact: true }); await count.waitFor();
    assert.match(await count.locator('..').innerText(), /已选 1 个分类.*13 人使用/s);
    const summaryLayout = () => count.evaluate(el => { const range = el.parentElement.firstElementChild.getBoundingClientRect(); const count = el.getBoundingClientRect(); return { delta: Math.abs(range.top - count.top), left: count.left, right: range.right }; });
    assert.ok((await summaryLayout()).delta < 3);
    assert.ok(await view.locator('.account-user').evaluate(el => { const name = el.querySelector('.account-user-name').getBoundingClientRect(), role = el.querySelector('small').getBoundingClientRect(); return Math.abs(name.top-role.top) < 4 && name.right <= role.left; }));
    const cdp = await context.newCDPSession(view);
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 480, height: 620, deviceScaleFactor: 1, mobile: false });
    assert.ok((await summaryLayout()).delta < 3);
    assert.ok(await view.locator('.account-user').evaluate(el => { const name = el.querySelector('.account-user-name').getBoundingClientRect(), role = el.querySelector('small').getBoundingClientRect(); return Math.abs(name.top-role.top) < 4 && name.right <= role.left; }));
    assert.ok(await view.locator('.sync-sharing-settings').evaluate(el => el.scrollWidth <= el.clientWidth));
    await view.screenshot({ path: path.join(evidence, 'narrow-sharing.png') });
    await cdp.send('Emulation.clearDeviceMetricsOverride'); await cdp.detach();

    assert.equal(await view.getByRole('button', { name: '重新连接', exact: true }).count(), 0);
    assert.equal(await view.locator('.sync-sharing-settings').count(), 1);
    assert.equal(await view.locator('.sync-sharing-settings .sync-row').count(), 3);
    assert.ok(await view.locator('.shared-library-settings .sync-row').evaluate(el => { const label = el.querySelector('strong').getBoundingClientRect(), button = el.querySelector('button').getBoundingClientRect(); return Math.abs(label.top + label.height / 2 - button.top - button.height / 2) < 3 && el.scrollWidth <= el.clientWidth; }));
    assert.ok(await view.locator('.sync-sharing-settings').evaluate(el => el.scrollWidth <= el.clientWidth));
    await count.focus(); await click(count);
    const dialog = view.getByRole('dialog', { name: '使用成员（13）' }); await dialog.waitFor();
    await until(() => dialog.locator('tbody tr').count(), n => n === 13);
    const names = await dialog.locator('tbody td:first-child').allTextContents();
    assert.equal(names[0], '002'); assert.equal(names[1], '测试成员（m003）'); assert.equal(names.at(-1), 'm014');
    assert.ok((await dialog.locator('tbody td:last-child').allTextContents()).every(text => text === '常用'));
    await dialog.getByRole('columnheader', { name: '使用分类', exact: true }).waitFor();
    assert.ok(await dialog.locator('.account-panel-body').evaluate(el => el.scrollHeight > el.clientHeight));
    assert.equal(await dialog.locator('.account-panel-body').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 255, 255)');
    assert.ok(await dialog.evaluate(el => el.querySelector('.account-panel-body').getBoundingClientRect().bottom < el.querySelector('.dialog-actions').getBoundingClientRect().top));
    await view.screenshot({ path: path.join(evidence, 'usage-members.png') });
    await view.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' });
    assert.equal(await count.evaluate(el => document.activeElement === el), true);
    await click(view.getByRole('button', { name: '暂停同步', exact: true }));
    await view.getByText('同步已暂停，共享保留最后同步内容。').waitFor();
    await view.screenshot({ path: path.join(evidence, 'unified-sharing.png') });
    service.kill(); await until(() => service.exitCode !== null || service.signalCode !== null);
    await assert.rejects(invoke('team_refresh')); await click(count);
    await dialog.getByText('离线数据', { exact: true }).waitFor();
    await view.screenshot({ path: path.join(evidence, 'offline-users.png') });
    await click(dialog.getByRole('button', { name: '关闭', exact: true }));
    await view.getByRole('button', { name: '重新连接', exact: true }).waitFor();
    service = startServer(); await until(async () => (await fetch(base + '/health')).ok);
    // 后台可能已自动重连并撤下按钮；在同一浏览器任务内查找和点击，避免等待已消失的入口。
    await view.evaluate(() => Array.from(document.querySelectorAll('button')).find(button => button.textContent === '重新连接')?.click());
    await view.getByRole('button', { name: '重新连接', exact: true }).waitFor({ state: 'hidden' });
    for (const username of extra) await api(username, 'PUT', 'me/phrase-subscription', { grantId: null, expectedVersion: 1 });
    await until(async () => (await api('source', 'GET', 'me/phrase-grants')).users.length, n => n === 1);
    await view.close(); await startClient('recipient-a'); await memberView(v => v.phrases[0]?.ready);
  });
  await check('断网与离线重启保留完整缓存，联网后恢复授权核验', async () => {
    service.kill(); await until(() => service.exitCode !== null || service.signalCode !== null);
    await assert.rejects(invoke('team_refresh')); assert.equal((await invoke('get_team_status')).offline, true);
    await startClient('recipient-a'); const cached = await memberView(v => v.state === '离线' && v.phrases[0]?.ready); oldKey = cached.phrases[0].id;
    service = startServer(); await until(async () => (await fetch(base + '/health')).ok); await invoke('team_refresh');
    await memberView(v => v.state === '已同步' && v.phrases.length === 1);
  });
  await check('来源编辑经 SSE 更新，旧引用拒绝；移出范围撤下正文及素材', async () => {
    await editSource(value => ({ ...value, title: '来源已更新' })); const edited = await memberView(v => v.phrases[0]?.title === '来源已更新');
    assert.notEqual(edited.phrases[0].id, oldKey);
    await assert.rejects(invoke('show_phrase_preview', { id: oldKey, anchor: { left: 0, top: 0, right: 50, bottom: 50 } }));
    await editSource(value => ({ ...value, rootId: secretRoot, groupId: null })); await memberView(v => v.phrases.length === 0);
    const oldPath = edited.phrases[0].segments[0].blocks.find(b => b.kind === 'file').path; await assert.rejects(fs.access(oldPath));
    await editSource(value => ({ ...value, rootId: sourceRoot, groupId: group })); const restored = await memberView(v => v.phrases.length === 1); oldKey = restored.phrases[0].id;
  });
  await check('来源停止分享立即撤下缓存，重新分享不自动恢复使用，退出清理成员缓存', async () => {
    await api('source', 'PUT', `me/phrase-sharing`, { enabled: false, mode: "selected", rootIds: [], expectedVersion: grant.version });
    await memberView(v => !v.phrases.length && !v.subscription.grantId);
    await assert.rejects(invoke('download_member_materials', { ids: [oldKey], attachments: true }));
    const overview = await api('source', 'GET', 'me/phrase-grants'); grant = overview.sharing;
    await api('source', 'PUT', `me/phrase-sharing`, { enabled: true, mode: "selected", rootIds: [sourceRoot], expectedVersion: grant.version });
    const after = await invoke('get_member_view'); assert.equal(after.subscription.grantId, null); assert.equal(after.phrases.length, 0);
    assert.equal((await invoke('get_library')).phrases[0].id, ownId);
  });
  await check('修改连接预填当前值，认证失败保留原连接；切换账号隔离本人内容', async () => {
    const view = await settings();
    const before = await invoke('get_team_status');
    const trigger = view.getByRole('button', { name: '修改连接', exact: true }); await click(trigger);
    const dialog = view.getByRole('dialog', { name: '修改连接', exact: true }); await dialog.waitFor();
    assert.equal(await dialog.getByLabel('服务器地址', { exact: true }).inputValue(), base);
    assert.equal(await dialog.getByLabel('用户名', { exact: true }).inputValue(), '002');
    assert.equal(await dialog.getByLabel('密码', { exact: true }).inputValue(), '');
    await view.screenshot({ path: path.join(evidence, 'modify-connection.png') });
    await dialog.getByLabel('服务器地址', { exact: true }).fill(base + '/invalid');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true })); await dialog.getByRole('alert').waitFor();
    const failed = await invoke('get_team_status'); assert.equal(failed.sessionEpoch, before.sessionEpoch); assert.equal(failed.user.id, before.user.id);
    await dialog.getByLabel('服务器地址', { exact: true }).fill(base);
    await dialog.getByLabel('用户名', { exact: true }).fill('001');
    await dialog.getByLabel('密码', { exact: true }).fill('incorrect-password');
    await click(dialog.getByRole('button', { name: '连接', exact: true }));
    await until(() => dialog.getByRole('alert').innerText(), text => text.includes('用户名') && text.includes('密码'));
    assert.equal((await invoke('get_team_status')).sessionEpoch, before.sessionEpoch);
    await dialog.getByLabel('密码', { exact: true }).fill('');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true }));
    await dialog.waitFor({ state: 'hidden' }); await until(() => invoke('get_team_status'), value => value.user?.username === '001');
    assert.ok(!(await invoke('get_library')).phrases.some(p => p.id === ownId));
    await click(trigger); await dialog.waitFor(); await dialog.getByLabel('用户名', { exact: true }).fill('002');
    assert.equal(await dialog.getByLabel('密码', { exact: true }).inputValue(), '');
    await dialog.getByLabel('密码', { exact: true }).fill(password);
    await click(dialog.getByRole('button', { name: '连接', exact: true })); await dialog.waitFor({ state: 'hidden' });
    await until(() => invoke('get_library'), value => value.phrases.some(p => p.id === ownId));
    await view.close(); await invoke('team_logout'); assert.equal((await invoke('get_member_view')).phrases.length, 0);
  });
  assert.deepEqual(faults, []);
  await fs.writeFile(path.join(evidence, 'report.json'), JSON.stringify({ checks, clientVersion: await invoke('plugin:app|version'), limits: ['真实 Windows WebView2、IPC、SQLite、凭据和回环 HTTP/SSE；接收方两个隔离设备轮流运行', '未触发真实企业微信、剪贴板或实体输入；局域网、代理、DPI和安装升级未验证'] }, null, 2));
  console.log('证据：' + evidence);
} catch (error) {
  await page?.screenshot({ path: path.join(evidence, 'failure.png') }).catch(() => {});
  await context?.pages().find(p => !p.isClosed() && p.url().includes('#settings'))?.screenshot({ path: path.join(evidence, 'failure-settings.png') }).catch(() => {});
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({ checks, error: String(error) }, null, 2)); throw error;
} finally {
  if (page && !page.isClosed()) await invoke('team_logout').catch(() => {});
  await stopClient(); if (service?.exitCode === null) service.kill();
}
