import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import { Icon } from './ui';

/** 搜索结果在用户触发操作时固定关键词和库身份，异步投递不得借用后来的搜索输入。 */
export interface SearchUsage { query: string; libraryId: string }

/** 两处窗口只保存关键词快照。先订阅再读取，刷新序号与库身份共同阻止旧响应覆盖新视图。 */
export function useSearchHistory(libraryId: string | undefined, onError: (message: string) => void) {
  const [snapshot, setSnapshot] = useState<{ libraryId: string; items: string[] }>({ libraryId: '', items: [] });
  const [clearing, setClearing] = useState(false);
  const current = useRef(libraryId); current.current = libraryId;
  const error = useRef(onError); error.current = onError;
  const refreshRef = useRef<() => Promise<void>>(async () => {});
  const clearingRef = useRef(false);
  useEffect(() => {
    let disposed = false, sequence = 0, stop: (() => void) | undefined;
    const refresh = async () => {
      if (!libraryId) return;
      const request = ++sequence;
      try {
        const items = await call<string[]>('get_search_history', { libraryId });
        if (!disposed && request === sequence) setSnapshot({ libraryId, items: items || [] });
      } catch { if (!disposed && request === sequence) error.current('搜索记录无法读取，请稍后重试。'); }
    };
    refreshRef.current = refresh;
    void listen<string>('search-history-changed', e => { if (!disposed && e.payload === libraryId) void refresh(); }).then(fn => {
      if (disposed) fn(); else { stop = fn; void refresh(); }
    }).catch(() => { if (!disposed) { error.current('搜索记录刷新通知无法订阅。'); void refresh(); } });
    return () => { disposed = true; stop?.(); };
  }, [libraryId]);

  const record = async (usage: SearchUsage) => {
    if (!usage.query.trim()) return;
    try { await call('record_search_history', { ...usage }); }
    catch { if (current.current === usage.libraryId) error.current('已提交操作，但搜索记录保存失败。'); return; }
    if (current.current === usage.libraryId) await refreshRef.current();
  };
  const clear = async () => {
    if (!libraryId || clearingRef.current) return;
    clearingRef.current = true; setClearing(true);
    try {
      await call('clear_search_history', { libraryId });
      if (current.current === libraryId) await refreshRef.current();
    } catch { if (current.current === libraryId) error.current('搜索记录清空失败，请重试。'); }
    finally { clearingRef.current = false; setClearing(false); }
  };
  return { items: snapshot.libraryId === libraryId ? snapshot.items : [], clearing, record, clear };
}

/** 关键词按钮只重新搜索；使用独立清空入口，避免与清空当前搜索混淆。 */
export function SearchHistoryBar({ items, clearing, onSelect, onClear }: {
  items: string[]; clearing: boolean; onSelect: (query: string) => void; onClear: () => void;
}) {
  if (!items.length) return null;
  return <section className="search-history" aria-label="搜索记录">
    <div className="search-history-items">{items.map(query => <button type="button" key={query} title={query} onClick={() => onSelect(query)}>{query}</button>)}</div>
    <button type="button" className="clear-search-history" aria-label="清空搜索记录" title="清空搜索记录" disabled={clearing} onClick={onClear}><Icon name="trash" /></button>
  </section>;
}
