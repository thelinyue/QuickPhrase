import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import { labelOf, textOf, type ShortcutCandidates } from './types';
import { phraseTitleColor } from './ui';

/** 原生监听拥有候选状态与键盘操作；无激活窗口只绘制快照，保持目标输入焦点。 */
export function ShortcutCandidatesWindow({ titleColor }: { titleColor?: string } = {}) {
  const [payload, setPayload] = useState<ShortcutCandidates | null>(null);
  const [error, setError] = useState(''); const root = useRef<HTMLElement>(null); const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    let disposed = false; let off: (() => void) | undefined; let updated = false;
    void listen<ShortcutCandidates | null>('shortcut-candidates', e => { updated = true; if (!disposed) { setPayload(e.payload); setError(''); } })
      .then(un => { if (disposed) un(); else { off = un; void call<ShortcutCandidates | null>('get_shortcut_candidates').then(value => { if (!disposed && !updated) setPayload(value); }).catch(e => { if (!disposed) setError(String(e)); }); } }).catch(e => setError(String(e)));
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => {
    if (!payload) return;
    list.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
  }, [payload]);
  // 测量整组内容，不随上下选择改变外框；自然宽度不受当前窗口或省略号影响。
  const measureKey = payload ? JSON.stringify([payload.sessionId, payload.layoutKey, payload.maxWidth, payload.maxHeight, payload.phrases]) : '';
  useLayoutEffect(() => {
    const element = root.current;
    if (!payload || !element) return;
    let disposed = false, frame = 0, last = '';
    const measure = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        if (disposed) return;
        const rows = Array.from(element.querySelectorAll<HTMLElement>('[data-candidate]'));
        let natural = 240; const widths = new Map<HTMLElement, number>();
        for (const row of rows) {
          const content = row.querySelector<HTMLElement>('.candidate-content')!;
          const old = content.style.cssText;
          content.style.cssText = 'position:absolute;width:max-content;max-width:none;visibility:hidden';
          content.classList.add('candidate-measuring');
          const style = getComputedStyle(row), number = row.querySelector<HTMLElement>('.candidate-number')!;
          const extra = ['paddingLeft','paddingRight','borderLeftWidth','borderRightWidth','columnGap'].reduce((sum, key) => sum + (parseFloat(style[key as keyof CSSStyleDeclaration] as string) || 0), 0) + number.getBoundingClientRect().width + 2 + (list.current ? list.current.offsetWidth - list.current.clientWidth : 0);
          widths.set(content, content.scrollWidth + extra);
          natural = Math.max(natural, content.scrollWidth + extra);
          content.classList.remove('candidate-measuring'); content.style.cssText = old;
        }
        const width = Math.ceil(Math.min(natural, payload.maxWidth));
        element.style.width = `${width}px`;
        widths.forEach((naturalWidth, content) => content.classList.toggle('candidate-crowded', naturalWidth > width));
        const height = Math.ceil(Math.min(payload.maxHeight, rows.reduce((sum, row) => sum + row.getBoundingClientRect().height, 0) + element.querySelector('header')!.getBoundingClientRect().height + element.querySelector('footer')!.getBoundingClientRect().height + 2));
        const key = `${width}:${height}`;
        if (key === last) return; last = key;
        void call('resize_shortcut_candidates', { sessionId: payload.sessionId, layoutKey: payload.layoutKey, width, height }).catch(e => { if (!disposed) setError(String(e)); });
      });
    };
    const observer = new MutationObserver(measure);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['style'] });
    window.addEventListener('resize', measure);
    void document.fonts?.ready.then(() => { if (!disposed) measure(); }); measure();
    return () => { disposed = true; cancelAnimationFrame(frame); observer.disconnect(); window.removeEventListener('resize', measure); };
  }, [measureKey]);
  if (!payload) return <div className="shortcut-candidates-window" />;
  return <section ref={root} className="shortcut-candidates-window" aria-label="快捷键候选" onContextMenu={e => e.preventDefault()}>
    <header><strong title={payload.shortcut}>{payload.shortcut}</strong><span>{payload.phrases.length} 条候选</span><button aria-label="取消候选" tabIndex={-1} onMouseDown={e => e.preventDefault()} onClick={() => void call('cancel_shortcut_candidates').catch(e => setError(String(e)))}>×</button></header>
    <div ref={list} className="shortcut-candidates-list" role="listbox" aria-label="候选话术">{payload.phrases.map((phrase, index) => {
      // 仅压缩显示用的空白；完整正文保留在提示中，不改变原生发送载荷。
      const text = textOf(phrase), title = phrase.title.replace(/\s+/g, ' ').trim();
      const summary = text.replace(/\s+/g, ' ').trim() || labelOf({ ...phrase, title: '' });
      const hasContent = !!text.trim() || phrase.segments.some(segment => segment.blocks.some(block => block.kind !== 'text'));
      const preview = hasContent && summary !== title ? summary : '';
      return <button key={phrase.id} role="option" aria-selected={index === payload.selectedIndex} data-candidate={phrase.id} tabIndex={-1} title={[title, text || summary].filter(Boolean).join('\n')} onMouseDown={e => e.preventDefault()} onClick={() => void call('select_shortcut_candidate', { index }).catch(e => setError(String(e)))}>
        <span className="candidate-number">{index < 10 ? (index + 1) % 10 : ''}</span><span className="candidate-content">
          {title && <span className={`candidate-label${preview ? ' has-preview' : ''}`} style={{ color: phraseTitleColor(phrase.color, titleColor) }}>{title}</span>}
          {preview && <span className="candidate-preview">{preview}</span>}
          {!title && !preview && <span className="candidate-label">{labelOf(phrase)}</span>}
        </span>
      </button>;
    })}</div>
    <footer className={error ? 'candidate-error' : undefined}>{error || <><span>数字 / 空格 插入</span><span>↑↓ 选择</span><span>Enter 发送</span><span>Esc 取消</span></>}</footer>
  </section>;
}
