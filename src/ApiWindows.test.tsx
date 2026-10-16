import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { listen } from '@tauri-apps/api/event';
import type { Library, Settings } from './types';
import { openWindow, useLibrary } from './api';

const windows = vi.hoisted(() => ({ getByLabel: vi.fn(), created: vi.fn(), invoke: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ invoke: windows.invoke }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}), emitTo: vi.fn() }));
vi.mock('@tauri-apps/api/webviewWindow', () => ({ WebviewWindow: class {
  static getByLabel = windows.getByLabel;
  constructor(label: string, options: { url: string }) { windows.created(label, options); }
  async once(event: string, callback: () => void) { if (event === 'tauri://created') callback(); }
} }));
afterEach(() => { cleanup(); vi.clearAllMocks(); });

it('从云文件新建话术遇到已有编辑器只聚焦，拒绝替换已有草稿', async () => {
  const editor = { show: vi.fn(async () => {}), setFocus: vi.fn(async () => {}) }; windows.getByLabel.mockResolvedValue(editor);
  await expect(openWindow('editor', '', '', 'cloud:1:asset', 'root', 'group')).rejects.toThrow('请先完成或关闭当前话术编辑窗口');
  expect(editor.show).toHaveBeenCalledOnce(); expect(editor.setFocus).toHaveBeenCalledOnce(); expect(windows.created).not.toHaveBeenCalled();
});

it('异步查询窗口期间切换个人库，编辑窗口仍携带入口原库和分类', async () => {
  windows.invoke.mockResolvedValue({ libraryId: 'original', phrases: [] });
  const hook = renderHook(() => useLibrary()); await waitFor(() => expect(hook.result.current.library?.libraryId).toBe('original'));
  let resolve!: (value: null) => void; windows.getByLabel.mockImplementationOnce(() => new Promise(done => { resolve = done; }));
  const opening = openWindow('editor', '', '', 'cloud:1:asset', 'root', 'group');
  windows.invoke.mockResolvedValue({ libraryId: 'changed', phrases: [] }); await hook.result.current.refresh();
  resolve(null); await opening;
  const query = new URLSearchParams(windows.created.mock.calls[0][1].url.split('?')[1]);
  expect(query.get('libraryId')).toBe('original'); expect(query.get('categoryId')).toBe('root'); expect(query.get('groupId')).toBe('group');
});

it('设置通知不读取整库，迟到整库响应不覆盖新设置，旧账号通知不改变当前库', async () => {
  const handlers = new Map<string, (event: { payload: unknown }) => void>();
  vi.mocked(listen).mockImplementation(async (name, handler) => { handlers.set(String(name), handler as (event: { payload: unknown }) => void); return () => {}; });
  const initial = { libraryId: 'settings-A', settingsRevision: 1, settings: { theme: 'default' }, phrases: [], categories: [{ id: 'root' }], files: [] } as unknown as Library;
  windows.invoke.mockResolvedValue(initial);
  const hook = renderHook(() => useLibrary()); await waitFor(() => expect(hook.result.current.library?.libraryId).toBe('settings-A'));
  const content = hook.result.current.library!.categories;
  let resolve!: (value: Library) => void; windows.invoke.mockImplementationOnce(() => new Promise(done => { resolve = done; }));
  const pending = hook.result.current.refresh(); const calls = windows.invoke.mock.calls.length;
  act(() => handlers.get('settings-changed')!({ payload: { libraryId: 'settings-A', revision: 3, settings: { theme: 'blue' } as Settings } }));
  expect(windows.invoke.mock.calls).toHaveLength(calls); expect(hook.result.current.library!.categories).toBe(content);
  await act(async () => { resolve({ ...initial, settingsRevision: 2 }); await pending; });
  expect(hook.result.current.library!.settings.theme).toBe('blue');
  act(() => handlers.get('settings-changed')!({ payload: { libraryId: 'old-account', revision: 9, settings: { theme: 'green' } } }));
  expect(hook.result.current.library!.settings.theme).toBe('blue');
});
