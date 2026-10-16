import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MainWindow } from './App';
import { CommonDockWindow } from './CommonPhrases';
import { useSearchHistory, SearchHistoryBar } from './SearchHistory';
import { call, useTeams, useTeamView } from './api';
import type { DeliveryReport, Library, Phrase, SearchHit } from './types';

const events = vi.hoisted(() => new Map<string, Set<(event: { payload: unknown }) => void>>());
const menus = vi.hoisted(() => [] as { text: string; action: () => void }[][]);
vi.mock('./api', () => ({ call: vi.fn(), openWindow: vi.fn(), closeWindow: vi.fn(), useLibrary: vi.fn(), useTeams: vi.fn(), useTeamView: vi.fn() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: (event: { payload: unknown }) => void) => {
  if (!events.has(name)) events.set(name, new Set()); events.get(name)!.add(callback);
  return () => { events.get(name)?.delete(callback); };
}) }));
vi.mock('@tauri-apps/api/menu', () => ({ Menu: { new: vi.fn(async ({ items }: { items: { text: string; action: () => void }[] }) => {
  menus.push(items); return { popup: vi.fn(async () => {}), close: vi.fn(async () => {}) };
}) } }));

const phrase = (id: string): Phrase => ({ id, rootId: 'root', groupId: null, title: id, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: 'UPS 搜索内容' }] }] });
const hits: SearchHit[] = ['结果一', '结果二'].map(id => ({ id, kind: 'phrase', phrase: phrase(id), file: null, privateKey: `private:A:phrase:${id}:1` }));
const library: Library = {
  libraryId: 'A', categories: [{ id: 'root', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' }],
  phrases: [phrase('普通话术')], files: [], commonPhrases: [], dataDir: '',
  settings: { showSearch: true, dockSearch: true, showCommon: false, dockCommon: false, showPreview: true, showTitle: true, showNumbers: true, expand: true, categoryHeight: 31 } as Library['settings'],
};
let stored: Map<string, string[]>;
const notify = (name: string, payload: unknown) => events.get(name)?.forEach(fn => fn({ payload }));
const recorded = () => vi.mocked(call).mock.calls.filter(([name]) => name === 'record_search_history');
const searches = () => vi.mocked(call).mock.calls.filter(([name]) => name === 'search_scoped_content');
const deliveries = () => vi.mocked(call).mock.calls.filter(([name]) => name === 'deliver_content');

beforeEach(() => {
  stored = new Map([['A', ['UPS', 'SSH']], ['B', ['账号B关键词']]]);
  vi.mocked(useTeams).mockReturnValue({ status: { server: '', username: '', user: null, teams: [], offline: false }, version: 0, loaded: true });
  vi.mocked(useTeamView).mockReturnValue({ view: null, error: '' });
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  Element.prototype.scrollIntoView = vi.fn();
  vi.mocked(call).mockImplementation(async (name, args) => {
    if (name === 'get_search_history') return [...(stored.get(String(args?.libraryId)) || [])];
    if (name === 'record_search_history') {
      const id = String(args?.libraryId), query = String(args?.query).trim();
      stored.set(id, [query, ...(stored.get(id) || []).filter(item => item !== query)].slice(0, 10));
      notify('search-history-changed', id); return null;
    }
    if (name === 'clear_search_history') { stored.set(String(args?.libraryId), []); notify('search-history-changed', args?.libraryId); return null; }
    if (name === 'search_scoped_content') return hits;
    if (name === 'get_target') return { hwnd: 1, focus: 2, pid: 3 };
    if (name === 'deliver_content') return { status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null };
    return null;
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); events.clear(); menus.length = 0; });

function view(dock = false, value = library) {
  const result = render(dock ? <CommonDockWindow library={value} /> : <MainWindow library={value} refresh={vi.fn(async () => {})} />);
  fireEvent.focus(input(dock));
  return result;
}
function input(dock: boolean) { return screen.getByRole('textbox', { name: dock ? '底部搜索话术、文件' : '搜索话术、文件' }); }
function results(dock: boolean) { return dock ? screen.getByRole('listbox', { name: '底部搜索结果' }) : screen.getByRole('region', { name: '搜索结果' }); }

it('主窗口初始与后台更新不显示历史，进入搜索才展开，离开或失焦收起且可再次点击', async () => {
  render(<MainWindow library={library} refresh={vi.fn(async () => {})} />);
  await waitFor(() => expect(vi.mocked(call).mock.calls.some(([name]) => name === 'get_search_history')).toBe(true));
  expect(document.activeElement).not.toBe(input(false));
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  await act(async () => notify('search-history-changed', 'A'));
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  fireEvent.pointerDown(input(false)); act(() => input(false).focus());
  expect(await screen.findByRole('region', { name: '搜索记录' })).toBeTruthy();
  fireEvent.pointerDown(screen.getByRole('region', { name: '搜索记录' }));
  expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
  fireEvent.pointerDown(screen.getByRole('listbox', { name: '话术列表' }));
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  fireEvent.pointerDown(input(false)); expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
  fireEvent.blur(window); fireEvent.focus(window);
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  fireEvent.pointerDown(input(false)); expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
  fireEvent.keyDown(input(false), { key: 'Escape' });
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  await act(async () => notify('ui-action', 'search'));
  expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
  expect(document.activeElement).toBe(input(false));
  await act(async () => notify('tauri://blur', null));
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  fireEvent.pointerDown(input(false));
  act(() => screen.getByRole('button', { name: '主菜单' }).focus());
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
});
describe('底部搜索面板', () => {
  it('失焦关闭浮层但保留短语和关键词，返回原输入框及Esc后重点击均可展开', async () => {
    render(<CommonDockWindow library={{ ...library, commonPhrases: [{ id: 'C', text: '您好', position: 0 }], settings: { ...library.settings, dockCommon: true } }} />);
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    fireEvent.focus(input(true)); await screen.findByRole('region', { name: '搜索记录' });
    await search(true); fireEvent.blur(window);
    expect(screen.queryByRole('listbox', { name: '底部搜索结果' })).toBeNull();
    expect(input(true)).toHaveProperty('value', 'UPS'); expect(screen.getByRole('option', { name: '您好' })).toBeTruthy();
    act(() => input(true).focus()); fireEvent.focus(window);
    expect(screen.getByRole('listbox', { name: '底部搜索结果' })).toBeTruthy();
    fireEvent.keyDown(input(true), { key: 'Escape' }); expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    fireEvent.pointerDown(input(true)); expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
  });
  it.each([false, true])('混合结果按类型分组，编号和跨组键盘投递一致：底部=%s', async dock => {
    const base = vi.mocked(call).getMockImplementation()!;
    const file: SearchHit = { id: 'file1', kind: 'file', phrase: null, file: { id: 'file1', categoryId: 'files', name: 'UPS说明.txt', path: 'C:\\UPS说明.txt', position: 0, missing: false } };
    vi.mocked(call).mockImplementation(async (name, args) => name === 'search_scoped_content' ? [file, ...hits] : base(name, args));
    view(dock); fireEvent.change(input(dock), { target: { value: 'UPS' } });
    await waitFor(() => expect(within(results(dock)).getAllByRole('option')).toHaveLength(3));
    expect(within(results(dock)).getAllByRole('group').map(group => group.getAttribute('aria-label'))).toEqual(['话术', '文件']);
    expect([...results(dock).querySelectorAll('[data-row]')].map(row => row.getAttribute('data-row'))).toEqual(['结果一', '结果二', 'file1']);
    expect([...results(dock).querySelectorAll('.row-index')].map(el => el.textContent)).toEqual(['1.', '2.', '3.']);
    expect(screen.queryByRole('tablist')).toBeNull(); expect(screen.queryByRole('button', { name: '全部' })).toBeNull();
    if (!dock) await waitFor(() => expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'sync_sequence_map').at(-1)?.[1]?.ids).toEqual([...hits.map(hit => hit.privateKey), null]));
    fireEvent.keyDown(input(dock), { key: 'ArrowDown' }); fireEvent.keyDown(input(dock), { key: 'ArrowDown' });
    expect(within(results(dock)).getAllByRole('option')[2].getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(input(dock), { key: 'Enter' });
    await waitFor(() => expect(deliveries()).toHaveLength(1));
    expect(deliveries()[0][1]).toMatchObject({ id: 'file1', kind: 'file', mode: 'insert' });
    expect(searches()).toHaveLength(1);
  });
  it.each([false, true])('仅文件或无结果时不显示空分组：底部=%s', async dock => {
    const base = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation(async (name, args) => name === 'search_scoped_content' ? args?.query === 'none' ? [] : [{ id: 'f', kind: 'file', phrase: null, file: { id: 'f', name: 'UPS.pdf', categoryId: 'files', path: 'C:\\UPS.pdf', position: 0, missing: false } }] : base(name, args));
    view(dock); fireEvent.change(input(dock), { target: { value: 'UPS' } });
    await within(await screen.findByRole(dock ? 'listbox' : 'region', { name: dock ? '底部搜索结果' : '搜索结果' })).findByRole('option');
    expect(within(results(dock)).getAllByRole('group').map(group => group.getAttribute('aria-label'))).toEqual(['文件']);
    fireEvent.change(input(dock), { target: { value: 'none' } }); await screen.findByText('没有找到匹配的内容');
    expect(within(results(dock)).queryAllByRole('group')).toHaveLength(0);
  });
  it('收起保留关键词和分组、不保存设置；展开重新查询并聚焦，短语保持独立', async () => {
    view(true, { ...library, commonPhrases: [{ id: 'common', text: '您好', position: 0 }], settings: { ...library.settings, dockCommon: true } });
    await search(true);
    fireEvent.compositionStart(input(true));
    fireEvent.click(screen.getByRole('button', { name: '收起底部搜索' }));
    expect(screen.queryByRole('textbox')).toBeNull(); expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    expect(screen.getByRole('option', { name: '您好' })).toBeTruthy();
    expect(vi.mocked(call).mock.calls.some(([name]) => name === 'save_settings')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '展开底部搜索' }));
    expect(input(true)).toHaveProperty('value', 'UPS');
    expect(screen.queryByRole('tablist')).toBeNull();
    await waitFor(() => expect(searches()).toHaveLength(2));
    await waitFor(() => expect(document.activeElement).toBe(input(true)));
    fireEvent.keyDown(input(true), { key: 'Escape' });
    expect(input(true)).toHaveProperty('value', ''); expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  });
  it('搜索结果替换历史，关闭后恢复历史并可选择其他关键词，不触发投递', async () => {
    view(true); await search(true);
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    expect(screen.getByRole('region', { name: '搜索结果' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '关闭底部搜索结果' }));
    expect(input(true)).toHaveProperty('value', ''); expect(document.activeElement).toBe(input(true));
    expect(screen.queryByRole('region', { name: '搜索结果' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'SSH' }));
    await waitFor(() => expect(searches().at(-1)?.[1]).toMatchObject({ query: 'SSH' }));
    expect(input(true)).toHaveProperty('value', 'SSH');
    await waitFor(() => expect(within(results(true)).getAllByRole('option')).toHaveLength(2));
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    expect(deliveries()).toHaveLength(0); expect(recorded()).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: '关闭底部搜索结果' }));
    fireEvent.click(screen.getByRole('button', { name: '清空搜索记录' }));
    await waitFor(() => expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull());
    expect(input(true)).toHaveProperty('value', '');
  });
  it('输入法确认不投递，发送中的重复Enter只提交一次', async () => {
    view(true); await search(true);
    fireEvent.compositionStart(input(true)); fireEvent.keyDown(input(true), { key: 'Enter', isComposing: true });
    expect(deliveries()).toHaveLength(0);
    fireEvent.compositionEnd(input(true)); await waitFor(() => expect(searches()).toHaveLength(2));
    const base = vi.mocked(call).getMockImplementation()!;
    let finish!: (value: DeliveryReport) => void;
    vi.mocked(call).mockImplementation(async (name, args) => name === 'deliver_content' ? new Promise(resolve => { finish = resolve; }) : base(name, args));
    fireEvent.keyDown(input(true), { key: 'Enter' }); fireEvent.keyUp(input(true), { key: 'Enter' }); fireEvent.keyDown(input(true), { key: 'Enter' });
    await waitFor(() => expect(deliveries()).toHaveLength(1));
    await act(async () => finish({ status: 'submitted', completedSegments: 1, completedBlocks: 1 } as DeliveryReport));
  });
});
async function search(dock: boolean, query = 'UPS') {
  fireEvent.change(input(dock), { target: { value: query } });
  await waitFor(() => expect(within(results(dock)).getAllByRole('option')).toHaveLength(2));
}

describe.each([false, true])('搜索记录入口，底部=%s', dock => {
  it('点击历史切换到结果，保持焦点、首条选中，清空后同词也刷新', async () => {
    view(dock); const button = await screen.findByRole('button', { name: 'UPS' });
    fireEvent.click(button);
    await waitFor(() => expect(within(results(dock)).getAllByRole('option')).toHaveLength(2));
    expect(input(dock)).toHaveProperty('value', 'UPS'); expect(document.activeElement).toBe(input(dock));
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
    expect(searches().at(-1)?.[1]).toEqual({ query: 'UPS', scope: 'all' });
    fireEvent.keyDown(input(dock), { key: 'ArrowDown' }); expect(within(results(dock)).getAllByRole('option')[1].getAttribute('aria-selected')).toBe('true');
    const count = searches().length;
    fireEvent.click(screen.getByRole('button', { name: dock ? '清空底部搜索' : '清空搜索' }));
    fireEvent.click(screen.getByRole('button', { name: 'UPS' }));
    expect(deliveries()).toHaveLength(0); expect(recorded()).toHaveLength(0);
    await waitFor(() => expect(searches()).toHaveLength(count + 1));
    await waitFor(() => expect(within(results(dock)).getAllByRole('option')[0].getAttribute('aria-selected')).toBe('true'));
  });

  it.each(['send', 'double', 'enter', 'context-insert', 'context-send'])('通过%s使用结果，成功后记录并保留退出搜索设置', async entry => {
    view(dock, { ...library, settings: { ...library.settings, exitSearch: true } });
    await search(dock, ' 新关键词 ');
    expect(recorded()).toHaveLength(0);
    const row = within(results(dock)).getAllByRole('option')[0];
    if (entry === 'send') fireEvent.click(within(row).getByRole('button', { name: '直接发送 结果一' }));
    if (entry === 'double') fireEvent.doubleClick(row);
    if (entry === 'enter') fireEvent.keyDown(input(dock), { key: 'Enter' });
    if (entry.startsWith('context')) {
      fireEvent.contextMenu(row);
      if (dock) { await waitFor(() => expect(menus).toHaveLength(1)); menus[0].find(item => item.text === (entry === 'context-send' ? '直接发送' : '添加到输入区'))!.action(); }
      else fireEvent.click(screen.getByRole('menuitem', { name: entry === 'context-send' ? '直接发送' : '添加到输入区（双击）' }));
    }
    await waitFor(() => expect(recorded()).toHaveLength(1));
    expect(recorded()[0][1]).toEqual({ libraryId: 'A', query: ' 新关键词 ' });
    expect(deliveries()[0][1]?.mode).toBe(entry === 'send' || entry === 'context-send' ? 'send' : 'insert');
    await waitFor(() => expect(input(dock)).toHaveProperty('value', ''));
    // 真实投递可能令窗口失焦；重新进入搜索后仍可读取成功记录。
    fireEvent.pointerDown(input(dock));
    expect(await screen.findByRole('button', { name: '新关键词' })).toBeTruthy();
  });

  it.each(['failed', 'interrupted'])('投递%s不记搜索记录', async status => {
    const base = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation(async (name, args) => name === 'deliver_content' ? { status, completedSegments: 0, completedBlocks: 0, error: '操作已停止' } : base(name, args));
    view(dock); await search(dock, '未完成'); fireEvent.keyDown(input(dock), { key: 'Enter' });
    expect((await screen.findByRole('status')).textContent).toContain('操作已停止'); expect(recorded()).toHaveLength(0);
  });

  it('清空记录不清空搜索，清空搜索不删除记录，隐藏搜索不占记录空间', async () => {
    const rendered = view(dock); await search(dock);
    fireEvent.click(screen.getByRole('button', { name: dock ? '清空底部搜索' : '清空搜索' }));
    expect(input(dock)).toHaveProperty('value', ''); expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
    await search(dock); await act(async () => { await call('clear_search_history', { libraryId: 'A' }); });
    await waitFor(() => expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull());
    expect(input(dock)).toHaveProperty('value', 'UPS'); expect(within(results(dock)).getAllByRole('option')).toHaveLength(2);
    stored.set('A', ['UPS']); await act(async () => notify('search-history-changed', 'A'));
    fireEvent.click(screen.getByRole('button', { name: dock ? '清空底部搜索' : '清空搜索' }));
    expect(await screen.findByRole('region', { name: '搜索记录' })).toBeTruthy();
    const next = { ...library, settings: { ...library.settings, showSearch: false, dockSearch: false } };
    rendered.rerender(dock ? <CommonDockWindow library={next} /> : <MainWindow library={next} refresh={vi.fn(async () => {})} />);
    expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  });

  it('投递期间输入变化仍记录触发时的关键词，保存失败只报告记录错误', async () => {
    let finish!: (report: DeliveryReport) => void;
    const base = vi.mocked(call).getMockImplementation()!;
    vi.mocked(call).mockImplementation(async (name, args) => {
      if (name === 'deliver_content') return new Promise<DeliveryReport>(resolve => { finish = resolve; });
      if (name === 'record_search_history') throw new Error('保存失败');
      return base(name, args);
    });
    view(dock); await search(dock, '原搜索词'); fireEvent.keyDown(input(dock), { key: 'Enter' });
    await waitFor(() => expect(finish).toBeTypeOf('function'));
    fireEvent.change(input(dock), { target: { value: '后来输入' } });
    await act(async () => finish({ status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null }));
    expect(recorded()[0][1]).toEqual({ libraryId: 'A', query: '原搜索词' });
    expect((await screen.findByRole('status')).textContent).toContain('已提交操作，但搜索记录保存失败');
    expect(deliveries()).toHaveLength(1);
  });
});

it('主窗口和底部记录通过通知共用，清空两处更新', async () => {
  const main = render(<MainWindow library={library} refresh={vi.fn(async () => {})} />);
  const dock = render(<CommonDockWindow library={library} />);
  fireEvent.focus(input(false));
  fireEvent.focus(input(true));
  await waitFor(() => expect(screen.getAllByRole('region', { name: '搜索记录' })).toHaveLength(2));
  await search(false, '两窗口'); fireEvent.keyDown(input(false), { key: 'Enter' });
  await waitFor(() => expect(recorded()).toHaveLength(1));
  fireEvent.click(screen.getByRole('button', { name: '清空搜索' }));
  await waitFor(() => expect(within(main.container).getByRole('button', { name: '两窗口' })).toBeTruthy());
  await waitFor(() => expect(within(dock.container).getByRole('button', { name: '两窗口' })).toBeTruthy());
  fireEvent.click(within(dock.container).getByRole('button', { name: '清空搜索记录' }));
  await waitFor(() => expect(screen.queryAllByRole('region', { name: '搜索记录' })).toHaveLength(1));
  expect(within(main.container).getByRole('button', { name: '清空搜索记录' })).toHaveProperty('disabled', true);
});

it('主窗口切换到本地文件后，历史仍搜索全部来源', async () => {
  view(); fireEvent.click(screen.getByRole('button', { name: '文件' }));
  fireEvent.pointerDown(input(false));
  fireEvent.click(await screen.findByRole('button', { name: 'UPS' }));
  await waitFor(() => expect(searches().at(-1)?.[1]).toEqual({ query: 'UPS', scope: 'all' }));
  expect(recorded()).toHaveLength(0);
});

it('主窗口结果浮层按类型分组，搜索时保留常用短语布局，关闭后恢复记录', async () => {
  view(false, { ...library, commonPhrases: [{ id: 'common', text: '谢谢', position: 0 }], settings: { ...library.settings, showCommon: true } });
  expect(screen.getByRole('region', { name: '常用短语' })).toBeTruthy();
  fireEvent.click(await screen.findByRole('button', { name: 'UPS' }));
  await waitFor(() => expect(within(results(false)).getByRole('group', { name: '话术' })).toBeTruthy());
  expect(screen.getByRole('region', { name: '常用短语' })).toBeTruthy();
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '关闭搜索' }));
  expect(screen.getByRole('region', { name: '常用短语' })).toBeTruthy();
  expect(screen.getByRole('region', { name: '搜索记录' })).toBeTruthy();
});

it.each(['phrase', 'file'] as const)('主窗口搜索结果的%s右键菜单保留列表、滚动及关键词，Esc恢复焦点，外部点击仍收起', async kind => {
  const base = vi.mocked(call).getMockImplementation()!;
  const file: SearchHit = { id: 'file1', kind: 'file', phrase: null, file: { id: 'file1', categoryId: 'files', name: 'UPS说明.txt', path: 'C:\\UPS说明.txt', position: 0, missing: false } };
  vi.mocked(call).mockImplementation(async (name, args) => name === 'search_scoped_content' ? [...hits, file] : base(name, args));
  view(); fireEvent.change(input(false), { target: { value: 'UPS' } });
  await waitFor(() => expect(within(results(false)).getAllByRole('option')).toHaveLength(3));
  const panel = results(false), list = panel.querySelector('#search-results')!;
  const row = within(panel).getAllByRole('option')[kind === 'phrase' ? 1 : 2];
  list.scrollTop = 50;
  fireEvent.pointerDown(row, { button: 2 }); fireEvent.contextMenu(row);
  const menu = await screen.findByRole('menu');
  expect(menu.contains(document.activeElement)).toBe(true);
  expect(results(false)).toBe(panel); expect(list.scrollTop).toBe(50);
  fireEvent.keyDown(document.activeElement!, { key: 'ArrowDown' });
  const copy = within(menu).getByRole('menuitem', { name: kind === 'phrase' ? '复制正文到剪贴板' : '复制文件' });
  fireEvent.pointerDown(copy); act(() => copy.focus());
  expect(results(false)).toBe(panel); expect(input(false)).toHaveProperty('value', 'UPS');
  fireEvent.keyDown(copy, { key: 'Escape' });
  expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(row);
  expect(results(false)).toBe(panel);
  fireEvent.contextMenu(row);
  fireEvent.click(screen.getByRole('menuitem', { name: kind === 'phrase' ? '复制正文到剪贴板' : '复制文件' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith(kind === 'phrase' ? 'copy_phrase_text' : 'copy_linked_files', kind === 'phrase' ? { id: '结果二' } : { ids: ['file1'] }));
  expect(results(false)).toBe(panel); expect(deliveries()).toHaveLength(0);
  fireEvent.pointerDown(screen.getByRole('button', { name: '主菜单' }));
  expect(screen.queryByRole('region', { name: '搜索结果' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '主菜单' }));
  expect(screen.getByRole('menu')).toBeTruthy();
  expect(screen.queryByRole('region', { name: '搜索结果' })).toBeNull();
  fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
  fireEvent.pointerDown(input(false));
  fireEvent.contextMenu(within(results(false)).getAllByRole('option')[0]);
  fireEvent.blur(window);
  expect(screen.queryByRole('menu')).toBeNull();
  expect(screen.queryByRole('region', { name: '搜索结果' })).toBeNull();
  expect(input(false)).toHaveProperty('value', 'UPS');
});

it('普通列表操作、搜索结果复制和序号投递回执不记录', async () => {
  view(); fireEvent.doubleClick(screen.getByRole('option'));
  await waitFor(() => expect(deliveries()).toHaveLength(1)); expect(recorded()).toHaveLength(0);
  await search(false); fireEvent.contextMenu(within(results(false)).getAllByRole('option')[0]);
  fireEvent.click(screen.getByRole('menuitem', { name: '复制正文到剪贴板' }));
  await act(async () => notify('shortcut-delivery', { status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null }));
  expect(recorded()).toHaveLength(0);
});

function History({ libraryId }: { libraryId: string }) {
  const history = useSearchHistory(libraryId, vi.fn());
  return <SearchHistoryBar items={history.items} clearing={history.clearing} onSelect={vi.fn()} onClear={() => void history.clear()} />;
}

it('切库立即隐藏旧记录，旧读取响应与旧库通知不能覆盖当前库', async () => {
  let finish!: (items: string[]) => void;
  const base = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation(async (name, args) => name === 'get_search_history' && args?.libraryId === 'A' ? new Promise<string[]>(resolve => { finish = resolve; }) : base(name, args));
  const rendered = render(<History libraryId="A" />); await waitFor(() => expect(finish).toBeTypeOf('function'));
  rendered.rerender(<History libraryId="B" />);
  expect(screen.queryByRole('button', { name: 'UPS' })).toBeNull();
  expect(await screen.findByRole('button', { name: '账号B关键词' })).toBeTruthy();
  await act(async () => { finish(['旧库响应']); notify('search-history-changed', 'A'); });
  expect(screen.queryByRole('button', { name: '旧库响应' })).toBeNull();
  expect(screen.getByRole('button', { name: '账号B关键词' })).toBeTruthy();
});

it('同库较早读取晚返回，不能覆盖后来的清空刷新', async () => {
  let finish!: (items: string[]) => void; let requests = 0;
  const base = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation(async (name, args) => name === 'get_search_history' && ++requests === 1 ? new Promise<string[]>(resolve => { finish = resolve; }) : base(name, args));
  render(<History libraryId="A" />); await waitFor(() => expect(finish).toBeTypeOf('function'));
  stored.set('A', []); await act(async () => notify('search-history-changed', 'A'));
  await act(async () => finish(['已清空的旧记录']));
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
});

it('主窗口空历史保留提示，遮罩只关闭，清空入口打开绑定库的独立确认', async () => {
  stored.set('A', []); view();
  const history = await screen.findByRole('region', { name: '搜索记录' });
  expect(within(history).getByText('搜索方式，可使用拼音、关键词、拼音首字母')).toBeTruthy();
  expect(within(history).getByRole('button', { name: '清空搜索记录' })).toHaveProperty('disabled', true);
  fireEvent.click(document.querySelector('.search-backdrop')!);
  expect(screen.queryByRole('region', { name: '搜索记录' })).toBeNull();
  expect(deliveries()).toHaveLength(0);
  stored.set('A', ['UPS']); await act(async () => notify('search-history-changed', 'A'));
  fireEvent.pointerDown(input(false));
  fireEvent.click(screen.getByRole('button', { name: '清空搜索记录' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('open_search_history_confirm', { libraryId: 'A' }));
  expect(stored.get('A')).toEqual(['UPS']);
});

it('主窗口浮层失焦后保留关键词但不能用Enter投递隐藏结果，重新进入才可操作', async () => {
  view(); await search(false);
  fireEvent.blur(window);
  expect(input(false)).toHaveProperty('value', 'UPS');
  expect(screen.queryByRole('region', { name: '搜索结果' })).toBeNull();
  fireEvent.keyDown(input(false), { key: 'Enter' });
  expect(deliveries()).toHaveLength(0);
  fireEvent.pointerDown(input(false));
  fireEvent.keyDown(input(false), { key: 'Enter' });
  await waitFor(() => expect(deliveries()).toHaveLength(1));
});
