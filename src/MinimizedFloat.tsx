import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { Menu } from '@tauri-apps/api/menu';
import { LogicalPosition } from '@tauri-apps/api/dpi';

/** 最小化恢复入口只读取主题，不挂载话术库、输入监测或发送服务。
 * 超过阈值才交给系统拖动；原生拖动会取消 WebView 的指针捕获，取消后仍须吞掉本次点击。
 */
export function MinimizedFloat() {
  const press = useRef<{ id: number; x: number; y: number; dragged: boolean; released: boolean } | null>(null);
  const [pressed, setPressed] = useState(false);
  const [error, setError] = useState('');
  const menu = useRef<Menu | null>(null);
  const menuOpening = useRef(false);
  const open = () => invoke('window_action', { action: 'show' }).catch(() => setError('闪语暂时无法打开，请通过托盘或快捷键重试。'));
  useEffect(() => () => { void menu.current?.close(); }, []);
  useEffect(() => {
    let disposed = false, stop: (() => void) | undefined, request = 0;
    const refresh = async () => {
      const current = ++request;
      try { const theme = await invoke<string>('get_minimized_float_theme'); if (!disposed && request === current) document.documentElement.dataset.theme = theme; }
      catch { if (!disposed) setError('外观暂时无法更新，仍可双击打开闪语。'); }
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
  return <button className={`minimized-float${pressed ? ' pressed' : ''}`} aria-label="打开闪语" title={error || '双击打开闪语，拖动调整位置，右键显示菜单'}
    onContextMenu={e => {
      e.preventDefault(); setPressed(false); press.current = null;
      // WebView 的原生右键消息可能重复到达；创建及展示期间只允许一个系统菜单。
      if (menuOpening.current) return;
      menuOpening.current = true;
      const position = new LogicalPosition(e.clientX, e.clientY);
      // 系统菜单可超出小条边界；隐藏交给原生队列，不能仅隐藏 WebView 后被轮询重新显示。
      void (async () => {
        await menu.current?.close();
        const hide = (permanent: boolean) => { void invoke('hide_minimized_float', { permanent }).catch(e => setError(`悬浮图标暂时无法隐藏：${String(e)}`)); };
        menu.current = await Menu.new({ items: [
          { text: '隐藏悬浮图标', action: () => hide(false) },
          { text: '永久隐藏悬浮图标', action: () => hide(true) },
          { text: '打开主界面（双击）', action: () => { void open(); } },
        ] });
        await menu.current.popup(position);
      })().catch(() => setError('悬浮窗菜单暂时无法打开，请重试。')).finally(() => { menuOpening.current = false; });
    }} onPointerDown={down} onPointerMove={move}
    onPointerUp={() => { if (press.current) press.current.released = true; setPressed(false); }} onPointerCancel={() => { if (press.current) press.current.dragged = true; setPressed(false); }}
    onLostPointerCapture={() => { if (press.current && !press.current.released) press.current.dragged = true; setPressed(false); }} onDoubleClick={() => {
      if (press.current?.dragged) return;
      press.current = null; setPressed(false);
      void open();
    }}><img src="/shanyu.png" width="24" height="24" alt="" draggable={false} /><span>闪语</span></button>;
}
