import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import { labelOf, textOf, type ShortcutCandidates } from './types';

/** 原生监听拥有候选状态与键盘操作；无激活窗口只绘制快照，保持目标输入焦点。 */
export function ShortcutCandidatesWindow() {
  const [payload, setPayload] = useState<ShortcutCandidates | null>(null);
  const [error, setError] = useState(''); const list = useRef<HTMLDivElement>(null);
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
  const open = !!payload;
  useEffect(() => {
    if (!open) return;
    void call('resize_shortcut_candidates').catch(e => setError(String(e)));
  }, [open, payload?.shortcut, payload?.target.hwnd, payload?.target.focus, payload?.phrases.length]);
  if (!payload) return <div className="shortcut-candidates-window" />;
  return <section className="shortcut-candidates-window" aria-label="快捷键候选" onContextMenu={e => e.preventDefault()}>
    <header><strong>{payload.shortcut}</strong><span>{payload.phrases.length} 条候选</span><button aria-label="取消候选" tabIndex={-1} onMouseDown={e => e.preventDefault()} onClick={() => void call('cancel_shortcut_candidates').catch(e => setError(String(e)))}>×</button></header>
    <div ref={list} className="shortcut-candidates-list" role="listbox" aria-label="候选话术">{payload.phrases.map((phrase, index) => <button key={phrase.id} role="option" aria-selected={index === payload.selectedIndex} data-candidate={phrase.id} tabIndex={-1} title={textOf(phrase)} onMouseDown={e => e.preventDefault()} onClick={() => void call('select_shortcut_candidate', { index }).catch(e => setError(String(e)))}>
      <span className="candidate-number">{index < 10 ? (index + 1) % 10 : ''}</span><span className="candidate-label">{labelOf(phrase)}</span>
    </button>)}</div>
    <footer>{error || '数字 / 空格 插入　↑↓ 选择　Enter 发送　Esc 取消'}</footer>
  </section>;
}
