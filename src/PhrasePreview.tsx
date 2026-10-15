import { useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { convertFileSrc } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import { Icon, phraseTitleColor } from './ui';
import { contentKey, labelOf, type Phrase } from './types';

interface PreviewPayload { phrase: Phrase; fontSize: number; titleColor: string; theme: string; maxHeight: number }
type HoverState = { timer?: ReturnType<typeof setTimeout>; triggered: boolean };
let activeHover: HoverState | undefined;
/** 源窗口点击、滚动或Esc会同时取消延时请求，避免刚关闭后又被旧计时器打开。 */
export function dismissPhrasePreview() {
  if (activeHover) { clearTimeout(activeHover.timer); activeHover.timer = undefined; activeHover.triggered = true; }
  void call('hide_phrase_preview').catch(() => {});
}

/** 行高亮即时反馈，正文只在停留350毫秒后打开；离开行取消未执行请求，浮层的跨窗停留由原生层判断。 */
export function usePhraseHover(phrase: Phrase | undefined, busy: boolean) {
  const hover = useRef<HoverState>({ triggered: false });
  const cancel = () => { clearTimeout(hover.current.timer); hover.current.timer = undefined; };
  const hide = () => { if (phrase) void call('hide_phrase_preview', { id: phrase.id }).catch(() => {}); };
  useEffect(() => {
    if (busy) { cancel(); if (hover.current.triggered) hide(); }
    return () => { cancel(); if (hover.current.triggered) hide(); hover.current.triggered = false; if (activeHover === hover.current) activeHover = undefined; };
  }, [phrase?.id, busy]);
  const enter = (e: MouseEvent<HTMLDivElement>) => {
    if (!phrase || busy) return;
    if ((e.target as HTMLElement).closest('.row-send')) { cancel(); if (hover.current.triggered) hide(); hover.current.triggered = false; return; }
    if (hover.current.timer || hover.current.triggered || e.buttons) return;
    const row = e.currentTarget;
    if (activeHover && activeHover !== hover.current) clearTimeout(activeHover.timer);
    activeHover = hover.current;
    hover.current.timer = setTimeout(() => {
      hover.current.timer = undefined; hover.current.triggered = true;
      const rect = row.getBoundingClientRect();
      void call('show_phrase_preview', { id: contentKey(phrase), anchor: { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom } }).catch(() => {});
    }, 350);
  };
  return { onMouseEnter: enter, onMouseMove: enter, onMouseLeave: () => { cancel(); hover.current.triggered = false; if (activeHover === hover.current) activeHover = undefined; }, onMouseDown: () => { cancel(); hover.current.triggered = true; hide(); } };
}

function PreviewImage({ path, name }: { path: string; name: string }) {
  const [failed, setFailed] = useState(false);
  return failed ? <span className="phrase-preview-file">图片无法预览：{name}</span> : <img src={convertFileSrc(path)} alt={name} onError={() => setFailed(true)} />;
}

/** 与编辑器使用同一份有序分段，完整呈现文字、图片与文件名，不拼接成丢失换行的摘要。 */
export function PhrasePreviewContent({ phrase }: { phrase: Phrase }) {
  return <>{phrase.segments.map((segment, index) => <div className="phrase-preview-segment" key={index}>
    {phrase.segments.length > 1 && <small>第 {index + 1} 段</small>}
    <div>{segment.blocks.map((block, i) => block.kind === 'text' ? <span key={i}>{block.text}</span> : block.kind === 'image' ? <PreviewImage key={`${i}-${block.path}`} path={block.path} name={block.name} /> : <span className="phrase-preview-file" key={i}><Icon name="attachment" />{block.name}</span>)}</div>
  </div>)}</>;
}

/** 鼠标附近的轻量提示按内容收紧高度，长文仅滚动正文；不激活窗口，移开鼠标或Esc收起。 */
export function PhrasePreviewWindow() {
  const [preview, setPreview] = useState<PreviewPayload | null>(null);
  const root = useRef<HTMLElement>(null);
  useEffect(() => {
    let disposed = false, received = false; let unlisten: (() => void) | undefined;
    void listen<PreviewPayload>('phrase-preview', e => { received = true; setPreview(e.payload); }).then(async fn => {
      if (disposed) { fn(); return; } unlisten = fn;
      const current = await call<PreviewPayload | null>('get_phrase_preview');
      if (!disposed && !received) setPreview(current);
    }).catch(() => {});
    return () => { disposed = true; unlisten?.(); };
  }, []);
  useLayoutEffect(() => {
    if (!preview || !root.current) return;
    document.documentElement.dataset.theme = preview.theme;
    document.documentElement.style.setProperty('--phrase-size', `${preview.fontSize}px`);
    const element = root.current;
    const header = element.querySelector('header')!;
    element.querySelector('.phrase-preview-body')!.scrollTop = 0;
    const measure = () => {
      const content = element.querySelector<HTMLElement>('.phrase-preview-content')!;
      // 向上取整保留行高小数；标题换行和图片加载同样触发重新测量。
      const height = Math.ceil(Math.max(content.scrollHeight, content.getBoundingClientRect().height) + header.getBoundingClientRect().height + 2);
      void call('resize_phrase_preview', { id: preview.phrase.id, height: Math.min(height, preview.maxHeight) }).catch(() => {});
    };
    const observer = new ResizeObserver(measure); observer.observe(element.querySelector('.phrase-preview-content')!); observer.observe(header); measure();
    return () => observer.disconnect();
  }, [preview]);
  if (!preview) return null;
  const phrase = preview.phrase;
  return <section className="phrase-preview-window" ref={root} role="region" aria-label="话术正文预览">
    <header><strong style={{ color: phraseTitleColor(phrase.color, preview.titleColor), background: phrase.background || undefined }}>{labelOf(phrase)}</strong>
      {phrase.shortcut && <span className="row-shortcut">{phrase.shortcut.replaceAll('+', ',')}</span>}
    </header>
    <div className="phrase-preview-body"><div className="phrase-preview-content"><PhrasePreviewContent phrase={phrase} /></div></div>
  </section>;
}
