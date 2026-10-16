import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { open, confirm } from '@tauri-apps/plugin-dialog';
import { call, closeWindow } from './api';
import { FileEditor } from './FileAssociation';
import { cloudNameError } from './cloudRename';
import type { Library } from './types';
vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(), useTeams: () => ({ status: { sessionEpoch: 3, user: { id: 'u' }, companyFiles: true, server: 'server', offline: false } }) }));
vi.mock('./ui', () => ({ Titlebar: ({ title }: { title: string }) => <h1>{title}</h1>, FileIcon: () => null }));
vi.mock('@tauri-apps/api/event', () => ({ listen: async () => () => {} }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(), confirm: vi.fn() }));
const library = { categories: [{ id: 'files', kind: 'file', name: '资料', parentId: null }], files: [] } as unknown as Library;
beforeEach(() => {
  vi.mocked(call).mockImplementation(async command => command === 'get_company_files' ? { files: [{ assetId: 'cloud', name: '云资料.pdf', size: 2, createdAt: 1, uploaderName: '我' }], version: 1, uploads: [] } : undefined);
  vi.mocked(open).mockResolvedValue(['C:\\资料.txt']); vi.mocked(confirm).mockResolvedValue(true);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });
it('切换来源先确认，取消保留本地选择，确认后清空', async () => {
  render(<FileEditor library={library} id="" categoryId="files" />);
  fireEvent.click(screen.getByRole('button', { name: '＋ 选择本地文件' })); await screen.findByText('资料.txt');
  vi.mocked(confirm).mockResolvedValueOnce(false);
  fireEvent.click(screen.getByRole('button', { name: '关联云文件' }));
  await waitFor(() => expect(confirm).toHaveBeenCalledOnce()); expect(screen.getByText('资料.txt')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '关联云文件' })); await screen.findByText('云资料.pdf');
  expect(screen.queryByText('资料.txt')).toBeNull(); expect(screen.getByText('已选择 0 个文件 · 仅管理关联')).toBeTruthy();
});
it('云端关联直接提交身份，失败保留选择及分类，不生成话术', async () => {
  render(<FileEditor library={library} id="" categoryId="files" />);
  fireEvent.click(screen.getByRole('button', { name: '关联云文件' })); await screen.findByText('云资料.pdf');
  fireEvent.click(screen.getByRole('checkbox', { name: '选择 云资料.pdf' }));
  const previous = vi.mocked(call).getMockImplementation()!;
  vi.mocked(call).mockImplementation(async (command, args) => { if (command === 'add_cloud_files') throw new Error('服务器不可用'); return previous(command, args); });
  fireEvent.click(screen.getByRole('button', { name: '确定关联' })); await screen.findByText('Error: 服务器不可用');
  expect(call).toHaveBeenCalledWith('add_cloud_files', { sessionEpoch: 3, categoryId: 'files', assetIds: ['cloud'], replaceId: null });
  expect((screen.getByRole('checkbox', { name: '选择 云资料.pdf' }) as HTMLInputElement).checked).toBe(true);
  expect(closeWindow).not.toHaveBeenCalled();
});
it('分类删除后保留选择并拒绝保存，不自动改选其他分类', async () => {
  const view = render(<FileEditor library={library} id="" categoryId="files" />);
  fireEvent.click(screen.getByRole('button', { name: '＋ 选择本地文件' })); await screen.findByText('资料.txt');
  view.rerender(<FileEditor library={{ ...library, categories: [{ ...library.categories[0], id: 'other', name: '其他资料' }] }} id="" categoryId="files" />);
  expect((screen.getByRole('combobox', { name: '文件分类' }) as HTMLSelectElement).value).toBe('files');
  expect(screen.getByRole('option', { name: '原文件分类已不存在，请重新选择' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '确定关联' })); await screen.findByRole('alert');
  expect(screen.getByText('资料.txt')).toBeTruthy(); expect(closeWindow).not.toHaveBeenCalled();
  expect(vi.mocked(call).mock.calls.some(([command]) => command === 'add_files' || command === 'add_cloud_files')).toBe(false);
  fireEvent.change(screen.getByRole('combobox', { name: '文件分类' }), { target: { value: 'other' } });
  fireEvent.click(screen.getByRole('button', { name: '确定关联' }));
  await waitFor(() => expect(call).toHaveBeenCalledWith('add_files', { paths: ['C:\\资料.txt'], categoryId: 'other' }));
});
it('云文件名拒绝路径、保留名称和尾部句点', () => {
  for (const name of ['CON.pdf', '../资料.pdf', '错误?.txt', '资料.txt.', ' 资料.txt']) expect(cloudNameError(name)).not.toBe('');
  expect(cloudNameError('新版资料.pdf')).toBe('');
});
