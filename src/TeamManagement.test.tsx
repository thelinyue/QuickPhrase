import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { TeamManagementWindow } from './TeamManagement';
import { TeamSelector } from './TeamSelector';
import type { TeamMembers, TeamStatus, TeamUser } from './types';

const listeners = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ emitTo: vi.fn(async () => {}), listen: vi.fn(async (name, callback) => { listeners.set(name, callback); return () => listeners.delete(name); }) }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ onCloseRequested: vi.fn(async () => () => {}), startDragging: vi.fn(), close: vi.fn(), minimize: vi.fn() }) }));
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
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 0 }));
  vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(members) : args?.action === 'users' ? [person('001'), person('002')] : args?.action === 'deleted-teams' || args?.action === 'asset-cleanup' ? [] : null);
  vi.mocked(confirm).mockResolvedValue(true);
  HTMLDialogElement.prototype.showModal = vi.fn(function(this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); listeners.clear(); });

describe('按身份管理共享库', () => {
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
  it('默认未授权不能创建共享库；管理员自身及其他管理员不可通过成员表降权，须先移交再退出', async () => {
    render(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '新建共享库' })).toBeNull();
    expect(screen.queryByRole('button', { name: '账号管理' })).toBeNull();
    await screen.findByRole('combobox', { name: '001的共享库角色' });
    expect((screen.getByRole('combobox', { name: '001的共享库角色' }) as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByRole('combobox', { name: '003的共享库角色' }) as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '退出共享库' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('button', { name: '移交管理员' })).toBeTruthy();
  });
  it('成员调整明确保存，失败保留草稿；切页取消放弃时仍保留编辑', async () => {
    status.teams = [team, { ...team, teamId: 'B', name: '售后小组' }];
    render(<TeamManagementWindow teamId="A" />);
    const role = await screen.findByRole('combobox', { name: '002的共享库角色' });
    fireEvent.change(role, { target: { value: 'editor' } }); expect(actions('set-members')).toHaveLength(0);
    vi.mocked(confirm).mockResolvedValue(false);
    fireEvent.click(screen.getByRole('button', { name: '返回' }));
    await waitFor(() => expect(confirm).toHaveBeenCalled());
    expect(screen.getByRole('combobox', { name: '002的共享库角色' })).toBeTruthy();
    vi.mocked(call).mockImplementation(async (_, args) => { if (args?.action === 'set-members') throw new Error('成员已变化，草稿已保留'); return structuredClone(members); });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await screen.findByText(/成员已变化，草稿已保留/);
    expect((screen.getByRole('combobox', { name: '002的共享库角色' }) as HTMLSelectElement).value).toBe('editor');
    expect(actions('set-members')[0][1]?.request).toEqual({ expectedMembershipVersion: 4, expectedLifecycleVersion: 1, members: members.members.map(m => ({ userId: m.userId, teamId: m.teamId, role: m.userId === '002' ? 'editor' : m.role })) });
  });
  it('移交只列在用查看者和编辑者；成功降权后管理入口撤下', async () => {
    const rendered = render(<TeamManagementWindow teamId="A" />);
    await screen.findByRole('combobox', { name: '001的共享库角色' });
    fireEvent.click(screen.getByRole('button', { name: '移交管理员' }));
    expect(screen.getByRole('option', { name: '002 · 客服乙' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: '003 · 经理丙' })).toBeNull();
    expect(screen.getByText(/你将成为查看者/)).toBeTruthy();
    submitDialog(); await waitFor(() => expect(actions('transfer-manager')).toHaveLength(1));
    expect(actions('transfer-manager')[0][1]?.request).toEqual({ targetUserId: '002', expectedMembershipVersion: 4, expectedLifecycleVersion: 1 });
    status = { ...status, teams: [{ ...team, role: 'member', editable: false, membershipVersion: 5 }] };
    rendered.rerender(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '移交管理员' })).toBeNull();
    expect(screen.queryByRole('button', { name: '删除共享库' })).toBeNull();
    expect((screen.getByRole('button', { name: '退出共享库' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('保存失败但成员版本未变时保留草稿，用户可明确重试', async () => {
    let fail = true;
    vi.mocked(call).mockImplementation(async (_, args) => {
      if (args?.action === 'set-members') { if (fail) throw new Error('网络暂不可用'); return null; }
      return structuredClone(members);
    });
    render(<TeamManagementWindow mode="teams" teamId="A" />);
    fireEvent.change(await screen.findByRole('combobox', { name: '002的共享库角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' })); await screen.findByRole('alert');
    await waitFor(() => expect((screen.getByRole('button', { name: '保存' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByRole('button', { name: '采用最新' })).toBeNull();
    expect((screen.getByRole('combobox', { name: '002的共享库角色' }) as HTMLSelectElement).value).toBe('editor');
    expect(actions('set-members')).toHaveLength(1);
    fail = false; fireEvent.click(screen.getByRole('button', { name: '保存' })); await waitFor(() => expect(actions('set-members')).toHaveLength(2));
    expect(actions('set-members')[1][1]?.request).toEqual(actions('set-members')[0][1]?.request);
  });
  it('查看者确认后退出，离线时退出操作不可用', async () => {
    status.teams = [{ ...team, role: 'member', editable: false }];
    const rendered = render(<TeamManagementWindow teamId="A" />);
    fireEvent.click(screen.getByRole('button', { name: '退出共享库' }));
    await waitFor(() => expect(actions('leave-team')).toHaveLength(1));
    expect(actions('leave-team')[0][1]?.request).toEqual({ expectedMembershipVersion: 4, expectedLifecycleVersion: 1 });
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('售前小组'), expect.objectContaining({ title: '退出共享库' }));
    await waitFor(() => expect((screen.getByRole('button', { name: '退出共享库' }) as HTMLButtonElement).disabled).toBe(false));
    status = { ...status, offline: true }; rendered.rerender(<TeamManagementWindow teamId="A" />);
    expect(screen.queryByRole('button', { name: '退出共享库' })).toBeNull();
    expect(screen.getByText('离线，管理暂不可用。')).toBeTruthy();
  });
  it('服务管理员授予创建共享库权限只提交该字段，资料修改不夹带密码和停用状态', async () => {
    status.user = person('admin', { admin: true });
    render(<TeamManagementWindow tab="users" />);
    const permission = await screen.findByRole('checkbox', { name: '001的创建共享库权限' });
    fireEvent.click(permission); await waitFor(() => expect(actions('update-user')).toHaveLength(1));
    expect(actions('update-user')[0][1]?.request).toEqual({ canCreateTeams: true });
    await waitFor(() => expect((permission as HTMLInputElement).disabled).toBe(false));
    fireEvent.click(screen.getAllByRole('button', { name: '修改资料' })[0]);
    fireEvent.change(screen.getByRole('textbox', { name: '昵称（可选）' }), { target: { value: '新昵称' } });
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
    await screen.findByRole('combobox', { name: '002的共享库角色' });
    status = { ...logged, user: person('another') }; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    expect(screen.queryByRole('combobox', { name: '002的共享库角色' })).toBeNull();
    expect(screen.getByRole('button', { name: '管理成员' })).toBeTruthy();
  });
  it('搜索和分页不裁剪成员草稿，保存完整列表', async () => {
    const many = { ...members, memberCount: 21, members: Array.from({ length: 21 }, (_, i) => ({ ...members.members[1], userId: `U${i}`, username: `用户名${i}`, displayName: '', role: 'member' as const })) };
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'members' ? structuredClone(many) : null);
    render(<TeamManagementWindow mode="teams" teamId="A" />);
    fireEvent.change(await screen.findByRole('combobox', { name: '用户名0的共享库角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    fireEvent.change(screen.getByRole('combobox', { name: '用户名20的共享库角色' }), { target: { value: 'editor' } });
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
    await screen.findByRole('combobox', { name: '002的共享库角色' });
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
    fireEvent.change(await screen.findByRole('combobox', { name: '002的共享库角色' }), { target: { value: 'editor' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' })); await waitFor(() => expect(reject).toBeTypeOf('function'));
    current = { ...current, lifecycleVersion: 3 }; status = { ...status, teams: [{ ...team, lifecycleVersion: 3 }] }; view.rerender(<TeamManagementWindow mode="teams" teamId="A" />);
    await act(async () => reject(new Error('旧保存失败')));
    await waitFor(() => expect((screen.getByRole('combobox', { name: '002的共享库角色' }) as HTMLSelectElement).value).toBe('member'));
    expect(screen.queryByText('旧保存失败')).toBeNull(); expect(screen.queryByText('有未保存的调整')).toBeNull();
  });
  it('服务管理员卡片不提供停用或重置，按组角色跳入成员页，更多菜单支持Esc', async () => {
    status.user = person('admin', { admin: true });
    vi.mocked(call).mockImplementation(async (_, args) => args?.action === 'users' ? [status.user, person('001', { memberships: [{ teamId: 'A', name: team.name, role: 'editor' }] })] : null);
    render(<TeamManagementWindow mode="users" />);
    const myself = (await screen.findByText('本人')).closest('article')!;
    expect(within(myself).queryByRole('switch')).toBeNull(); expect(within(myself).queryByRole('button', { name: '重置密码' })).toBeNull();
    expect(within(myself).getByRole('button', { name: '修改密码' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '售前小组 · 编辑者' }));
    expect(openWindow).toHaveBeenCalledWith('team-management', '', '', 'teams', '', '', 'A');
    const more = screen.getByRole('button', { name: '更多' }); fireEvent.click(more);
    expect(screen.getByText('账号、共享库、云文件及回收记录，不含个人内容。')).toBeTruthy();
    fireEvent.keyDown(document, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(more);
  });
});

describe('共享库下拉', () => {
  it('底部集中管理及创建共享库，未授权不显示新建，离线不可提交', () => {
    const view = render(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '共享库' }));
    expect(screen.queryByRole('menuitem', { name: '新建共享库' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: '账号管理' })).toBeNull();
    status.user!.canCreateTeams = true;
    view.rerender(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('menuitem', { name: '新建共享库' }));
    expect(openWindow).toHaveBeenCalledWith('team-management', '', '', 'create');
    status.offline = true; view.rerender(<TeamSelector status={status} teamId="A" onChange={vi.fn()} onError={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '共享库' }));
    expect((screen.getByRole('menuitem', { name: '新建共享库' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('超过八组提供搜索，库名在入口内，Esc关闭并归还焦点', () => {
    status.teams = Array.from({ length: 9 }, (_, i) => ({ ...team, teamId: `T${i}`, name: `团队${i}`, role: 'member', editable: false }));
    const change = vi.fn(); render(<TeamSelector status={status} teamId="T4" onChange={change} onError={vi.fn()} />);
    const trigger = screen.getByRole('button', { name: '共享库' }); expect(trigger.textContent).toContain('共享库：团队4');
    fireEvent.click(trigger); const search = screen.getByRole('textbox', { name: '搜索共享库' });
    fireEvent.change(search, { target: { value: '团队4' } });
    expect(screen.getAllByRole('menuitem').filter(item => item.getAttribute('aria-current') === 'true')).toHaveLength(1);
    fireEvent.keyDown(search, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(trigger); expect(change).not.toHaveBeenCalled();
  });
});
