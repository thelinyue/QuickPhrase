import { act, cleanup, fireEvent, render as testingRender, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { confirm, open } from '@tauri-apps/plugin-dialog';
import { emitTo } from '@tauri-apps/api/event';
import { WindowCloseScope } from './close';
import { Editor } from './Editor';
import { FileEditor } from './FileAssociation';
import { CommonPhraseEditor, CommonPhraseSort } from './CommonPhrases';
import { CompanyPickerWindow } from './CompanyPicker';
import { DeletionWindow } from './RecycleBin';
import { call } from './api';
import type { CompanyFile, Library } from './types';

const native = vi.hoisted(() => ({ handler: null as null | ((e: { preventDefault: () => void }) => Promise<void>), close: vi.fn(async () => {}), destroyed: vi.fn() }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ close: native.close, onCloseRequested: async (handler: typeof native.handler) => { native.handler = handler; return () => { native.handler = null; }; } }) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: async () => () => {}, emitTo: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => false), open: vi.fn(async () => ['C:/资料.pdf']) }));
vi.mock('./api', () => ({ call: vi.fn(async () => null), closeWindow: () => native.close(), useTeams: () => ({ status: { sessionEpoch: 1, companyFiles: true, user: { id: 'u' }, teams: [] }, loaded: true }) }));
vi.mock('./CompanyFiles', () => ({ CompanyFilePicker: () => null, CloudFileTable: ({ selected, onChange }: { selected: CompanyFile[]; onChange: (files: CompanyFile[]) => void }) => <label><input type="checkbox" checked={!!selected.length} onChange={e => onChange(e.target.checked ? [{ assetId: 'asset', name: '云资料.pdf' } as CompanyFile] : [])} />选择云资料.pdf</label> }));
const library = { libraryId: 'local', dataDir: '', settings: {} as Library['settings'], categories: [{ id: 'root', name: '话术分类', kind: 'phrase', parentId: null, position: 0, background: '' }, { id: 'files', name: '文件分类', kind: 'file', parentId: null, position: 0, background: '' }], phrases: [{ id: 'p', rootId: 'root', groupId: null, title: '原标题', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '原正文' }] }] }], files: [], commonPhrases: [{ id: 'a', text: '甲', position: 0 }, { id: 'b', text: '乙', position: 1 }] } as Library;
const render = (element: import('react').ReactNode) => testingRender(element, { wrapper: WindowCloseScope });
beforeEach(() => {
  vi.clearAllMocks(); vi.mocked(confirm).mockResolvedValue(false); vi.mocked(call).mockResolvedValue(null);
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  native.close.mockImplementation(async () => { const event = { preventDefault: vi.fn() }; await native.handler!(event); if (!event.preventDefault.mock.calls.length) native.destroyed(); });
});
afterEach(() => { cleanup(); location.hash = ''; });

it.each(['取消', '关闭窗口', 'Escape'])('独立话术编辑器 %s 保留拒绝放弃的草稿，改回原值后直接关闭', async action => {
  render(<Editor library={library} id="p" afterId="" categoryId="root" groupId="" />);
  const input = screen.getByLabelText('话术标题（可不填）'); fireEvent.change(input, { target: { value: '新标题' } }); input.focus();
  if (action === 'Escape') fireEvent.keyDown(input, { key: 'Escape' }); else fireEvent.click(screen.getByRole('button', { name: action }));
  await waitFor(() => expect(confirm).toHaveBeenCalledOnce()); expect(native.destroyed).not.toHaveBeenCalled(); expect(input).toHaveProperty('value', '新标题');
  fireEvent.change(input, { target: { value: '原标题' } }); await act(() => native.close()); expect(native.destroyed).toHaveBeenCalledOnce(); expect(confirm).toHaveBeenCalledOnce();
});

it('话术保存期间原生关闭也被拒绝，失败保留输入；重试成功不重复询问放弃', async () => {
  let fail!: (e: Error) => void;
  vi.mocked(call).mockImplementation(async command => command === 'save_phrase' ? new Promise((_, reject) => { fail = reject; }) : null);
  render(<Editor library={library} id="p" afterId="" categoryId="root" groupId="" />);
  fireEvent.change(screen.getByLabelText('话术标题（可不填）'), { target: { value: '草稿' } });
  fireEvent.click(screen.getByRole('button', { name: '确定' })); await act(() => native.close());
  expect(native.destroyed).not.toHaveBeenCalled(); expect(screen.getByRole('button', { name: '取消' })).toHaveProperty('disabled', true); expect(confirm).not.toHaveBeenCalled();
  await act(async () => fail(new Error('写入失败'))); expect(screen.getByRole('alert').textContent).toContain('写入失败'); expect(screen.getByLabelText('话术标题（可不填）')).toHaveProperty('value', '草稿');
  vi.mocked(call).mockResolvedValue(null); fireEvent.click(screen.getByRole('button', { name: '确定' })); await waitFor(() => expect(native.destroyed).toHaveBeenCalledOnce()); expect(confirm).not.toHaveBeenCalled();
});

it('文件菜单先消费 Esc，快捷键弹窗 Esc 不连带关闭父编辑器，关闭恢复入口焦点', async () => {
  vi.mocked(call).mockImplementation(async command => command === 'set_shortcut_input' ? null : 'Ctrl+A');
  render(<Editor library={library} id="p" afterId="" categoryId="root" groupId="" />);
  fireEvent.click(screen.getByRole('button', { name: /^文件$/ })); fireEvent.keyDown(screen.getByRole('button', { name: '本机文件' }), { key: 'Escape' });
  expect(screen.queryByRole('button', { name: '本机文件' })).toBeNull(); expect(native.destroyed).not.toHaveBeenCalled();
  const trigger = screen.getByRole('button', { name: '自定义快捷键' }); trigger.focus(); fireEvent.click(trigger);
  const first = screen.getByLabelText('第一个按键'); await waitFor(() => expect(first).toHaveProperty('disabled', false));
  fireEvent.keyDown(first, { key: 'Escape' }); expect(native.close).not.toHaveBeenCalled();
  fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull()); expect(document.activeElement).toBe(trigger); expect(native.destroyed).not.toHaveBeenCalled();
});

it('附件导入期间阻止原生关闭，导入失败保留正文', async () => {
  let fail!: (e: Error) => void;
  vi.mocked(open).mockResolvedValueOnce(['C:/图片.png']); vi.mocked(call).mockImplementation(async command => command === 'import_images' ? new Promise((_, reject) => { fail = reject; }) : null);
  render(<Editor library={library} id="p" afterId="" categoryId="root" groupId="" />);
  fireEvent.click(screen.getByRole('button', { name: /^图片$/ })); await waitFor(() => expect(fail).toBeTypeOf('function')); await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled();
  await act(async () => fail(new Error('导入失败'))); expect(screen.getByRole('textbox', { name: '第1段文字' }).textContent).toBe('原正文');
  await act(() => native.close()); expect(native.destroyed).toHaveBeenCalledOnce(); expect(confirm).not.toHaveBeenCalled();
});

it('文件关联草稿拒绝关闭后保留选择，保存过程中不能关闭，成功后正常退出', async () => {
  let finish!: () => void; vi.mocked(call).mockImplementation(async command => command === 'add_files' ? new Promise<null>(resolve => { finish = () => resolve(null); }) : null);
  render(<FileEditor library={library} id="" categoryId="files" />); fireEvent.click(screen.getByRole('button', { name: '＋ 选择本地文件' })); await screen.findByText('资料.pdf');
  await act(() => native.close()); expect(confirm).toHaveBeenCalledOnce(); expect(native.destroyed).not.toHaveBeenCalled(); expect(screen.getByText('资料.pdf')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '确定关联' })); await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled(); expect(screen.getByRole('button', { name: '取消' })).toHaveProperty('disabled', true);
  await act(async () => finish()); expect(native.destroyed).toHaveBeenCalledOnce(); expect(confirm).toHaveBeenCalledOnce();
});

it('短语编辑有草稿才询问；排序只选中不算修改，移动后取消关闭保留顺序', async () => {
  const editor = render(<CommonPhraseEditor library={library} id="a" />); fireEvent.change(screen.getByLabelText('短语内容'), { target: { value: '修改' } }); await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled(); expect(confirm).toHaveBeenCalledOnce(); editor.unmount();
  vi.clearAllMocks(); render(<CommonPhraseSort library={library} />);
  const first = screen.getByRole('option', { name: '甲' }); fireEvent.keyDown(first, { key: ' ' }); await act(() => native.close()); expect(confirm).not.toHaveBeenCalled(); expect(native.destroyed).toHaveBeenCalledOnce();
  native.destroyed.mockClear(); fireEvent.keyDown(first, { key: 'ArrowDown', altKey: true }); await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled(); expect(confirm).toHaveBeenCalledOnce(); expect(screen.getAllByRole('option').map(row => row.textContent)).toEqual(['乙', '甲']);
  fireEvent.click(screen.getByRole('button', { name: '确定' })); await waitFor(() => expect(native.destroyed).toHaveBeenCalledOnce()); expect(confirm).toHaveBeenCalledOnce();
});

it('删除提交不能用原生关闭绕过，失败保留确认对象与回收选项', async () => {
  let fail!: (e: Error) => void; vi.mocked(call).mockImplementation(async command => command === 'delete_item' ? new Promise((_, reject) => { fail = reject; }) : null);
  render(<DeletionWindow library={library} id="p" kind="phrase" />); fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: '确定' })); await act(() => native.close());
  expect(native.destroyed).not.toHaveBeenCalled(); await act(async () => fail(new Error('删除失败'))); expect(screen.getByRole('checkbox')).toHaveProperty('checked', false); expect(confirm).not.toHaveBeenCalled();
});

it('云文件回传期间不重复提交、不关闭，账号复核失败保留选择，重试成功再退出', async () => {
  location.hash = '#company-picker?epoch=1&limit=1&parent=editor&requestId=request'; let fail!: (e: Error) => void;
  vi.mocked(call).mockImplementation(async command => command === 'get_team_status' ? new Promise((_, reject) => { fail = reject; }) : null);
  render(<CompanyPickerWindow />); fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: '添加文件' })); fireEvent.click(screen.getByRole('button', { name: '添加文件' })); await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled();
  await act(async () => fail(new Error('账号复核失败'))); expect(screen.getByRole('checkbox')).toHaveProperty('checked', true); expect(emitTo).not.toHaveBeenCalled();
  vi.mocked(call).mockResolvedValue({ sessionEpoch: 1 }); fireEvent.click(screen.getByRole('button', { name: '添加文件' })); await waitFor(() => expect(native.destroyed).toHaveBeenCalledOnce()); expect(emitTo).toHaveBeenCalledOnce(); expect(confirm).not.toHaveBeenCalled();
});
