import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { SearchHistoryConfirm } from './SearchHistoryConfirm';
import { call } from './api';

vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn() }));
beforeEach(() => { location.hash = '#search-history-confirm?libraryId=A'; vi.mocked(call).mockResolvedValue(null); });
afterEach(() => { cleanup(); vi.resetAllMocks(); location.hash = ''; });

it.each(['取消', '关闭', 'Escape'])('%s 不修改记录，默认聚焦取消', async action => {
  render(<SearchHistoryConfirm />);
  expect(document.activeElement).toBe(screen.getByRole('button', { name: '取消' }));
  if (action === 'Escape') fireEvent.keyDown(window, { key: 'Escape' });
  else fireEvent.click(screen.getByRole('button', { name: action }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('close_search_history_confirm'));
  expect(vi.mocked(call).mock.calls.some(([name]) => name === 'clear_search_history')).toBe(false);
});

it('提交锁阻止重复及取消，固定原库；失败留窗，由用户重试后关闭', async () => {
  let reject!: (reason: Error) => void;
  vi.mocked(call).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
  render(<SearchHistoryConfirm />);
  const submit = screen.getByRole('button', { name: '确定' });
  fireEvent.click(submit); fireEvent.click(submit); fireEvent.keyDown(window, { key: 'Escape' });
  expect(call).toHaveBeenCalledTimes(1);
  expect(screen.getByRole('button', { name: '取消' })).toHaveProperty('disabled', true);
  location.hash = '#search-history-confirm?libraryId=B';
  await act(async () => reject(new Error('磁盘写入失败')));
  expect(screen.getByRole('alert').textContent).toContain('磁盘写入失败');
  expect(call).toHaveBeenCalledTimes(1);
  fireEvent.click(submit);
  await waitFor(() => expect(call).toHaveBeenCalledWith('close_search_history_confirm'));
  expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'clear_search_history')).toEqual([
    ['clear_search_history', { libraryId: 'A' }], ['clear_search_history', { libraryId: 'A' }],
  ]);
});
