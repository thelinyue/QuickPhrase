import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PhraseRow } from './PhraseRow';
import { dismissPhrasePreview, PhrasePreviewContent, PhrasePreviewWindow } from './PhrasePreview';
import { call } from './api';
import type { Phrase, Settings } from './types';

const previewEvent = vi.hoisted(() => ({ callback: (_: { payload: { phrase: Phrase; fontSize: number; titleColor: string; theme: string; maxHeight: number } }) => {} }));
vi.mock('./api', () => ({ call: vi.fn(async () => null), closeWindow: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (_: string, callback: typeof previewEvent.callback) => { previewEvent.callback = callback; return () => {}; }) }));
beforeEach(() => { vi.mocked(call).mockResolvedValue(null); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.clearAllMocks(); });
const phrase: Phrase = { id: 'A', rootId: 'common', groupId: null, title: '远程排查', color: '', background: '', shortcut: 'Y+C', position: 0, segments: [{ blocks: [{ kind: 'text', text: '第一行\n第二行' }] }] };
const props = { phrase, index: 0, selected: false, busy: false, settings: { showTitle: true, showPreview: true } as Settings, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: vi.fn() };
const tick = (time: number) => act(() => vi.advanceTimersByTime(time));
const requests = () => vi.mocked(call).mock.calls.filter(([command]) => command === 'show_phrase_preview');

describe('正文悬停预览', () => {
  it('快速掠过不打开，停留后只读取当前行，系统正文提示不再重复出现', () => {
    vi.useFakeTimers(); render(<PhraseRow {...props} />); const row = screen.getByRole('option');
    fireEvent.mouseEnter(row); tick(200); fireEvent.mouseLeave(row); tick(500); expect(requests()).toHaveLength(0);
    fireEvent.mouseEnter(row); tick(349); expect(requests()).toHaveLength(0); tick(1);
    fireEvent.mouseMove(row); tick(500); expect(requests()).toHaveLength(1); expect(requests()[0][1]?.id).toBe('A');
    expect(row.querySelector('.row-title')?.hasAttribute('title')).toBe(false);
    expect(row.querySelector('.row-preview')?.hasAttribute('title')).toBe(false);
  });
  it('Esc或列表滚动关闭时取消未执行计时器，移动鼠标不会马上重开', () => {
    vi.useFakeTimers(); render(<PhraseRow {...props} />); const row = screen.getByRole('option');
    fireEvent.mouseEnter(row); tick(200); dismissPhrasePreview(); fireEvent.mouseMove(row); tick(700);
    expect(requests()).toHaveLength(0);
    fireEvent.mouseLeave(row); fireEvent.mouseEnter(row); tick(350); expect(requests()).toHaveLength(1);
  });
  it('悬停发送按钮不会弹出正文，点击发送会取消旧预览且只提交发送', () => {
    vi.useFakeTimers(); const deliver = vi.fn(); render(<PhraseRow {...props} onDeliver={deliver} />);
    const row = screen.getByRole('option'), send = screen.getByRole('button');
    fireEvent.mouseEnter(send); tick(500); expect(requests()).toHaveLength(0);
    fireEvent.mouseMove(row); tick(200); fireEvent.mouseDown(send); fireEvent.click(send); tick(500);
    expect(requests()).toHaveLength(0); expect(deliver).toHaveBeenCalledExactlyOnceWith('send');
  });
  it('卸载和忙碌状态取消延时，搜索行切换后使用新话术身份', () => {
    vi.useFakeTimers(); const view = render(<PhraseRow {...props} />);
    fireEvent.mouseEnter(screen.getByRole('option')); tick(200); view.rerender(<PhraseRow {...props} busy />); tick(500); expect(requests()).toHaveLength(0);
    view.rerender(<PhraseRow {...props} phrase={{ ...phrase, id: 'B' }} query="排查" />);
    fireEvent.mouseEnter(screen.getByRole('option')); tick(350); expect(requests()[0][1]?.id).toBe('B');
    fireEvent.mouseLeave(screen.getByRole('option')); fireEvent.mouseEnter(screen.getByRole('option')); tick(100); view.unmount(); tick(500); expect(requests()).toHaveLength(1);
  });
  it('完整保留换行、图文和分段顺序，图片失效时仍保留可识别名称', () => {
    const mixed: Phrase = { ...phrase, segments: [{ blocks: [{ kind: 'text', text: '前文\n保留换行' }, { kind: 'image', path: 'sample.png', name: '说明图.png' }, { kind: 'text', text: '后文' }, { kind: 'file', path: '附件.txt', name: '附件.txt' }] }, { blocks: [{ kind: 'text', text: '第二段' }] }] };
    const view = render(<PhrasePreviewContent phrase={mixed} />);
    const first = view.container.querySelector('.phrase-preview-segment > div')!;
    expect([...first.children].map(el => el.tagName)).toEqual(['SPAN', 'IMG', 'SPAN', 'SPAN']);
    expect(first.children[0].textContent).toBe('前文\n保留换行'); expect(first.children[2].textContent).toBe('后文');
    expect(screen.getByText('第 2 段')).toBeTruthy(); expect(screen.getByText('第二段')).toBeTruthy();
    fireEvent.error(screen.getByAltText('说明图.png')); expect(screen.getByText('图片无法预览：说明图.png')).toBeTruthy();
  });
});

describe('预览窗口标题与测高', () => {
  const preview = { phrase, fontSize: 14, titleColor: '#123456', theme: 'default', maxHeight: 300 };
  const resize = { measure: () => {}, observe: vi.fn(), disconnect: vi.fn() };
  let contentHeight = 80.25;
  beforeEach(() => {
    contentHeight = 80.25;
    vi.stubGlobal('ResizeObserver', class {
      constructor(measure: () => void) { resize.measure = measure; }
      observe = resize.observe;
      disconnect = resize.disconnect;
    });
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(() => Math.floor(contentHeight));
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function(this: HTMLElement) {
      return { height: this.tagName === 'HEADER' ? 30.5 : contentHeight } as DOMRect;
    });
  });

  it.each([
    { title: '', shortcut: '' }, { title: ' \t\n', shortcut: '' },
    { title: '', shortcut: 'Y+C' }, { title: ' \t\n', shortcut: 'Y+C' },
    { title: '远程排查', shortcut: '' }, { title: '远程排查', shortcut: 'Y+C' },
  ])('标题为 "$title"、快捷键为 "$shortcut" 时只展示真实标题，正文不充当标题', async ({ title, shortcut }) => {
    const item = { ...phrase, title, shortcut };
    vi.mocked(call).mockImplementation(async command => command === 'get_phrase_preview' ? { ...preview, phrase: item } : null);
    render(<PhrasePreviewWindow />);
    const region = await screen.findByRole('region', { name: '话术正文预览' });
    const header = region.querySelector('header'), strong = region.querySelector('strong');
    const hasTitle = !!title.trim(), hasHeader = hasTitle || !!shortcut;
    expect(!!header).toBe(hasHeader);
    expect(!!strong).toBe(hasTitle);
    expect(strong?.textContent ?? '').toBe(hasTitle ? title : '');
    expect(region.querySelector('.row-shortcut')?.textContent ?? '').toBe(shortcut.replaceAll('+', ','));
    expect(region.querySelector('.phrase-preview-content')?.textContent).toBe('第一行\n第二行');
    expect(resize.observe.mock.calls.map(([target]) => target)).toEqual([
      region.querySelector('.phrase-preview-content'), ...(hasHeader ? [header] : []),
    ]);
    expect(call).toHaveBeenCalledWith('resize_phrase_preview', { id: 'A', height: hasHeader ? 113 : 83 });
    expect(item.title).toBe(title);
  });

  it('真实标题与正文相同仍保留标题、自定义颜色和背景', async () => {
    const item = { ...phrase, title: '第一行\n第二行', color: '#654321', background: '#fff0c7' };
    vi.mocked(call).mockImplementation(async command => command === 'get_phrase_preview' ? { ...preview, phrase: item } : null);
    render(<PhrasePreviewWindow />);
    const region = await screen.findByRole('region', { name: '话术正文预览' });
    expect(region.querySelector('strong')?.textContent).toBe(item.title);
    expect(region.querySelector('strong')?.style.color).toBe('rgb(101, 67, 33)');
    expect(region.querySelector('strong')?.style.background).toBe('rgb(255, 240, 199)');
  });

  it('无标题与有标题话术切换后重建测量、归零滚动并限制高度', async () => {
    const initial = { ...preview, phrase: { ...phrase, title: '', shortcut: '' } };
    vi.mocked(call).mockImplementation(async command => command === 'get_phrase_preview' ? initial : null);
    render(<PhrasePreviewWindow />);
    const region = await screen.findByRole('region', { name: '话术正文预览' });
    const body = region.querySelector('.phrase-preview-body')!;
    body.scrollTop = 50;
    await act(async () => previewEvent.callback({ payload: { ...preview, phrase: { ...phrase, id: 'B' }, maxHeight: 100 } }));
    expect(body.scrollTop).toBe(0);
    expect(region.querySelector('strong')?.textContent).toBe(phrase.title);
    expect(call).toHaveBeenCalledWith('resize_phrase_preview', { id: 'B', height: 100 });
    expect(resize.disconnect).toHaveBeenCalledTimes(1);
    body.scrollTop = 50;
    await act(async () => previewEvent.callback({ payload: { ...initial, phrase: { ...initial.phrase, id: 'C' } } }));
    expect(body.scrollTop).toBe(0);
    expect(region.querySelector('header')).toBeNull();
    expect(call).toHaveBeenCalledWith('resize_phrase_preview', { id: 'C', height: 83 });
    expect(resize.disconnect).toHaveBeenCalledTimes(2);
  });

  it('无标题图文和分段不生成媒体名称标题，内容变化后仍重新测高', async () => {
    const item: Phrase = { ...phrase, title: '', shortcut: '', segments: [{ blocks: [{ kind: 'image', path: 'sample.png', name: '说明图.png' }, { kind: 'file', path: '附件.txt', name: '附件.txt' }] }, { blocks: [{ kind: 'text', text: '第二段\n保留换行' }] }] };
    vi.mocked(call).mockImplementation(async command => command === 'get_phrase_preview' ? { ...preview, phrase: item, maxHeight: 120 } : null);
    render(<PhrasePreviewWindow />);
    const region = await screen.findByRole('region', { name: '话术正文预览' });
    expect(region.querySelector('header')).toBeNull();
    expect(screen.getByAltText('说明图.png')).toBeTruthy();
    expect(screen.getByText('附件.txt')).toBeTruthy();
    expect(screen.getByText('第 2 段')).toBeTruthy();
    expect(region.querySelectorAll('.phrase-preview-segment')[1].textContent).toBe('第 2 段第二段\n保留换行');
    contentHeight = 180.75;
    act(() => resize.measure());
    expect(call).toHaveBeenLastCalledWith('resize_phrase_preview', { id: 'A', height: 120 });
  });
});
