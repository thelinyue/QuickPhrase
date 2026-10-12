import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { call } from './api';
import { ShortcutDialog } from './ShortcutDialog';

vi.mock('./api', () => ({ call: vi.fn(async (command, args) => command === 'validate_phrase_shortcut' ? args.shortcut : undefined), closeWindow: vi.fn() }));
beforeAll(() => { HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); }; });
afterEach(() => { cleanup(); vi.clearAllMocks(); });
async function open(value = '') {
  const onConfirm = vi.fn(async () => {}), close = vi.fn();
  render(<ShortcutDialog id="P" value={value} onConfirm={onConfirm} close={close} />);
  await waitFor(() => expect((screen.getByLabelText('第一个按键') as HTMLInputElement).disabled).toBe(false));
  return { onConfirm, close };
}
const key = (label: string, value: string, code = `Key${value}`) => fireEvent.keyDown(screen.getByLabelText(label), { key: value, code });

describe('自定义快捷键录入', () => {
  it('使用说明展开保留两种按键示例，收起与取消不保存且无视频入口', async () => {
    const { onConfirm, close } = await open('Ctrl+Y+C');
    const help = screen.getByRole('button', { name: '自定义快捷键使用说明' });
    expect(help.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(help);
    expect(help.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('Ctrl+Y+C')).toBeTruthy();
    expect(screen.getByText('1+1（无功能键）')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '打开视频教程' })).toBeNull();
    fireEvent.click(help);
    expect(help.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByText('1+1（无功能键）')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    expect(onConfirm).not.toHaveBeenCalled(); expect(close).toHaveBeenCalledOnce();
  });
  it('直接录入两键，确认只提交规范值，Enter录入不会意外提交', async () => {
    const { onConfirm, close } = await open();
    fireEvent.change(screen.getByLabelText('功能键'), { target: { value: 'Ctrl' } });
    key('第一个按键', 'y', 'KeyY'); key('第二个按键', 'c', 'KeyC');
    key('第二个按键', 'Enter', 'Enter'); expect(onConfirm).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(onConfirm).toHaveBeenCalledExactlyOnceWith('Ctrl+Y+C'));
    expect(close).toHaveBeenCalledOnce(); expect(call).toHaveBeenCalledWith('validate_phrase_shortcut', { id: 'P', shortcut: 'Ctrl+Y+C' });
  });
  it('无修饰键允许重复序列，单个裸键拒绝，组合过程和长按不改录入值', async () => {
    const { onConfirm } = await open(); key('第一个按键', '1', 'Digit1');
    fireEvent.click(screen.getByRole('button', { name: '确定' })); expect(onConfirm).not.toHaveBeenCalled(); expect(screen.getByRole('alert')).toBeTruthy();
    key('第二个按键', '1', 'Digit1');
    fireEvent.keyDown(screen.getByLabelText('第二个按键'), { key: '2', code: 'Digit2', repeat: true });
    fireEvent.keyDown(screen.getByLabelText('第二个按键'), { key: 'Process', code: 'KeyA', isComposing: true });
    fireEvent.click(screen.getByRole('button', { name: '确定' })); await waitFor(() => expect(onConfirm).toHaveBeenCalledExactlyOnceWith('1+1'));
  });
  it('兼容旧的多修饰键绑定，取消删除不提交，确认删除提交空值', async () => {
    const a = await open('Ctrl+Alt+1'); expect((screen.getByLabelText('功能键') as HTMLSelectElement).value).toBe('Ctrl+Alt');
    expect((screen.getByLabelText('第二个按键') as HTMLInputElement).value).toBe('');
    fireEvent.click(screen.getByRole('button', { name: '确定' })); await waitFor(() => expect(a.onConfirm).toHaveBeenCalledExactlyOnceWith('Ctrl+Alt+1'));
    cleanup(); const b = await open('Ctrl+Y+C'); fireEvent.click(screen.getByRole('button', { name: '删除话术快捷键' }));
    fireEvent.click(screen.getByRole('button', { name: '取消' })); expect(b.onConfirm).not.toHaveBeenCalled(); expect(b.close).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: '确定' })); await waitFor(() => expect(b.onConfirm).toHaveBeenCalledExactlyOnceWith(''));
  });
  it('冲突或保存失败保留对话框，快速双击不会重复提交', async () => {
    await open('Ctrl+Y+C');
    vi.mocked(call).mockRejectedValueOnce(new Error('快捷键与已有话术冲突。'));
    fireEvent.click(screen.getByRole('button', { name: '确定' })); await screen.findByRole('alert'); expect(screen.getByRole('dialog')).toBeTruthy();
    vi.mocked(call).mockImplementationOnce(async () => new Promise(() => {}));
    fireEvent.click(screen.getByRole('button', { name: '确定' })); fireEvent.click(screen.getByRole('button', { name: '保存中' }));
    expect(vi.mocked(call).mock.calls.filter(([cmd]) => cmd === 'validate_phrase_shortcut')).toHaveLength(2);
  });
});
