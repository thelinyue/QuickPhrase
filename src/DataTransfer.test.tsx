import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { open, save } from '@tauri-apps/plugin-dialog';
import { DataTransfer } from './DataTransfer';
import { call, useTeams, useTeamView } from './api';
import type { Library, TeamStatus, TeamView } from './types';

vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn(), useTeamView: vi.fn(), closeWindow: vi.fn() }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(), save: vi.fn() }));
const group = { teamId: 'group', name: '售前', version: 3, lifecycleVersion: 1, editable: true, role: 'editor' as const, membershipVersion: 2 };
const category = { id: 'root', name: '欢迎', kind: 'phrase' as const, parentId: null, position: 0, background: '' };
const library: Library = { libraryId: 'library-a', categories: [category], phrases: [], commonPhrases: [], files: [], settings: {} as Library['settings'], dataDir: '' };
let status: TeamStatus, view: TeamView;
const target = { source: 'private', libraryId: library.libraryId, sessionEpoch: 1 };
const preview = { id: 'batch', kind: 'backup', filename: '话术.zip', categories: 1, phrases: 1, commonPhrases: 0, files: 0, images: 1, attachments: 1, recycled: 0, rows: [{ category: '欢迎', title: '示例', text: '正文摘要' }], notes: [], target, targetLabel: '本机个人库', merge: false, restoreSettings: false, state: 'ready' };
beforeEach(() => {
  status = { sessionEpoch: 1, server: 'http://server:7823', username: '0001', user: { id: 'user', mustChangePassword: false } as TeamStatus['user'], teams: [group], offline: false };
  view = { team: group, categories: [category], phrases: [], offline: false };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 0 }));
  vi.mocked(useTeamView).mockImplementation(() => ({ view, error: '' }));
  vi.mocked(call).mockImplementation(async command => command === 'get_import_pending' ? null : command === 'preview_import' ? preview : command === 'confirm_import' ? { state: 'completed', message: '导入完成' } : undefined);
  vi.mocked(open).mockResolvedValue('D:/话术.zip'); vi.mocked(save).mockResolvedValue('D:/backup.zip');
  HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.open = true; });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const commands = (name: string) => vi.mocked(call).mock.calls.filter(([command]) => command === name);
const enabled = async (name: string) => waitFor(() => expect((screen.getByRole('button', { name }) as HTMLButtonElement).disabled).toBe(false));
async function prepare() { fireEvent.click(screen.getByRole('button', { name: '选择文件' })); await enabled('预览导入'); fireEvent.click(screen.getByRole('button', { name: '预览导入' })); await screen.findByRole('dialog'); }

it('默认个人库和独立副本，设置恢复默认关闭，改选项使旧预览失效', async () => {
  render(<DataTransfer mode="import" library={library} />);
  expect((screen.getByLabelText('目标库') as HTMLSelectElement).value).toBe('private');
  expect((screen.getByLabelText('合并同级唯一同名分类') as HTMLInputElement).checked).toBe(false);
  expect((screen.getByLabelText('恢复备份设置') as HTMLInputElement).checked).toBe(false);
  await prepare();
  expect(call).toHaveBeenCalledWith('preview_import', expect.objectContaining({ target, merge: false, restoreSettings: false }));
  expect(commands('confirm_import')).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: '取消' }));
  fireEvent.click(screen.getByLabelText('合并同级唯一同名分类'));
  expect(screen.queryByRole('button', { name: '重新预览' })).toBeNull();
  expect(screen.queryByRole('dialog')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '预览导入' }));
  await waitFor(() => expect(commands('preview_import')).toHaveLength(2));
  expect(commands('preview_import')[1][1]).toEqual(expect.objectContaining({ merge: true, target }));
});
it('重复确认只提交一次，版本冲突保留预览并显示实际原因', async () => {
  let reject!: (reason: Error) => void;
  vi.mocked(call).mockImplementation(async command => command === 'get_import_pending' ? null : command === 'preview_import' ? preview : command === 'confirm_import' ? new Promise((_, r) => { reject = r; }) : undefined);
  render(<DataTransfer mode="import" library={library} />); await prepare();
  const form = screen.getByRole('dialog').querySelector('form')!;
  fireEvent.submit(form); fireEvent.submit(form);
  await waitFor(() => expect(commands('confirm_import')).toHaveLength(1));
  expect(commands('confirm_import')[0][1]).toEqual(expect.objectContaining({ previewId: 'batch', target }));
  await act(async () => reject(new Error('个人库已更新，请重新预览。')));
  expect((await screen.findByRole('alert')).textContent).toContain('个人库已更新');
  expect(screen.getByRole('dialog')).toBeTruthy();
});
it('待确认批次只核对，不自动重发，核对后沿用原预览编号', async () => {
  vi.mocked(call).mockImplementation(async command => command === 'get_import_pending' ? { ...preview, state: 'pending' } : command === 'check_import_result' ? { state: 'ready', message: '尚未提交，可重试。' } : undefined);
  render(<DataTransfer mode="import" library={library} />);
  await screen.findByText('结果待确认');
  expect(commands('confirm_import')).toHaveLength(0);
  expect((screen.getByRole('button', { name: '选择文件' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: '核对结果' }));
  await enabled('预览导入');
  expect(call).toHaveBeenCalledWith('check_import_result', expect.objectContaining({ previewId: 'batch', target }));
  expect(commands('confirm_import')).toHaveLength(0);
});
it('共享库入口预选当前组，离线成员能导出完整缓存但不能导入', async () => {
  status = { ...status, offline: true, teams: [{ ...group, editable: false, role: 'member' }] };
  view = { ...view, team: status.teams[0], offline: true };
  const rendered = render(<DataTransfer mode="export" library={library} initialTeamId="group" />);
  expect((screen.getByLabelText('来源库') as HTMLSelectElement).value).toBe('team');
  expect((screen.getByLabelText('选择共享库') as HTMLSelectElement).value).toBe('group');
  expect(screen.queryByText(/完整备份/)).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '导出' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('export_data', expect.objectContaining({ target: { source: 'team', teamId: 'group', version: 3, lifecycleVersion: 1, sessionEpoch: 1 }, categoryId: null })));
  rendered.unmount(); render(<DataTransfer mode="import" library={library} initialTeamId="group" />);
  expect((screen.getByRole('button', { name: '选择文件' }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByLabelText('恢复备份设置')).toBeNull();
});
it('切号后丢弃旧预览响应并取消旧任务', async () => {
  let deliver!: (value: typeof preview) => void;
  vi.mocked(call).mockImplementation(async command => command === 'get_import_pending' ? null : command === 'preview_import' ? new Promise(r => { deliver = r; }) : undefined);
  const rendered = render(<DataTransfer mode="import" library={library} />);
  fireEvent.click(screen.getByRole('button', { name: '选择文件' })); await enabled('预览导入'); fireEvent.click(screen.getByRole('button', { name: '预览导入' }));
  await waitFor(() => expect(commands('preview_import')).toHaveLength(1));
  status = { ...status, sessionEpoch: 2 };
  rendered.rerender(<DataTransfer mode="import" library={{ ...library, libraryId: 'library-b' }} />);
  await act(async () => deliver(preview));
  expect(screen.queryByRole('dialog')).toBeNull();
  expect(screen.queryByText('话术.zip')).toBeNull();
  expect(commands('cancel_data_transfer').length).toBeGreaterThan(0);
  expect(commands('confirm_import')).toHaveLength(0);
});
it('个人全库与分类范围名称明确，导出使用本机目标且不强制同步', async () => {
  render(<DataTransfer mode="export" library={{ ...library, ownerLabel: '0001' }} />);
  expect((screen.getByRole('option', { name: '我的话术 · 0001' }) as HTMLOptionElement).selected).toBe(true);
  expect(screen.getByText(/导出当前本机版本/)).toBeTruthy();
  expect(screen.getByLabelText('完整备份（含图片、附件）')).toBeTruthy();
  fireEvent.change(screen.getByLabelText('导出分类'), { target: { value: 'root' } });
  expect(screen.getByLabelText('话术包（含图片、附件）')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '导出' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('export_data', expect.objectContaining({ target, categoryId: 'root' })));
  expect(commands('backup_private_server')).toHaveLength(0);
  expect(commands('confirm_private_sync')).toHaveLength(0);
});
