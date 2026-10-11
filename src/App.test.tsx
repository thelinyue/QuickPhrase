import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest';
import { MainWindow } from './App';
import { call } from './api';
import type { Library, Phrase, SearchHit } from './types';

const callbacks = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(), useLibrary: vi.fn() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: (event: {payload: unknown}) => void) => { callbacks.set(name, callback); return () => callbacks.delete(name); }) }));
beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  vi.mocked(call).mockImplementation(async name => name === 'get_target' ? { hwnd: 1, focus: 2, pid: 3 } : name === 'deliver_content' ? { status: 'submitted', completedSegments: 1, completedBlocks: 1, error: null } : name === 'search_content' ? [] : null);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); callbacks.clear(); });
const phrase = (id: string, groupId: string | null = null, rootId = 'common'): Phrase => ({ id, rootId, groupId, title: id, color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: id }] }] });
const library: Library = {
  categories: [{ id: 'common', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' }, { id: 'group', name: '分组', kind: 'phrase', parentId: 'common', position: 0, background: '' }, { id: 'after', name: '后组', kind: 'phrase', parentId: 'common', position: 1, background: '' }, { id: 'other', name: '其他', kind: 'phrase', parentId: null, position: 1, background: '' }, { id: 'local-files', name: '文件', kind: 'file', parentId: null, position: 0, background: '' }],
  phrases: [phrase('A'), phrase('B', 'group'), phrase('C', 'after'), phrase('D', null, 'other')], files: [{ id: 'F', name: '文件', categoryId: 'local-files', path: 'missing.txt', position: 0, missing: true }], commonPhrases: [], dataDir: '',
  settings: { showTitle: true, showPreview: true, showNumbers: true, expand: true, showSearch: true, showCommon: false, categoryHeight: 31, fileCategoryHeight: 31 } as Library['settings'],
};
const mapping = () => vi.mocked(call).mock.calls.filter(([name]) => name === 'sync_sequence_map').at(-1)?.[1];
const view = () => render(<MainWindow library={library} refresh={vi.fn(async () => {})} />);
describe('主窗口实际编号、搜索映射与列表导航', () => {
  it('折叠后连续编号，切换分类重建映射，文件槽位为空', async () => {
    const rendered = view(); await waitFor(() => expect(mapping()).toEqual({ ids: ['A','B','C'], loading: false }));
    fireEvent.click(screen.getByRole('button', { name: '分组' }));
    await waitFor(() => expect(mapping()).toEqual({ ids: ['A','C'], loading: false }));
    expect(rendered.container.querySelector('[data-row="C"] .row-index')?.textContent).toBe('2.');
    fireEvent.click(screen.getByRole('button', { name: '其他' })); await waitFor(() => expect(mapping()?.ids).toEqual(['D']));
    fireEvent.click(screen.getByRole('button', { name: '本地文件' })); await waitFor(() => expect(mapping()?.ids).toEqual([null]));
  });
  it('搜索加载和组合输入暂停序号，混合结果保留文件位置，Esc恢复列表', async () => {
    let resolve!: (value: SearchHit[]) => void;
    vi.mocked(call).mockImplementation(async name => name === 'search_content' ? new Promise<SearchHit[]>(done => { resolve = done; }) : null);
    view(); const input = screen.getByRole('textbox', { name: '搜索话术、本地文件' });
    fireEvent.change(input, { target: { value: '匹配' } });
    await waitFor(() => expect(mapping()?.loading).toBe(true));
    await waitFor(() => expect(resolve).toBeTypeOf('function'));
    await act(async () => resolve([{ id: 'C', kind: 'phrase', phrase: library.phrases[2], file: null }, { id: 'F', kind: 'file', file: library.files[0], phrase: null }, { id: 'A', kind: 'phrase', phrase: library.phrases[0], file: null }]));
    await waitFor(() => expect(mapping()).toEqual({ ids: ['C',null,'A'], loading: false }));
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
