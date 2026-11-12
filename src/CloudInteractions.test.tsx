import { useRef } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { CloudLoading } from './CloudLoading';
import { CommandSplitter } from './CommandSplitter';

let width: number, hidden: boolean, media: MediaQueryList, resize: () => void, nextFrame: number;
const frames = new Map<number, FrameRequestCallback>();
const disconnect = vi.fn();
beforeEach(() => {
  width = 1000; hidden = false; nextFrame = 0; frames.clear();
  media = Object.assign(new EventTarget(), { matches: false }) as MediaQueryList;
  vi.stubGlobal('matchMedia', () => media);
  vi.stubGlobal('innerWidth', 1000);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frames.set(++nextFrame, callback); return nextFrame; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
  vi.stubGlobal('ResizeObserver', class { constructor(callback: () => void) { resize = callback; } observe() {} disconnect = disconnect; });
  vi.stubGlobal('PointerEvent', class extends MouseEvent { pointerId: number; constructor(type: string, init: PointerEventInit = {}) { super(type, init); this.pointerId = init.pointerId ?? 1; } });
  vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden);
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('commands-workspace') ? width : 0; });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.clearAllMocks(); });

function Workspace({ active = true }: { active?: boolean }) {
  const workspace = useRef<HTMLDivElement>(null);
  return <div className="commands-workspace" ref={workspace}><div id="cloud-command-list">原列表</div><article>原正文</article><CommandSplitter workspace={workspace} active={active} /></div>;
}
function setup() {
  const result = render(<Workspace />), separator = screen.getByRole('separator'), workspace = separator.parentElement!;
  let captured: number | null = null;
  separator.setPointerCapture = id => { captured = id; };
  separator.hasPointerCapture = id => captured === id;
  separator.releasePointerCapture = () => { captured = null; };
  return { ...result, separator, workspace, pixels: () => parseFloat(workspace.style.getPropertyValue('--command-list-width')) };
}
function tick(time: number) { const pending = [...frames.values()]; frames.clear(); act(() => pending.forEach(callback => callback(time))); }

it('分隔条键盘调宽、边界与恢复默认均保留正文节点和列表滚动位置', () => {
  const { separator, pixels } = setup(), list = screen.getByText('原列表'), detail = screen.getByText('原正文');
  list.scrollTop = 90;
  expect(pixels()).toBe(340);
  expect(separator.getAttribute('aria-controls')).toBe(list.id);
  fireEvent.keyDown(separator, { key: 'ArrowRight' }); expect(pixels()).toBe(356);
  fireEvent.keyDown(separator, { key: 'ArrowLeft', shiftKey: true }); expect(pixels()).toBe(308);
  fireEvent.keyDown(separator, { key: 'Home' }); expect(pixels()).toBe(240);
  fireEvent.keyDown(separator, { key: 'ArrowLeft' }); expect(pixels()).toBe(240);
  fireEvent.keyDown(separator, { key: 'End' }); expect(pixels()).toBe(680);
  fireEvent.keyDown(separator, { key: 'ArrowRight' }); expect(pixels()).toBe(680);
  fireEvent.keyDown(separator, { key: 'Escape' }); expect(pixels()).toBe(340);
  fireEvent.keyDown(separator, { key: 'ArrowRight' }); fireEvent.doubleClick(separator); expect(pixels()).toBe(340);
  expect(separator.getAttribute('aria-valuetext')).toBe('命令列表宽度 34%');
  expect(screen.getByText('原正文')).toBe(detail); expect(list.scrollTop).toBe(90);
  width = 862; act(() => resize());
  fireEvent.keyDown(separator, { key: 'Home' }); expect(Number(separator.getAttribute('aria-valuenow'))).toBe(Number(separator.getAttribute('aria-valuemin')));
  fireEvent.keyDown(separator, { key: 'End' }); expect(Number(separator.getAttribute('aria-valuenow'))).toBe(Number(separator.getAttribute('aria-valuemax')));
});

it('指针拖动到边界只限制内容宽度，正常松手和丢失捕获后不再跟随', () => {
  const { separator, workspace, pixels } = setup();
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 });
  fireEvent.pointerMove(separator, { clientX: 460 }); expect(pixels()).toBe(460);
  fireEvent.pointerMove(separator, { clientX: 2000 }); expect(pixels()).toBe(680);
  fireEvent.pointerMove(separator, { clientX: -100 }); expect(pixels()).toBe(240);
  fireEvent.pointerUp(separator); expect(workspace.dataset.resizing).toBeUndefined();
  fireEvent.pointerMove(separator, { clientX: 600 }); expect(pixels()).toBe(240);
  fireEvent.pointerDown(separator, { button: 0, clientX: 240 }); fireEvent.pointerMove(separator, { clientX: 500 });
  fireEvent.lostPointerCapture(separator); fireEvent.pointerMove(separator, { clientX: 600 }); expect(pixels()).toBe(500);
});

it('Esc和指针取消恢复拖动前比例，失焦保留当前合法宽度并结束拖动', () => {
  const { separator, pixels } = setup();
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 }); fireEvent.pointerMove(separator, { clientX: 500 });
  fireEvent.pointerCancel(separator); expect(pixels()).toBe(340);
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 }); fireEvent.pointerMove(separator, { clientX: 600 });
  fireEvent.keyDown(separator, { key: 'Escape' }); expect(pixels()).toBe(340);
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 }); fireEvent.pointerMove(separator, { clientX: 500 });
  fireEvent(window, new Event('blur')); fireEvent.pointerMove(separator, { clientX: 600 }); expect(pixels()).toBe(500);
});

it('899 DIP及隐藏模块结束拖动，恢复宽窗与模块后保留比例', () => {
  const { separator, workspace, pixels, rerender } = setup();
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 }); fireEvent.pointerMove(separator, { clientX: 500 });
  width = 800; window.innerWidth = 899; fireEvent.resize(window);
  expect(workspace.dataset.resizing).toBeUndefined(); expect(separator.tabIndex).toBe(-1); expect(pixels()).toBe(400);
  fireEvent.pointerMove(separator, { clientX: 800 }); expect(pixels()).toBe(400);
  width = 1000; window.innerWidth = 900; act(() => resize()); expect(pixels()).toBe(500); expect(separator.tabIndex).toBe(0);
  rerender(<Workspace active={false} />); expect(separator.tabIndex).toBe(-1); expect(frames.size).toBe(0);
  fireEvent.keyDown(separator, { key: 'Home' }); expect(pixels()).toBe(500);
  rerender(<Workspace />); expect(pixels()).toBe(500); expect(separator.tabIndex).toBe(0);
});

it('减少动态效果立即停止把手弹簧，卸载清理捕获、观察者和待执行帧', () => {
  const { separator, workspace, pixels, unmount } = setup();
  fireEvent.pointerDown(separator, { button: 0, clientX: 340 }); fireEvent.pointerMove(separator, { clientX: 500 });
  expect(frames.size).toBeGreaterThan(0);
  Object.assign(media, { matches: true }); act(() => media.dispatchEvent(new Event('change')));
  expect(frames.size).toBe(0); expect(separator.querySelector('path')!.getAttribute('d')).toBe('M12 10 Q12 40 12 70'); expect(pixels()).toBe(500);
  unmount(); expect(frames.size).toBe(0); expect(workspace.dataset.resizing).toBeUndefined(); expect(disconnect).toHaveBeenCalledOnce();
});

it('点阵保持可读状态文案，仅可见模块运行，后台、减少动态效果及卸载均停止帧', () => {
  const { rerender, unmount } = render(<CloudLoading label="正在读取文件目录…" active={false} />);
  expect(screen.getByRole('status').textContent).toBe('正在读取文件目录…'); expect(frames.size).toBe(0);
  rerender(<CloudLoading label="正在读取文件目录…" active />); expect(frames.size).toBe(1);
  tick(1000); expect(frames.size).toBe(1);
  hidden = true; fireEvent(document, new Event('visibilitychange')); expect(frames.size).toBe(0);
  hidden = false; fireEvent(document, new Event('visibilitychange')); expect(frames.size).toBe(1);
  Object.assign(media, { matches: true }); act(() => media.dispatchEvent(new Event('change'))); expect(frames.size).toBe(0);
  expect(screen.getByRole('status').textContent).toBe('正在读取文件目录…');
  Object.assign(media, { matches: false }); act(() => media.dispatchEvent(new Event('change'))); expect(frames.size).toBe(1);
  unmount(); expect(frames.size).toBe(0);
  fireEvent(document, new Event('visibilitychange')); act(() => media.dispatchEvent(new Event('change'))); expect(frames.size).toBe(0);
});
