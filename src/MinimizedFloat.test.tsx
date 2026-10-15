import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { invoke } from '@tauri-apps/api/core';
import { MinimizedFloat } from './MinimizedFloat';
import { Menu } from '@tauri-apps/api/menu';

const native = vi.hoisted(() => ({ drag: vi.fn(async () => {}), listeners: new Map<string, () => void>() }));
vi.mock('@tauri-apps/api/menu', () => ({ Menu: { new: vi.fn(async () => ({ popup: vi.fn(), close: vi.fn() })) } }));
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
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.doubleClick(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
  vi.mocked(invoke).mockResolvedValue('green'); native.listeners.get('library-changed')?.();
  await waitFor(() => expect(document.documentElement.dataset.theme).toBe('green'));
});
it('超过6DIP才原生拖动，拖动后的双击事件不还原，下一次正常双击可以还原', async () => {
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0, clientX: 20, clientY: 20 });
  fireEvent.pointerMove(button(), { clientX: 26, clientY: 20 }); expect(native.drag).not.toHaveBeenCalled();
  fireEvent.pointerMove(button(), { clientX: 27, clientY: 20 }); expect(native.drag).toHaveBeenCalledTimes(1);
  fireEvent.pointerUp(button()); fireEvent.doubleClick(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0, clientX: 10, clientY: 10 }); fireEvent.pointerUp(button()); fireEvent.doubleClick(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
});
it('指针取消和拖动失败不误还原，并给出中文错误', async () => {
  native.drag.mockRejectedValueOnce(new Error('失败'));
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0, clientX: 0, clientY: 0 }); fireEvent.pointerMove(button(), { clientX: 10, clientY: 0 });
  await waitFor(() => expect(button().title).toBe('悬浮窗暂时无法拖动，请重试。'));
  fireEvent.doubleClick(button()); expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.pointerCancel(button()); fireEvent.doubleClick(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
});
it('抬键前失去捕获视为取消，正常抬键后失去捕获仍允许双击', () => {
  render(<MinimizedFloat />);
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.lostPointerCapture(button()); fireEvent.doubleClick(button());
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  fireEvent.pointerDown(button(), { button: 0 }); fireEvent.pointerUp(button()); fireEvent.lostPointerCapture(button()); fireEvent.doubleClick(button());
  expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
});
it('右键不打开主窗口，原生菜单分别隐藏本次、永久隐藏及打开主界面', async () => {
  render(<MinimizedFloat />); fireEvent.contextMenu(button(), { clientX: 20, clientY: 12 }); fireEvent.contextMenu(button(), { clientX: 20, clientY: 12 });
  await waitFor(() => expect(Menu.new).toHaveBeenCalledTimes(1));
  expect(invoke).not.toHaveBeenCalledWith('window_action', expect.anything());
  const items = vi.mocked(Menu.new).mock.calls[0][0]!.items as { text: string; action: () => void }[];
  expect(items.map(item => item.text)).toEqual(['隐藏悬浮图标', '永久隐藏悬浮图标', '打开主界面（双击）']);
  items[0].action(); expect(invoke).toHaveBeenCalledWith('hide_minimized_float', { permanent: false });
  items[1].action(); expect(invoke).toHaveBeenCalledWith('hide_minimized_float', { permanent: true });
  items[2].action(); expect(invoke).toHaveBeenCalledWith('window_action', { action: 'show' });
});
it('永久隐藏失败保留入口并显示中文提示', async () => {
  render(<MinimizedFloat />); fireEvent.contextMenu(button());
  await waitFor(() => expect(Menu.new).toHaveBeenCalled());
  vi.mocked(invoke).mockRejectedValueOnce('设置无法保存');
  const items = vi.mocked(Menu.new).mock.calls[0][0]!.items as { text: string; action: () => void }[]; items[1].action();
  await waitFor(() => expect(button().title).toContain('设置无法保存'));
});
