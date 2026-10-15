import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { UsageGuideWindow } from './UsageGuide';
import { call, closeWindow } from './api';
import type { Settings } from './types';

const native = vi.hoisted(() => ({ isVisible: vi.fn(async () => true), minimize: vi.fn() }));
vi.mock('./api', () => ({ call: vi.fn(async () => null), closeWindow: vi.fn(async () => {}), openWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => native }));
let frame: FrameRequestCallback;
beforeEach(() => {
  native.isVisible.mockResolvedValue(true);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frame = callback; return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); });

it('只有内容完成首帧且原生窗口可见后才保存本机已展示状态', async () => {
  native.isVisible.mockResolvedValueOnce(false); const first = render(<UsageGuideWindow />);
  expect(screen.getByRole('heading', { name: '创建分类' })).toBeTruthy(); expect(screen.getByRole('heading', { name: '添加话术' })).toBeTruthy(); expect(screen.getByRole('heading', { name: '聚焦目标，再插入' })).toBeTruthy();
  expect(call).not.toHaveBeenCalled(); await act(async () => frame(0)); expect(call).not.toHaveBeenCalled(); first.unmount();
  render(<UsageGuideWindow />); await act(async () => frame(0)); expect(call).toHaveBeenCalledWith('mark_usage_guide_shown');
});

it('指引显示当前快捷键及点击设置，查看主题不提交投递或示例数据', () => {
  render(<UsageGuideWindow settings={{ searchHotkey: '8', windowHotkey: '9', commonHotkey: '0', phraseDoubleClick: 'send', commonSingleClick: 'insert' } as Settings} />);
  expect(screen.getByText(/当前话术双击设置为直接发送/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '搜索与快捷键' })); expect(screen.getByText('Alt+8')).toBeTruthy(); expect(screen.getByText(/单击添加到输入区，双击不重复插入/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '本地文件与云文件' })); expect(screen.getByText(/单文件需为1字节至128MB/)).toBeTruthy();
  expect(call).not.toHaveBeenCalled();
});

it('关闭按钮和非输入法Esc关闭指引，开始使用交由原生处理关闭与主窗口激活', async () => {
  render(<UsageGuideWindow />); fireEvent.keyDown(document, { key: 'Escape', isComposing: true }); expect(closeWindow).not.toHaveBeenCalled();
  fireEvent.keyDown(document, { key: 'Escape' }); expect(closeWindow).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: '关闭窗口' })); expect(closeWindow).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole('button', { name: '开始使用' })); await waitFor(() => expect(call).toHaveBeenCalledWith('window_action', { action: 'start-using' }));
});
