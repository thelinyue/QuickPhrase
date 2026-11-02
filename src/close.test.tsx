import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { confirm } from '@tauri-apps/plugin-dialog';
import { WindowCloseScope, useCloseGuard } from './close';
import { FormDialog } from './ui';

const native = vi.hoisted(() => ({ handlers: new Set<(e: { preventDefault: () => void }) => Promise<void>>(), destroyed: vi.fn(), close: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true) }));
vi.mock('./api', () => ({ call: vi.fn(), closeWindow: () => native.close() }));
vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ close: native.close, onCloseRequested: async (callback: (e: { preventDefault: () => void }) => Promise<void>) => { native.handlers.add(callback); return () => native.handlers.delete(callback); } }) }));
beforeEach(() => {
  vi.clearAllMocks(); vi.mocked(confirm).mockResolvedValue(true);
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  // 与 Tauri 一致：每个监听各自决定 destroy，不能用共享 preventDefault 的假宿主掩盖多监听问题。
  native.close.mockImplementation(async () => { for (const handler of native.handlers) { const event = { preventDefault: vi.fn() }; await handler(event); if (!event.preventDefault.mock.calls.length) native.destroyed(); } });
});
afterEach(() => { cleanup(); native.handlers.clear(); });
function Guard({ busy = false, dirty = false, prepare }: { busy?: boolean; dirty?: boolean; prepare?: () => Promise<boolean> }) {
  useCloseGuard({ busy: () => busy, dirty: () => dirty, beforeClose: prepare }); return null;
}

it('父草稿、子弹窗共用单一原生入口，拒绝后保留；接受只确认一次', async () => {
  render(<WindowCloseScope><Guard dirty /><FormDialog close={vi.fn()} spec={{ title: '名称', fields: <input name="name" aria-label="名称" />, onSubmit: vi.fn() }} /></WindowCloseScope>);
  const input = screen.getByRole('textbox'); fireEvent.change(input, { target: { value: '新名称' } }); input.focus();
  expect(native.handlers.size).toBe(1); vi.mocked(confirm).mockResolvedValueOnce(false);
  await act(() => native.close()); expect(native.destroyed).not.toHaveBeenCalled(); expect(input).toHaveProperty('value', '新名称'); expect(document.activeElement).toBe(input);
  vi.mocked(confirm).mockClear(); await act(() => native.close()); expect(confirm).toHaveBeenCalledOnce(); expect(native.destroyed).toHaveBeenCalledOnce();
});

it('原生关闭、弹窗 ×、取消和 Esc 连点只询问一次，提交中所有入口均被拦截', async () => {
  let decide!: (accepted: boolean) => void, fail!: (e: Error) => void;
  const close = vi.fn(), submit = vi.fn(() => new Promise<void>((_, reject) => { fail = reject; }));
  render(<WindowCloseScope><FormDialog close={close} windowClose spec={{ title: '保存', fields: <input name="name" aria-label="名称" />, onSubmit: submit }} /></WindowCloseScope>);
  fireEvent.change(screen.getByRole('textbox'), { target: { value: '草稿' } });
  vi.mocked(confirm).mockImplementationOnce(() => new Promise(resolve => { decide = resolve; }));
  fireEvent.click(screen.getByRole('button', { name: '关闭弹窗' })); fireEvent.click(screen.getByRole('button', { name: '取消' }));
  fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true })); await act(() => native.close());
  expect(confirm).toHaveBeenCalledOnce(); expect(close).not.toHaveBeenCalled(); expect(native.destroyed).not.toHaveBeenCalled();
  await act(async () => decide(false));
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!); await act(() => native.close());
  fireEvent.click(screen.getByRole('button', { name: '关闭弹窗' })); fireEvent.click(screen.getByRole('button', { name: '关闭窗口' }));
  fireEvent.click(screen.getByRole('button', { name: '取消' })); fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }));
  expect(submit).toHaveBeenCalledOnce(); expect(close).not.toHaveBeenCalled(); expect(native.destroyed).not.toHaveBeenCalled();
  await act(async () => fail(new Error('保存失败'))); expect(screen.getByRole('textbox')).toHaveProperty('value', '草稿'); expect(screen.getByRole('alert').textContent).toContain('保存失败');
});

it('自动保存准备必须成功，失败不放行；处理中重复关闭不会重复 flush', async () => {
  let finish!: (ok: boolean) => void;
  const prepare = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve; }));
  render(<WindowCloseScope><Guard prepare={prepare} /></WindowCloseScope>);
  let request!: Promise<void>; act(() => { request = native.close(); }); await act(() => native.close());
  expect(prepare).toHaveBeenCalledOnce(); await act(async () => { finish(false); await request; }); expect(native.destroyed).not.toHaveBeenCalled();
  act(() => { request = native.close(); }); await act(async () => { finish(true); await request; }); expect(native.destroyed).toHaveBeenCalledOnce(); expect(confirm).not.toHaveBeenCalled();
});

it('确认返回前进入提交状态也不能关窗；关闭失败可重新请求', async () => {
  let finish!: (accepted: boolean) => void;
  vi.mocked(confirm).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const view = render(<WindowCloseScope><Guard dirty /></WindowCloseScope>);
  let request!: Promise<void>; act(() => { request = native.close(); });
  view.rerender(<WindowCloseScope><Guard dirty busy /></WindowCloseScope>);
  await act(async () => { finish(true); await request; }); expect(native.destroyed).not.toHaveBeenCalled();
  view.rerender(<WindowCloseScope><Guard dirty /></WindowCloseScope>);
  native.close.mockRejectedValueOnce(new Error('窗口暂不可用'));
  await act(async () => { for (const handler of native.handlers) await handler({ preventDefault: vi.fn() }); });
  expect(screen.getByRole('alert').textContent).toContain('窗口暂不可用');
  await act(() => native.close()); expect(native.destroyed).toHaveBeenCalledOnce();
});

it('弹窗卸载后确认回执不作用于新表单；主窗不安装销毁型原生监听', async () => {
  let finish!: (accepted: boolean) => void;
  vi.mocked(confirm).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const close = vi.fn();
  const view = render(<WindowCloseScope enabled={false}><FormDialog close={close} spec={{ title: '旧表单', fields: <input aria-label="名称" />, onSubmit: vi.fn() }} /></WindowCloseScope>);
  fireEvent.change(screen.getByRole('textbox'), { target: { value: '旧草稿' } }); fireEvent.click(screen.getByRole('button', { name: '关闭弹窗' }));
  view.unmount(); await act(async () => finish(true)); expect(close).not.toHaveBeenCalled(); expect(native.handlers.size).toBe(0);
});
