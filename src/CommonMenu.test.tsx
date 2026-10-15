import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CommonMenuWindow } from './CommonMenu';
import { CommonPhraseBar } from './CommonPhrases';
import { call, openWindow } from './api';
import { confirm } from '@tauri-apps/plugin-dialog';
import type { Library } from './types';

const events = vi.hoisted(() => new Map<string, Set<(e: { payload: any }) => void>>());
vi.mock('./api', () => ({ call: vi.fn(), openWindow: vi.fn(), closeWindow: vi.fn(), useTeams: vi.fn() }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (name, fn) => {
  if (!events.has(name)) events.set(name, new Set()); events.get(name)!.add(fn);
  return () => { events.get(name)?.delete(fn); };
}) }));
const payload = { requestId: 'menu-1', libraryId: 'A', phraseId: 'P', canSort: true, theme: 'default' };
const library = { libraryId: 'A', commonPhrases: [{ id: 'P', text: '您好', position: 0 }], settings: {} } as Library;
const notify = (name: string, value: unknown) => act(() => events.get(name)?.forEach(fn => fn({ payload: value })));
beforeEach(() => { vi.mocked(call).mockImplementation(async name => name === 'get_common_menu' ? payload : null); vi.mocked(confirm).mockResolvedValue(false); });
afterEach(() => { cleanup(); vi.clearAllMocks(); events.clear(); });

describe('定制常用短语菜单', () => {
  it('仅显示四项管理动作，无插入或发送；快速双击只选择一次', async () => {
    render(<CommonMenuWindow />);
    await screen.findByRole('menuitem', { name: '新增个人短语' });
    expect(screen.getAllByRole('menuitem').map(el => el.textContent)).toEqual(['新增个人短语', '修改这个短语', '删除这个短语', '快速排序短语']);
    fireEvent.click(screen.getByRole('menuitem', { name: '修改这个短语' }));
    fireEvent.click(screen.getByRole('menuitem', { name: '修改这个短语' }));
    expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'choose_common_menu')).toEqual([['choose_common_menu', { requestId: 'menu-1', action: 'edit' }]]);
  });
  it('空白菜单只有新增和排序，键盘跳过禁用项并用Esc恢复来源焦点', async () => {
    vi.mocked(call).mockResolvedValue({ ...payload, phraseId: null, canSort: false });
    render(<CommonMenuWindow />); const add = await screen.findByRole('menuitem', { name: '新增个人短语' });
    expect(screen.getAllByRole('menuitem')).toHaveLength(2);
    expect((screen.getByRole('menuitem', { name: '快速排序短语' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.keyDown(add, { key: 'End' }); expect(document.activeElement).toBe(add);
    fireEvent.keyDown(add, { key: 'ArrowDown' }); expect(document.activeElement).toBe(add);
    fireEvent.keyDown(add, { key: 'Escape' });
    expect(call).toHaveBeenCalledWith('close_common_menu', { requestId: 'menu-1', restore: true });
  });
  it('新菜单重置选择锁，Home/End/方向键定位，Tab关闭', async () => {
    render(<CommonMenuWindow />); const add = await screen.findByRole('menuitem', { name: '新增个人短语' });
    fireEvent.keyDown(add, { key: 'End' }); expect(document.activeElement?.textContent).toBe('快速排序短语');
    fireEvent.keyDown(document.activeElement!, { key: 'ArrowUp' }); expect(document.activeElement?.textContent).toBe('删除这个短语');
    fireEvent.keyDown(document.activeElement!, { key: 'Home' }); expect(document.activeElement).toBe(add);
    fireEvent.click(add); notify('common-menu', { ...payload, requestId: 'menu-2' }); fireEvent.click(add);
    expect(call).toHaveBeenCalledWith('choose_common_menu', { requestId: 'menu-2', action: 'add' });
    fireEvent.keyDown(add, { key: 'Tab' }); expect(call).toHaveBeenCalledWith('close_common_menu', { requestId: 'menu-2', restore: true });
  });
});

describe('来源窗口验证菜单动作', () => {
  const show = async () => {
    fireEvent.contextMenu(screen.getByRole('option', { name: '您好' }), { clientX: 70, clientY: 40 });
    await waitFor(() => expect(call).toHaveBeenCalledWith('open_common_menu', expect.objectContaining({ x: 70, y: 40 })));
    return (vi.mocked(call).mock.calls.find(([name]) => name === 'open_common_menu')![1]!.payload as typeof payload).requestId;
  };
  it('右键不插入，取消删除保留短语，确认删除携带原库标识', async () => {
    const onDeliver = vi.fn(); render(<CommonPhraseBar library={library} busy={false} onDeliver={onDeliver} onError={vi.fn()} />);
    let requestId = await show(); notify('common-menu-action', { requestId, action: 'delete', restore: true });
    await waitFor(() => expect(confirm).toHaveBeenCalled());
    expect(onDeliver).not.toHaveBeenCalled(); expect(vi.mocked(call).mock.calls.some(([name]) => name === 'delete_item')).toBe(false);
    vi.mocked(call).mockClear(); vi.mocked(confirm).mockResolvedValue(true);
    requestId = await show(); notify('common-menu-action', { requestId, action: 'delete', restore: true });
    await waitFor(() => expect(call).toHaveBeenCalledWith('delete_item', { id: 'P', kind: 'common', libraryId: 'A' }));
  });
  it('切换个人库关闭旧菜单并忽略迟到的编辑动作', async () => {
    const props = { busy: false, onDeliver: vi.fn(), onError: vi.fn() };
    const view = render(<CommonPhraseBar library={library} {...props} />); const requestId = await show();
    view.rerender(<CommonPhraseBar library={{ ...library, libraryId: 'B' }} {...props} />);
    expect(call).toHaveBeenCalledWith('close_common_menu', { requestId, restore: false });
    notify('common-menu-action', { requestId, action: 'edit', restore: true }); expect(openWindow).not.toHaveBeenCalled();
  });
  it('无关话术修订变化不关闭短语菜单，当前短语修改会使菜单失效', async () => {
    const props = { busy: false, onDeliver: vi.fn(), onError: vi.fn() };
    const first = { ...library, privateRevisions: { 'common:P': 1, 'phrase:Q': 2 } };
    const view = render(<CommonPhraseBar library={first} {...props} />); const requestId = await show();
    view.rerender(<CommonPhraseBar library={{ ...first, privateRevisions: { 'phrase:Q': 3, 'common:P': 1 } }} {...props} />);
    expect(call).not.toHaveBeenCalledWith('close_common_menu', expect.anything());
    view.rerender(<CommonPhraseBar library={{ ...first, privateRevisions: { 'common:P': 2 } }} {...props} />);
    expect(call).toHaveBeenCalledWith('close_common_menu', { requestId, restore: false });
    notify('common-menu-action', { requestId, action: 'delete', restore: true }); expect(confirm).not.toHaveBeenCalled();
  });
  it('删除确认期间切库后不提交旧删除', async () => {
    let answer!: (value: boolean) => void; vi.mocked(confirm).mockReturnValue(new Promise(resolve => { answer = resolve; }));
    const props = { busy: false, onDeliver: vi.fn(), onError: vi.fn() };
    const view = render(<CommonPhraseBar library={library} {...props} />); const requestId = await show();
    notify('common-menu-action', { requestId, action: 'delete', restore: true }); await waitFor(() => expect(confirm).toHaveBeenCalled());
    view.rerender(<CommonPhraseBar library={{ ...library, libraryId: 'B' }} {...props} />);
    await act(async () => answer(true));
    expect(props.onError).toHaveBeenCalledWith(expect.stringContaining('请重新确认删除'));
    expect(vi.mocked(call).mock.calls.some(([name]) => name === 'delete_item')).toBe(false);
  });
});
