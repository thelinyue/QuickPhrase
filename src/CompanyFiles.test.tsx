import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { call, useTeams } from './api';
import { CompanyFilesWindow, CompanyFilePicker } from './CompanyFiles';
import { confirm } from '@tauri-apps/plugin-dialog';
import type { CompanyFile, CompanyTransfer, TeamStatus, TeamUser } from './types';
const callbacks = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
const windowEvents = vi.hoisted(() => ({ close: vi.fn(), onCloseRequested: vi.fn(async (_callback: (event: { preventDefault: () => void }) => Promise<void>) => () => {}), toggleMaximize: vi.fn() }));
vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn(), openWindow: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name, callback) => { callbacks.set(name, callback); return () => callbacks.delete(name); }) }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ ...windowEvents, startDragging: vi.fn(), minimize: vi.fn() }), convertFileSrc: (path: string) => path }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(), save: vi.fn(), confirm: vi.fn(async () => true) }));
const file: CompanyFile = { assetId: 'cloud', name: '价格表.xlsx', size: 12, uploadedBy: 'admin', uploaderName: '管理员', createdAt: 1, deletedAt: null, cached: false };
let status: TeamStatus;
beforeEach(() => {
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
  status = { sessionEpoch: 1, server: 'server', username: 'employee', user: { id: 'employee', username: 'employee', admin: false, disabled: false, mustChangePassword: false, displayName: '', contact: '', canCreateTeams: false }, teams: [], offline: false, companyFiles: true };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 1 }));
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' || command === 'clear_company_transfers' ? [] : command === 'get_file_icon' ? '/icons/xlsx.png' : { version: 1, files: [file], uploads: [] });
});
afterEach(() => { cleanup(); callbacks.clear(); vi.clearAllMocks(); });
it('无共享库用户可查看下载云文件，不能管理文件', async () => {
  render(<CompanyFilesWindow />); await screen.findByText(file.name); expect(screen.queryByRole('button', { name: '上传新文件' })).toBeNull(); expect(screen.queryByRole('button', { name: '删除' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '下载' })); await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: 'cloud' }));
});
it('旧Hub关闭目录能力，忽略旧会话的传输通知', async () => {
  status = { ...status, companyFiles: false }; render(<CompanyFilesWindow />); await screen.findByText('当前 Hub 尚不支持云文件，请升级服务端。'); expect(call).not.toHaveBeenCalled();
  await act(async () => callbacks.get('company-transfer')?.({ payload: { sessionEpoch: 0, assetId: 'old', direction: 'download', done: 1, total: 2, status: '传输中' } })); expect(screen.queryByText(/传输中/)).toBeNull();
});
it('云文件选择保持ID和会话，不将ID冒充本地路径', async () => {
  const confirm = vi.fn(), close = vi.fn(); render(<CompanyFilePicker limit={1} onConfirm={confirm} close={close} />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: '添加文件' })); expect(confirm).toHaveBeenCalledWith([file], 1);
});
it('账号改变时丢弃先前目录响应', async () => {
  let resolve: (value: unknown) => void = () => {}; vi.mocked(call).mockImplementationOnce(() => new Promise(r => { resolve = r; }));
  const { rerender } = render(<CompanyFilesWindow />); status = { ...status, sessionEpoch: 2, companyFiles: false }; rerender(<CompanyFilesWindow />);
  await act(async () => resolve({ version: 1, files: [file], uploads: [] })); expect(screen.queryByText(file.name)).toBeNull();
});
it('跨搜索保留已选文件并遵守整条话术的附件上限', async () => {
  const other: CompanyFile = { ...file, assetId: 'other', name: '说明.pdf' };
  vi.mocked(call).mockImplementation(async (_command, args) => ({ version: 1, files: args?.query === '说明' ? [other] : [file], uploads: [] }));
  const confirm = vi.fn(); render(<CompanyFilePicker limit={2} onConfirm={confirm} close={vi.fn()} />);
  await screen.findByText(file.name); fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.change(screen.getByRole('textbox', { name: '搜索云文件' }), { target: { value: '说明' } });
  await screen.findByText(other.name); fireEvent.click(screen.getByRole('checkbox')); expect(screen.getByText('已选择 2/2')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '添加文件' })); expect(confirm).toHaveBeenCalledWith([file, other], 1);
});
it('账号切换清空文件选择，旧任务错误不进入新账号窗口', async () => {
  let reject: (value: unknown) => void = () => {};
  vi.mocked(call).mockImplementation(async command => command === 'download_company_file' ? new Promise((_resolve, r) => { reject = r; }) : command === 'get_company_transfers' ? [] : { version: 1, files: [file], uploads: [] });
  const { rerender } = render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '下载' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: 'cloud' }));
  status = { ...status, sessionEpoch: 2 }; rerender(<CompanyFilesWindow />);
  await act(async () => reject('旧账号下载失败')); expect(screen.queryByText('旧账号下载失败')).toBeNull();
  cleanup();
  const picker = render(<CompanyFilePicker limit={1} onConfirm={vi.fn()} close={vi.fn()} />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('checkbox'));
  status = { ...status, sessionEpoch: 3 }; picker.rerender(<CompanyFilePicker limit={1} onConfirm={vi.fn()} close={vi.fn()} />);
  expect(screen.getByRole('button', { name: '添加文件' }).hasAttribute('disabled')).toBe(true);
});

const permissions = { canUpload: false, canRecycle: false, canRestore: false, version: 0, uploadVersion: 0 };
const employee: TeamUser = { id: 'worker', username: '1002', displayName: '员工', admin: false, disabled: false, mustChangePassword: false, contact: '', canCreateTeams: false, companyFilePermissions: permissions };
function administrator() { status = { ...status, companyFilePermissions: true, user: { ...status.user!, admin: true, companyFilePermissions: permissions } }; }
function permissionApi(records: () => TeamUser[], save: () => Promise<unknown> = async () => null) {
  vi.mocked(call).mockImplementation(async (command, args) => command === 'team_admin' ? args?.action === 'users' ? records() : save() : command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '/icons/xlsx.png' : { version: 1, files: [file], uploads: [] });
}

it('顶部按钮与标题栏按桌面参考排列，未下载文件显示系统图标', async () => {
  administrator(); const { container } = render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(Array.from(container.querySelectorAll('.company-toolbar button')).map(b => b.textContent)).toEqual(['刷新', '下载管理', '上传管理', '上传新文件']);
  expect(screen.getByRole('button', { name: '最小化窗口' })).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: '最大化或还原窗口' })); expect(windowEvents.toggleMaximize).toHaveBeenCalled();
  expect(screen.getByText('未下载')).toBeTruthy(); await waitFor(() => expect(container.querySelector('.company-file-name img')?.getAttribute('src')).toBe('/icons/xlsx.png'));
  expect(Array.from(container.querySelectorAll('tbody button')).every(button => button.textContent === '' && button.getAttribute('title') && button.getAttribute('aria-label'))).toBe(true);
  expect(call).toHaveBeenCalledWith('get_file_icon', { path: file.name });
});

it('分页全选只选当前页，翻页与搜索清空勾选，支持页码跳转', async () => {
  const files = Array.from({ length: 25 }, (_, i) => ({ ...file, assetId: `file-${i}`, name: `资料${i}.pdf`, createdAt: i }));
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files, uploads: [] });
  render(<CompanyFilesWindow />); await screen.findByText('资料24.pdf'); expect(screen.getAllByRole('checkbox').length).toBe(11);
  fireEvent.click(screen.getByRole('checkbox', { name: '选择当前页全部文件' })); expect(screen.getAllByRole('checkbox').every(c => (c as HTMLInputElement).checked)).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: '下一页' })); expect(screen.getAllByRole('checkbox').every(c => !(c as HTMLInputElement).checked)).toBe(true); expect(screen.getByText('资料14.pdf')).toBeTruthy();
  fireEvent.change(screen.getByRole('textbox', { name: '跳转页码' }), { target: { value: '3' } }); fireEvent.click(screen.getByRole('button', { name: '跳转' })); expect(screen.getAllByRole('checkbox').length).toBe(6);
  fireEvent.click(screen.getByRole('checkbox', { name: '选择当前页全部文件' })); fireEvent.change(screen.getByRole('textbox', { name: '搜索云文件' }), { target: { value: '资料' } });
  await waitFor(() => expect(screen.getAllByRole('checkbox').every(c => !(c as HTMLInputElement).checked)).toBe(true));
  fireEvent.change(screen.getByRole('combobox', { name: '每页条数' }), { target: { value: '20' } }); await waitFor(() => expect(screen.getAllByRole('checkbox').length).toBe(21));
});

it('独立授权显示上传和删除入口，共享库角色不授予服务管理权限', async () => {
  status = { ...status, companyFilePermissions: true, user: { ...status.user!, companyFilePermissions: { ...permissions, canUpload: true, canRestore: true } } };
  const { rerender } = render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.getByRole('button', { name: '上传新文件' })).toBeTruthy(); expect(screen.queryByRole('button', { name: '删除' })).toBeNull(); expect(screen.queryByRole('button', { name: '云文件权限管理' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '云文件回收站' })); await screen.findByRole('button', { name: '恢复' }); expect(screen.queryByRole('button', { name: '永久删除' })).toBeNull();
  status = { ...status, user: { ...status.user!, companyFilePermissions: permissions } }; rerender(<CompanyFilesWindow />);
  await waitFor(() => expect(screen.queryByRole('button', { name: '云文件回收站' })).toBeNull()); expect(screen.queryByRole('button', { name: '恢复' })).toBeNull();
});

it('旧Hub保持管理员固定授权，并提示升级后配置用户名授权', async () => {
  status = { ...status, user: { ...status.user!, admin: true } }; render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.getByRole('button', { name: '上传新文件' })).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: '云文件权限管理' }));
  expect(await screen.findByText('当前 Hub 使用固定管理员权限，升级后可按用户名授权。')).toBeTruthy(); expect(call).not.toHaveBeenCalledWith('team_admin', expect.anything());
});

it('授权逐行保存只提交三项权限和预期版本，离开草稿页需确认', async () => {
  administrator(); permissionApi(() => [status.user!, employee]); render(<CompanyFilesWindow />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('button', { name: '云文件权限管理' })); fireEvent.click(await screen.findByRole('checkbox', { name: '1002的上传权限' }));
  vi.mocked(confirm).mockResolvedValueOnce(false); fireEvent.click(screen.getByRole('button', { name: '云文件管理' })); await waitFor(() => expect(confirm).toHaveBeenCalled()); expect(screen.getByRole('checkbox', { name: '1002的上传权限' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '保存' })); await waitFor(() => expect(call).toHaveBeenCalledWith('team_admin', { action: 'update-user', id: employee.id, sessionEpoch: 1, request: { companyFilePermissions: { canUpload: true, canRecycle: false, canRestore: false, expectedVersion: 0 } } }));
  expect(await screen.findByText('1002 的云文件权限已保存。')).toBeTruthy();
});

it('保存冲突读取最新授权，保留原草稿并提供采用最新', async () => {
  administrator(); let saved = false;
  permissionApi(() => [{ ...employee, companyFilePermissions: saved ? { ...permissions, canRestore: true, version: 1 } : permissions }], async () => { saved = true; throw new Error('409:权限已变更'); });
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '云文件权限管理' })); fireEvent.click(await screen.findByRole('checkbox', { name: '1002的上传权限' })); fireEvent.click(screen.getByRole('button', { name: '保存' }));
  expect(await screen.findByText('权限已变更，草稿已保留')).toBeTruthy(); expect((screen.getByRole('checkbox', { name: '1002的上传权限' }) as HTMLInputElement).checked).toBe(true);
  expect(screen.getByRole('button', { name: '保存' }).hasAttribute('disabled')).toBe(true); fireEvent.click(screen.getByRole('button', { name: '采用最新' })); expect((screen.getByRole('checkbox', { name: '1002的恢复权限' }) as HTMLInputElement).checked).toBe(true);
});

it('窗口关闭先确认未保存授权，取消关闭保留草稿', async () => {
  administrator(); permissionApi(() => [employee]); render(<CompanyFilesWindow />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('button', { name: '云文件权限管理' })); fireEvent.click(await screen.findByRole('checkbox', { name: '1002的上传权限' }));
  const event = { preventDefault: vi.fn() }, handler = windowEvents.onCloseRequested.mock.calls[0][0]; vi.mocked(confirm).mockResolvedValueOnce(false);
  await act(async () => handler(event)); expect(event.preventDefault).toHaveBeenCalled(); expect(windowEvents.close).not.toHaveBeenCalled(); expect((screen.getByRole('checkbox', { name: '1002的上传权限' }) as HTMLInputElement).checked).toBe(true);
  vi.mocked(confirm).mockResolvedValueOnce(true); await act(async () => handler(event)); expect(windowEvents.close).toHaveBeenCalled();
});

it('传输快照恢复任务，较旧快照不覆盖事件，清空保留运行和待核对上传', async () => {
  administrator(); const running: CompanyTransfer = { sessionEpoch: 1, assetId: 'running', name: '在途.pdf', direction: 'upload', done: 2, total: 10, status: '传输中', revision: 1 };
  const unresolved = { ...running, assetId: 'pending', name: '待核对.xlsx', status: '待核对' }, finished = { ...running, assetId: 'done', name: '完成.docx', status: '已完成' };
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [running, unresolved, finished] : command === 'clear_company_transfers' ? [running, unresolved] : command === 'get_file_icon' ? '' : { version: 1, files: [file], uploads: [] });
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '上传管理' })); await screen.findByText(finished.name);
  await act(async () => callbacks.get('company-transfer')?.({ payload: { ...running, done: 8, revision: 2 } })); expect(screen.getByText('80%')).toBeTruthy();
  await act(async () => callbacks.get('company-transfer')?.({ payload: running })); expect(screen.getByText('80%')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '清空' })); await waitFor(() => expect(screen.queryByText(finished.name)).toBeNull()); expect(screen.getByText(unresolved.name)).toBeTruthy(); expect(screen.getByText(running.name)).toBeTruthy();
  fireEvent.click(screen.getAllByRole('button', { name: '查询结果' })[0]); await waitFor(() => expect(call).toHaveBeenCalledWith('query_company_upload', { sessionEpoch: 1, id: 'pending' }));
});

it('批量下载在后台运行，仍能进入下载管理停止任务', async () => {
  let finish: () => void = () => {};
  vi.mocked(call).mockImplementation(async command => command === 'download_company_file' ? new Promise<void>(r => { finish = r; }) : command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files: [file], uploads: [] });
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('checkbox', { name: '选择当前页全部文件' })); fireEvent.click(screen.getByRole('button', { name: '批量操作' })); fireEvent.click(screen.getByRole('menuitem', { name: '批量下载' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: file.assetId })); fireEvent.click(screen.getByRole('button', { name: '下载管理' })); expect(await screen.findByRole('button', { name: '返回' })).toBeTruthy();
  await act(async () => finish());
});

it('永久删除逐文件输入完整名称，回收站显示上传者和删除者', async () => {
  administrator(); vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files: [{ ...file, uploaderUsername: '1001', deleterUsername: '1003', deletedAt: 2 }], uploads: [] });
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '云文件回收站' })); await screen.findByRole('button', { name: '永久删除' }); expect(screen.getByText('1001')).toBeTruthy(); expect(screen.getByText('1003')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '永久删除' })); const dialog = screen.getByRole('dialog', { name: '永久删除云文件' }); fireEvent.change(within(dialog).getByRole('textbox', { name: '输入完整文件名' }), { target: { value: file.name } }); fireEvent.submit(dialog.querySelector('form')!);
  await waitFor(() => expect(call).toHaveBeenCalledWith('manage_company_file', { sessionEpoch: 1, id: file.assetId, operation: 'purge', confirmName: file.name }));
});
