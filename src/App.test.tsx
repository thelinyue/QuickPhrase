import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest';
import { MainWindow } from './App';
import { call, openWindow, useTeams, useTeamView } from './api';
import { useMembers } from './useMembers';
import type { Library, Phrase, SearchHit, TeamStatus, TeamView } from './types';

const callbacks = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
const nativeMenu = vi.hoisted(() => ({ new: vi.fn(async (_options: { items?: { text?: string; enabled?: boolean; action?: () => void }[] }) => ({ popup: vi.fn(async () => {}), close: vi.fn(async () => {}) })) }));
vi.mock('@tauri-apps/api/menu', () => ({ Menu: nativeMenu }));
vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(), useLibrary: vi.fn(), useTeams: vi.fn(), useTeamView: vi.fn() }));
vi.mock('./useMembers', () => ({ useMembers: vi.fn(() => null) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: (event: {payload: unknown}) => void) => { callbacks.set(name, callback); return () => callbacks.delete(name); }) }));
beforeEach(() => {
  vi.mocked(useMembers).mockReturnValue(null);
  const status = { server: '', username: '', user: null, teams: [], offline: false };
  vi.mocked(useTeams).mockReturnValue({ status, version: 0, loaded: true });
  vi.mocked(useTeamView).mockReturnValue({ view: null, error: '' });
  Element.prototype.scrollIntoView = vi.fn();
  HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.open = true; });
  vi.mocked(call).mockImplementation(async name => name === 'get_target' ? { hwnd: 1, focus: 2, pid: 3 } : name === 'deliver_content' ? { status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null } : name === 'search_scoped_content' ? [] : null);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); callbacks.clear(); });
const phrase = (id: string, groupId: string | null = null, rootId = 'common'): Phrase => ({ id, rootId, groupId, title: id, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: id }] }] });
const library: Library = {
  categories: [{ id: 'common', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' }, { id: 'group', name: '分组', kind: 'phrase', parentId: 'common', position: 0, background: '' }, { id: 'after', name: '后组', kind: 'phrase', parentId: 'common', position: 1, background: '' }, { id: 'other', name: '其他', kind: 'phrase', parentId: null, position: 1, background: '' }, { id: 'local-files', name: '文件', kind: 'file', parentId: null, position: 0, background: '' }],
  phrases: [phrase('A'), phrase('B', 'group'), phrase('C', 'after'), phrase('D', null, 'other')], files: [{ id: 'F', name: '文件', categoryId: 'local-files', path: 'missing.txt', position: 0, missing: true }], commonPhrases: [], dataDir: '',
  settings: { showTitle: true, showPreview: true, showNumbers: true, expand: true, showSearch: true, showCommon: false, categoryHeight: 31, fileCategoryHeight: 31 } as Library['settings'],
};
const mapping = () => { const args = vi.mocked(call).mock.calls.filter(([name]) => name === 'sync_sequence_map').at(-1)?.[1]; return args ? { ids: args.ids, loading: args.loading } : undefined; };
const view = () => render(<MainWindow library={library} refresh={vi.fn(async () => {})} />);
const teamFixture = () => {
  const status: TeamStatus = { server: 'http://192.168.1.20', username: '001', user: { id: 'employee', username: '001', admin: false, disabled: false, mustChangePassword: false, displayName: '', contact: '', canCreateTeams: false }, teams: [{ teamId: 'A-team', name: '售前小组', version: 1, editable: false, role: 'member', membershipVersion: 1, lifecycleVersion: 1 }, { teamId: 'B-team', name: '售后小组', version: 1, editable: true, role: 'editor', membershipVersion: 1, lifecycleVersion: 1 }], offline: false };
  const views = new Map<string, TeamView>(status.teams.map(t => [t.teamId, { team: t, categories: [{ id: `${t.teamId}-root`, name: `${t.name}分类`, kind: 'phrase', parentId: null, position: 0, background: '' }, { id: `${t.teamId}-child`, name: `${t.name}分组`, kind: 'phrase', parentId: `${t.teamId}-root`, position: 0, background: '' }], phrases: [{ ...phrase(`team:${t.teamId}:shared:1:1`, `${t.teamId}-child`, `${t.teamId}-root`), title: `${t.name}话术`, contentRef: { source: 'team', teamId: t.teamId, id: 'shared', revision: 1, lifecycleVersion: 1 }, ready: true }], offline: false }]));
  vi.mocked(useTeams).mockReturnValue({ status, version: 1, loaded: true });
  vi.mocked(useTeamView).mockImplementation(id => ({ view: views.get(id) || null, error: '' }));
  return status;
};

describe('新增话术继承右键所在分类', () => {
  function headingPositions(tops: number[]) {
    document.querySelectorAll('.group-heading').forEach((el, index) => {
      vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({ top: tops[index] } as DOMRect);
    });
  }
  it.each([[80, ''], [120, 'group'], [219, 'group'], [220, 'after'], [600, 'after']])('空白坐标 %i 归属 %s，菜单打开后不随布局变化', (y, group) => {
    view(); headingPositions([120, 220]);
    fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' }), { clientY: y });
    headingPositions([500, 700]);
    fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
    expect(openWindow).toHaveBeenCalledWith('editor', '', '', '', 'common', group, '');
  });
  it('滚动后折叠分类仍按视口坐标归属，切换一级分类不会沿用旧分组', () => {
    view(); fireEvent.click(screen.getByRole('button', { name: '后组' })); headingPositions([-100, 50]);
    fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' }), { clientY: 300 });
    fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
    expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'common', 'after', '');
    fireEvent.click(screen.getByRole('button', { name: /^其他$/ }));
    fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' }), { clientY: 300 });
    fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
    expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'other', '', '');
  });
  it('右键个人话术行与分类标题均带入二级分类', async () => {
    view(); fireEvent.contextMenu(document.querySelector('[data-row="B"]')!);
    fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
    expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'common', 'group', '');
    fireEvent.contextMenu(document.querySelector('.group-heading')!);
    await waitFor(() => expect(nativeMenu.new).toHaveBeenCalled());
    nativeMenu.new.mock.calls.at(-1)![0].items!.find(item => item.text === '新增话术')!.action!();
    expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'common', 'group', '');
  });
  it('共享库的标题、话术行及折叠后的空白处均带入该库分组', async () => {
    teamFixture(); view(); fireEvent.click(screen.getByRole('button', { name: '共享话术' }));
    fireEvent.click(screen.getByRole('menuitem', { name: /^售后小组/ }));
    await screen.findByText('售后小组话术');
    for (const target of [document.querySelector('[data-row="team:B-team:shared:1:1"]')!, document.querySelector('.group-heading')!]) {
      fireEvent.contextMenu(target); fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
      expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'B-team-root', 'B-team-child', 'B-team');
    }
    fireEvent.click(screen.getByRole('button', { name: '售后小组分组' })); headingPositions([100]);
    fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' }), { clientY: 500 });
    fireEvent.click(screen.getByRole('menuitem', { name: '新增话术' }));
    expect(openWindow).toHaveBeenLastCalledWith('editor', '', '', '', 'B-team-root', 'B-team-child', 'B-team');
  });
});
it('左下菜单按按钮上边缘向上弹出，不使用点击位置', () => {
  view();
  const trigger = screen.getByRole('button', { name: '主菜单' });
  vi.spyOn(trigger, 'getBoundingClientRect').mockReturnValue({ x: 2, y: 706, left: 2, top: 706, right: 27, bottom: 731, width: 25, height: 25, toJSON: () => ({}) });
  fireEvent.click(trigger, { clientX: 15, clientY: 724 });
  const menu = screen.getByRole('menu');
  expect(menu.style.bottom).toBe(`${innerHeight - 706 + 4}px`);
  expect(menu.style.top).toBe('');
  expect(menu.style.maxHeight).toBe('700px');
});

it('主菜单使用指引复用独立帮助窗口', () => {
  view(); fireEvent.click(screen.getByRole('button', { name: '主菜单' })); fireEvent.click(screen.getByRole('menuitem', { name: '使用指引' }));
  expect(openWindow).toHaveBeenCalledWith('usage-guide');
});

it.each(['右键分类', '分类菜单'])('%s只保留分类操作，全局管理入口仍在主菜单', source => {
  const status = teamFixture();
  vi.mocked(useTeams).mockReturnValue({ status: { ...status, companyFiles: true, user: { ...status.user!, admin: true } }, version: 1, loaded: true });
  view();
  if (source === '右键分类') fireEvent.contextMenu(screen.getByRole('button', { name: /^常用$/ }));
  else fireEvent.click(screen.getByRole('button', { name: '一级分类菜单' }));
  for (const name of ['新增话术', '新增云文件', '新增二级分类', '重命名', '删除', '导出该一级分类的话术', '设置一级分类背景色']) expect(screen.getByRole('menuitem', { name })).toBeTruthy();
  const management = ['账号与共享', '云文件', '账号管理', '共享话术管理'];
  for (const name of management) expect(screen.queryByRole('menuitem', { name: new RegExp(`^${name}$`) })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '主菜单' }));
  for (const name of management) expect(screen.getByRole('menuitem', { name: new RegExp(`^${name}$`) })).toBeTruthy();
});

describe('共享话术入口、独立状态和撤权', () => {
  it('调整入口顺序保持当前共享话术、选中话术和编号', async () => {
    teamFixture(); const rendered = view();
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售前小组/ }));
    await waitFor(() => expect(mapping()?.ids).toEqual(['team:A-team:shared:1:1']));
    const row = document.querySelector('[data-row="team:A-team:shared:1:1"]')!;
    fireEvent.click(row);
    const before = mapping();
    rendered.rerender(<MainWindow library={{ ...library, settings: { ...library.settings, teamsFirst: true } }} refresh={vi.fn(async () => {})} />);
    expect(Array.from(document.querySelectorAll('.scope-bar .scope-name')).map(el => el.getAttribute('aria-label') || el.textContent)).toEqual(['共享话术', '个人话术']);
    expect(screen.getByText('售前小组话术')).toBeTruthy();
    expect(row.classList.contains('selected')).toBe(true);
    expect(mapping()).toEqual(before);
    expect(openWindow).not.toHaveBeenCalled();
  });
  it('私人入口旁始终保留共享话术入口，在主窗口按组显示；查看者无法维护', async () => {
    teamFixture(); view();
    expect(screen.getByRole('button', { name: '个人话术' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售前小组/ }));
    await waitFor(() => expect(screen.getByText('售前小组话术')).toBeTruthy());
    expect(document.querySelector('[data-row="A"]')).toBeNull(); expect(openWindow).not.toHaveBeenCalled();
    expect((screen.getByRole('button', { name: '新增一级分类' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: '售前小组分组' }));
    await waitFor(() => expect(mapping()?.ids).toEqual([]));
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售后小组/ }));
    await waitFor(() => expect(screen.getByText('售后小组话术')).toBeTruthy());
    expect((screen.getByRole('button', { name: '新增一级分类' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售前小组/ }));
    await waitFor(() => expect(screen.queryByText('售前小组话术')).toBeNull());
    expect(screen.getByRole('button', { name: '售前小组分组' }).getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(screen.getByRole('button', { name: '个人话术' })); await waitFor(() => expect(document.querySelector('[data-row="A"]')).toBeTruthy());
  });
  it('切组清空搜索、选中和旧编号，撤权立即撤下入口及内容', async () => {
    const status = teamFixture(); const rendered = view();
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售前小组/ }));
    await waitFor(() => expect(mapping()?.ids).toEqual(['team:A-team:shared:1:1']));
    const input = screen.getByRole('textbox', { name: '搜索话术、本地文件' }); fireEvent.change(input, { target: { value: '欢迎' } });
    await waitFor(() => expect(call).toHaveBeenCalledWith('search_scoped_content', { query: '欢迎', scope: 'team', teamId: 'A-team' }));
    fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售后小组/ }));
    expect((input as HTMLInputElement).value).toBe(''); await waitFor(() => expect(mapping()?.ids).toEqual(['team:B-team:shared:1:1']));
    vi.mocked(useTeams).mockReturnValue({ status: { ...status, teams: [status.teams[0]] }, version: 2, loaded: true });
    vi.mocked(useTeamView).mockReturnValue({ view: null, error: '' });
    rendered.rerender(<MainWindow library={library} refresh={vi.fn(async () => {})} />);
    await waitFor(() => expect(document.querySelector('[data-row="team:B-team:shared:1:1"]')).toBeNull());
    expect(screen.queryByText('共享话术：售后小组')).toBeNull(); expect(mapping()?.ids).not.toContain('team:B-team:shared:1:1');
  });
});
describe('用户自行创建分类与空库', () => {
  it('空库只引导创建一级分类，话术和文件均不会打开无归属的编辑器', async () => {
    render(<MainWindow library={{ ...library, categories: [], phrases: [], files: [] }} refresh={vi.fn(async () => {})} />);
    expect(screen.getByText('还没有一级分类')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '新增话术' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '主菜单' }));
    expect(screen.queryByRole('menuitem', { name: '新增话术' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: '新增二级分类' })).toBeNull();
    fireEvent.pointerDown(document.body);
    fireEvent.click(document.querySelector('.category-tabs .add-category')!);
    fireEvent.change(screen.getByRole('textbox', { name: '分类名称' }), { target: { value: '自己创建' } });
    fireEvent.submit(document.querySelector('dialog form')!);
    await waitFor(() => expect(call).toHaveBeenCalledWith('save_category', { category: { id: '', name: '自己创建', kind: 'phrase', parentId: null, position: 0, background: '' } }));
    await waitFor(() => expect(document.querySelector('dialog')).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: '本地文件' }));
    expect(screen.getByText('还没有一级分类')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '新增关联文件' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '主菜单' }));
    expect((screen.getByRole('menuitem', { name: '新增关联文件' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('menuitem', { name: '新增一级分类' })).toBeNull();
    fireEvent.pointerDown(document.body);
    fireEvent.click(document.querySelector('.category-tabs .add-category')!);
    fireEvent.change(screen.getByRole('textbox', { name: '分类名称' }), { target: { value: '我的文件' } });
    fireEvent.submit(document.querySelector('dialog form')!);
    await waitFor(() => expect(call).toHaveBeenCalledWith('save_category', { category: { id: '', name: '我的文件', kind: 'file', parentId: null, position: 0, background: '' } }));
    expect(openWindow).not.toHaveBeenCalled();
  });
  it('最后一个一级分类可删除，保留话术删除仍需要其他目标', () => {
    render(<MainWindow library={{ ...library, categories: library.categories.filter(c => c.id !== 'other') }} refresh={vi.fn(async () => {})} />);
    fireEvent.click(screen.getByRole('button', { name: '一级分类菜单' }));
    expect((screen.getByRole('menuitem', { name: '删除' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('menuitem', { name: '删除' }));
    expect(openWindow).toHaveBeenCalledWith('delete-confirm', 'common', '', 'category');
    fireEvent.click(screen.getByRole('button', { name: '主菜单' }));
    for (const name of ['新增话术', '新增一级分类', '新增二级分类', '删除一级分类但保留话术', '退出闪语']) expect(screen.queryByRole('menuitem', { name })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '本地文件' }));
    fireEvent.contextMenu(screen.getByRole('button', { name: '文件' }));
    expect((screen.getByRole('menuitem', { name: '删除' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
describe('主窗口实际编号、搜索映射与列表导航', () => {
  it('折叠后连续编号，切换分类重建映射，文件槽位为空', async () => {
    const rendered = view(); await waitFor(() => expect(mapping()).toEqual({ ids: ['A','B','C'], loading: false }));
    fireEvent.click(screen.getByRole('button', { name: '分组' }));
    await waitFor(() => expect(mapping()).toEqual({ ids: ['A','C'], loading: false }));
    expect(rendered.container.querySelector('[data-row="C"] .row-index')?.textContent).toBe('2.');
    fireEvent.click(screen.getByRole('button', { name: '其他' })); await waitFor(() => expect(mapping()?.ids).toEqual(['D']));
    fireEvent.click(screen.getByRole('button', { name: '本地文件' })); await waitFor(() => expect(mapping()?.ids).toEqual([null]));
  });
  it('当前库搜索和组合输入暂停序号，Esc恢复列表', async () => {
    let resolve!: (value: SearchHit[]) => void;
    vi.mocked(call).mockImplementation(async name => name === 'search_scoped_content' ? new Promise<SearchHit[]>(done => { resolve = done; }) : null);
    view(); const input = screen.getByRole('textbox', { name: '搜索话术、本地文件' });
    fireEvent.change(input, { target: { value: '匹配' } });
    await waitFor(() => expect(mapping()?.loading).toBe(true));
    await waitFor(() => expect(resolve).toBeTypeOf('function'));
    expect(call).toHaveBeenCalledWith('search_scoped_content', { query: '匹配', scope: 'private', teamId: null });
    await act(async () => resolve([{ id: 'C', kind: 'phrase', phrase: library.phrases[2], file: null }, { id: 'A', kind: 'phrase', phrase: library.phrases[0], file: null }]));
    await waitFor(() => expect(mapping()).toEqual({ ids: ['C','A'], loading: false }));
    fireEvent.compositionStart(input); await waitFor(() => expect(mapping()?.loading).toBe(true));fireEvent.change(input, { target: { value: '匹配中' } });
    fireEvent.compositionEnd(input); fireEvent.keyDown(input, { key: 'Escape' });
    await waitFor(() => expect(mapping()).toEqual({ ids: ['A','B','C'], loading: false }));
    expect(document.activeElement).toBe(screen.getByRole('listbox', { name: '话术列表' }));
  });
  it('显示侧栏聚焦列表，左右切分类、上下选择、Enter只插入一次', async () => {
    view(); await act(async () => callbacks.get('ui-action')?.({ payload: 'show' }));
    const list = screen.getByRole('listbox', { name: '话术列表' }); expect(document.activeElement).toBe(list);
    const first=document.querySelector<HTMLElement>('[data-row="A"]')!;first.focus();fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(list);fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(document.querySelector('[data-row="B"]')?.getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(list, { key: 'Enter' }); fireEvent.keyDown(list, { key: 'Enter', repeat: true });
    await waitFor(() => expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'deliver_content')).toHaveLength(1));
    expect(call).toHaveBeenCalledWith('deliver_content', { id:'B', kind:'phrase', mode:'insert', target:{hwnd:1,focus:2,pid:3}, triggerKey:13 });
    fireEvent.keyDown(list, { key: 'ArrowRight' }); await waitFor(() => expect(mapping()?.ids).toEqual(['D']));
  });
});

const cloudFile = { assetId: 'cloud-file', name: '价格表.xlsx', size: 12, uploaderName: '管理员', createdAt: 1, deletedAt: null, cached: false };
function cloudFixture() {
  const status = teamFixture(); status.companyFiles = true; status.sessionEpoch = 7;
  HTMLDialogElement.prototype.close = function () { this.open = false; };
  const previous = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation(async (name, args) => name === 'get_company_files' ? { version: 1, files: [cloudFile], uploads: [] } : previous(name, args));
  return status;
}
async function nextCloud() {
  await screen.findByText(cloudFile.name); fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: '下一步' }));
}
describe('右键新增云文件话术', () => {
  it.each(['话术行', '分组前空白', '分组后空白', '一级分类'])('%s带入正确分类，选择只打开编辑器不执行保存或投递', async source => {
    cloudFixture(); view();
    if (source === '话术行') fireEvent.contextMenu(document.querySelector('[data-row="B"]')!);
    else if (source.includes('空白')) {
      document.querySelectorAll('.group-heading').forEach((el, index) => vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({ top: 100 + index * 100 } as DOMRect));
      fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' }), { clientY: source === '分组前空白' ? 80 : 400 });
    }
    else fireEvent.contextMenu(screen.getByRole('button', { name: '其他' }));
    fireEvent.click(screen.getByRole('menuitem', { name: '新增云文件' })); await nextCloud();
    await waitFor(() => expect(openWindow).toHaveBeenCalledWith('editor', '', '', 'cloud:7:cloud-file', source === '一级分类' ? 'other' : 'common', source === '话术行' ? 'group' : source === '分组后空白' ? 'after' : '', ''));
    expect(vi.mocked(call).mock.calls.some(([name]) => ['save_phrase', 'save_company_private_phrase', 'download_company_file', 'deliver_content'].includes(name))).toBe(false);
  });
  it('二级分类原生菜单带入被右键的分组', async () => {
    cloudFixture(); view(); fireEvent.contextMenu(document.querySelector('.group-heading')!);
    await waitFor(() => expect(nativeMenu.new).toHaveBeenCalled()); const item = nativeMenu.new.mock.calls.at(-1)![0].items!.find(item => item.text === '新增云文件')!;
    expect(item.enabled).toBe(true); act(() => item.action!()); await nextCloud();
    await waitFor(() => expect(openWindow).toHaveBeenCalledWith('editor', '', '', 'cloud:7:cloud-file', 'common', 'group', ''));
  });
  it('共享话术文件引用不要求上传权限，仍使用当前共享库及分组', async () => {
    cloudFixture(); view(); fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售后小组/ }));
    await screen.findByText('售后小组话术'); fireEvent.contextMenu(document.querySelector('[data-row="team:B-team:shared:1:1"]')!);
    fireEvent.click(screen.getByRole('menuitem', { name: '新增云文件' })); await nextCloud();
    await waitFor(() => expect(openWindow).toHaveBeenCalledWith('editor', '', '', 'cloud:7:cloud-file', 'B-team-root', 'B-team-child', 'B-team'));
  });
  it('取消选择不创建窗口；已有编辑草稿时展示现有窗口保护提示', async () => {
    cloudFixture(); view(); const list = screen.getByRole('listbox', { name: '话术列表' });
    fireEvent.contextMenu(list); fireEvent.click(screen.getByRole('menuitem', { name: '新增云文件' })); await screen.findByText(cloudFile.name); fireEvent.click(screen.getByRole('button', { name: '取消' })); expect(openWindow).not.toHaveBeenCalled();
    vi.mocked(openWindow).mockRejectedValueOnce(new Error('请先完成或关闭当前话术编辑窗口，再生成新话术。'));
    fireEvent.contextMenu(list); fireEvent.click(screen.getByRole('menuitem', { name: '新增云文件' })); await nextCloud(); await screen.findByText(/请先完成或关闭当前话术编辑窗口/);
  });
  it.each(['账号', '个人库', '分类'])('选择期间%s变化取消旧目标，不打开编辑器', async change => {
    const status = cloudFixture(); const rendered = view(); fireEvent.contextMenu(document.querySelector('[data-row="B"]')!); fireEvent.click(screen.getByRole('menuitem', { name: '新增云文件' })); await screen.findByText(cloudFile.name);
    if (change === '账号') { status.sessionEpoch = 8; vi.mocked(useTeams).mockReturnValue({ status: { ...status }, version: 2, loaded: true }); }
    const next = change === '个人库' ? { ...library, libraryId: 'new-library' } : change === '分类' ? { ...library, categories: library.categories.filter(c => c.id !== 'group') } : library;
    rendered.rerender(<MainWindow library={next} refresh={vi.fn(async () => {})} />);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '选择云文件' })).toBeNull()); expect(openWindow).not.toHaveBeenCalled();
  });
  it.each(['离线', '初始密码', '无分类', '只读共享库'])('%s时禁用入口，不支持云文件时隐藏', async condition => {
    const status = cloudFixture(); if (condition === '离线') status.offline = true; if (condition === '初始密码') status.user!.mustChangePassword = true;
    render(<MainWindow library={condition === '无分类' ? { ...library, categories: [] } : library} refresh={vi.fn(async () => {})} />);
    if (condition === '只读共享库') { fireEvent.click(screen.getByRole('button', { name: '共享话术' })); fireEvent.click(screen.getByRole('menuitem', { name: /^售前小组/ })); await screen.findByText('售前小组话术'); }
    fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' })); expect((screen.getByRole('menuitem', { name: '新增云文件' }) as HTMLButtonElement).disabled).toBe(true);
    cleanup(); vi.mocked(useTeams).mockReturnValue({ status: { ...status, companyFiles: false }, version: 2, loaded: true }); view(); fireEvent.contextMenu(screen.getByRole('listbox', { name: '话术列表' })); expect(screen.queryByRole('menuitem', { name: '新增云文件' })).toBeNull();
  });
});


describe('主窗口四个设置开关独立保存', () => {
  const names = ['吸附', '置顶', '显示常用短语', '隐藏搜索框'];
  const keys = ['dock', 'pinned', 'showCommon', 'showSearch'] as const;
  function setup() {
    let saved: Library = { ...library, libraryId: 'private-a', settings: { ...library.settings, dock: false, pinned: false } };
    let release!: () => void; let fail = false;
    const held = new Promise<void>(resolve => { release = resolve; });
    let writes = 0;
    vi.mocked(call).mockImplementation(async (name, args) => {
      if (name === 'get_library') return { ...saved, settings: { ...saved.settings } };
      if (name === 'save_settings') {
        if (++writes === 1) { await held; if (fail) throw new Error('模拟写入失败'); }
        saved = { ...saved, settings: args!.settings as Library['settings'] };
      }
      return null;
    });
    // 不发送 library-changed，验证主动刷新足以更新成功后的图标。
    const refresh = vi.fn(async () => { rendered.rerender(<MainWindow library={{ ...saved }} refresh={refresh} />); });
    const rendered = render(<MainWindow library={saved} refresh={refresh} />);
    return { release, refresh, rendered, saved: () => saved, fail: () => { fail = true; }, switchLibrary: () => { saved = { ...saved, libraryId: 'private-b' }; rendered.rerender(<MainWindow library={saved} refresh={refresh} />); } };
  }
  const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;
  const writes = () => vi.mocked(call).mock.calls.filter(([name]) => name === 'save_settings');

  it.each(names)('点击%s只禁用自身，重复点击不重复写入', async name => {
    const state = setup(); const clicked = button(name); fireEvent.click(clicked); fireEvent.click(clicked);
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(clicked.disabled).toBe(true); expect(clicked.getAttribute('aria-busy')).toBe('true');
    for (const other of names.filter(n => n !== name)) { expect(button(other).disabled).toBe(false); expect(button(other).getAttribute('aria-busy')).toBe('false'); }
    await act(async () => state.release());
    await waitFor(() => expect(clicked.disabled).toBe(false));
    expect(clicked.getAttribute('aria-pressed')).toBe(name === '隐藏搜索框' ? 'false' : 'true');
    expect(writes()).toHaveLength(1);
  });

  it('连续点击四个按钮依次合并最新设置，事件延迟不覆盖前一次修改', async () => {
    const state = setup(); for (const name of names) fireEvent.click(button(name));
    await waitFor(() => expect(writes()).toHaveLength(1));
    await act(async () => state.release());
    await waitFor(() => expect(writes()).toHaveLength(4));
    expect(keys.map(key => state.saved().settings[key])).toEqual([true, true, true, false]);
    expect(writes().map(([, args]) => args!.libraryId)).toEqual(Array(4).fill('private-a'));
    expect(state.refresh).toHaveBeenCalledTimes(4);
    fireEvent.click(button('吸附'));
    await waitFor(() => expect(button('吸附').getAttribute('aria-pressed')).toBe('false'));
    expect(state.saved().settings.pinned).toBe(true);
  });

  it('某个按钮失败只恢复自身，后续按钮继续保存并保留失败项旧值', async () => {
    const state = setup(); state.fail(); fireEvent.click(button('吸附')); fireEvent.click(button('置顶'));
    await waitFor(() => expect(writes()).toHaveLength(1)); await act(async () => state.release());
    await waitFor(() => expect(button('置顶').getAttribute('aria-pressed')).toBe('true'));
    expect(button('吸附').disabled).toBe(false); expect(button('吸附').getAttribute('aria-pressed')).toBe('false');
    expect(screen.getByRole('status').textContent).toContain('设置保存失败');
  });

  it('切库取消尚未执行的旧库操作，新库按钮不沿用等待状态', async () => {
    const state = setup(); fireEvent.click(button('吸附')); fireEvent.click(button('置顶'));
    await waitFor(() => expect(writes()).toHaveLength(1)); state.switchLibrary();
    expect(button('置顶').disabled).toBe(false);
    await act(async () => state.release()); expect(writes()).toHaveLength(1); expect(state.refresh).not.toHaveBeenCalled();
    fireEvent.click(button('置顶')); await waitFor(() => expect(writes()).toHaveLength(2));
    expect(writes()[1][1]!.libraryId).toBe('private-b');
  });

  it('读取最新设置期间切库，不提交已失效操作', async () => {
    const state = setup(); let resolve!: (value: Library) => void;
    const original = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation((name, args) => name === 'get_library' ? new Promise(done => { resolve = done; }) : original(name, args));
    fireEvent.click(button('吸附')); await waitFor(() => expect(resolve).toBeTruthy());
    const old = state.saved(); state.switchLibrary(); await act(async () => resolve(old));
    expect(writes()).toHaveLength(0);
  });

  it('快捷键展开隐藏搜索和常用短语仍走独立保存，成功后恢复焦点', async () => {
    const state = setup(); fireEvent.click(button('隐藏搜索框')); await waitFor(() => expect(writes()).toHaveLength(1)); await act(async () => state.release());
    await waitFor(() => expect(button('显示搜索框').disabled).toBe(false));
    await act(async () => callbacks.get('ui-action')?.({ payload: 'search' }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('textbox', { name: '搜索话术、本地文件' })));
    await act(async () => callbacks.get('ui-action')?.({ payload: 'common' }));
    await waitFor(() => expect(state.saved().settings.showCommon).toBe(true));
    expect(document.activeElement?.classList.contains('add-common')).toBe(true);
  });
});


describe('成员分类混合显示且整类只读', () => {
  function setupMember() {
    const status=teamFixture(); status.memberPhraseSync=true; status.sessionEpoch=7;
    const followedRoot='member-category:grant:common', followedGroup='member-category:grant:group';
    const key='member:grant:2:3:generation:source:5';
    const memberPhrase: Phrase={...phrase(key,followedGroup,followedRoot),title:'来源话术',contentRef:{source:'member',grantId:'grant',grantVersion:2,subscriptionVersion:3,generation:'generation',id:'source',revision:5}};
    vi.mocked(useMembers).mockReturnValue({categories:[{...library.categories[0],id:followedRoot,readOnly:true,sourceLabel:'成员002'},{...library.categories[1],id:followedGroup,parentId:followedRoot,readOnly:true,sourceLabel:'成员002'}],phrases:[memberPhrase],subscription:{grantId:'grant',version:3},ownerLabel:'成员002',state:'已同步',message:'',lastSuccessfulSyncAt:123});
    return {key,followedRoot};
  }
  it('本人分类在前；同名来源有标记，分类菜单不能修改但仍可创建本人一级分类', async () => {
    setupMember();view();
    const categories=[...document.querySelectorAll('.category-tabs button:not(.add-category)')];
    expect(categories.map(c=>c.textContent)).toEqual(['常用','其他','常用成员002 · 只读']);
    const followed=screen.getByRole('button',{name:'常用成员002 · 只读'});
    fireEvent.click(followed);
    expect(screen.getByRole('button',{name:'分组'})).toBeTruthy();
    expect(screen.queryByRole('button',{name:'编辑分类 分组'})).toBeNull();
    fireEvent.contextMenu(followed);
    expect(screen.queryByRole('menuitem',{name:'重命名'})).toBeNull();
    expect(screen.queryByRole('menuitem',{name:'复制到共享话术'})).toBeNull();
    fireEvent.click(screen.getByRole('menuitem',{name:'新增本人一级分类'}));
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.getByText('新增一级分类')).toBeTruthy();
  });
  it('话术继承分类只读；仍可投递与下载，序号使用含授权版本的来源引用', async () => {
    const {key}=setupMember();view();
    fireEvent.click(screen.getByRole('button',{name:'常用成员002 · 只读'}));
    expect(mapping()?.ids).toEqual([key]);
    const row=document.querySelector(`[data-row="${key}"]`)!;
    fireEvent.contextMenu(row);
    for (const name of ['编辑','移动到其他分类','复制到其他分类','删除','复制到共享话术']) expect(screen.queryByRole('menuitem',{name})).toBeNull();
    fireEvent.click(screen.getByRole('menuitem',{name:'下载全部素材'}));
    await waitFor(()=>expect(call).toHaveBeenCalledWith('download_member_materials',{ids:[key],attachments:true}));
    fireEvent.doubleClick(row);
    await waitFor(()=>expect(call).toHaveBeenCalledWith('deliver_content',expect.objectContaining({id:key,kind:'phrase'})));
  });
  it.each([false, true])('成员话术可设置或清除自己的快捷键，正文仍只读（清除=%s）', async remove => {
    const { key } = setupMember(); view();
    vi.mocked(call).mockImplementation(async (name, args) => name === 'validate_phrase_shortcut' ? args?.shortcut : null);
    fireEvent.click(screen.getByRole('button', { name: '常用成员002 · 只读' }));
    fireEvent.contextMenu(document.querySelector(`[data-row="${key}"]`)!);
    fireEvent.click(screen.getByRole('menuitem', { name: '自定义快捷键' }));
    expect(screen.getByText('仅保存在本机当前账号，不影响来源成员的话术。')).toBeTruthy();
    await waitFor(() => expect((screen.getByRole('button', { name: '确定' }) as HTMLButtonElement).disabled).toBe(false));
    if (remove) fireEvent.click(screen.getByRole('button', { name: '删除话术快捷键' }));
    else fireEvent.keyDown(screen.getByRole('textbox', { name: '第一个按键' }), { key: 'k', code: 'KeyK', ctrlKey: true });
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(call).toHaveBeenCalledWith('save_member_shortcut', { id: key, shortcut: remove ? '' : 'Ctrl+K', sessionEpoch: 7 }));
    expect(vi.mocked(call).mock.calls.some(([name]) => name === 'save_phrase')).toBe(false);
  });
  it('本人移动选择器排除只读分类；右键本人分类不受当前跟随分类影响', async () => {
    setupMember();view();
    fireEvent.contextMenu(document.querySelector('[data-row="A"]')!);
    fireEvent.click(screen.getByRole('menuitem',{name:'移动到其他分类'}));
    expect(screen.getAllByRole('option').map(c=>c.textContent).some(s=>s?.includes('成员002'))).toBe(false);
    expect(document.querySelector('select[name="root"]')?.querySelector('option[value^="member-category:"]')).toBeNull();
    fireEvent.click(screen.getByRole('button',{name:'取消'}));
    fireEvent.click(screen.getByRole('button',{name:'常用成员002 · 只读'}));
    fireEvent.contextMenu(screen.getByRole('button',{name:/^其他$/}));
    fireEvent.click(screen.getByRole('menuitem',{name:'新增话术'}));
    await waitFor(()=>expect(openWindow).toHaveBeenCalledWith('editor','','','','other','',''));
  });
});
