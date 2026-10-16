import { act, cleanup, fireEvent, render, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MainWindow } from './App';
import { CommonDockWindow } from './CommonPhrases';
import { call, useTeams } from './api';
import type { Library, Phrase, SearchHit } from './types';

vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(), useLibrary: vi.fn(), useTeams: vi.fn(), useTeamView: vi.fn(() => ({ view: null, error: '' })) }));
vi.mock('./useMembers', () => ({ useMembers: vi.fn(() => null) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}) }));
const phrase = (id: string): Phrase => ({ id, rootId: 'root', groupId: null, title: id, color: '', background: '', shortcut: 'Q+W', position: 0, segments: [{ blocks: [{ kind: 'text', text: `P ${id}` }] }] });
const phrases = [phrase('A'), phrase('B')];
const hits = (items = phrases): SearchHit[] => items.map(p => ({ id: p.id, kind: 'phrase', phrase: p, file: null, sourceLabel: '个人话术' }));
const library: Library = { libraryId: 'one', dataDir: '', categories: [{ id: 'root', name: '常用', parentId: null, kind: 'phrase', background: '', position: 0 }], phrases, files: [], commonPhrases: [], settings: { showSearch: true, showCommon: false, dockSearch: true, dockCommon: false, showPreview: true, showTitle: true, expand: true, categoryHeight: 31, fileCategoryHeight: 31 } as Library['settings'] };
let requests: { resolve: (hits: SearchHit[]) => void; reject: (error: Error) => void }[];
beforeEach(() => {
  requests = [];
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  Element.prototype.scrollIntoView = vi.fn();
  vi.mocked(useTeams).mockReturnValue({ version: 0, loaded: true, status: { server: '', username: '', user: null, teams: [], offline: false } });
  vi.mocked(call).mockImplementation(async command => command === 'search_scoped_content' ? new Promise<SearchHit[]>((resolve, reject) => requests.push({ resolve, reject })) : command === 'get_search_history' ? [] : null);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); });
const searches = () => vi.mocked(call).mock.calls.filter(([command]) => command === 'search_scoped_content');
const settle = async (index: number, items = hits()) => { await waitFor(() => expect(requests.length).toBeGreaterThan(index)); await act(async () => requests[index].resolve(items)); };

describe.each(['主搜索', '底部搜索'])('%s后台刷新', location => {
  function setup() {
    const element = (value: Library) => location === '主搜索' ? <MainWindow library={value} refresh={vi.fn(async () => {})} /> : <CommonDockWindow library={value} />;
    const view = render(element(library));
    const input = view.getByRole('textbox', { name: location === '主搜索' ? '搜索话术、文件' : '底部搜索话术、文件' });
    const panel = () => view.container.querySelector<HTMLElement>(location === '主搜索' ? '#search-results' : '.dock-search-results')!;
    const row = (id: string) => panel().querySelector<HTMLElement>(`[data-row="${id}"]`)!;
    fireEvent.change(input, { target: { value: 'P' } });
    return { ...view, input, panel, row, update: (value: Library) => view.rerender(element(value)) };
  }
  it('相同内容新快照、主题及同步通知不重新搜索，保留选中与滚动位置', async () => {
    const view = setup(); await settle(0); fireEvent.click(view.row('B'));
    const row = view.row('B'); view.panel().scrollTop = 44;
    const team = vi.mocked(useTeams).mock.results.at(-1)!.value;
    vi.mocked(useTeams).mockReturnValue({ ...team, version: 3, status: { ...team.status, teamSyncTimes: { team: 1234 } } });
    view.update({ ...structuredClone(library), settings: { ...library.settings, theme: 'green' } });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 160)); });
    expect(searches()).toHaveLength(1); expect(view.row('B')).toBe(row);
    expect(row.getAttribute('aria-selected')).toBe('true'); expect(view.panel().scrollTop).toBe(44);
    expect((within(row).getByRole('button') as HTMLButtonElement).disabled).toBe(false);
  });
  it('内容变化时保留行并暂停投递，更新后按身份保留选中而非原序号', async () => {
    const view = setup(); await settle(0); fireEvent.click(view.row('B')); const row = view.row('B');
    const changed = [{ ...phrases[1], title: '新的 B' }, phrases[0]];
    view.update({ ...library, phrases: changed });
    expect(view.row('B')).toBe(row); expect((within(row).getByRole('button') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.doubleClick(row); fireEvent.keyDown(view.input, { key: 'Enter' });
    expect(call).not.toHaveBeenCalledWith('deliver_content', expect.anything());
    await settle(1, hits(changed));
    expect(view.row('B')).toBe(row); expect(row.getAttribute('aria-selected')).toBe('true');
    expect(row.textContent).toContain('新的 B'); expect((within(row).getByRole('button') as HTMLButtonElement).disabled).toBe(false);
  });
  it('关键词改变立即撤下旧结果，晚到的响应不覆盖新结果', async () => {
    const view = setup(); await waitFor(() => expect(requests).toHaveLength(1));
    fireEvent.change(view.input, { target: { value: 'B' } }); await settle(1, hits([phrases[1]]));
    await settle(0, hits([phrases[0]])); expect(view.row('A')).toBeNull(); expect(view.row('B')).toBeTruthy();
  });
  it('中文组合输入期间不搜索、不投递，结束后读取最终关键词', async () => {
    const view = setup(); await settle(0);
    fireEvent.compositionStart(view.input); fireEvent.change(view.input, { target: { value: '拼' } });
    fireEvent.keyDown(view.input, { key: 'Enter', isComposing: true });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 160)); });
    expect(searches()).toHaveLength(1); expect(call).not.toHaveBeenCalledWith('deliver_content', expect.anything());
    fireEvent.compositionEnd(view.input); await settle(1);
    expect(searches()[1][1]?.query).toBe('拼');
  });
  it('后台查询失败保留旧行但不能投递，显示明确错误', async () => {
    const view = setup(); await settle(0); const row = view.row('A');
    view.update({ ...library, phrases: [{ ...phrases[0], title: '更新' }, phrases[1]] });
    await waitFor(() => expect(requests).toHaveLength(2)); await act(async () => requests[1].reject(new Error('查询失败')));
    expect(view.row('A')).toBe(row); expect((within(row).getByRole('button') as HTMLButtonElement).disabled).toBe(true);
    expect(view.container.textContent).toContain('查询失败');
  });
  it('账号身份改变立即撤下结果，前一账号的在途请求不能恢复它们', async () => {
    const view = setup(); await settle(0);
    view.update({ ...library, phrases: [{ ...phrases[0], title: '更新' }] }); await waitFor(() => expect(requests).toHaveLength(2));
    const team = vi.mocked(useTeams).mock.results.at(-1)!.value;
    vi.mocked(useTeams).mockReturnValue({ ...team, status: { ...team.status, sessionEpoch: 9 } });
    view.update({ ...library }); expect(view.row('A')).toBeNull();
    await settle(1); expect(view.row('A')).toBeNull(); await settle(2, []); expect(view.row('A')).toBeNull();
  });
});
