import { useCallback, useRef, useState } from 'react';

export type NoticeKind = 'success' | 'error' | 'warning' | 'progress';
export interface Notice { id: number; kind: NoticeKind; message: string; title?: string }

/** 普通成功不能覆盖尚未关闭的错误；相同错误只保留一份，避免后台重复通知刷屏。 */
export function useNotices() {
  const nextId = useRef(0);
  const [items, setItems] = useState<Notice[]>([]);
  const show = useCallback((message: string, kind: NoticeKind = 'error', title?: string) => {
    if (!message) return;
    const notice = { id: ++nextId.current, kind, message, title };
    setItems(previous => {
      if (kind === 'success' || kind === 'progress') {
        return previous.some(item => item.kind === 'error' || item.kind === 'warning') ? previous : [notice];
      }
      const important = previous.filter(item => item.kind === 'error' || item.kind === 'warning');
      if (important.some(item => item.kind === kind && item.message === message && item.title === title)) return important;
      if (kind === 'warning') {
        const firstError = important.findIndex(item => item.kind === 'error');
        if (firstError >= 0) return [...important.slice(0, firstError), notice, ...important.slice(firstError)];
      }
      return [...important, notice];
    });
  }, []);
  const error = useCallback((message: string) => show(message, 'error'), [show]);
  const success = useCallback((message: string) => show(message, 'success'), [show]);
  const progress = useCallback((message: string) => show(message, 'progress'), [show]);
  const dismiss = useCallback((id: number) => setItems(previous => previous.filter(item => item.id !== id)), []);
  const clear = useCallback(() => setItems([]), []);
  return { current: items[0], count: items.length, show, error, success, progress, dismiss, clear };
}
