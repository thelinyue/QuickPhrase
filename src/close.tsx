import { createContext, useContext, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, type SyntheticEvent } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { confirm } from '@tauri-apps/plugin-dialog';

interface CloseGuard { busy?: () => boolean; dirty?: () => boolean; beforeClose?: () => Promise<boolean> }
const askDiscard = () => confirm('放弃未保存的修改？', { title: '未保存的修改', kind: 'warning', okLabel: '放弃修改', cancelLabel: '继续编辑' });
const CloseContext = createContext<{ enabled: boolean; register: (read: () => CloseGuard) => () => void; discard: () => Promise<boolean> } | null>(null);

/** 每个独立窗口只装一个原生监听。Tauri 的多个 onCloseRequested 会各自决定 destroy，
 * 不能靠另一个监听的 preventDefault 保护草稿；子表单只登记状态，不负责销毁宿主。 */
export function WindowCloseScope({ children, enabled = true }: { children: ReactNode; enabled?: boolean }) {
  const guards = useRef(new Set<() => CloseGuard>()), confirming = useRef(false), closing = useRef(false), accepted = useRef(false);
  const [error, setError] = useState('');
  const context = useMemo(() => ({
    enabled,
    register(read: () => CloseGuard) { guards.current.add(read); return () => { guards.current.delete(read); }; },
    async discard() {
      if (confirming.current) return false;
      confirming.current = true;
      try { return await askDiscard(); } finally { confirming.current = false; }
    },
  }), [enabled]);
  useEffect(() => {
    if (!enabled) return;
    let disposed = false, stop: (() => void) | undefined;
    const busy = () => [...guards.current].some(read => read().busy?.());
    void getCurrentWindow().onCloseRequested(async event => {
      if (accepted.current) { accepted.current = false; if (busy()) event.preventDefault(); return; }
      const current = [...guards.current].map(read => read());
      if (!closing.current && !confirming.current && !busy() && !current.some(g => g.dirty?.() || g.beforeClose)) return;
      event.preventDefault();
      if (closing.current || confirming.current || busy()) return;
      closing.current = true; setError('');
      const focus = document.activeElement as HTMLElement | null;
      try {
        if (current.some(g => g.dirty?.()) && !await context.discard()) return;
        if (disposed || busy()) return;
        for (const read of guards.current) { const prepare = read().beforeClose; if (prepare && !await prepare()) return; if (disposed || busy()) return; }
        accepted.current = true;
        await getCurrentWindow().close();
      } catch (e) { accepted.current = false; if (!disposed) setError(`窗口未关闭：${String(e)} 请重试。`); }
      finally { closing.current = false; if (!disposed && focus?.isConnected) focus.focus(); }
    }).then(fn => { if (disposed) fn(); else stop = fn; }).catch(e => { if (!disposed) setError(`无法启用窗口关闭保护：${String(e)} 请重新打开窗口。`); });
    return () => { disposed = true; stop?.(); };
  }, [enabled, context]);
  return <CloseContext.Provider value={context}>{children}{error && <p className="window-close-error validation-error" role="alert">{error}</p>}</CloseContext.Provider>;
}

export function useCloseGuard(guard: CloseGuard) {
  const context = useContext(CloseContext), latest = useRef(guard); latest.current = guard;
  useEffect(() => context?.register(() => latest.current), [context]);
}

/** 页面弹窗的 ×、取消和原生 cancel 共用请求；未确认前不卸载，也不让 Esc 穿透宿主。 */
export function useModalClose({ close, busy = () => false, dirty = () => false }: CloseGuard & { close: () => void | Promise<void> }) {
  const ref = useRef<HTMLDialogElement>(null), pending = useRef(false), mounted = useRef(false);
  const latest = useRef({ close, busy, dirty }); latest.current = { close, busy, dirty };
  const trigger = useRef(document.activeElement as HTMLElement | null), context = useContext(CloseContext);
  const [error, setError] = useState('');
  useCloseGuard({ busy: () => pending.current || latest.current.busy(), dirty: () => latest.current.dirty() });
  useEffect(() => {
    mounted.current = true; ref.current?.showModal();
    if (document.activeElement?.hasAttribute('data-dialog-close')) {
      ref.current?.querySelector<HTMLElement>('input:not(:disabled):not([type="hidden"]),select:not(:disabled),textarea:not(:disabled),.dialog-actions button:not(:disabled),footer button:not(:disabled)')?.focus();
    }
    return () => { mounted.current = false; if (trigger.current?.isConnected) trigger.current.focus(); };
  }, []);
  async function requestClose() {
    if (pending.current || latest.current.busy()) return;
    pending.current = true; setError('');
    const focus = document.activeElement as HTMLElement | null;
    try {
      if (latest.current.dirty() && !await (context?.discard() ?? askDiscard())) return;
      if (mounted.current && !latest.current.busy()) await latest.current.close();
    } catch (e) { if (mounted.current) setError(`弹窗未关闭：${String(e)} 请重试。`); }
    finally { pending.current = false; if (mounted.current && focus?.isConnected) focus.focus(); }
  }
  return { ref, requestClose, isClosing: () => pending.current, windowClose: !!context?.enabled, error, onCancel: (event: SyntheticEvent) => { event.preventDefault(); event.stopPropagation(); void requestClose(); }, onKeyDown: (event: ReactKeyboardEvent) => { if (event.key === 'Escape') event.stopPropagation(); } };
}

/** 只在独立编辑／排序窗启用；主页面不因 Esc 退出，页面弹窗和轻量浮层优先消费按键。 */
export function useEditorEscape(close: () => Promise<unknown>) {
  const latest = useRef(close); latest.current = close;
  useEffect(() => {
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing || event.repeat || document.querySelector('dialog[open]')) return;
      event.preventDefault(); void latest.current();
    };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
  }, []);
}
