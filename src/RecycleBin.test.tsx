import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { DeletionWindow } from './RecycleBin';
import { call, closeWindow } from './api';
import type { Library } from './types';

vi.mock('./api', () => ({ call: vi.fn(async () => {}), closeWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}) }));
afterEach(() => { cleanup(); vi.clearAllMocks(); });
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
