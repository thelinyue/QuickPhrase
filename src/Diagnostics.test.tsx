import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { call, closeWindow } from './api';
import { save } from '@tauri-apps/plugin-dialog';
import { Diagnostics } from './Diagnostics';

vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ save: vi.fn(async () => null) }));
const stopped = { running: false, remainingSeconds: 0, categories: [], records: 7, dropped: 0, overwritten: 0, diskFailed: false, panicRecorded: false };
beforeEach(() => { vi.mocked(call).mockResolvedValue(stopped); });
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('按选择启动，成功后通过正常关闭请求返回，不修改设置', async () => {
  render(<Diagnostics />);
  await screen.findByText('未在记录');
  fireEvent.click(screen.getByRole('checkbox', { name: /窗口与显示/ }));
  vi.mocked(call).mockResolvedValue({ ...stopped, running: true, remainingSeconds: 300 });
  fireEvent.click(screen.getByRole('button', { name: '开始记录' }));
  await waitFor(() => expect(closeWindow).toHaveBeenCalledOnce());
  expect(call).toHaveBeenCalledWith('diagnostics_start', { categories: ['input', 'window'] });
  expect(vi.mocked(call).mock.calls.every(([name]) => name.startsWith('diagnostics_'))).toBe(true);
});
it('启动失败保持设置窗口打开并可重试', async () => {
  render(<Diagnostics />); await screen.findByText('未在记录');
  vi.mocked(call).mockRejectedValueOnce(new Error('无法启动记录'));
  fireEvent.click(screen.getByRole('button', { name: '开始记录' }));
  await screen.findByRole('alert'); expect(closeWindow).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: '开始记录' }).hasAttribute('disabled')).toBe(false);
});
it('重新进入显示剩余时间，停止并导出后取消文件选择仍保留记录', async () => {
  vi.mocked(call).mockResolvedValue({ ...stopped, running: true, remainingSeconds: 125, categories: ['network'] });
  render(<Diagnostics />); await screen.findByText('记录中 · 剩余 2:05');
  expect(screen.queryByRole('button', { name: '导出日志' })).toBeNull();
  expect((screen.getByRole('checkbox', { name: /账号与同步/ }) as HTMLInputElement).checked).toBe(true);
  vi.mocked(call).mockResolvedValue(stopped);
  fireEvent.click(screen.getByRole('button', { name: '停止并导出' }));
  await screen.findByText('未在记录');
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(call).not.toHaveBeenCalledWith('diagnostics_export', expect.anything());
  expect(screen.getByRole('button', { name: '导出日志' }).hasAttribute('disabled')).toBe(false);
});
it('停止失败不打开保存对话框，允许重试', async () => {
  vi.mocked(call).mockResolvedValue({ ...stopped, running: true, remainingSeconds: 125, categories: ['input'] });
  render(<Diagnostics />); await screen.findByText('记录中 · 剩余 2:05');
  vi.mocked(call).mockRejectedValueOnce(new Error('停止失败'));
  fireEvent.click(screen.getByRole('button', { name: '停止并导出' }));
  await screen.findByText('Error: 停止失败');
  expect(save).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: '停止并导出' }).hasAttribute('disabled')).toBe(false);
});
it('导出取消或失败均不清除记录', async () => {
  render(<Diagnostics />); await screen.findByText('未在记录');
  await act(async () => fireEvent.click(screen.getByRole('button', { name: '导出日志' })));
  expect(call).not.toHaveBeenCalledWith('diagnostics_export', expect.anything());
  vi.mocked(save).mockResolvedValue('D:/report.zip');
  vi.mocked(call).mockRejectedValueOnce(new Error('文件正在使用'));
  fireEvent.click(screen.getByRole('button', { name: '导出日志' }));
  await screen.findByText('Error: 文件正在使用');
  expect(call).toHaveBeenCalledWith('diagnostics_export', { path: 'D:/report.zip' });
  expect(call).not.toHaveBeenCalledWith('diagnostics_clear');
  expect(screen.getByText('未在记录')).toBeTruthy();
});
