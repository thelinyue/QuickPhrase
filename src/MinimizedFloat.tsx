import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';

/** 最小化恢复入口只读取主题，不挂载话术库、输入监测或发送服务。
 * 超过阈值才交给系统拖动；原生拖动会取消 WebView 的指针捕获，取消后仍须吞掉本次点击。
 */
export function MinimizedFloat() {
  const press = useRef<{ id: number; x: number; y: number; dragged: boolean; released: boolean } | null>(null);
  const [pressed, setPressed] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    let disposed = false, stop: (() => void) | undefined, request = 0;
    const refresh = async () => {
      const current = ++request;
      try { const theme = await invoke<string>('get_minimized_float_theme'); if (!disposed && request === current) document.documentElement.dataset.theme = theme; }
      catch { if (!disposed) setError('外观暂时无法更新，仍可单击打开闪语。'); }
    };
    void listen('library-changed', () => void refresh()).then(fn => { if (disposed) fn(); else { stop = fn; void refresh(); } }).catch(() => void refresh());
    return () => { disposed = true; stop?.(); };
  }, []);
  function down(e: PointerEvent<HTMLButtonElement>) {
    if (e.button !== 0) return;
    press.current = { id: e.pointerId, x: e.clientX, y: e.clientY, dragged: false, released: false };
    e.currentTarget.setPointerCapture(e.pointerId); setPressed(true);
  }
  function move(e: PointerEvent<HTMLButtonElement>) {
    const start = press.current;
    if (!start || start.id !== e.pointerId || start.dragged || Math.hypot(e.clientX - start.x, e.clientY - start.y) <= 6) return;
    start.dragged = true; setPressed(false);
    e.currentTarget.releasePointerCapture(e.pointerId);
    void getCurrentWindow().startDragging().catch(() => setError('悬浮窗暂时无法拖动，请重试。'));
  }
  return <button className={`minimized-float${pressed ? ' pressed' : ''}`} aria-label="打开闪语" title={error || '单击打开闪语，拖动调整位置'}
    onContextMenu={e => e.preventDefault()} onPointerDown={down} onPointerMove={move}
    onPointerUp={() => { if (press.current) press.current.released = true; setPressed(false); }} onPointerCancel={() => { if (press.current) press.current.dragged = true; setPressed(false); }}
    onLostPointerCapture={() => { if (press.current && !press.current.released) press.current.dragged = true; setPressed(false); }} onClick={() => {
      if (press.current?.dragged) return;
      press.current = null; setPressed(false);
      void invoke('window_action', { action: 'show' }).catch(() => setError('闪语暂时无法打开，请通过托盘或快捷键重试。'));
    }}><img src="/shanyu.png" width="32" height="32" alt="" draggable={false} /><span>闪语</span></button>;
}
