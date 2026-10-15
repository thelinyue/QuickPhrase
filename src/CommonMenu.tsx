import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, openWindow } from './api';
import type { CommonPhrase, Library } from './types';

interface MenuPayload { requestId: string; libraryId: string; phraseId: string | null; canSort: boolean; theme: string }
interface MenuAction { requestId: string; action: 'add' | 'edit' | 'delete' | 'sort' | null; restore: boolean }
// 只比较有序短语及其修订；无关话术刷新或修订映射的键顺序变化不应关闭管理菜单。
const menuContents = (library: Library) => JSON.stringify([library.libraryId, library.commonPhrases.map(p => [p, library.privateRevisions?.[`common:${p.id}`]])]);

/** 菜单只携带操作对象；来源窗口复核当前库和内容，再复用既有编辑、删除及排序流程。 */
export function useCommonMenu(library: Library, onError: (message: string) => void) {
  const current = useRef(library); current.current = library;
  const error = useRef(onError); error.current = onError;
  const opened = useRef<{ payload: MenuPayload; trigger: HTMLElement; contents: string } | null>(null);
  const ready = useRef<Promise<unknown>>(Promise.resolve());
  const contents = menuContents(library);
  useEffect(() => {
    let disposed = false, off: (() => void) | undefined;
    ready.current = listen<MenuAction>('common-menu-action', e => {
      const context = opened.current;
      if (!context || e.payload.requestId !== context.payload.requestId) return;
      opened.current = null;
      if (e.payload.restore && context.trigger.isConnected) context.trigger.focus();
      if (!e.payload.action) return;
      const execute = async () => {
        const valid = () => menuContents(current.current) === context.contents;
        if (!valid()) throw new Error('短语已变化，请重新打开菜单。');
        const phrase = current.current.commonPhrases.find(p => p.id === context.payload.phraseId);
        switch (e.payload.action) {
          case 'add': await openWindow('common-editor'); break;
          case 'sort': if (current.current.commonPhrases.length > 1) await openWindow('common-sort'); break;
          case 'edit': if (phrase) await openWindow('common-editor', phrase.id); break;
          case 'delete':
            if (phrase && await confirm(`删除「${phrase.text}」？`, { title: '删除常用短语', kind: 'warning' })) {
              if (!valid()) throw new Error('短语已变化，请重新确认删除。');
              await call('delete_item', { id: phrase.id, kind: 'common', libraryId: context.payload.libraryId });
            }
            break;
        }
      };
      void execute().catch(e => error.current(String(e)));
    }).then(fn => { if (disposed) fn(); else off = fn; }).catch(e => error.current(String(e)));
    return () => {
      disposed = true; off?.();
      if (opened.current) void call('close_common_menu', { requestId: opened.current.payload.requestId, restore: false }).catch(() => {});
      opened.current = null;
    };
  }, []);
  useEffect(() => {
    if (opened.current && opened.current.contents !== contents) {
      void call('close_common_menu', { requestId: opened.current.payload.requestId, restore: false }).catch(e => error.current(String(e)));
      opened.current = null;
    }
  }, [contents]);
  return async (trigger: HTMLElement, x: number, y: number, phrase?: CommonPhrase) => {
    await ready.current;
    const payload: MenuPayload = { requestId: crypto.randomUUID(), libraryId: current.current.libraryId!, phraseId: phrase?.id || null, canSort: current.current.commonPhrases.length > 1, theme: current.current.settings.theme };
    opened.current = { payload, trigger, contents };
    try { await call('open_common_menu', { payload, x, y }); }
    catch (e) { if (opened.current?.payload.requestId === payload.requestId) opened.current = null; throw e; }
  };
}

/** 独立轻量窗口只渲染菜单，不读取话术库；键盘和鼠标共用同一组允许的管理动作。 */
export function CommonMenuWindow() {
  const [payload, setPayload] = useState<MenuPayload | null>(null);
  const [error, setError] = useState(''); const root = useRef<HTMLElement>(null); const pending = useRef(false);
  useEffect(() => {
    let disposed = false, received = false, off: (() => void) | undefined;
    void listen<MenuPayload>('common-menu', e => { received = true; pending.current = false; setError(''); setPayload(e.payload); }).then(async fn => {
      if (disposed) { fn(); return; } off = fn;
      const value = await call<MenuPayload | null>('get_common_menu');
      if (!disposed && !received) setPayload(value);
    }).catch(e => { if (!disposed) setError(String(e)); });
    return () => { disposed = true; off?.(); };
  }, []);
  useLayoutEffect(() => {
    if (!payload) return;
    document.documentElement.dataset.theme = payload.theme || 'default';
    root.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  }, [payload]);
  const close = () => payload && call('close_common_menu', { requestId: payload.requestId, restore: true }).catch(e => setError(String(e)));
  const choose = async (action: NonNullable<MenuAction['action']>) => {
    if (!payload || pending.current) return;
    pending.current = true;
    try { await call('choose_common_menu', { requestId: payload.requestId, action }); }
    catch (e) { pending.current = false; setError(String(e)); }
  };
  const items: { action: NonNullable<MenuAction['action']>; text: string; disabled?: boolean }[] = [
    { action: 'add', text: '新增个人短语' },
    ...(payload?.phraseId ? [{ action: 'edit' as const, text: '修改这个短语' }, { action: 'delete' as const, text: '删除这个短语' }] : []),
    { action: 'sort', text: '快速排序短语', disabled: !payload?.canSort },
  ];
  return <section ref={root} className="common-menu-window" role="menu" aria-label="常用短语菜单" onContextMenu={e => e.preventDefault()} onKeyDown={e => {
    if (e.nativeEvent.isComposing) return;
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); void close(); }
    const buttons = [...root.current!.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')];
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) {
      e.preventDefault(); const next = e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : (index + (e.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }
  }}>{payload && items.map(item => <button key={item.action} role="menuitem" disabled={item.disabled} onClick={() => void choose(item.action)}>{item.text}</button>)}{error && <span className="common-menu-error" role="alert" title={error}>{error}</span>}</section>;
}
