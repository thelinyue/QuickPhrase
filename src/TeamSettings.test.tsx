import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { TeamSettings } from './TeamSettings';
import { call, openWindow, useTeams } from './api';
import type { TeamStatus } from './types';

vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn(), openWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true) }));
let status: TeamStatus;
beforeEach(() => {
  status = { server: 'http://127.0.0.1:7823', username: 'admin', offline: false, privateSync: true, teams: [], user: { id: 'admin-id', username: 'admin', displayName: '', contact: '', admin: true, disabled: false, mustChangePassword: false, canCreateTeams: false } };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 0, loaded: true }));
  vi.mocked(call).mockImplementation(async command => command === 'get_private_status' ? { libraryId: 'local', username: '', enabled: false, state: '已同步', pending: 0, conflicts: 0, settingsPending: 0, message: '', sameAccount: false } : null);
  HTMLDialogElement.prototype.showModal = vi.fn(function(this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

it('未登录以连接表单为主，密码显示切换不改变值，提交期间锁定表单', async () => {
  status = { ...status, user: null };
  let complete!: () => void;
  vi.mocked(call).mockImplementation(async () => new Promise<void>(resolve => { complete = resolve; }));
  render(<TeamSettings />);
  expect(screen.getByRole('heading', { name: '账号' })).toBeTruthy();
  expect(screen.getByLabelText('服务地址')).toBeTruthy();
  expect(screen.queryByText('闪云')).toBeNull();
  const password = screen.getByLabelText('密码') as HTMLInputElement;
  fireEvent.change(password, { target: { value: 'test-password' } });
  fireEvent.click(screen.getByRole('button', { name: '显示密码' })); expect(password.type).toBe('text'); expect(password.value).toBe('test-password');
  fireEvent.submit(password.closest('form')!);
  expect(call).toHaveBeenCalledWith('team_login', { server: status.server, username: 'admin', password: 'test-password' });
  expect(password.closest('fieldset')!.disabled).toBe(true);
  expect(screen.queryByRole('button', { name: '管理话术' })).toBeNull();
  await act(async () => complete()); expect(password.value).toBe('');
});

it('首次改密自动弹出，取消保留提醒并禁止管理及同步提交', async () => {
  status.user = { ...status.user!, mustChangePassword: true };
  render(<TeamSettings />);
  const modal = screen.getByRole('dialog', { name: '修改初始密码' });
  fireEvent.click(within(modal).getByRole('button', { name: '取消' }));
  expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.getByText('请先修改初始密码。')).toBeTruthy();
  expect((screen.getByRole('button', { name: '账号管理' }) as HTMLButtonElement).disabled).toBe(true);
  expect((screen.getByRole('button', { name: '开启同步' }) as HTMLButtonElement).disabled).toBe(true);
});

it('改密核对两次密码，失败保留输入，关闭清空并恢复触发焦点', async () => {
  render(<TeamSettings />);
  const trigger = screen.getByRole('button', { name: '修改密码' }); trigger.focus(); fireEvent.click(trigger);
  fireEvent.change(screen.getByLabelText('当前密码'), { target: { value: 'old-password' } });
  fireEvent.change(screen.getByLabelText('新密码'), { target: { value: 'new-password' } });
  fireEvent.change(screen.getByLabelText('确认新密码'), { target: { value: 'another-password' } });
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
  await screen.findByText('Error: 两次新密码不一致。');
  expect(vi.mocked(call).mock.calls.some(([cmd]) => cmd === 'team_change_password')).toBe(false);
  expect((screen.getByLabelText('新密码') as HTMLInputElement).value).toBe('new-password');
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '取消' }));
  expect(document.activeElement).toBe(trigger);
  fireEvent.click(trigger); expect((screen.getByLabelText('新密码') as HTMLInputElement).value).toBe('');
});

it('两个管理入口使用独立路由，用户看不到账号管理，离线无法提交管理', async () => {
  const view = render(<TeamSettings />);
  fireEvent.click(screen.getByRole('button', { name: '管理话术' })); await waitFor(() => expect(openWindow).toHaveBeenCalledWith('team-management'));
  await waitFor(() => expect((screen.getByRole('button', { name: '账号管理' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: '账号管理' })); await waitFor(() => expect(openWindow).toHaveBeenCalledWith('user-management'));
  status = { ...status, offline: true, user: { ...status.user!, admin: false } }; view.rerender(<TeamSettings />);
  expect(screen.queryByRole('button', { name: '账号管理' })).toBeNull();
  expect((screen.getByRole('button', { name: '管理话术' }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByRole('button', { name: '共享数据备份' })).toBeNull();
});

it('改密期间退出账号，旧请求完成不能恢复弹窗或成功提示', async () => {
  let complete!: () => void;
  vi.mocked(call).mockImplementation(async command => command === 'team_change_password' ? new Promise<void>(resolve => { complete = resolve; }) : null);
  const view = render(<TeamSettings />);
  fireEvent.click(screen.getByRole('button', { name: '修改密码' }));
  for (const [label, value] of [['当前密码', 'old-password'], ['新密码', 'new-password'], ['确认新密码', 'new-password']]) fireEvent.change(screen.getByLabelText(label), { target: { value } });
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!); await waitFor(() => expect(complete).toBeTypeOf('function'));
  status = { ...status, user: null, sessionEpoch: 2 }; view.rerender(<TeamSettings />);
  await act(async () => complete());
  expect(screen.queryByRole('dialog')).toBeNull(); expect(screen.queryByText('密码已修改')).toBeNull(); expect(screen.getByRole('button', { name: '登录' })).toBeTruthy();
});

it('大量共享话术仅显示数量和管理入口，不在账号页逐库展开', async () => {
  status = { ...status, teams: Array.from({ length: 100 }, (_, i) => ({ teamId: `T${i}`, name: `团队${i}` })) as TeamStatus['teams'] };
  render(<TeamSettings />);
  expect(screen.getByText('可管理：100')).toBeTruthy();
  expect(screen.getByRole('combobox', { name: '默认共享话术' }).querySelectorAll('option')).toHaveLength(status.teams.length + 1);
  fireEvent.click(screen.getByRole('button', { name: '管理话术' }));
  await waitFor(() => expect(openWindow).toHaveBeenCalledWith('team-management'));
});


it('离线也可修改连接，预填地址和用户名，取消不改连接且销毁密码', async () => {
  status = { ...status, offline: true };
  render(<TeamSettings />);
  const trigger = screen.getByRole('button', { name: '修改连接' }); trigger.focus(); fireEvent.click(trigger);
  const dialog = screen.getByRole('dialog', { name: '修改连接' });
  expect((within(dialog).getByLabelText('服务地址') as HTMLInputElement).value).toBe(status.server);
  expect((within(dialog).getByLabelText('用户名') as HTMLInputElement).value).toBe(status.username);
  fireEvent.change(within(dialog).getByLabelText('密码'), { target: { value: 'temporary-password' } });
  fireEvent.click(within(dialog).getByRole('button', { name: '取消' }));
  expect(call).not.toHaveBeenCalledWith('team_login', expect.anything());
  expect(document.activeElement).toBe(trigger);
  fireEvent.click(trigger); expect((screen.getByLabelText('密码') as HTMLInputElement).value).toBe('');
});
it('修改连接失败保留表单供重试，提交期间禁止重复，成功关闭', async () => {
  const base = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation((cmd, args) => cmd === 'team_login' ? Promise.reject('用户名或密码错误') : base(cmd, args));
  render(<TeamSettings />); fireEvent.click(screen.getByRole('button', { name: '修改连接' }));
  fireEvent.change(screen.getByLabelText('服务地址'), { target: { value: ' http://new-hub:7823 ' } });
  fireEvent.change(screen.getByLabelText('用户名'), { target: { value: ' 002 ' } });
  fireEvent.change(screen.getByLabelText('密码'), { target: { value: 'new-password' } });
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
  await screen.findByText('用户名或密码错误');
  expect(call).toHaveBeenCalledWith('team_login', { server: 'http://new-hub:7823', username: '002', password: 'new-password' });
  expect(screen.getByText(status.server)).toBeTruthy();
  let complete!: () => void;
  vi.mocked(call).mockImplementation((cmd, args) => cmd === 'team_login' ? new Promise<void>(resolve => { complete = resolve; }) : base(cmd, args));
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
  expect((screen.getByLabelText('密码').closest('fieldset') as HTMLFieldSetElement).disabled).toBe(true);
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
  expect(vi.mocked(call).mock.calls.filter(([cmd]) => cmd === 'team_login')).toHaveLength(2);
  await act(async () => complete()); expect(screen.queryByRole('dialog')).toBeNull();
});

it('正常连接隐藏重连按钮，离线才显示且只刷新当前连接', async () => {
  const view = render(<TeamSettings />);
  const brand = screen.getByText('闪云');
  const icon = brand.querySelector('img')!;
  expect(icon.getAttribute('src')).toBe('/shanyun.svg');
  expect(icon.getAttribute('alt')).toBe('');
  expect(icon.getAttribute('aria-hidden')).toBe('true');
  expect(icon.getAttribute('width')).toBe('16');
  expect(screen.queryByRole('img', { name: '闪云' })).toBeNull();
  expect(screen.getByText(status.server).title).toBe(status.server);
  expect(screen.getByText('已连接')).toBeTruthy();
  expect(screen.queryByRole('button', { name: '刷新连接' })).toBeNull();
  expect(screen.queryByRole('button', { name: '重连' })).toBeNull();
  status = { ...status, offline: true }; view.rerender(<TeamSettings />);
  expect(screen.getByText('闪云').querySelector('img')).toBe(icon);
  expect(screen.getByText(status.server).title).toBe(status.server);
  expect(screen.getByText('已断开连接')).toBeTruthy();
  expect(screen.queryByLabelText('密码')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '重连' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('team_refresh'));
  expect(call).not.toHaveBeenCalledWith('team_login', expect.anything());
  status = { ...status, offline: false }; view.rerender(<TeamSettings />);
  expect(screen.queryByRole('button', { name: '重连' })).toBeNull();
  expect(screen.getByRole('button', { name: '修改连接' })).toBeTruthy();
});

it('长服务地址保留完整悬停文本，不改变闪云品牌与连接操作', () => {
  status = { ...status, server: `https://${'long-service.'.repeat(12)}example.com:7823` };
  render(<TeamSettings />);
  expect(screen.getByText(status.server).title).toBe(status.server);
  expect(screen.getAllByText('闪云')).toHaveLength(1);
  expect(screen.getByRole('button', { name: '修改连接' })).toBeTruthy();
});

it('重连失败仍保留账号与重连入口，不弹出登录表单', async () => {
  status = { ...status, offline: true };
  const base = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation((command, args) => command === 'team_refresh' ? Promise.reject('无法连接闪云，请检查地址或网络。') : base(command, args));
  render(<TeamSettings />);
  fireEvent.click(screen.getByRole('button', { name: '重连' }));
  await screen.findByText('无法连接闪云，请检查地址或网络。');
  expect(screen.getByText('已断开连接')).toBeTruthy();
  expect(screen.getByText(status.server)).toBeTruthy();
  expect(screen.queryByRole('button', { name: '登录' })).toBeNull();
  expect(screen.queryByLabelText('密码')).toBeNull();
  expect(screen.getByRole('button', { name: '重连' }).hasAttribute('disabled')).toBe(false);
});
