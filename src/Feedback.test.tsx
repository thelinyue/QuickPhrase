import { act, cleanup, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Feedback } from './ui';
import { useNotices } from './notices';

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('窗口提示', () => {
  it('失败保留到明确关闭，不因点击正文或计时器消失', () => {
    vi.useFakeTimers();
    const clear = vi.fn();
    render(<Feedback message="设置保存失败，请检查后重试。" kind="error" clear={clear} />);
    expect(screen.getByRole('alert').textContent).toContain('设置保存失败');
    fireEvent.click(screen.getByText('设置保存失败，请检查后重试。'));
    act(() => vi.advanceTimersByTime(10000));
    expect(clear).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '关闭提示' }));
    expect(clear).toHaveBeenCalledOnce();
  });

  it('成功提示短暂显示，悬停和键盘焦点期间不消失', () => {
    vi.useFakeTimers();
    const clear = vi.fn();
    render(<Feedback message="已保存" kind="success" clear={clear} />);
    const notice = screen.getByRole('status');
    fireEvent.mouseEnter(notice);
    act(() => vi.advanceTimersByTime(4000));
    expect(clear).not.toHaveBeenCalled();
    fireEvent.mouseLeave(notice);
    fireEvent.focus(screen.getByRole('button', { name: '关闭提示' }));
    act(() => vi.advanceTimersByTime(4000));
    expect(clear).not.toHaveBeenCalled();
    fireEvent.blur(screen.getByRole('button', { name: '关闭提示' }), { relatedTarget: document.body });
    act(() => vi.advanceTimersByTime(3500));
    expect(clear).toHaveBeenCalledOnce();
  });

  it('长提示默认折叠，可展开完整内容，新提示重新折叠', () => {
    const message = '剪贴板暂时不可用。请检查占用程序后再试。'.repeat(8);
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('feedback-message') && this.textContent === message ? 160 : 20; });
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(40);
    const clear = vi.fn();
    const { rerender } = render(<Feedback message={message} kind="warning" title="操作已中断，请先检查目标" clear={clear} />);
    const notice = screen.getByRole('alert');
    expect(notice.textContent).toContain('操作已中断，请先检查目标');
    expect(notice.textContent).toContain(message);
    const details = screen.getByRole('button', { name: '查看详情' });
    expect(details.getAttribute('aria-expanded')).toBe('false');
    expect(notice.classList.contains('feedback-expanded')).toBe(false);
    fireEvent.click(details);
    expect(screen.getByRole('button', { name: '收起详情' }).getAttribute('aria-expanded')).toBe('true');
    expect(notice.classList.contains('feedback-expanded')).toBe(true);
    const content = notice.querySelector<HTMLElement>('.feedback-message')!;
    expect(content.getAttribute('tabindex')).toBe('0');
    content.focus(); expect(document.activeElement).toBe(content);
    content.scrollTop = 60;
    fireEvent.click(screen.getByRole('button', { name: '收起详情' }));
    expect(content.hasAttribute('tabindex')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '查看详情' }));
    expect(content.scrollTop).toBe(0);
    expect(clear).not.toHaveBeenCalled();
    rerender(<Feedback message="另一条提示" kind="error" clear={clear} />);
    expect(notice.classList.contains('feedback-expanded')).toBe(false);
    expect(screen.queryByRole('button', { name: '查看详情' })).toBeNull();
  });

  it('窗口尺寸变化后重新判断是否需要查看详情', () => {
    let overflowing = false; let resize = () => {};
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('feedback-message') && overflowing ? 80 : 20; });
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(40);
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: () => void) { resize = callback; }
      observe() {}
      disconnect() {}
    });
    render(<Feedback message="可随窗口宽度换行的提示" clear={vi.fn()} />);
    expect(screen.queryByRole('button', { name: '查看详情' })).toBeNull();
    overflowing = true; act(() => resize());
    expect(screen.getByRole('button', { name: '查看详情' }).getAttribute('aria-expanded')).toBe('false');
    overflowing = false; act(() => resize());
    expect(screen.queryByRole('button', { name: '查看详情' })).toBeNull();
  });

  it('成功提示展开详情期间不会自动关闭，进度停止仍可用', () => {
    vi.useFakeTimers();
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('feedback-message') ? 100 : 20; });
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(40);
    const clear = vi.fn(); const stop = vi.fn();
    const { rerender } = render(<Feedback message="成功详情" kind="success" clear={clear} />);
    fireEvent.click(screen.getByRole('button', { name: '查看详情' }));
    act(() => vi.advanceTimersByTime(5000));
    expect(clear).not.toHaveBeenCalled();
    rerender(<Feedback message="正在等待剪贴板释放…" kind="progress" clear={clear} stop={stop} />);
    fireEvent.click(screen.getByRole('button', { name: '停止' }));
    expect(stop).toHaveBeenCalledOnce();
  });

  it('部分执行优先，普通成功不能覆盖错误；逐条关闭且重复错误不刷屏', () => {
    const { result } = renderHook(() => useNotices());
    act(() => { result.current.error('设置失败'); result.current.success('已保存'); result.current.error('设置失败'); result.current.show('请先检查输入区', 'warning'); });
    expect(result.current.count).toBe(2);
    expect(result.current.current?.message).toBe('请先检查输入区');
    act(() => result.current.dismiss(result.current.current!.id));
    expect(result.current.current?.message).toBe('设置失败');
  });
});
