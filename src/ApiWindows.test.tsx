import { cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
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

it('新增云文件遇到已有编辑器只聚焦，拒绝替换已有草稿', async () => {
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
