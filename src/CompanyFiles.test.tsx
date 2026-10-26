import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { call, openWindow, useTeams } from './api';
import { CompanyFilesWindow, CloudFileTable } from './CompanyFiles';
import { confirm, open } from '@tauri-apps/plugin-dialog';
import type { CompanyFile, CompanyTransfer, TeamStatus, TeamUser } from './types';
// 表格交互在当前宿主验证；独立窗口消息在 CompanyPicker 测试和 WebView2 脚本验证。
function CompanyFilePicker({ limit, onConfirm, confirmLabel = '添加文件' }: { limit: number; onConfirm: (files: CompanyFile[], epoch: number) => void; close: () => void; confirmLabel?: string }) {
  const [selected, setSelected] = useState<CompanyFile[]>([]);
  return <><CloudFileTable status={status} limit={limit} selected={selected} onChange={setSelected} /><span>已选择 {selected.length}/{limit}</span><button disabled={!selected.length} onClick={() => onConfirm(selected, status.sessionEpoch!)}>{confirmLabel}</button></>;
}
const callbacks = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
type DragPayload = { type: 'enter' | 'over' | 'drop'; paths: string[]; position: { x: number; y: number } } | { type: 'leave' };
const windowEvents = vi.hoisted(() => ({ close: vi.fn(), onCloseRequested: vi.fn(async (_callback: (event: { preventDefault: () => void }) => Promise<void>) => () => {}), onDragDropEvent: vi.fn(async (_callback: (event: { payload: DragPayload }) => void) => () => {}), toggleMaximize: vi.fn() }));
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
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 1, loaded: true }));
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' || command === 'clear_company_transfers' ? [] : command === 'get_file_icon' ? '/icons/xlsx.png' : { version: 1, files: [file], uploads: [] });
});
afterEach(() => { cleanup(); callbacks.clear(); vi.clearAllMocks(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
it('无共享话术用户可查看下载云文件，不能管理文件', async () => {
  render(<CompanyFilesWindow />); await screen.findByText(file.name); expect(screen.queryByRole('button', { name: '上传新文件' })).toBeNull(); expect(screen.queryByRole('button', { name: '删除' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '下载' })); await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: 'cloud' }));
});
it('旧版闪云关闭目录能力，忽略旧会话的传输通知', async () => {
  status = { ...status, companyFiles: false }; render(<CompanyFilesWindow />); await screen.findByText('当前闪云版本不支持云文件，请升级服务端。'); expect(call).not.toHaveBeenCalled();
  await act(async () => callbacks.get('company-transfer')?.({ payload: { sessionEpoch: 0, assetId: 'old', direction: 'download', done: 1, total: 2, status: '传输中' } })); expect(screen.queryByText(/传输中/)).toBeNull();
});
it('云文件选择保持ID和会话，不将ID冒充本地路径', async () => {
  const confirm = vi.fn(), close = vi.fn(); render(<CompanyFilePicker limit={1} onConfirm={confirm} close={close} />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('checkbox', { name: `选择 ${file.name}` })); fireEvent.click(screen.getByRole('button', { name: '添加文件' })); expect(confirm).toHaveBeenCalledWith([file], 1);
});
it('账号改变时丢弃先前目录响应', async () => {
  let resolve: (value: unknown) => void = () => {}; vi.mocked(call).mockImplementationOnce(() => new Promise(r => { resolve = r; }));
  const { rerender } = render(<CompanyFilesWindow />); status = { ...status, sessionEpoch: 2, companyFiles: false }; rerender(<CompanyFilesWindow />);
  await act(async () => resolve({ version: 1, files: [file], uploads: [] })); expect(screen.queryByText(file.name)).toBeNull();
});
it('搜索清空已选文件并遵守整条话术的附件上限', async () => {
  const other: CompanyFile = { ...file, assetId: 'other', name: '说明.pdf' };
  vi.mocked(call).mockImplementation(async (_command, args) => ({ version: 1, files: args?.query === '说明' ? [other] : [file], uploads: [] }));
  const confirm = vi.fn(); render(<CompanyFilePicker limit={2} onConfirm={confirm} close={vi.fn()} />);
  await screen.findByText(file.name); fireEvent.click(screen.getByRole('checkbox', { name: `选择 ${file.name}` }));
  fireEvent.change(screen.getByRole('textbox', { name: '搜索云文件' }), { target: { value: '说明' } });
  await screen.findByText(other.name); expect(screen.getByText('已选择 0/2')).toBeTruthy(); fireEvent.click(screen.getByRole('checkbox', { name: `选择 ${other.name}` })); expect(screen.getByText('已选择 1/2')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '添加文件' })); expect(confirm).toHaveBeenCalledWith([other], 1);
});
it('账号切换清空文件选择，旧任务错误不进入新账号窗口', async () => {
  let reject: (value: unknown) => void = () => {};
  vi.mocked(call).mockImplementation(async command => command === 'download_company_file' ? new Promise((_resolve, r) => { reject = r; }) : command === 'get_company_transfers' ? [] : { version: 1, files: [file], uploads: [] });
  const { rerender } = render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '下载' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: 'cloud' }));
  status = { ...status, sessionEpoch: 2 }; rerender(<CompanyFilesWindow />);
  await act(async () => reject('旧账号下载失败')); expect(screen.queryByText('旧账号下载失败')).toBeNull();
  cleanup();
  const picker = render(<CompanyFilePicker limit={1} onConfirm={vi.fn()} close={vi.fn()} />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('checkbox', { name: `选择 ${file.name}` }));
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
  expect(screen.getByText('未缓存')).toBeTruthy(); await waitFor(() => expect(container.querySelector('.company-file-name img')?.getAttribute('src')).toBe('/icons/xlsx.png'));
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

it('独立授权显示上传和删除入口，共享话术角色不授予服务管理权限', async () => {
  status = { ...status, companyFilePermissions: true, user: { ...status.user!, companyFilePermissions: { ...permissions, canUpload: true, canRestore: true } } };
  const { rerender } = render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.getByRole('button', { name: '上传新文件' })).toBeTruthy(); expect(screen.queryByRole('button', { name: '删除' })).toBeNull(); expect(screen.queryByRole('button', { name: '云文件权限管理' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '云文件回收站' })); await screen.findByRole('button', { name: '恢复' }); expect(screen.queryByRole('button', { name: '永久删除' })).toBeNull();
  status = { ...status, user: { ...status.user!, companyFilePermissions: permissions } }; rerender(<CompanyFilesWindow />);
  await waitFor(() => expect(screen.queryByRole('button', { name: '云文件回收站' })).toBeNull()); expect(screen.queryByRole('button', { name: '恢复' })).toBeNull();
});

it('旧版闪云保持管理员固定授权，并提示升级后配置用户名授权', async () => {
  status = { ...status, user: { ...status.user!, admin: true } }; render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.getByRole('button', { name: '上传新文件' })).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: '云文件权限管理' }));
  expect(await screen.findByText('当前闪云版本使用固定管理员权限，升级后可按用户名授权。')).toBeTruthy(); expect(call).not.toHaveBeenCalledWith('team_admin', expect.anything());
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
  permissionApi(() => [{ ...employee, companyFilePermissions: saved ? { ...permissions, canRestore: true, version: 1, loaded: true } : permissions }], async () => { saved = true; throw new Error('409:权限已变更'); });
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

it('永久删除无需输入名称，回收站显示上传者和删除者', async () => {
  administrator(); vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files: [{ ...file, uploaderUsername: '1001', deleterUsername: '1003', deletedAt: 2 }], uploads: [] });
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '云文件回收站' })); await screen.findByRole('button', { name: '永久删除' }); expect(screen.getByText('1001')).toBeTruthy(); expect(screen.getByText('1003')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '永久删除' })); const dialog = screen.getByRole('dialog', { name: '永久删除云文件' }); expect(within(dialog).queryByRole('textbox')).toBeNull(); fireEvent.submit(dialog.querySelector('form')!);
  await waitFor(() => expect(call).toHaveBeenCalledWith('manage_company_file', { sessionEpoch: 1, id: file.assetId, operation: 'purge', confirmName: file.name }));
});

const waiting: CompanyTransfer = { sessionEpoch: 1, assetId: 'batch-1', name: '第一份.pdf', direction: 'upload', done: 0, total: 0, status: '等待上传', revision: 1 };
function uploadApi(records: CompanyTransfer[], enqueue: () => Promise<CompanyTransfer[]> = async () => records) {
  vi.mocked(call).mockImplementation(async command => command === 'enqueue_company_uploads' ? enqueue() : command === 'get_company_transfers' ? records : command === 'get_file_icon' ? '' : { version: 1, files: [file], uploads: [] });
}
async function drag(payload: DragPayload) { await act(async () => windowEvents.onDragDropEvent.mock.calls.at(-1)?.[0]({ payload })); }
function uploadBounds() {
  vi.spyOn(document.querySelector('.company-list')!, 'getBoundingClientRect').mockReturnValue({ x: 20, y: 80, left: 20, top: 80, right: 520, bottom: 380, width: 500, height: 300, toJSON: () => ({}) });
}

it('多选只提交一次批量入队，较新失败事件不被初始等待响应覆盖', async () => {
  administrator(); let resolve!: (records: CompanyTransfer[]) => void;
  uploadApi([], () => new Promise(done => { resolve = done; })); vi.mocked(open).mockResolvedValueOnce(['C:\\a.pdf', 'C:\\b.pdf']);
  render(<CompanyFilesWindow />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('button', { name: '上传新文件' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('enqueue_company_uploads', { sessionEpoch: 1, paths: ['C:\\a.pdf', 'C:\\b.pdf'] }));
  expect(open).toHaveBeenCalledWith(expect.objectContaining({ multiple: true, directory: false }));
  const failed = { ...waiting, status: '准备失败', revision: 3, message: '单文件需为1字节至128MB。' };
  await act(async () => callbacks.get('company-transfer')?.({ payload: failed }));
  await act(async () => resolve([waiting, { ...waiting, assetId: 'batch-2', name: '第二份.pdf' }]));
  expect(await screen.findByText(failed.message)).toBeTruthy(); expect(screen.getByText('准备失败')).toBeTruthy(); expect(screen.getByRole('button', { name: '停止' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: '继续原任务' })).toBeNull();
  expect(vi.mocked(call).mock.calls.some(([name]) => name === 'prepare_company_upload' || name === 'upload_company_file')).toBe(false);
});

it('原生拖拽按物理坐标识别正文区，拖出清除反馈，文件页和上传页直接入队', async () => {
  administrator(); uploadApi([waiting]); vi.stubGlobal('devicePixelRatio', 2);
  render(<CompanyFilesWindow />); await screen.findByText(file.name); uploadBounds();
  const event = { type: 'enter' as const, paths: ['C:\\拖拽.pdf'], position: { x: 100, y: 200 } };
  await drag(event); expect(screen.getByText('松开即可上传文件')).toBeTruthy();
  await drag({ ...event, type: 'over', position: { x: 100, y: 100 } }); expect(screen.queryByText('松开即可上传文件')).toBeNull();
  await drag(event); await drag({ type: 'leave' }); expect(screen.queryByText('松开即可上传文件')).toBeNull();
  await drag(event); await drag({ ...event, type: 'drop' });
  await screen.findByText(waiting.name); expect(call).toHaveBeenCalledWith('enqueue_company_uploads', { sessionEpoch: 1, paths: event.paths });
  uploadBounds(); await drag(event); await drag({ ...event, type: 'drop' });
  expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'enqueue_company_uploads')).toHaveLength(2);
});

it('拖动期间切号、撤权或离线不提交文件，管理弹窗和下载页也拒绝拖入', async () => {
  administrator(); uploadApi([waiting]); const rendered = render(<CompanyFilesWindow />); await screen.findByText(file.name); uploadBounds();
  const event = { type: 'enter' as const, paths: ['C:\\拖拽.pdf'], position: { x: 100, y: 200 } };
  await drag(event); status = { ...status, sessionEpoch: 2 }; rendered.rerender(<CompanyFilesWindow />); await drag({ ...event, type: 'drop' });
  await drag(event); status = { ...status, offline: true }; rendered.rerender(<CompanyFilesWindow />); await drag({ ...event, type: 'drop' });
  status = { ...status, offline: false, user: { ...status.user!, admin: false } }; rendered.rerender(<CompanyFilesWindow />); await drag(event); await drag({ ...event, type: 'drop' });
  administrator(); rendered.rerender(<CompanyFilesWindow />); await screen.findByText(file.name); uploadBounds();
  fireEvent.click(screen.getByRole('button', { name: '删除' })); await drag(event); await drag({ ...event, type: 'drop' });
  expect(call).not.toHaveBeenCalledWith('enqueue_company_uploads', expect.anything());
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '取消' }));
  fireEvent.click(screen.getByRole('button', { name: '下载管理' })); await screen.findByRole('button', { name: '返回' });
  await drag(event); await drag({ ...event, type: 'drop' }); expect(call).not.toHaveBeenCalledWith('enqueue_company_uploads', expect.anything());
});

it('等待及准备中的上传可停止，关闭窗口不取消后台任务，重开从快照恢复', async () => {
  administrator(); const preparing = { ...waiting, assetId: 'batch-2', name: '准备副本.pdf', status: '准备中', revision: 2 }; uploadApi([waiting, preparing]);
  render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '上传管理' })); await screen.findByText(preparing.name);
  fireEvent.click(screen.getAllByRole('button', { name: '停止' })[0]); await waitFor(() => expect(call).toHaveBeenCalledWith('cancel_company_transfer', { sessionEpoch: 1, id: waiting.assetId }));
  const event = { preventDefault: vi.fn() }; await act(async () => windowEvents.onCloseRequested.mock.calls.at(-1)?.[0](event)); expect(event.preventDefault).not.toHaveBeenCalled();
  cleanup(); render(<CompanyFilesWindow />); await screen.findByText(file.name); fireEvent.click(screen.getByRole('button', { name: '上传管理' })); expect(await screen.findByText(preparing.name)).toBeTruthy();
  expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'cancel_company_transfer')).toHaveLength(1); expect(call).not.toHaveBeenCalledWith('enqueue_company_uploads', expect.anything());
});

it('文件右键替代默认菜单，复用下载且普通用户不能删除', async () => {
  render(<CompanyFilesWindow />); const row = (await screen.findByText(file.name)).closest('tr')!;
  expect(fireEvent.contextMenu(row, { clientX: 100, clientY: 100 })).toBe(false);
  expect(screen.getAllByRole('menuitem').map(item => item.textContent)).toEqual(['下载', '另存为', '生成话术']);
  expect(screen.getByText('已选 1 项')).toBeTruthy();
  fireEvent.click(screen.getByRole('menuitem', { name: '下载' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('download_company_file', { sessionEpoch: 1, id: file.assetId }));
});
it('右键已勾选行保留批量范围，未勾选行切为单选，空白菜单不改变勾选', async () => {
  administrator(); const files = [file, { ...file, assetId: 'two', name: '说明.pdf' }, { ...file, assetId: 'three', name: '合同.docx' }];
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files, uploads: [] });
  const { container } = render(<CompanyFilesWindow />); await screen.findByText(file.name);
  fireEvent.click(screen.getByLabelText(`选择 ${file.name}`)); fireEvent.click(screen.getByLabelText('选择 说明.pdf'));
  fireEvent.contextMenu(screen.getByText(file.name).closest('tr')!);
  expect(screen.getAllByRole('menuitem').map(item => item.textContent)).toEqual(['批量下载', '批量删除']);
  fireEvent.contextMenu(screen.getByText('合同.docx').closest('tr')!);
  expect(screen.getByText('已选 1 项')).toBeTruthy(); expect((screen.getByLabelText('选择 合同.docx') as HTMLInputElement).checked).toBe(true);
  expect(screen.getByRole('menuitem', { name: '删除' })).toBeTruthy();
  fireEvent.contextMenu(container.querySelector('.company-list')!);
  expect(screen.getAllByRole('menuitem').map(item => item.textContent)).toEqual(['刷新', '上传新文件', '下载管理', '上传管理']);
  expect(screen.getByText('已选 1 项')).toBeTruthy();
});
it('右键删除与回收站恢复保留确认，永久删除只需明确确认', async () => {
  administrator(); render(<CompanyFilesWindow />); await screen.findByText(file.name);
  fireEvent.contextMenu(screen.getByText(file.name).closest('tr')!); fireEvent.click(screen.getByRole('menuitem', { name: '删除' }));
  expect(screen.getByText('将所选文件移入回收站？已有话术继续可用，不能再新增引用。')).toBeTruthy(); expect(call).not.toHaveBeenCalledWith('manage_company_file', expect.anything());
  fireEvent.click(screen.getByRole('button', { name: '取消' })); fireEvent.click(screen.getByRole('button', { name: '云文件回收站' }));
  await screen.findByRole('button', { name: '恢复' }); fireEvent.contextMenu(screen.getByText(file.name).closest('tr')!);
  expect(screen.getAllByRole('menuitem').map(item => item.textContent)).toEqual(['恢复', '永久删除']);
  fireEvent.click(screen.getByRole('menuitem', { name: '永久删除' })); expect(screen.queryByLabelText('输入完整文件名')).toBeNull(); expect(screen.getByRole('dialog', { name: '永久删除云文件' })).toBeTruthy();
});
it('菜单支持键盘选择和Esc恢复焦点，失焦及权限变化关闭', async () => {
  administrator(); const { rerender } = render(<CompanyFilesWindow />); const row = (await screen.findByText(file.name)).closest('tr')!;
  fireEvent.contextMenu(row); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: '下载' }));
  fireEvent.keyDown(screen.getByRole('menu'), { key: 'End' }); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: '删除' }));
  fireEvent.keyDown(screen.getByRole('menu'), { key: 'Home' }); fireEvent.keyDown(screen.getByRole('menu'), { key: 'ArrowDown' }); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: '另存为' }));
  fireEvent.keyDown(document, { key: 'Escape' }); expect(screen.queryByRole('menu')).toBeNull(); expect(document.activeElement).toBe(row);
  fireEvent.contextMenu(row); fireEvent.blur(window); expect(screen.queryByRole('menu')).toBeNull();
  fireEvent.contextMenu(row); status = { ...status, offline: true }; rerender(<CompanyFilesWindow />); expect(screen.queryByRole('menu')).toBeNull();
});
it('输入框保留编辑菜单，传输页空白不弹业务或默认菜单', async () => {
  const { container } = render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(fireEvent.contextMenu(screen.getByRole('textbox', { name: '搜索云文件' }))).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: '下载管理' })); await screen.findByText('暂无下载记录');
  expect(fireEvent.contextMenu(container.querySelector('.company-list')!)).toBe(false); expect(screen.queryByRole('menu')).toBeNull();
});
it('选择器新增入口使用下一步并只允许选择一个文件', async () => {
  const second = { ...file, assetId: 'second', name: '说明.pdf' }; vi.mocked(call).mockResolvedValue({ version: 1, files: [file, second], uploads: [] });
  const next = vi.fn(); render(<CompanyFilePicker limit={1} confirmLabel="下一步" onConfirm={next} close={vi.fn()} />); await screen.findByText(file.name);
  fireEvent.click(screen.getByRole('checkbox', { name: `选择 ${file.name}` })); expect((screen.getByRole('checkbox', { name: `选择 ${second.name}` }) as HTMLInputElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: '下一步' })); expect(next).toHaveBeenCalledWith([file], 1); expect(openWindow).not.toHaveBeenCalled();
});

it('管理员清空回收站覆盖全部分页和搜索，其他失败保留并报告', async () => {
  administrator();
  const all = Array.from({ length: 12 }, (_, i) => ({ ...file, assetId: `R${i}`, name: `回收${i}.txt`, deletedAt: 2 }));
  vi.mocked(call).mockImplementation(async (command, args) => {
    if (command === 'get_company_transfers') return [];
    if (command === 'get_file_icon') return '';
    if (command === 'manage_company_file') { if (args?.id === 'R1') throw new Error('网络中断'); return null; }
    return { version: 1, files: args?.query ? [all[0]] : all, uploads: [] };
  });
  render(<CompanyFilesWindow />); await screen.findByText('回收0.txt');
  fireEvent.click(screen.getByRole('button', { name: '云文件回收站' }));
  await screen.findByRole('button', { name: '清空回收站' });
  fireEvent.change(screen.getByRole('textbox', { name: '搜索云文件' }), { target: { value: '回收0' } });
  fireEvent.click(screen.getByRole('button', { name: '清空回收站' }));
  const dialog = await screen.findByRole('dialog', { name: '清空云文件回收站' });
  expect(dialog.textContent).toContain('12 个文件'); expect(within(dialog).queryByRole('textbox')).toBeNull();
  expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'manage_company_file')).toHaveLength(0);
  fireEvent.submit(dialog.querySelector('form')!);
  await waitFor(() => expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'manage_company_file')).toHaveLength(12));
  expect((await screen.findByRole('status')).textContent).toContain('失败 1 个');
  expect(screen.getByRole('status').textContent).toContain('网络中断');
  fireEvent.change(screen.getByRole('textbox', { name: '搜索云文件' }), { target: { value: '' } });
  const failedRow = (await screen.findByText('回收1.txt')).closest('tr')!;
  fireEvent.click(within(failedRow).getByRole('button', { name: '永久删除' }));
  expect(screen.queryByRole('status')).toBeNull();
  const retry = screen.getByRole('dialog', { name: '永久删除云文件' });
  expect(retry.textContent).toContain('未缓存或清除缓存的设备将无法再下载');
  fireEvent.submit(retry.querySelector('form')!);
  expect((await within(retry).findByRole('alert')).textContent).toContain('网络中断');
  expect(screen.queryByRole('status')).toBeNull();
});
it('普通成员没有清空回收站入口', async () => {
  status = { ...status, user: { ...status.user!, companyFilePermissions: { version: 1, uploadVersion: 1, canUpload: false, canRecycle: true, canRestore: true } } };
  render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.queryByRole('button', { name: '清空回收站' })).toBeNull();
});

it('离线打开仍加载最近目录，区分离线可用与未缓存并禁止云端操作', async () => {
  administrator(); status = { ...status, offline: true };
  const cached = { ...file, cached: true }, remote = { ...file, assetId: 'remote', name: '未缓存.pdf' };
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files: [cached, remote], uploads: [], offline: true });
  render(<CompanyFilesWindow />);
  await screen.findByText(file.name);
  expect(screen.getByText('连接已断开，显示最近目录；已缓存文件仍可使用。')).toBeTruthy();
  expect(screen.getByText('离线可用')).toBeTruthy();
  expect(screen.getByText('未缓存')).toBeTruthy();
  expect(call).toHaveBeenCalledWith('get_company_files', { sessionEpoch: 1, recycled: false, query: '' });
  expect(screen.getByRole('button', { name: '上传新文件' }).hasAttribute('disabled')).toBe(true);
  expect(screen.getAllByRole('button', { name: '下载' }).every(button => button.hasAttribute('disabled'))).toBe(true);
  expect(screen.getAllByRole('button', { name: '删除' }).every(button => button.hasAttribute('disabled'))).toBe(true);
});

it('目录请求异常返回离线快照时，账号尚未标记离线也显示提示并禁用写入', async () => {
  administrator();
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [] : command === 'get_file_icon' ? '' : { version: 1, files: [{ ...file, cached: true }], uploads: [], offline: true });
  render(<CompanyFilesWindow />); await screen.findByText(file.name);
  expect(screen.getByText('离线可用')).toBeTruthy();
  await waitFor(() => expect(screen.getByRole('button', { name: '下载' }).hasAttribute('disabled')).toBe(true));
  const row = screen.getByText(file.name).closest('tr')!;
  fireEvent.contextMenu(row);
  expect((await screen.findByRole('menuitem', { name: '生成话术' })).hasAttribute('disabled')).toBe(true);
  expect(screen.queryByRole('menuitem', { name: '重命名云文件' })).toBeNull();
});

it('目录异常后切到上传管理仍禁用云端操作，成功刷新后恢复', async () => {
  administrator(); let offline = true;
  vi.mocked(call).mockImplementation(async command => command === 'get_company_transfers' ? [{ sessionEpoch: 1, assetId: 'pending', name: '待核对.pdf', direction: 'upload', done: 0, total: 4, status: '待核对', revision: 1, message: '' }] : command === 'get_file_icon' ? '' : { version: 1, files: [{ ...file, cached: true }], uploads: [], offline });
  render(<CompanyFilesWindow />); await screen.findByText('离线可用');
  fireEvent.click(screen.getByRole('button', { name: '上传管理' }));
  await screen.findByText('待核对.pdf');
  expect(screen.getByRole('button', { name: '继续原任务' }).hasAttribute('disabled')).toBe(true);
  expect(screen.getByRole('button', { name: '查询结果' }).hasAttribute('disabled')).toBe(true);
  offline = false;
  fireEvent.click(screen.getByRole('button', { name: '返回' }));
  await screen.findByText('已下载');
  expect(screen.getByRole('button', { name: '下载' }).hasAttribute('disabled')).toBe(false);
});

it('离线选择器展示目录但不能新增关联，切入离线时清空旧选择', async () => {
  const picker = render(<CompanyFilePicker limit={1} onConfirm={vi.fn()} close={vi.fn()} />);
  await screen.findByText(file.name);
  fireEvent.click(screen.getByLabelText(`选择 ${file.name}`));
  expect(screen.getByRole('button', { name: '添加文件' }).hasAttribute('disabled')).toBe(false);
  status = { ...status, offline: true }; picker.rerender(<CompanyFilePicker limit={1} onConfirm={vi.fn()} close={vi.fn()} />);
  await screen.findByText(file.name);
  expect(screen.getByLabelText(`选择 ${file.name}`).hasAttribute('disabled')).toBe(true);
  expect(screen.getByRole('button', { name: '添加文件' }).hasAttribute('disabled')).toBe(true);
});
