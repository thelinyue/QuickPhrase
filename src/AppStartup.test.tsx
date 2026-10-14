import { act, cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { App } from './App';
import { call, openWindow, useLibrary } from './api';

const callbacks = vi.hoisted(() => new Map<string, (event: { payload: string }) => void>());
vi.mock('./api', () => ({ call: vi.fn(), openWindow: vi.fn(), useLibrary: vi.fn(), closeWindow: vi.fn() }));
vi.mock('./UsageGuide', () => ({ UsageGuideWindow: () => <div>本机使用指引</div> }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name, callback) => { callbacks.set(name, callback); return () => callbacks.delete(name); }) }));
beforeEach(() => {
  location.hash = '';
  vi.mocked(useLibrary).mockReturnValue({ library: null, error: '', refresh: vi.fn(async () => {}) });
  vi.mocked(call).mockImplementation(async name => name === 'get_usage_guide_state' ? { pending: true, autoOpen: false } : null);
});
afterEach(() => { cleanup(); callbacks.clear(); vi.clearAllMocks(); location.hash = ''; });

it('页面订阅晚于原生显示时，读取本机快照仍能打开首次指引', async () => {
  vi.mocked(call).mockImplementation(async name => name === 'get_usage_guide_state' ? { pending: true, autoOpen: true } : null);
  render(<App />); await waitFor(() => expect(openWindow).toHaveBeenCalledWith('usage-guide'));
});

it('自启不打开指引，读取状态期间的手动唤起补读状态而不丢请求', async () => {
  let resolve!: (state: unknown) => void, reads = 0;
  vi.mocked(call).mockImplementation(async name => name !== 'get_usage_guide_state' ? null : ++reads === 1 ? new Promise(done => { resolve = done; }) : { pending: true, autoOpen: true });
  render(<App />); await waitFor(() => expect(reads).toBe(1)); expect(openWindow).not.toHaveBeenCalled();
  await act(async () => callbacks.get('ui-action')?.({ payload: 'launch' }));
  await act(async () => resolve({ pending: true, autoOpen: false }));
  await waitFor(() => expect(openWindow).toHaveBeenCalledWith('usage-guide')); expect(reads).toBe(2);
});

it('已展示及已有用户不自动打开，卸载后的迟到状态不再开窗', async () => {
  let resolve!: (state: unknown) => void;
  vi.mocked(call).mockImplementation(async name => name === 'get_usage_guide_state' ? { pending: false, autoOpen: false } : null);
  const first = render(<App />); await waitFor(() => expect(call).toHaveBeenCalledWith('get_usage_guide_state'));
  await act(async () => callbacks.get('ui-action')?.({ payload: 'show' })); expect(openWindow).not.toHaveBeenCalled(); first.unmount();
  vi.mocked(call).mockImplementation(async name => name === 'get_usage_guide_state' ? new Promise(done => { resolve = done; }) : null);
  const second = render(<App />); await waitFor(() => expect(resolve).toBeTypeOf('function')); second.unmount();
  await act(async () => resolve({ pending: true, autoOpen: true })); expect(openWindow).not.toHaveBeenCalled();
});

it('独立指引不依赖账号库加载，也不触发主窗口首次展示检查', async () => {
  location.hash = '#usage-guide'; const rendered = render(<App />);
  expect(rendered.getByText('本机使用指引')).toBeTruthy(); expect(call).not.toHaveBeenCalledWith('get_usage_guide_state');
});
