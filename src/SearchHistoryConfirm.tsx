import { useEffect, useRef, useState } from 'react';
import { call } from './api';
import { Icon } from './ui';

/** 窗口创建时固定库身份；真正的有效性和重复提交由 Rust 再次校验。 */
export function SearchHistoryConfirm() {
  const [libraryId] = useState(() => new URLSearchParams(location.hash.split('?')[1]).get('libraryId') || '');
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  const pending = useRef(false), cancelRef = useRef<HTMLButtonElement>(null);
  const close = async () => {
    if (pending.current) return;
    try { await call('close_search_history_confirm'); } catch (e) { setError(String(e)); }
  };
  useEffect(() => {
    cancelRef.current?.focus();
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.isComposing) { e.preventDefault(); void close(); } };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
  }, []);
  const submit = async () => {
    if (pending.current) return;
    pending.current = true; setBusy(true); setError('');
    try {
      await call('clear_search_history', { libraryId });
      await call('close_search_history_confirm');
    } catch (e) { setError(`搜索记录清空失败：${String(e)}`); }
    finally { pending.current = false; setBusy(false); }
  };
  return <section className="search-history-confirm" aria-label="清空历史搜索确认" aria-busy={busy}>
    <header><span data-tauri-drag-region>提示</span><button aria-label="关闭" disabled={busy} onClick={() => void close()}><Icon name="close" /></button></header>
    <main><h2>清空提示</h2><p>是否清空历史搜索？</p>{error && <p role="alert">{error}</p>}</main>
    <footer><button className="secondary-button" ref={cancelRef} disabled={busy} onClick={() => void close()}>取消</button><button className="primary-button" disabled={busy} onClick={() => void submit()}>确定</button></footer>
  </section>;
}
