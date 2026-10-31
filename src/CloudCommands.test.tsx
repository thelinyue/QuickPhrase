import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { call, useTeams } from './api';
import { CloudCenter } from './CloudCenter';
import type { CloudCommand, CloudCommandsView, TeamStatus, TeamUser } from './types';

const events = vi.hoisted(() => new Map<string, (event: { payload: unknown }) => void>());
const windows = vi.hoisted(() => ({ close: vi.fn(), onCloseRequested: vi.fn(async () => () => {}), onDragDropEvent: vi.fn(async () => () => {}) }));
vi.mock('./api', () => ({ call: vi.fn(), useTeams: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (s: string) => s }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name, handler) => { events.set(name, handler); return () => events.delete(name); }) }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => windows }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true) }));
const raw = '  curl -fsSL "https://example.test/install?a=1&b=2" | sh \\\n  --path="$HOME"\n';
const command: CloudCommand = { id: 'one', name: '安装工具', category: 'Linux', description: '先核对来源\n然后运行', body: raw, revision: 3, updatedAt: 20, maintainedBy: 'maintainer', maintainerName: '维护人甲', deletedAt: null };
const viewer: TeamUser = { id: 'viewer', username: 'worker', displayName: '', contact: '', admin: false, disabled: false, mustChangePassword: false, canCreateTeams: false, cloudCommandPermissions: { canUse: true, canManage: false, version: 1 } };
let status: TeamStatus;
function api(commandName: string, args?: Record<string, unknown>): unknown {
  if (commandName === 'get_company_files') return { files: [], uploads: [], version: 1 };
  if (commandName === 'get_company_transfers') return [];
  if (commandName === 'get_cloud_commands') return { commands: [command], categories: ['','Linux'], total: 1 } satisfies CloudCommandsView;
  if (commandName === 'get_cloud_command') return command;
  if (commandName === 'team_admin' && args?.action === 'users') return [viewer];
  return null;
}
beforeEach(() => {
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open',''); };
  status = { sessionEpoch: 1, server: 'server', username: 'worker', user: { ...viewer }, teams: [], offline: false, cloudCommands: true, companyFiles: true, companyFilePermissions: true };
  vi.mocked(useTeams).mockImplementation(() => ({ status, version: 1, loaded: true }));
  vi.mocked(call).mockImplementation(async (name,args) => api(name,args));
});
afterEach(() => { cleanup(); events.clear(); vi.clearAllMocks(); });
async function openCommands() { fireEvent.click(screen.getByRole('tab', { name: '云命令' })); await screen.findByRole('button', { name: /安装工具.*Linux/ }); await screen.findByRole('button', { name: '复制命令' }); }
function maintainer() { status = { ...status, user: { ...viewer, cloudCommandPermissions: { canUse: true, canManage: true, version: 1 } } }; }

it('默认云文件，未授权普通账号不显示命令入口，旧服务端仅向管理员提示升级', async () => {
  status = { ...status, user: { ...viewer, cloudCommandPermissions: undefined } };
  const view = render(<CloudCenter />); expect(screen.getByText('闪云中心')).toBeTruthy(); expect(screen.queryByRole('tab', { name: '云命令' })).toBeNull();
  expect(screen.getByRole('tab', { name: '云文件' }).getAttribute('aria-selected')).toBe('true');
  expect(call).not.toHaveBeenCalledWith('get_cloud_commands',expect.anything());
  status = { ...status, cloudCommands: false, user: { ...viewer, admin: true } }; view.rerender(<CloudCenter />);
  expect(screen.getByText(/升级服务端后可用；云文件不受影响/)).toBeTruthy();
});

it('使用者直接阅读原样正文，复制只传所见修订号，不显示维护操作', async () => {
  render(<CloudCenter />); await openCommands();
  const code = document.querySelector('.command-code code'); expect(code?.textContent).toBe(raw);
  expect(screen.getByText(/维护人：维护人甲/)).toBeTruthy(); expect(screen.queryByRole('button', { name: '新建命令' })).toBeNull(); expect(screen.queryByRole('button', { name: '编辑' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '复制命令' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('copy_cloud_command',{ sessionEpoch: 1,id: 'one',expectedRevision: 3 }));
  expect(await screen.findByText('命令已原样复制，尚未执行。')).toBeTruthy();
  expect(vi.mocked(call).mock.calls.some(([name]) => /deliver|send|execute/.test(name))).toBe(false);
});

it('搜索、单层分类和分页使用服务端条件，切换模块不卸载文件监听', async () => {
  vi.mocked(call).mockImplementation(async (name,args) => name === 'get_cloud_commands' ? { commands: [command], categories: ['','Linux'], total: 23 } : api(name,args));
  render(<CloudCenter />); await openCommands();
  fireEvent.change(screen.getByRole('textbox',{ name: '搜索云命令' }),{ target: { value: '工具' } });
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_cloud_commands',{ sessionEpoch: 1,query: { q: '工具',category: null,recycled: false,offset: 0,limit: 10 } }));
  fireEvent.change(screen.getByRole('combobox',{ name: '命令分类' }),{ target: { value: '=Linux' } });
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_cloud_commands',expect.objectContaining({ query: expect.objectContaining({ category: 'Linux' }) })));
  fireEvent.click(await screen.findByRole('button',{ name: '命令下一页' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_cloud_commands',expect.objectContaining({ query: expect.objectContaining({ offset: 10 }) })));
  const listener = events.get('company-transfer');
  fireEvent.click(screen.getByRole('tab',{ name: '云文件' })); fireEvent.click(screen.getByRole('tab',{ name: '云命令' })); expect(events.get('company-transfer')).toBe(listener);
  expect((screen.getByRole('textbox',{ name: '搜索云命令' }) as HTMLInputElement).value).toBe('工具');
});

it('分类星号及未分类均按字面值筛选，不与全部分类混淆', async () => {
  vi.mocked(call).mockImplementation(async (name,args) => name === 'get_cloud_commands' ? { commands: [command],categories: ['*',''],total: 1 } : api(name,args));
  render(<CloudCenter />); await openCommands();
  fireEvent.change(screen.getByRole('combobox',{ name: '命令分类' }),{ target: { value: '=*' } });
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_cloud_commands',expect.objectContaining({ query: expect.objectContaining({ category: '*' }) })));
  await screen.findByRole('button',{ name: '复制命令' });
  fireEvent.change(screen.getByRole('combobox',{ name: '命令分类' }),{ target: { value: '=' } });
  await waitFor(() => expect(call).toHaveBeenCalledWith('get_cloud_commands',expect.objectContaining({ query: expect.objectContaining({ category: '' }) })));
});

it('维护者表单冲突保留草稿及原修订，不自动重试保存', async () => {
  maintainer(); vi.mocked(call).mockImplementation(async (name,args) => { if (name === 'save_cloud_command') throw new Error('409: 内容已更新，草稿已保留'); return api(name,args); });
  render(<CloudCenter />); await openCommands(); fireEvent.click(screen.getByRole('button',{ name: '编辑' }));
  fireEvent.change(screen.getByRole('textbox',{ name: '名称' }),{ target: { value: '我的草稿' } });
  fireEvent.click(screen.getByRole('button',{ name: '保存命令' }));
  expect(await screen.findByText(/409: 内容已更新/)).toBeTruthy(); expect((screen.getByRole('textbox',{ name: '名称' }) as HTMLInputElement).value).toBe('我的草稿');
  expect(call).toHaveBeenCalledWith('save_cloud_command',{ sessionEpoch: 1,id: 'one',request: { name: '我的草稿',category: 'Linux',description: command.description,body: raw,expectedRevision: 3 } });
  expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'save_cloud_command')).toHaveLength(1);
});

it('断网立即隐藏旧目录与正文，编辑草稿仅留内存且禁止保存，刷新允许重连', async () => {
  maintainer(); const view = render(<CloudCenter />); await openCommands(); fireEvent.click(screen.getByRole('button',{ name: '编辑' }));
  fireEvent.change(screen.getByRole('textbox',{ name: '名称' }),{ target: { value: '内存草稿' } });
  status = { ...status, offline: true }; view.rerender(<CloudCenter />);
  expect(document.querySelector('.command-code code')).toBeNull(); expect(document.querySelectorAll('.command-list-row')).toHaveLength(0);
  expect((screen.getByRole('textbox',{ name: '名称' }) as HTMLInputElement).value).toBe('内存草稿'); expect(screen.getByRole('button',{ name: '保存命令' }).hasAttribute('disabled')).toBe(true);
  fireEvent.click(screen.getByRole('button',{ name: '取消' })); await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  fireEvent.click(screen.getByRole('button',{ name: '刷新' })); await waitFor(() => expect(call).toHaveBeenCalledWith('team_refresh'));
  status = { ...status, offline: false }; view.rerender(<CloudCenter />); await screen.findByRole('button',{ name: '复制命令' });
});

it('撤权和切号卸载命令状态，旧账号迟到响应不能恢复正文', async () => {
  let resolve!: (value: CloudCommand) => void;
  vi.mocked(call).mockImplementation(async (name,args) => name === 'get_cloud_command' ? new Promise<CloudCommand>(done => { resolve = done; }) : api(name,args));
  const view = render(<CloudCenter />); fireEvent.click(screen.getByRole('tab',{ name: '云命令' })); await waitFor(() => expect(resolve).toBeTruthy());
  status = { ...status,sessionEpoch: 2,user: { ...viewer,id: 'other',cloudCommandPermissions: { canUse: false,canManage: false,version: 2 } } }; view.rerender(<CloudCenter />);
  await act(async () => { resolve(command); });
  expect(screen.queryByRole('tab',{ name: '云命令' })).toBeNull(); expect(screen.queryByText(command.name)).toBeNull(); expect(document.querySelector('.command-code')).toBeNull();
});

it('在途复制遇到授权版本变化不会锁死界面，也不接受旧成功回执', async () => {
  let resolve!: () => void;
  vi.mocked(call).mockImplementation(async (name,args) => name === 'copy_cloud_command' ? new Promise<void>(done => { resolve = done; }) : api(name,args));
  const view = render(<CloudCenter />); await openCommands(); fireEvent.click(screen.getByRole('button',{ name: '复制命令' }));
  await waitFor(() => expect(resolve).toBeTruthy());
  status = { ...status,user: { ...viewer,cloudCommandPermissions: { canUse: true,canManage: false,version: 3 } } }; view.rerender(<CloudCenter />);
  await act(async () => { resolve(); });
  await waitFor(() => expect(screen.getByRole('button',{ name: '复制命令' }).hasAttribute('disabled')).toBe(false));
  expect(screen.queryByText('命令已原样复制，尚未执行。')).toBeNull();
});

it('旧筛选目录晚到不会覆盖新搜索，服务端复制冲突要求刷新而不复制新版', async () => {
  let resolve!: (value: CloudCommandsView) => void;
  vi.mocked(call).mockImplementation(async (name,args) => { if (name === 'get_cloud_commands' && (args?.query as { q: string }).q === 'old') return new Promise<CloudCommandsView>(done => { resolve = done; }); if (name === 'copy_cloud_command') throw new Error('409: 命令已更新，请刷新后核对；未复制任何内容。'); return api(name,args); });
  render(<CloudCenter />); await openCommands(); fireEvent.change(screen.getByRole('textbox',{ name: '搜索云命令' }),{ target: { value: 'old' } }); await waitFor(() => expect(resolve).toBeTruthy());
  fireEvent.change(screen.getByRole('textbox',{ name: '搜索云命令' }),{ target: { value: 'new' } }); await screen.findByRole('button',{ name: '复制命令' });
  await act(async () => { resolve({ commands: [{ ...command,name: '过期目录' }],total: 1,categories: [] }); }); expect(screen.queryByText('过期目录')).toBeNull();
  fireEvent.click(screen.getByRole('button',{ name: '复制命令' })); expect(await screen.findByText(/未复制任何内容/)).toBeTruthy(); expect(screen.queryByText('命令已原样复制，尚未执行。')).toBeNull();
});

it('维护者回收站可恢复但不能永久删除，服务管理员拥有永久删除入口', async () => {
  maintainer(); vi.mocked(call).mockImplementation(async (name,args) => name === 'get_cloud_command' ? { ...command,deletedAt: 30 } : api(name,args));
  const view = render(<CloudCenter />); fireEvent.click(screen.getByRole('tab',{ name: '云命令' }));
  fireEvent.click(screen.getByLabelText('云命令更多操作')); fireEvent.click(screen.getByRole('button',{ name: '命令回收站' }));
  await screen.findByRole('button',{ name: '复制命令' }); fireEvent.click(screen.getByLabelText('命令管理操作'));
  expect(screen.getByRole('button',{ name: '恢复命令' })).toBeTruthy(); expect(screen.queryByRole('button',{ name: '永久删除' })).toBeNull();
  status = { ...status,user: { ...status.user!,admin: true } }; view.rerender(<CloudCenter />);
  await waitFor(() => expect(screen.getByRole('button',{ name: '复制命令' }).hasAttribute('disabled')).toBe(true));
  fireEvent.click(screen.getByLabelText('命令管理操作')); expect(screen.getByRole('button',{ name: '永久删除' })).toBeTruthy();
});

it('权限管理独立授权，维护自动包含使用，取消使用同步取消维护', async () => {
  status = { ...status,user: { ...viewer,admin: true } }; render(<CloudCenter />); fireEvent.click(screen.getByRole('button',{ name: '权限管理' }));
  const dialog = await screen.findByRole('dialog'); fireEvent.click(within(dialog).getByRole('tab',{ name: '云命令' }));
  fireEvent.click(await within(dialog).findByRole('checkbox',{ name: 'worker的维护命令权限' }));
  expect((within(dialog).getByRole('checkbox',{ name: 'worker的使用命令权限' }) as HTMLInputElement).checked).toBe(true);
  fireEvent.click(within(dialog).getByRole('checkbox',{ name: 'worker的使用命令权限' }));
  expect((within(dialog).getByRole('checkbox',{ name: 'worker的维护命令权限' }) as HTMLInputElement).checked).toBe(false);
  fireEvent.click(within(dialog).getByRole('button',{ name: '保存worker权限' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('team_admin',{ action: 'update-user',id: 'viewer',sessionEpoch: 1,request: { cloudCommandPermissions: { canUse: false,canManage: false,expectedVersion: 1 } } }));
});
