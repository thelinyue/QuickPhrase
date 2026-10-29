import { useCallback, useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';

export interface PendingDeliveryNotice { id: number; recipient: 'main' | 'common-dock'; status: 'failed' | 'interrupted'; message: string }

/** 原生层保留当前会话回执；订阅后取快照，并拒绝换库或较旧读取的迟到结果。 */
export function usePendingDeliveryNotices(scope: string, recipient: 'main' | 'common-dock') {
  const [snapshot, setSnapshot] = useState<{ scope: string; items: PendingDeliveryNotice[] }>({ scope: '', items: [] });
  const [problem, setProblem] = useState<{ scope: string; message: string } | null>(null);
  const refreshRef = useRef<() => Promise<void>>(async () => {});
  const subscribeRef = useRef<() => void>(() => {});
  const currentScope = useRef(scope); currentScope.current = scope;
  useEffect(() => {
    let disposed = false, subscribing = false, sequence = 0, stop: (() => void) | undefined;
    const reportProblem = () => {
      if (!disposed && currentScope.current === scope) setProblem({ scope, message: '无法读取或接收投递结果；如刚进行过插入或发送，请先检查目标，避免重复操作。' });
    };
    const refresh = async () => {
      const request = ++sequence;
      try {
        const items = await call<PendingDeliveryNotice[]>('pending_delivery_notices');
        if (!disposed && currentScope.current === scope && request === sequence) setSnapshot({ scope, items: Array.isArray(items) ? items : [] });
      } catch { if (request === sequence) reportProblem(); }
    };
    refreshRef.current = refresh;
    const subscribe = () => {
      if (disposed || subscribing || stop) return;
      subscribing = true;
      void listen('delivery-notices-changed', () => void refresh()).then(fn => { subscribing = false; if (disposed) fn(); else { stop = fn; void refresh(); } }).catch(() => { subscribing = false; reportProblem(); if (!disposed) void refresh(); });
    };
    subscribeRef.current = subscribe; subscribe();
    const visible = () => { subscribe(); void refresh(); };
    window.addEventListener('focus', visible); document.addEventListener('visibilitychange', visible);
    return () => { disposed = true; ++sequence; if (refreshRef.current === refresh) refreshRef.current = async () => {}; if (subscribeRef.current === subscribe) subscribeRef.current = () => {}; stop?.(); window.removeEventListener('focus', visible); document.removeEventListener('visibilitychange', visible); };
  }, [scope]);
  const dismiss = useCallback(async (id: number) => {
    await call('dismiss_delivery_notice', { id });
    setSnapshot(previous => previous.scope === currentScope.current ? { ...previous, items: previous.items.filter(item => item.id !== id) } : previous);
    await refreshRef.current();
  }, []);
  const items = snapshot.scope === scope ? snapshot.items.filter(item => item.recipient === recipient) : [];
  return { items: [...items.filter(item => item.status === 'interrupted'), ...items.filter(item => item.status !== 'interrupted')], dismiss, problem: problem?.scope === scope ? problem.message : '', dismissProblem: () => { setProblem(null); subscribeRef.current(); }, refresh: () => refreshRef.current() };
}
