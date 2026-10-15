import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { PrivateSyncSettings } from './PrivateSyncSettings';
import { call } from './api';
import type { TeamStatus } from './types';

vi.mock('./api', () => ({ call: vi.fn() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true), save: vi.fn(async () => null) }));
const account = { user: { id: 'user', mustChangePassword: false }, offline: false, privateSync: true } as TeamStatus;
const status = { libraryId: 'library', username: '', enabled: false, state: '已同步', pending: 0, conflicts: 0, settingsPending: 0, message: '', sameAccount: false };
beforeEach(() => {
  vi.mocked(call).mockImplementation(async command => command === 'get_private_status' ? status : undefined);
  HTMLDialogElement.prototype.showModal = vi.fn(function(this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

it('同步默认关闭，旧服务端和离线账号不能开启', async () => {
  const view = render(<PrivateSyncSettings account={{ ...account, privateSync: false }} />);
  await screen.findByText('仅保存在本机');
  expect((screen.getByRole('button', { name: '开启同步' }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByText('服务端需升级后才能同步个人话术。')).toBeTruthy();
  expect(vi.mocked(call).mock.calls.some(([command]) => command === 'confirm_private_sync')).toBe(false);
  view.rerender(<PrivateSyncSettings account={{ ...account, offline: true }} />);
  expect((screen.getByRole('button', { name: '开启同步' }) as HTMLButtonElement).disabled).toBe(true);
});
it('首次合并默认使用服务器，保留两份通过显式选择提交', async () => {
  vi.mocked(call).mockImplementation(async command => command === 'get_private_status' ? status : command === 'preview_private_sync' ? { id: 'preview', categories: 1, phrases: 1, commonPhrases: 0, resume: false, restore: false, conflicts: [{ key: 'phrase:P', label: '重复话术', local: '本机版', server: '云端版', setting: false }] } : undefined);
  render(<PrivateSyncSettings account={account} />);
  fireEvent.click(await screen.findByRole('button', { name: '开启同步' }));
  const choice = await screen.findByRole('combobox', { name: '合并 重复话术' });
  expect((choice as HTMLSelectElement).value).toBe('server');
  fireEvent.change(choice, { target: { value: 'both' } });
  await waitFor(() => expect((screen.getByRole('button', { name: '确认同步' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: '确认同步' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('confirm_private_sync', { request: { previewId: 'preview', choices: { 'phrase:P': 'both' } } }));
});
it('未确认的发送设置只在本机显式应用，失败保留预览', async () => {
  vi.mocked(call).mockImplementation(async command => command === 'get_private_status' ? { ...status, settingsPending: 1 } : command === 'get_private_settings' ? [{ key: 'enter', current: 'enter', next: 'ctrl-enter' }] : command === 'apply_private_settings' ? Promise.reject(new Error('快捷键被占用')) : undefined);
  render(<PrivateSyncSettings account={account} />);
  fireEvent.click(await screen.findByRole('button', { name: '设置待确认（1）' }));
  await screen.findByText(/发送按键/);
  expect(vi.mocked(call).mock.calls.some(([c]) => c === 'apply_private_settings' || c === 'save_settings')).toBe(false);
  await waitFor(() => expect((screen.getByRole('button', { name: '应用本机' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: '应用本机' }));
  await screen.findByRole('alert');
  expect(call).toHaveBeenCalledWith('apply_private_settings', { libraryId: 'library' });
  expect(screen.getByText(/发送按键/)).toBeTruthy();
  expect(vi.mocked(call).mock.calls.some(([c]) => c === 'save_settings')).toBe(false);
});

it('暂停只改变同步状态，不切库、不退出登录，并保留最近成功时间', async () => {
  const time = new Date(2026, 9, 14, 15, 30, 26).getTime();
  let current = { ...status, username: 'linyue', initialized: true, sameAccount: true, enabled: true, lastSuccessfulSyncAt: time };
  vi.mocked(call).mockImplementation(async command => {
    if (command === 'get_private_status') return current;
    if (command === 'pause_private_sync') current = { ...current, enabled: false };
  });
  render(<PrivateSyncSettings account={account} />);
  fireEvent.click(await screen.findByRole('button', { name: '暂停同步' }));
  await screen.findByRole('button', { name: '恢复同步' });
  expect(screen.getByText('2026-10-14 15:30:26')).toBeTruthy();
  expect(screen.getByText('所属账号：linyue')).toBeTruthy();
  expect(screen.queryByRole('button', { name: '本机个人库' })).toBeNull();
  expect(vi.mocked(call).mock.calls.some(([c]) => ['team_logout', 'use_local_private_library'].includes(c))).toBe(false);
});
it('退出或离线仍展示原所属账号和时间，禁止发起在线同步', async () => {
  vi.mocked(call).mockResolvedValue({ ...status, username: 'linyue', initialized: true, lastSuccessfulSyncAt: null });
  render(<PrivateSyncSettings account={{ ...account, user: null }} />);
  await screen.findByText('未登录，内容保留在本机');
  expect(screen.getByText('所属账号：linyue')).toBeTruthy();
  expect(screen.getByText('暂无成功同步记录')).toBeTruthy();
  expect((screen.getByRole('button', { name: '恢复同步' }) as HTMLButtonElement).disabled).toBe(true);
});
it('新账号独立内容首次开启，不显示恢复同步', async () => {
  vi.mocked(call).mockResolvedValue({ ...status, username: 'new-user', sameAccount: true, initialized: false });
  render(<PrivateSyncSettings account={account} />);
  await screen.findByText('所属账号：new-user');
  expect((screen.getByRole('button', { name: '开启同步' }) as HTMLButtonElement).disabled).toBe(false);
  expect(screen.queryByRole('button', { name: '恢复同步' })).toBeNull();
});
