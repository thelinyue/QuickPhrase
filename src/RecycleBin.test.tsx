import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, act, within } from '@testing-library/react';
import { DeletionWindow, RecycleWindow } from './RecycleBin';
import { call, closeWindow, useTeams } from './api';
import type { Library, TeamStatus, RecycleItem, TeamRecycle } from './types';

vi.mock('./api', () => ({ call: vi.fn(async () => {}), closeWindow: vi.fn(async () => {}), useTeams: vi.fn() }));
const { events } = vi.hoisted(() => ({ events: new Map<string, (e: { payload: unknown }) => void>() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name, handler) => { events.set(name, handler); return () => events.delete(name); }) }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
afterEach(() => { cleanup(); events.clear(); vi.clearAllMocks(); });
const library = { categories: [{ id: 'root', name: '分类', kind: 'phrase', parentId: null, position: 0, background: '' }], phrases: [{ id: 'A', rootId: 'root', groupId: null, title: '话术', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '正文' }] }] }] } as Library;

describe('回收站删除确认', () => {
  it('默认回收；取消不调用删除接口', () => {
    render(<DeletionWindow library={library} id="A" kind="phrase" />);
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText(/可以还原/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    expect(call).not.toHaveBeenCalled(); expect(closeWindow).toHaveBeenCalledOnce();
  });
  it('明确取消回收后永久删除；连点确定仅提交一次', async () => {
    render(<DeletionWindow library={library} id="A" kind="phrase" />);
    fireEvent.click(screen.getByRole('checkbox'));
    expect(screen.getByText(/删除后无法恢复/)).toBeTruthy();
    const button = screen.getByRole('button', { name: '确定' }); fireEvent.click(button); fireEvent.click(button);
    await waitFor(() => expect(closeWindow).toHaveBeenCalled());
    expect(call).toHaveBeenCalledExactlyOnceWith('delete_item', { id: 'A', kind: 'phrase', recycle: false });
  });
  it('分类删除保留点击时的主键且默认进入回收站', async () => {
    render(<DeletionWindow library={library} id="root" kind="category" />);
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(call).toHaveBeenCalledExactlyOnceWith('delete_category', { id: 'root', recycle: true }));
  });
});

let status: TeamStatus;
const privateRow: RecycleItem = { id: 'private-old', kind: 'phrase', label: '个人删除记录', location: '常用', deletedAt: 1000, phraseCount: 1 };
const sharedRow: TeamRecycle = { id: 'shared-old', teamId: 'A', label: '共享删除记录', deletedAt: 2, categories: [], phrases: [{ title: '共享标题', segments: [{ blocks: [{ kind: 'image', assetId: 'image', name: '图片.png' }, { kind: 'text', text: '共享正文' }] }] }] };
beforeEach(() => {
  status = { sessionEpoch: 1, server: 'server', username: 'user', offline: false, user: { id: 'user', username: 'user', admin: false, disabled: false, mustChangePassword: false, displayName: '', contact: '', canCreateTeams: false }, teams: [{ teamId: 'A', name: '测试小组', editable: true, version: 1, role: 'editor', membershipVersion: 1, lifecycleVersion: 1 }, { teamId: 'view-only', name: '查看组', editable: false, version: 1, role: 'member', membershipVersion: 1, lifecycleVersion: 1 }] };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 0, loaded: true }));
  vi.mocked(call).mockImplementation(async command => command === 'get_recycle_bin' ? [privateRow] : command === 'get_team_recycle' ? [sharedRow] : command === 'get_recycle_detail' ? { ...privateRow, categories: [], phrases: [library.phrases[0]] } : command === 'get_team_view' ? { team: status.teams[0] } : command.startsWith('empty_') ? 1 : undefined);
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
});

describe('统一回收站', () => {
  it('共享入口定位来源，查看者不在来源选项中，历史共享记录不推断类型', async () => {
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} initialTeamId="A" />);
    await screen.findByText('共享删除记录');
    expect(screen.getByRole('button', { name: '共享话术' }).textContent).toContain('共享话术：测试小组');
    expect(screen.getByRole('button', { name: '个人话术' }).getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(screen.getByRole('button', { name: '共享话术' }));
    expect(screen.queryByRole('menuitem', { name: /^查看组/ })).toBeNull();
    expect(screen.getByText('类型：回收记录')).toBeTruthy(); expect(screen.getByText('图片：图片.png')).toBeTruthy();
    expect(call).toHaveBeenCalledWith('get_team_recycle', { teamId: 'A', sessionEpoch: 1, expectedLifecycleVersion: 1, query: '' });
  });
  it('复用导航切换来源、重置搜索，旧详情响应不能覆盖新来源', async () => {
    let finish!: (value: unknown) => void;
    vi.mocked(call).mockImplementation(async command => command === 'get_recycle_bin' ? [privateRow] : command === 'get_recycle_detail' ? new Promise(resolve => { finish = resolve; }) : command === 'get_team_recycle' ? [sharedRow] : undefined);
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} />);
    await waitFor(() => expect(finish).toBeTypeOf('function'));
    fireEvent.change(screen.getByLabelText('搜索回收站'), { target: { value: '旧搜索' } });
    await act(async () => events.get('recycle-navigation')?.({ payload: { teamId: 'A' } }));
    await screen.findByText('共享正文');
    await act(async () => finish({ ...privateRow, categories: [], phrases: [library.phrases[0]] }));
    expect((screen.getByLabelText('搜索回收站') as HTMLInputElement).value).toBe('');
    expect(screen.queryByLabelText('回收话术标题')).toBeNull();
  });
  it('清空读取未筛选清单；取消不写入，连点确定仅提交一次并绑定来源', async () => {
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} initialTeamId="A" />);
    await screen.findByText('共享正文');
    fireEvent.change(screen.getByLabelText('搜索回收站'), { target: { value: '关键词' } });
    fireEvent.submit(screen.getByLabelText('搜索回收站').closest('form')!);
    await waitFor(() => expect(call).toHaveBeenCalledWith('get_team_recycle', expect.objectContaining({ query: '关键词' })));
    vi.mocked(call).mockImplementation(async (command, args) => command === 'get_team_recycle' ? args?.query === '' ? [sharedRow, { ...sharedRow, id: 'outside-search' }] : [sharedRow] : command === 'empty_team_recycle' ? 1 : undefined);
    fireEvent.click(screen.getByText('清空当前回收站'));
    const first = await screen.findByRole('dialog'); expect(within(first).getByText(/2 条回收记录/)).toBeTruthy();
    fireEvent.click(within(first).getByText('取消'));
    expect(vi.mocked(call).mock.calls.some(([command]) => command === 'empty_team_recycle')).toBe(false);
    fireEvent.click(screen.getByText('清空当前回收站'));
    const second = await screen.findByRole('dialog'); const confirm = within(second).getByText('确定'); fireEvent.click(confirm); fireEvent.click(confirm);
    await screen.findByText('已删除 1 条回收记录');
    expect(vi.mocked(call).mock.calls.filter(([command]) => command === 'empty_team_recycle')).toEqual([['empty_team_recycle', { teamId: 'A', sessionEpoch: 1, expectedLifecycleVersion: 1, ids: ['shared-old', 'outside-search'] }]]);
  });
  it('切号或降权立即撤下旧详情与确认框，迟到读取不能恢复旧内容', async () => {
    const view = render(<RecycleWindow library={{ ...library, libraryId: 'private' }} initialTeamId="A" />);
    await screen.findByText('共享正文'); fireEvent.click(screen.getByText('永久删除该记录')); expect(screen.getByRole('dialog')).toBeTruthy();
    status = { ...status, sessionEpoch: 2, teams: [] }; view.rerender(<RecycleWindow library={{ ...library, libraryId: 'other' }} initialTeamId="A" />);
    await screen.findByText('个人删除记录');
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByText('共享正文')).toBeNull();
    expect(screen.getByRole('button', { name: '个人话术' }).getAttribute('aria-pressed')).toBe('true');
  });
  it('读取失败不显示空态；离线共享不能读取但可以切换个人来源', async () => {
    vi.mocked(call).mockRejectedValue(new Error('读取失败'));
    const view = render(<RecycleWindow library={library} initialTeamId="A" />); await screen.findByRole('alert');
    expect(screen.queryByText('回收站为空')).toBeNull();
    status = { ...status, offline: true }; view.rerender(<RecycleWindow library={library} initialTeamId="A" />);
    expect(screen.getByText(/共享话术的回收记录需要联网/)).toBeTruthy();
    vi.mocked(call).mockImplementation(async command => command === 'get_recycle_bin' ? [privateRow] : { ...privateRow, categories: [], phrases: [] });
    fireEvent.click(screen.getByRole('button', { name: '个人话术' })); await screen.findByText('个人删除记录');
  });
  it('个人清空绑定 libraryId，失败保留确认框和记录', async () => {
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} />); await screen.findByText('个人删除记录');
    vi.mocked(call).mockImplementation(async command => command === 'get_recycle_bin' ? [privateRow] : command === 'empty_recycle' ? Promise.reject(new Error('事务失败')) : undefined);
    fireEvent.click(screen.getByText('清空当前回收站')); const dialog = await screen.findByRole('dialog'); fireEvent.click(within(dialog).getByText('确定'));
    await screen.findByRole('alert'); expect(screen.getByRole('dialog')).toBeTruthy(); expect(screen.getByText('个人删除记录')).toBeTruthy();
    expect(call).toHaveBeenCalledWith('empty_recycle', { libraryId: 'private', ids: ['private-old'] });
  });
  it('还原提交期间禁止来源切换与重复操作，失败保留原记录', async () => {
    let reject!: (error: Error) => void;
    const implementation = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation((command, args) => command === 'restore_team_record' ? new Promise((_, fail) => { reject = fail; }) : implementation(command, args));
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} initialTeamId="A" />);
    await screen.findByText('共享正文');
    fireEvent.click(screen.getByRole('button', { name: '还原' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('来源：共享话术：测试小组')).toBeTruthy();
    const confirm = within(dialog).getByText('确定'); fireEvent.click(confirm); fireEvent.click(confirm);
    await waitFor(() => expect(reject).toBeTypeOf('function'));
    expect((screen.getByRole('button', { name: '个人话术' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '共享话术' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => events.get('recycle-navigation')?.({ payload: { teamId: '' } }));
    expect(screen.getByRole('button', { name: '共享话术' }).getAttribute('aria-pressed')).toBe('true');
    await act(async () => reject(new Error('素材缺失')));
    await screen.findByRole('alert');
    expect(screen.getByRole('dialog')).toBeTruthy(); expect(screen.getByText('共享删除记录')).toBeTruthy();
    expect(vi.mocked(call).mock.calls.filter(([command]) => command === 'restore_team_record')).toHaveLength(1);
  });
  it('每页10条，两个按钮切换时重置搜索分页，撤下旧确认框', async () => {
    const implementation = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation((command, args) => command === 'get_recycle_bin' ? Promise.resolve(Array.from({ length: 11 }, (_, index) => ({ ...privateRow, id: `private-${index}`, label: `个人记录${index}` }))) : implementation(command, args));
    render(<RecycleWindow library={{ ...library, libraryId: 'private' }} />);
    await screen.findByText('个人记录0'); expect(screen.getAllByRole('option')).toHaveLength(10);
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    expect(screen.getByText('个人记录10')).toBeTruthy(); expect(screen.getByText('2／2')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('搜索回收站'), { target: { value: '待提交搜索' } });
    fireEvent.click(await screen.findByText('永久删除该记录')); expect(screen.getByRole('dialog')).toBeTruthy();
    await act(async () => events.get('recycle-navigation')?.({ payload: { teamId: 'A' } }));
    await screen.findByText('共享正文');
    expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.getByText('1／1')).toBeTruthy();
    expect((screen.getByLabelText('搜索回收站') as HTMLInputElement).value).toBe('');
    fireEvent.click(screen.getByRole('button', { name: '个人话术' }));
    await screen.findByText('个人记录0'); expect(screen.getByText('1／2')).toBeTruthy();
  });
});
