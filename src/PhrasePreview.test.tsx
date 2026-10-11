import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PhraseRow } from './PhraseRow';
import { dismissPhrasePreview, PhrasePreviewContent } from './PhrasePreview';
import { call } from './api';
import type { Phrase, Settings } from './types';

vi.mock('./api', () => ({ call: vi.fn(async () => null), closeWindow: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
afterEach(() => { cleanup(); vi.useRealTimers(); vi.clearAllMocks(); });
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
