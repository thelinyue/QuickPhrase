import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { TeamManagementWindow } from './TeamManagement';
import { TeamSelector } from './TeamSelector';
import type { TeamMembers, TeamStatus, TeamUser } from './types';

const listeners = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
const nativeClose = vi.hoisted(() => ({ request: null as null | ((event: { preventDefault: () => void }) => Promise<void>), close: vi.fn() }));
vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ emitTo: vi.fn(async () => {}), listen: vi.fn(async (name, callback) => { listeners.set(name, callback); return () => listeners.delete(name); }) }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ onCloseRequested: vi.fn(async callback => { nativeClose.request = callback; return () => {}; }), startDragging: vi.fn(), close: nativeClose.close, minimize: vi.fn() }) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true), save: vi.fn(async () => null) }));
const person = (id: string, extra: Partial<TeamUser> = {}): TeamUser => ({ id, username: id, displayName: `昵称${id}`, contact: '', admin: false, disabled: false, mustChangePassword: false, canCreateTeams: false, ...extra });
const team = { teamId: 'A', name: '售前小组', role: 'manager' as const, editable: true, version: 1, membershipVersion: 4, lifecycleVersion: 1 };
const members: TeamMembers = { membershipVersion: 4, lifecycleVersion: 1, memberCount: 3, phraseCount: 5, members: [
  { userId: '001', username: '001', displayName: '经理甲', disabled: false, teamId: 'A', role: 'manager' },
  { userId: '002', username: '002', displayName: '客服乙', disabled: false, teamId: 'A', role: 'member' },
  { userId: '003', username: '003', displayName: '经理丙', disabled: false, teamId: 'A', role: 'manager' },
] };
let status: TeamStatus;
const actions = (action: string) => vi.mocked(call).mock.calls.filter(([command, args]) => command === 'team_admin' && args?.action === action);
const submitDialog = () => fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
beforeEach(() => {
  status = { server: 'http://192.168.1.20', username: '001', user: person('001'), teams: [team], offline: false };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 0, loaded: true }));
  vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(members) : args?.action === 'users' ? [person('001'), person('002')] : args?.action === 'deleted-teams' || args?.action === 'asset-cleanup' ? [] : null);
  vi.mocked(confirm).mockResolvedValue(true);
  HTMLDialogElement.prototype.showModal = vi.fn(function(this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); listeners.clear(); });

describe('按身份管理共享话术', () => {
  it('逐库同步时间随搜索和分页展示，未记录的库不推算时间', () => {
    status.teams = Array.from({ length: 21 }, (_, i) => ({ ...team, teamId: `T${i}`, name: `团队${i}` }));
    status.teamSyncTimes = { T20: new Date(2026, 9, 14, 15, 30, 26).getTime() };
    render(<TeamManagementWindow mode="teams" />);
    expect(screen.getAllByText('最近同步：暂无成功同步记录')).toHaveLength(20);
    expect(screen.queryByText('团队20')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    expect(screen.getByText('团队20')).toBeTruthy();
    expect(screen.getByText('最近同步：2026-10-14 15:30:26')).toBeTruthy();
    expect(screen.queryByText('团队0')).toBeNull();
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索共享话术' }), { target: { value: '团队20' } });
    expect(screen.getByText('共 1 组共享话术')).toBeTruthy();
    expect(screen.getByText('1／1')).toBeTruthy();
    expect(screen.getByText('最近同步：2026-10-14 15:30:26')).toBeTruthy();
  });
  it('初始密码生成8位，提交失败保留输入，取消后重新打开清空密码', async () => {
    status.user = person('admin', { admin: true });
    vi.mocked(call).mockImplementation(async (_, args) => { if (args?.action === 'create-user') throw new Error('创建失败'); return args?.action === 'users' ? [status.user] : null; });
    render(<TeamManagementWindow mode="users" />);
    fireEvent.click(await screen.findByRole('button', { name: '新建账号' }));
    fireEvent.change(screen.getByLabelText('用户名'), { target: { value: 'alex.lin' } });
    fireEvent.change(screen.getByLabelText('昵称（可选）'), { target: { value: '小林' } });
    fireEvent.click(screen.getByRole('button', { name: '生成密码' }));
    const input = screen.getByLabelText('初始密码') as HTMLInputElement; const generated = input.value;
    expect(generated).toMatch(/^[A-Za-z0-9_-]{8}$/); expect(input.type).toBe('text');
    fireEvent.click(screen.getByRole('button', { name: '隐藏初始密码' })); expect(input.value).toBe(generated);
    submitDialog(); await screen.findByRole('alert');
    expect(input.value).toBe(generated); expect(actions('create-user')[0][1]?.request).toMatchObject({ username: 'alex.lin', displayName: '小林', contact: '', password: generated });
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    fireEvent.click(screen.getByRole('button', { name: '新建账号' }));
    expect((screen.getByLabelText('初始密码') as HTMLInputElement).value).toBe('');
  });
  it('默认未授权不能创建共享话术；管理员自身及其他管理员不可通过成员表降权，须先移交再退出', async () => {
    render(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '新建共享话术' })).toBeNull();
    expect(screen.queryByRole('button', { name: '账号管理' })).toBeNull();
    await screen.findByRole('combobox', { name: '001的共享话术角色' });
    expect((screen.getByRole('combobox', { name: '001的共享话术角色' }) as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByRole('combobox', { name: '003的共享话术角色' }) as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '退出共享话术' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('button', { name: '移交管理员' })).toBeTruthy();
  });
  it('成员调整明确保存，失败保留草稿；切页取消放弃时仍保留编辑', async () => {
    status.teams = [team, { ...team, teamId: 'B', name: '售后小组' }];
    render(<TeamManagementWindow teamId="A" />);
    const role = await screen.findByRole('combobox', { name: '002的共享话术角色' });
    fireEvent.change(role, { target: { value: 'editor' } }); expect(actions('set-members')).toHaveLength(0);
    vi.mocked(confirm).mockResolvedValue(false);
    fireEvent.click(screen.getByRole('button', { name: '返回' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled());
    expect(screen.getByRole('combobox', { name: '002的共享话术角色' })).toBeTruthy();
    vi.mocked(call).mockImplementation(async (_, args) => { if (args?.action === 'set-members') throw new Error('成员已变化，草稿已保留'); return structuredClone(members); });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await screen.findByText(/成员已变化，草稿已保留/);
    expect((screen.getByRole('combobox', { name: '002的共享话术角色' }) as HTMLSelectElement).value).toBe('editor');
    expect(actions('set-members')[0][1]?.request).toEqual({ expectedMembershipVersion: 4, expectedLifecycleVersion: 1, members: members.members.map(m => ({ userId: m.userId, teamId: m.teamId, role: m.userId === '002' ? 'editor' : m.role })) });
  });
  it('移交只列在用查看者和编辑者；成功降权后管理入口撤下', async () => {
    const rendered = render(<TeamManagementWindow teamId="A" />);
    await screen.findByRole('combobox', { name: '001的共享话术角色' });
    fireEvent.click(screen.getByRole('button', { name: '移交管理员' }));
    expect(screen.getByRole('option', { name: '002 · 客服乙' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: '003 · 经理丙' })).toBeNull();
    expect(screen.getByText(/你将成为查看者/)).toBeTruthy();
    submitDialog(); await waitFor(() => expect(actions('transfer-manager')).toHaveLength(1));
    expect(actions('transfer-manager')[0][1]?.request).toEqual({ targetUserId: '002', expectedMembershipVersion: 4, expectedLifecycleVersion: 1 });
    status = { ...status, teams: [{ ...team, role: 'member', editable: false, membershipVersion: 5 }] };
    rendered.rerender(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '移交管理员' })).toBeNull();
    expect(screen.queryByRole('button', { name: '删除整组共享话术' })).toBeNull();
    expect((screen.getByRole('button', { name: '退出共享话术' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('保存失败但成员版本未变时保留草稿，用户可明确重试', async () => {
    let fail = true;
    vi.mocked(call).mockImplementation(async (_, args) => {
      if (args?.action === 'set-members') { if (fail) throw new Error('网络暂不可用'); return null; }
      return structuredClone(members);
    });
    render(<TeamManagementWindow mode="teams" teamId="A" />);
    fireEvent.change(await screen.findByRole('combobox', { name: '002的共享话术角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' })); await screen.findByRole('alert');
    await waitFor(() => expect((screen.getByRole('button', { name: '保存' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByRole('button', { name: '采用最新' })).toBeNull();
    expect((screen.getByRole('combobox', { name: '002的共享话术角色' }) as HTMLSelectElement).value).toBe('editor');
    expect(actions('set-members')).toHaveLength(1);
    fail = false; fireEvent.click(screen.getByRole('button', { name: '保存' })); await waitFor(() => expect(actions('set-members')).toHaveLength(2));
    expect(actions('set-members')[1][1]?.request).toEqual(actions('set-members')[0][1]?.request);
  });
  it('查看者确认后退出，离线时退出操作不可用', async () => {
    status.teams = [{ ...team, role: 'member', editable: false }];
    const rendered = render(<TeamManagementWindow teamId="A" />);
    fireEvent.click(screen.getByRole('button', { name: '退出共享话术' }));
    await waitFor(() => expect(actions('leave-team')).toHaveLength(1));
    expect(actions('leave-team')[0][1]?.request).toEqual({ expectedMembershipVersion: 4, expectedLifecycleVersion: 1 });
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('售前小组'), expect.objectContaining({ title: '退出共享话术' }));
    await waitFor(() => expect((screen.getByRole('button', { name: '退出共享话术' }) as HTMLButtonElement).disabled).toBe(false));
    status = { ...status, offline: true }; rendered.rerender(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '退出共享话术' })).toBeNull();
    expect(screen.getByText('离线，管理暂不可用。')).toBeTruthy();
  });
  it('服务管理员授予创建共享话术权限只提交该字段，资料修改不夹带密码和停用状态', async () => {
    status.user = person('admin', { admin: true });
    render(<TeamManagementWindow tab="users" />);
    const permission = await screen.findByRole('switch', { name: '001的创建共享话术权限' });
    fireEvent.click(permission); await waitFor(() => expect(actions('update-user')).toHaveLength(1));
    expect(actions('update-user')[0][1]?.request).toEqual({ canCreateTeams: true });
    await waitFor(() => expect((permission as HTMLInputElement).disabled).toBe(false));
    fireEvent.click(screen.getAllByRole('button', { name: '修改资料' })[0]);
    fireEvent.change(await screen.findByRole('textbox', { name: '昵称（可选）' }), { target: { value: '新昵称' } });
    submitDialog(); await waitFor(() => expect(actions('update-user')).toHaveLength(2));
    expect(actions('update-user')[1][1]?.request).toEqual({ username: '001', displayName: '新昵称', contact: '' });
  });
  it('账号切换后，旧用户名目录响应不能重新显示人员资料', async () => {
    status.user = person('admin', { admin: true });
    let complete!: (users: TeamUser[]) => void;
    vi.mocked(call).mockImplementation(async () => new Promise<TeamUser[]>(resolve => { complete = resolve; }));
    const rendered = render(<TeamManagementWindow tab="users" />);
    await waitFor(() => expect(complete).toBeTypeOf('function'));
    status = { ...status, user: person('employee'), teams: [] }; rendered.rerender(<TeamManagementWindow tab="users" />);
    await act(async () => complete([person('secret', { contact: '旧公司人员联系方式' })]));
    expect(screen.queryByText('旧公司人员联系方式')).toBeNull();
    expect(screen.queryByRole('button', { name: '账号管理' })).toBeNull();
  });
  it('授权异步到达仍进入指定成员页，后续切号回到列表', async () => {
    const logged = status;
    status = { ...status, user: null, teams: [] };
    const view = render(<TeamManagementWindow mode="teams" teamId="A" />);
    status = logged; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    await screen.findByRole('combobox', { name: '002的共享话术角色' });
    status = { ...logged, user: person('another') }; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    expect(screen.queryByRole('combobox', { name: '002的共享话术角色' })).toBeNull();
    expect(screen.getByRole('button', { name: '管理成员' })).toBeTruthy();
  });
  it('搜索和分页不裁剪成员草稿，保存完整列表', async () => {
    const many = { ...members, memberCount: 21, members: Array.from({ length: 21 }, (_, i) => ({ ...members.members[1], userId: `U${i}`, username: `用户名${i}`, displayName: '', role: 'member' as const })) };
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(many) : null);
    render(<TeamManagementWindow mode="teams" teamId="A" />);
    fireEvent.change(await screen.findByRole('combobox', { name: '用户名0的共享话术角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    fireEvent.change(screen.getByRole('combobox', { name: '用户名20的共享话术角色' }), { target: { value: 'editor' } });
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索成员' }), { target: { value: '无匹配项' } });
    expect(screen.getByText('没有匹配的成员')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(actions('set-members')).toHaveLength(1));
    expect(actions('set-members')[0][1]?.request).toEqual({ expectedMembershipVersion: 4, expectedLifecycleVersion: 1, members: many.members.map((m, i) => ({ userId: m.userId, teamId: m.teamId, role: i === 0 || i === 20 ? 'editor' : 'member' })) });
  });
  it('添加成员期间降权，旧响应不能恢复弹窗或成员草稿', async () => {
    let complete!: (identity: unknown) => void;
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(members) : args?.action === 'lookup-member' ? new Promise(resolve => { complete = resolve; }) : null);
    const view = render(<TeamManagementWindow mode="teams" teamId="A" />);
    await screen.findByRole('combobox', { name: '002的共享话术角色' });
    fireEvent.click(screen.getByRole('button', { name: '添加成员' }));
    fireEvent.change(screen.getByRole('textbox', { name: '用户名' }), { target: { value: 'secret' } }); submitDialog();
    await waitFor(() => expect(complete).toBeTypeOf('function'));
    status = { ...status, teams: [{ ...team, role: 'member', editable: false }] }; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    await act(async () => complete({ userId: 'secret', username: 'secret', displayName: '旧请求人员', disabled: false }));
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByText('旧请求人员')).toBeNull();
    expect(screen.queryByRole('button', { name: '保存' })).toBeNull();
  });
  it('删除恢复后清除旧草稿，旧保存失败不能覆盖新生命周期', async () => {
    let reject!: (reason: Error) => void;
    let current = structuredClone(members);
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(current) : args?.action === 'set-members' ? new Promise((_, fail) => { reject = fail; }) : null);
    const view = render(<TeamManagementWindow mode="teams" teamId="A" />);
    fireEvent.change(await screen.findByRole('combobox', { name: '002的共享话术角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' })); await waitFor(() => expect(reject).toBeTypeOf('function'));
    current = { ...current, lifecycleVersion: 3 }; status = { ...status, teams: [{ ...team, lifecycleVersion: 3 }] }; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    await act(async () => reject(new Error('旧保存失败')));
    await waitFor(() => expect((screen.getByRole('combobox', { name: '002的共享话术角色' }) as HTMLSelectElement).value).toBe('member'));
    expect(screen.queryByText('旧保存失败')).toBeNull(); expect(screen.queryByText('有未保存的调整')).toBeNull();
  });
  it('服务管理员卡片不提供停用或重置，按组角色跳入成员页，更多菜单支持Esc', async () => {
    status.user = person('admin', { admin: true });
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [status.user, person('001', { memberships: [{ teamId: 'A', name: team.name, role: 'editor' }] })] : null);
    render(<TeamManagementWindow mode="users" />);
    const myself = (await screen.findByText('本人')).closest('article')!;
    expect(within(myself).queryByRole('switch', { name: 'admin的启用状态' })).toBeNull(); expect((within(myself).getByRole('switch', { name: 'admin的创建共享话术权限' }) as HTMLInputElement).disabled).toBe(true); expect(within(myself).queryByRole('button', { name: '重置密码' })).toBeNull();
    expect(within(myself).getByRole('button', { name: '修改密码' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '售前小组 · 编辑者' }));
    await waitFor(() => expect(openWindow).toHaveBeenCalledWith('team-management', '', '', 'teams', '', '', 'A'));
    const more = screen.getByRole('button', { name: '更多' }); fireEvent.click(more);
    expect(screen.getByText('账号、共享话术、云文件及回收记录，不含个人内容。')).toBeTruthy();
    fireEvent.keyDown(document, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(more);
  });
});

describe('账号卡片快捷编辑', () => {
  beforeEach(() => { status.user = person('admin', { admin: true }); });
  async function editField(field = '昵称') {
    fireEvent.click(await screen.findByRole('button', { name: `修改001的${field}` }));
    return await screen.findByRole('textbox', { name: `001的${field}` }) as HTMLInputElement;
  }
  it('失焦与中文选字不保存，Enter只提交资料且保持其他字段，Esc恢复显示和焦点', async () => {
    render(<TeamManagementWindow mode="users" />);
    const input = await editField(); expect(input.maxLength).toBe(50);
    fireEvent.change(input, { target: { value: '新昵称' } }); fireEvent.blur(input); expect(actions('update-user')).toHaveLength(0);
    fireEvent.compositionStart(input); fireEvent.keyDown(input, { key: 'Enter' }); expect(actions('update-user')).toHaveLength(0);
    fireEvent.compositionEnd(input); fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(actions('update-user')).toHaveLength(1));
    expect(actions('update-user')[0][1]?.request).toEqual({ displayName: '新昵称' });
    await waitFor(() => expect(screen.queryByRole('textbox', { name: '001的昵称' })).toBeNull());
    const contact = await editField('联系方式'); expect(contact.maxLength).toBe(100);
    fireEvent.change(contact, { target: { value: '123456' } }); fireEvent.keyDown(contact, { key: 'Escape' });
    expect(actions('update-user')).toHaveLength(1);
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: '修改001的联系方式' })));
  });
  it('未变化不提交，清空可保存，失败保留草稿并防止重复提交', async () => {
    let reject!: (error: Error) => void;
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [person('001', { contact: '123' })] : new Promise((_, fail) => { reject = fail; }));
    render(<TeamManagementWindow mode="users" />);
    await editField(); fireEvent.click(screen.getByRole('button', { name: '保存昵称' })); expect(actions('update-user')).toHaveLength(0);
    const input = await editField('联系方式'); fireEvent.change(input, { target: { value: '' } });
    const save = screen.getByRole('button', { name: '保存联系方式' }); fireEvent.click(save); fireEvent.click(save);
    await waitFor(() => expect(actions('update-user')).toHaveLength(1));
    expect(actions('update-user')[0][1]?.request).toEqual({ contact: '' });
    await act(async () => reject(new Error('保存失败，请重试')));
    expect((screen.getByRole('textbox', { name: '001的联系方式' }) as HTMLInputElement).value).toBe('');
    expect(screen.getByRole('alert').textContent).toContain('保存失败'); expect((save as HTMLButtonElement).disabled).toBe(false);
  });
  it('切换字段和筛选需确认放弃，拒绝时保留草稿', async () => {
    render(<TeamManagementWindow mode="users" />);
    const input = await editField(); fireEvent.change(input, { target: { value: '草稿' } });
    vi.mocked(confirm).mockResolvedValue(false);
    fireEvent.click(screen.getByRole('button', { name: '修改001的联系方式' }));
    await waitFor(() => expect(confirm).toHaveBeenCalledTimes(1)); expect(input.value).toBe('草稿');
    fireEvent.change(screen.getByRole('combobox', { name: '用户名状态筛选' }), { target: { value: 'disabled' } });
    await waitFor(() => expect(confirm).toHaveBeenCalledTimes(2)); expect(input.isConnected).toBe(true);
    vi.mocked(confirm).mockResolvedValue(true);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '002' } });
    await waitFor(() => expect(screen.queryByRole('article', { name: '账号 001' })).toBeNull());
    expect(actions('update-user')).toHaveLength(0); expect(screen.getByRole('article', { name: '账号 002' })).toBeTruthy();
  });
  it('保存未完成时切号，旧响应不能恢复资料或成功提示', async () => {
    let complete!: () => void;
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [person('001')] : new Promise<void>(resolve => { complete = resolve; }));
    const view = render(<TeamManagementWindow mode="users" />);
    const input = await editField(); fireEvent.change(input, { target: { value: '旧账号草稿' } }); fireEvent.click(screen.getByRole('button', { name: '保存昵称' }));
    await waitFor(() => expect(complete).toBeTypeOf('function'));
    status = { ...status, user: person('other'), sessionEpoch: 2 }; view.rerender(<TeamManagementWindow mode="users" />);
    await act(async () => complete());
    expect(screen.queryByDisplayValue('旧账号草稿')).toBeNull(); expect(screen.queryByText('账号资料已更新')).toBeNull();
  });
  it('离线撤下编辑，重新连接不恢复旧草稿', async () => {
    const view = render(<TeamManagementWindow mode="users" />);
    const input = await editField(); fireEvent.change(input, { target: { value: '草稿' } });
    status = { ...status, offline: true }; view.rerender(<TeamManagementWindow mode="users" />);
    expect(screen.queryByRole('textbox')).toBeNull();
    status = { ...status, offline: false }; view.rerender(<TeamManagementWindow mode="users" />);
    await screen.findByRole('button', { name: '修改001的昵称' }); expect(screen.queryByDisplayValue('草稿')).toBeNull();
  });
  it('有草稿时翻页和原生关闭均可取消，确认关闭后清除草稿再关闭窗口', async () => {
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [person('001'), ...Array.from({ length: 20 }, (_, i) => person(`U${i}`))] : null);
    render(<TeamManagementWindow mode="users" />);
    const input = await editField(); fireEvent.change(input, { target: { value: '未保存资料' } });
    vi.mocked(confirm).mockResolvedValue(false);
    fireEvent.click(screen.getByRole('button', { name: '下一页' })); await waitFor(() => expect(confirm).toHaveBeenCalledTimes(1));
    expect(screen.getByText('1／2')).toBeTruthy(); expect(input.value).toBe('未保存资料');
    const preventDefault = vi.fn(); await act(async () => nativeClose.request!({ preventDefault }));
    expect(preventDefault).toHaveBeenCalled(); expect(nativeClose.close).not.toHaveBeenCalled(); expect(input.isConnected).toBe(true);
    vi.mocked(confirm).mockResolvedValue(true); await act(async () => nativeClose.request!({ preventDefault }));
    expect(nativeClose.close).toHaveBeenCalledTimes(1); expect(screen.queryByRole('textbox')).toBeNull(); expect(actions('update-user')).toHaveLength(0);
  });
  it('权限失败保持原值且重复点击不重复提交；停用拒绝不提交', async () => {
    let reject!: (e: Error) => void;
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [person('001')] : new Promise((_, fail) => { reject = fail; }));
    render(<TeamManagementWindow mode="users" />);
    const permission = await screen.findByRole('switch', { name: '001的创建共享话术权限' }) as HTMLInputElement;
    fireEvent.click(permission); fireEvent.click(permission); await waitFor(() => expect(actions('update-user')).toHaveLength(1));
    expect(actions('update-user')[0][1]?.request).toEqual({ canCreateTeams: true });
    await act(async () => reject(new Error('授权失败'))); expect(permission.checked).toBe(false);
    vi.mocked(confirm).mockResolvedValue(false); fireEvent.click(screen.getByRole('switch', { name: '001的启用状态' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled()); expect(actions('update-user')).toHaveLength(1);
  });
  it('只显示首组，其余浮层支持键盘、Esc焦点恢复、跳转和外部关闭', async () => {
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [person('001', { memberships: [
      { teamId: 'A', name: '一组', role: 'member' }, { teamId: 'B', name: '二组', role: 'editor' }, { teamId: 'C', name: '三组', role: 'manager' },
    ] })] : null);
    render(<TeamManagementWindow mode="users" />);
    const more = await screen.findByRole('button', { name: '001的其他2组共享话术' }); expect(screen.queryByText('二组')).toBeNull();
    fireEvent.click(more); const second = screen.getByRole('menuitem', { name: '二组 · 编辑者' }); expect(document.activeElement).toBe(second);
    fireEvent.scroll(screen.getByRole('searchbox')); expect(screen.getByRole('menu')).toBeTruthy();
    fireEvent.keyDown(second, { key: 'ArrowDown' }); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: '三组 · 库管理员' }));
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(more);
    fireEvent.click(more); fireEvent.pointerDown(document.body); expect(screen.queryByRole('menu')).toBeNull();
    fireEvent.click(more); fireEvent.scroll(more.closest('.user-cards')!); expect(screen.queryByRole('menu')).toBeNull();
    fireEvent.click(more); fireEvent.click(screen.getByRole('menuitem', { name: '二组 · 编辑者' }));
    await waitFor(() => expect(openWindow).toHaveBeenCalledWith('team-management', '', '', 'teams', '', '', 'B'));
  });
});

describe('共享话术下拉', () => {
  it('底部集中管理及创建共享话术，未授权不显示新建，离线不可提交', () => {
    const view = render(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '共享话术' }));
    expect(screen.queryByRole('menuitem', { name: '新建共享话术' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: '账号管理' })).toBeNull();
    status.user!.canCreateTeams = true;
    view.rerender(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('menuitem', { name: '新建共享话术' }));
    expect(openWindow).toHaveBeenCalledWith('team-management', '', '', 'create');
    status.offline = true; view.rerender(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '共享话术' }));
    expect((screen.getByRole('menuitem', { name: '新建共享话术' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('超过八组提供搜索，库名在入口内，Esc关闭并归还焦点', () => {
    status.teams = Array.from({ length: 9 }, (_, i) => ({ ...team, teamId: `T${i}`, name: `团队${i}`, role: 'member', editable: false }));
    const change = vi.fn(); render(<TeamSelector status={status} teamId="T4" onChange={change} onError={vi.fn()} />);
    const trigger = screen.getByRole('button', { name: '共享话术' }); expect(trigger.textContent).toContain('共享话术：团队4');
    fireEvent.click(trigger); const search = screen.getByRole('textbox', { name: '搜索共享话术' });
    fireEvent.change(search, { target: { value: '团队4' } });
    expect(screen.getAllByRole('menuitem').filter(item => item.getAttribute('aria-current') === 'true')).toHaveLength(1);
    fireEvent.keyDown(search, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(trigger); expect(change).not.toHaveBeenCalled();
  });
});
