import { createRef } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { confirm } from '@tauri-apps/plugin-dialog';
import { Button, FormDialog, Icon, IconButton } from './ui';

vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn() }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ confirm: vi.fn(async () => true) }));
beforeEach(() => { vi.clearAllMocks(); vi.mocked(confirm).mockResolvedValue(true); HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.open = true; }); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('共用按钮', () => {
  it('默认不是提交按钮，保留原生属性、事件和 ref', async () => {
    const submit = vi.fn(), click = vi.fn(), ref = createRef<HTMLButtonElement>();
    render(<form onSubmit={submit}><Button ref={ref} name="action" value="save" title="说明" data-action="save" onClick={click}>保存草稿</Button></form>);
    const button = screen.getByRole('button', { name: '保存草稿' }) as HTMLButtonElement;
    expect(ref.current).toBe(button);
    expect(button.type).toBe('button'); expect(button.name).toBe('action'); expect(button.value).toBe('save');
    expect(button.dataset.action).toBe('save'); expect(button.title).toBe('说明');
    await userEvent.click(button);
    expect(click).toHaveBeenCalledOnce(); expect(submit).not.toHaveBeenCalled();
  });

  it('显式 submit 保留表单校验及键盘提交', async () => {
    const submit = vi.fn((event: React.FormEvent) => event.preventDefault());
    const user = userEvent.setup();
    render(<form onSubmit={submit}><input aria-label="名称" name="name" required /><Button type="submit" variant="primary">确定</Button></form>);
    await user.click(screen.getByRole('button', { name: '确定' })); expect(submit).not.toHaveBeenCalled();
    await user.type(screen.getByRole('textbox', { name: '名称' }), '有效名称{Enter}');
    expect(submit).toHaveBeenCalledOnce();
  });

  it.each(['disabled', 'busy'] as const)('%s 时阻止操作，不接管业务回调', async state => {
    const click = vi.fn();
    render(<Button {...{ [state]: true }} onClick={click}>执行</Button>);
    const button = screen.getByRole('button', { name: '执行' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    if (state === 'busy') expect(button.getAttribute('aria-busy')).toBe('true');
    await userEvent.click(button); expect(click).not.toHaveBeenCalled();
  });

  it('图标按钮保留可访问名称、选中状态和键盘操作', async () => {
    const click = vi.fn(), ref = createRef<HTMLButtonElement>(), user = userEvent.setup();
    render(<IconButton ref={ref} aria-label="保存权限" aria-pressed size="compact" danger onClick={click}><Icon name="save" /></IconButton>);
    const button = screen.getByRole('button', { name: '保存权限', pressed: true });
    expect(ref.current).toBe(button); expect(button.classList.contains('ui-icon-button')).toBe(true);
    expect(button.classList.contains('ui-button--compact')).toBe(true); expect(button.classList.contains('ui-button--danger')).toBe(true);
    await user.tab(); expect(document.activeElement).toBe(button);
    await user.keyboard('{Enter}'); await user.keyboard(' '); expect(click).toHaveBeenCalledTimes(2);
  });
});

describe('通用表单样板', () => {
  it.each(['关闭弹窗', '取消', 'Escape'])('%s 共用草稿确认，拒绝后保留内容和焦点', async action => {
    const close = vi.fn(); vi.mocked(confirm).mockResolvedValueOnce(false);
    render(<FormDialog close={close} spec={{ title: '编辑名称', fields: <input name="name" aria-label="名称" defaultValue="原名称" />, onSubmit: vi.fn() }} />);
    const input = screen.getByRole('textbox', { name: '名称' }); input.focus();
    fireEvent.change(input, { target: { value: '草稿' } });
    if (action === 'Escape') fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }));
    else fireEvent.click(screen.getByRole('button', { name: action }));
    await waitFor(() => expect(confirm).toHaveBeenCalledOnce());
    expect(close).not.toHaveBeenCalled(); expect(input).toHaveProperty('value', '草稿'); expect(document.activeElement).toBe(input);
  });

  it('未修改或已改回原值直接关闭；遮罩点击、失焦不关闭', async () => {
    const close = vi.fn();
    render(<FormDialog close={close} spec={{ title: '编辑名称', fields: <input name="name" aria-label="名称" defaultValue="原名称" />, onSubmit: vi.fn() }} />);
    const input = screen.getByRole('textbox', { name: '名称' });
    fireEvent.change(input, { target: { value: '草稿' } }); fireEvent.change(input, { target: { value: '原名称' } });
    fireEvent.click(screen.getByRole('dialog')); fireEvent.blur(window);
    expect(close).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '关闭弹窗' }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce()); expect(confirm).not.toHaveBeenCalled();
  });

  it('安全复核输入不是编辑草稿，取消危险确认不再询问放弃', async () => {
    const close = vi.fn();
    render(<FormDialog close={close} spec={{ title: '删除确认', danger: true, hasDraft: false, fields: <input name="confirmation" aria-label="复核名称" />, onSubmit: vi.fn() }} />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '已核对的名称' } });
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce()); expect(confirm).not.toHaveBeenCalled();
  });

  it('危险确认使用共用语义，失败保留草稿，成功后关闭并恢复触发焦点', async () => {
    const close = vi.fn(), submit = vi.fn().mockRejectedValueOnce(new Error('名称不匹配，请核对。')).mockResolvedValueOnce(undefined);
    const trigger = document.createElement('button'); document.body.append(trigger); trigger.focus();
    const { unmount } = render(<FormDialog close={close} spec={{ title: '删除确认', submit: '永久删除', danger: true, fields: <label>名称<input name="name" defaultValue="草稿" required /></label>, onSubmit: submit }} />);
    const confirm = screen.getByRole('button', { name: '永久删除' });
    expect(confirm.classList.contains('ui-button--danger')).toBe(true);
    fireEvent.submit(confirm.closest('form')!); await screen.findByText('Error: 名称不匹配，请核对。');
    expect(close).not.toHaveBeenCalled(); expect((screen.getByRole('textbox', { name: '名称' }) as HTMLInputElement).value).toBe('草稿');
    fireEvent.submit(confirm.closest('form')!); await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(submit.mock.calls[1][0].get('name')).toBe('草稿');
    unmount(); expect(document.activeElement).toBe(trigger); trigger.remove();
  });

  it('处理中只提交一次，禁用字段和操作，Escape 不提前关闭', async () => {
    let release!: () => void;
    const close = vi.fn(), submit = vi.fn(() => new Promise<void>(resolve => { release = resolve; }));
    render(<FormDialog close={close} spec={{ title: '保存', fields: <input aria-label="名称" name="name" />, onSubmit: submit }} />);
    const dialog = screen.getByRole('dialog'), form = dialog.querySelector('form')!;
    fireEvent.submit(form); fireEvent.submit(form); expect(submit).toHaveBeenCalledOnce();
    expect((screen.getByRole('button', { name: '提交中…' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('button', { name: '提交中…' }).getAttribute('aria-busy')).toBe('true');
    expect(dialog.querySelector('fieldset')!.disabled).toBe(true);
    const cancel = new Event('cancel', { bubbles: false, cancelable: true }); fireEvent(dialog, cancel);
    expect(cancel.defaultPrevented).toBe(true); expect(close).not.toHaveBeenCalled();
    await act(async () => release()); expect(close).toHaveBeenCalledOnce();
  });
});
