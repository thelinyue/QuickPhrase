import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import { useDeliveryProgress } from './useDeliveryProgress';
import { usePendingDeliveryNotices, type PendingDeliveryNotice } from './usePendingDeliveryNotices';

const listeners = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name: string, callback: (event: { payload: unknown }) => void) => { listeners.set(name, callback); return () => listeners.delete(name); }) }));
vi.mock('./api', () => ({ call: vi.fn() }));
beforeEach(() => { vi.mocked(call).mockResolvedValue([]); });
afterEach(() => { cleanup(); listeners.clear(); vi.clearAllMocks(); });

it('剪贴板等待按操作与序号去重，结束后的旧事件不能重新显示', async () => {
  const { result } = renderHook(() => useDeliveryProgress('main'));
  await waitFor(() => expect(listeners.has('delivery-progress')).toBe(true));
  const send = (operation: number, sequence: number, waiting: boolean, recipient = 'main') => act(() => listeners.get('delivery-progress')!({ payload: { operation, sequence, waiting, recipient } }));
  send(2, 1, true); expect(result.current.waiting?.operation).toBe(2);
  send(2, 2, false); send(2, 1, true); expect(result.current.waiting).toBeNull();
  send(1, 3, true); expect(result.current.waiting).toBeNull();
  send(3, 1, true, 'common-dock'); expect(result.current.waiting).toBeNull();
  send(4, 1, true); expect(result.current.waiting?.operation).toBe(4);
});

it('回执只在指定窗口显示，部分执行优先并可逐条确认', async () => {
  const rows: PendingDeliveryNotice[] = [
    { id: 1, recipient: 'main', status: 'failed', message: '未完成' },
    { id: 2, recipient: 'main', status: 'interrupted', message: '请先检查' },
    { id: 3, recipient: 'common-dock', status: 'failed', message: '底栏失败' },
  ];
  vi.mocked(call).mockImplementation(async (name, args) => name === 'pending_delivery_notices' ? rows : name === 'dismiss_delivery_notice' ? rows.splice(rows.findIndex(item => item.id === args?.id), 1) : null);
  const { result } = renderHook(() => usePendingDeliveryNotices('account-a', 'main'));
  await waitFor(() => expect(result.current.items.map(item => item.id)).toEqual([2, 1]));
  await act(async () => result.current.dismiss(2));
  expect(result.current.items.map(item => item.id)).toEqual([1]);
  expect(call).toHaveBeenCalledWith('dismiss_delivery_notice', { id: 2 });
});

it('确认后较早读取的迟到快照不会恢复已关闭的回执', async () => {
  const item: PendingDeliveryNotice = { id: 1, recipient: 'main', status: 'failed', message: '未完成' };
  let requests = 0; let resolveOld: (items: PendingDeliveryNotice[]) => void = () => {};
  vi.mocked(call).mockImplementation(async name => {
    if (name === 'dismiss_delivery_notice') return null;
    if (name !== 'pending_delivery_notices') return null;
    if (++requests === 1) return [item];
    if (requests === 2) return new Promise(resolve => { resolveOld = resolve; });
    return [];
  });
  const { result } = renderHook(() => usePendingDeliveryNotices('account-a', 'main'));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  act(() => listeners.get('delivery-notices-changed')!({ payload: null }));
  await waitFor(() => expect(requests).toBe(2));
  await act(async () => result.current.dismiss(1));
  await act(async () => resolveOld([item]));
  expect(result.current.items).toHaveLength(0);
});

it('换账号后旧快照迟到不会恢复到新账号', async () => {
  const pending: ((items: PendingDeliveryNotice[]) => void)[] = [];
  vi.mocked(call).mockImplementation(async name => name === 'pending_delivery_notices' ? new Promise(resolve => pending.push(resolve)) : null);
  const { result, rerender } = renderHook(({ scope }) => usePendingDeliveryNotices(scope, 'main'), { initialProps: { scope: 'a' } });
  await waitFor(() => expect(pending.length).toBe(1));
  rerender({ scope: 'b' }); await waitFor(() => expect(pending.length).toBe(2));
  await act(async () => pending[1]([{ id: 2, recipient: 'main', status: 'failed', message: '新账号' }]));
  await act(async () => pending[0]([{ id: 1, recipient: 'main', status: 'failed', message: '旧账号' }]));
  expect(result.current.items.map(item => item.message)).toEqual(['新账号']);
});

it('读取失败时提示先检查目标，并在后续结果存入时主动再次读取', async () => {
  vi.mocked(call).mockRejectedValueOnce(new Error('IPC 失败'));
  const { result } = renderHook(() => usePendingDeliveryNotices('account-a', 'main'));
  await waitFor(() => expect(result.current.problem).toMatch(/检查目标.*避免重复/));
  act(() => result.current.dismissProblem());
  expect(result.current.problem).toBe('');
  vi.mocked(call).mockResolvedValueOnce([{ id: 1, recipient: 'main', status: 'interrupted', message: '已提交一部分，请检查' }]);
  await act(async () => result.current.refresh());
  expect(result.current.items[0]?.message).toBe('已提交一部分，请检查');
});

it('订阅失败不能静默丢失结果，且旧账号迟到的读取失败不会污染新账号', async () => {
  vi.mocked(listen).mockRejectedValueOnce(new Error('订阅失败'));
  const { result, unmount } = renderHook(() => usePendingDeliveryNotices('account-a', 'main'));
  await waitFor(() => expect(result.current.problem).toMatch(/检查目标.*避免重复/));
  unmount();

  let rejectOld: (error: Error) => void = () => {};
  let requests = 0;
  vi.mocked(call).mockImplementation(async name => name !== 'pending_delivery_notices' ? null : new Promise((resolve, reject) => {
    if (++requests === 1) rejectOld = reject;
    else resolve([]);
  }));
  const next = renderHook(({ scope }) => usePendingDeliveryNotices(scope, 'main'), { initialProps: { scope: 'a' } });
  await waitFor(() => expect(requests).toBe(1));
  next.rerender({ scope: 'b' });
  await waitFor(() => expect(requests).toBe(2));
  await act(async () => rejectOld(new Error('旧账号读取失败')));
  expect(next.result.current.problem).toBe('');
});

it('订阅失败后关闭警告会重试订阅，恢复后能收到其他窗口的新回执', async () => {
  vi.mocked(listen).mockRejectedValueOnce(new Error('暂时无法订阅'));
  const { result } = renderHook(() => usePendingDeliveryNotices('account-a', 'main'));
  await waitFor(() => expect(result.current.problem).toMatch(/检查目标/));
  act(() => result.current.dismissProblem());
  await waitFor(() => expect(listeners.has('delivery-notices-changed')).toBe(true));
  vi.mocked(call).mockResolvedValue([{ id: 4, recipient: 'main', status: 'failed', message: '来自另一个窗口' }]);
  act(() => listeners.get('delivery-notices-changed')!({ payload: null }));
  await waitFor(() => expect(result.current.items[0]?.message).toBe('来自另一个窗口'));
  expect(result.current.problem).toBe('');
});
