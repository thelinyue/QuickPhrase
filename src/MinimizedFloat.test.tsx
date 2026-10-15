import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { invoke } from '@tauri-apps/api/core';
import { MinimizedFloat } from './MinimizedFloat';

const native = vi.hoisted(() => ({ drag: vi.fn(async () => {}), listeners: new Map<string, () => void>() }));
vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn(async () => 'default') }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ startDragging: native.drag }) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (event, handler) => { native.listeners.set(event, handler); return () => native.listeners.delete(event); }) }));
beforeEach(() => {
  vi.clearAllMocks(); native.listeners.clear(); vi.mocked(invoke).mockResolvedValue('default');
  window.PointerEvent = MouseEvent as typeof PointerEvent;
  HTMLElement.prototype.setPointerCapture = vi.fn(); HTMLElement.prototype.releasePointerCapture = vi.fn();
});
afterEach(cleanup);
const button = () => screen.getByRole('button', { name: '打开闪语' });
it('仅读主题，并通过原有入口还原窗口', async () => {
  render(<MinimizedFloat />);
  await waitFor(() => expect(invoke).toHaveBeenCalledWith('get_minimized_float_theme'));
  expect(invoke).toHaveBeenCalledTimes(1);
  fireEvent.pointerDown(button(), { button: 0, clientX: 20, clientY: 20 });
  expect(button().classList.contains('pressed')).toBe(true);
  fireEvent.pointerUp(button()); fireEvent.lostPointerCapture(button()); fireEvent.click(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
  vi.mocked(invoke).mockResolvedValue('green'); native.listeners.get('library-changed')?.();
  await waitFor(() => expect(document.documentElement.dataset.theme).toBe('green'));
});
it('超过6DIP才原生拖动，松开后的点击不还原，下一次单击可以还原', async () => {
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0, clientX: 20, clientY: 20 });
  fireEvent.pointerMove(button(), { clientX: 26, clientY: 20 }); expect(native.drag).not.toHaveBeenCalled();
  fireEvent.pointerMove(button(), { clientX: 27, clientY: 20 }); expect(native.drag).toHaveBeenCalledTimes(1);
  fireEvent.pointerUp(button()); fireEvent.click(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0, clientX: 10, clientY: 10 }); fireEvent.pointerUp(button()); fireEvent.click(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
});
it('指针取消和拖动失败不误还原，并给出中文错误', async () => {
  native.drag.mockRejectedValueOnce(new Error('失败'));
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0, clientX: 0, clientY: 0 }); fireEvent.pointerMove(button(), { clientX: 10, clientY: 0 });
  await waitFor(() => expect(button().title).toBe('悬浮窗暂时无法拖动，请重试。'));
  fireEvent.click(button()); expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.pointerCancel(button()); fireEvent.click(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
});
it('抬键前失去捕获视为取消，正常抬键后失去捕获仍允许单击', () => {
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.lostPointerCapture(button()); fireEvent.click(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.pointerUp(button()); fireEvent.lostPointerCapture(button()); fireEvent.click(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
});
