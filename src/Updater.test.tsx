import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { UpdateNotice, UpdateSettings, type UpdateStatus } from './Updater';
import { call, openWindow } from './api';

let update: (event: { payload: UpdateStatus }) => void;
const unlisten = vi.fn();
vi.mock('./api', () => ({ call: vi.fn(), openWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (_name, callback) => { update = callback; return unlisten; }) }));
const initial: UpdateStatus = { config: { automatic: true, route: 'default', customProxy: '' }, phase: 'idle', release: null, downloaded: 0, total: null, message: '', lastCheck: 0, revision: 1 };
beforeEach(() => {
  vi.mocked(call).mockResolvedValue(initial);
  HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });
async function mount(status = initial) { vi.mocked(call).mockResolvedValue(status); render(<UpdateSettings />); await screen.findByRole('combobox', { name: '下载线路' }); }
function publish(patch: Partial<UpdateStatus>) { act(() => update({ payload: { ...initial, revision: 2, ...patch } })); }

it('四种线路可选，自定义和自动检查选择只在保存并检查时提交', async () => {
  await mount();
  expect(screen.getAllByRole('option')).toHaveLength(4);
  fireEvent.change(screen.getByLabelText('下载线路'), { target: { value: 'custom' } });
  fireEvent.change(screen.getByLabelText('代理地址'), { target: { value: 'https://example.com/proxy/' } });
  fireEvent.click(screen.getByRole('checkbox'));
  expect(call).not.toHaveBeenCalledWith('save_update_config', expect.anything());
  fireEvent.click(screen.getByRole('button', { name: '保存并检查' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('check_update'));
  expect(call).toHaveBeenCalledWith('save_update_config', { config: { automatic: false, route: 'custom', customProxy: 'https://example.com/proxy/' } });
  expect(vi.mocked(call).mock.calls.findIndex(([c]) => c === 'save_update_config')).toBeLessThan(vi.mocked(call).mock.calls.findIndex(([c]) => c === 'check_update'));
});

it('检查失败显示错误，允许切换线路，重复点击不重复提交', async () => {
  await mount(); let fail!: (reason: Error) => void;
  vi.mocked(call).mockImplementation(async command => command === 'check_update' ? new Promise((_resolve, reject) => { fail = reject; }) : initial);
  const button = screen.getByRole('button', { name: '检查更新' }); fireEvent.click(button); fireEvent.click(button);
  expect(vi.mocked(call).mock.calls.filter(([c]) => c === 'check_update')).toHaveLength(1);
  await act(async () => fail(new Error('网络连接失败')));
  expect(screen.getByRole('alert').textContent).toContain('网络连接失败');
  expect((screen.getByLabelText('下载线路') as HTMLSelectElement).closest('fieldset')?.disabled).toBe(false);
});

it('下载中禁止改线路，可取消，迟到的状态不覆盖新进度', async () => {
  await mount();
  publish({ phase: 'downloading', downloaded: 40, total: 100 });
  expect((screen.getByLabelText('下载线路') as HTMLSelectElement).closest('fieldset')?.disabled).toBe(true);
  expect((screen.getByLabelText('更新下载进度') as HTMLProgressElement).value).toBe(40);
  publish({ revision: 1, phase: 'idle' });
  expect(screen.getByLabelText('更新下载进度')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '取消下载' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('cancel_update'));
});

it('重开设置恢复下载状态，不重新开始任务', async () => {
  await mount({ ...initial, phase: 'downloading', downloaded: 80, total: 100 });
  expect((screen.getByLabelText('更新下载进度') as HTMLProgressElement).value).toBe(80);
  cleanup(); expect(unlisten).toHaveBeenCalledTimes(1);
  await mount({ ...initial, phase: 'downloading', downloaded: 90, total: 100 });
  expect((screen.getByLabelText('更新下载进度') as HTMLProgressElement).value).toBe(90);
  expect(call).not.toHaveBeenCalledWith('download_update');
});

it('稍后继续使用，安装明确说明免安装转安装，后端拒绝保留确认框', async () => {
  await mount({ ...initial, phase: 'ready', release: { version: '1.0.0', notes: '<script>不作为HTML执行</script>' }, message: '已下载' });
  expect(document.querySelector('.update-notes script')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '稍后' })); expect(call).not.toHaveBeenCalledWith('install_update');
  fireEvent.click(screen.getByRole('button', { name: '退出并安装' }));
  const dialog = screen.getByRole('dialog'); expect(dialog.textContent).toContain('免安装版将转为安装版');
  vi.mocked(call).mockRejectedValue(new Error('请先关闭编辑窗口'));
  fireEvent.submit(dialog.querySelector('form')!);
  await screen.findByText('Error: 请先关闭编辑窗口'); expect(screen.getByRole('dialog')).toBeTruthy();
});

it('下载失败可重试，配置有未保存修改时不能使用旧线路下载', async () => {
  await mount({ ...initial, phase: 'error', release: { version: '1.0.0', notes: '' }, message: '校验失败' });
  fireEvent.change(screen.getByLabelText('下载线路'), { target: { value: 'direct' } });
  expect((screen.getByRole('button', { name: '重新下载' }) as HTMLButtonElement).disabled).toBe(true);
});

it('后台失败不弹提示，验证通过的新版提示可关闭并打开关于页', async () => {
  vi.mocked(call).mockResolvedValue({ ...initial, phase: 'error', message: '连接失败' }); render(<UpdateNotice />);
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_update_status'));
  expect(screen.queryByRole('status')).toBeNull();
  publish({ phase: 'available', release: { version: '1.0.0', notes: '' } });
  fireEvent.click(screen.getByText('发现新版本 v1.0.0，查看更新')); expect(openWindow).toHaveBeenCalledWith('settings', '', '', '关于');
  fireEvent.click(screen.getByRole('button', { name: '关闭更新提示' })); expect(screen.queryByRole('status')).toBeNull();
});
