import { useEffect, useRef, useState } from 'react';
import { emitTo, listen } from '@tauri-apps/api/event';
import { getCurrentWindow, currentMonitor } from '@tauri-apps/api/window';
import { WebviewWindow } from '@tauri-apps/api/webviewWindow';
import { call, closeWindow, useTeams } from './api';
import { CloudFileTable } from './CompanyFiles';
import { Button, Titlebar } from './ui';
import { useCloseGuard, useEditorEscape } from './close';
import type { CompanyFile } from './types';

type Selection = { requestId: string; files: CompanyFile[]; epoch: number };
/** 请求编号和接收窗口一一对应；父页面卸载后关闭选择窗口，迟到消息不能写入另一个草稿。 */
export function CompanyFilePicker({ limit, onConfirm, close, confirmLabel = '添加文件' }: { limit: number; onConfirm: (files: CompanyFile[], epoch: number) => void; close: () => void; confirmLabel?: string }) {
  const { status, loaded } = useTeams();
  const handlers = useRef({ onConfirm, close }); handlers.current = { onConfirm, close };
  const [error, setError] = useState('');
  const originalEpoch = useRef<number | null>(null);
  const epoch = status.sessionEpoch || 0;
  useEffect(() => {
    if (!loaded) return;
    if (originalEpoch.current !== null && originalEpoch.current !== epoch) { handlers.current.close(); return; }
    originalEpoch.current = epoch;
    let disposed = false, stop: (() => void) | undefined, child: WebviewWindow | undefined;
    const requestId = crypto.randomUUID(), parent = getCurrentWindow().label;
    void (async () => {
      await call('ensure_update_idle');
      stop = await listen<Selection>('company-picker-result', e => {
        if (!disposed && e.payload.requestId === requestId && e.payload.epoch === epoch) {
          if (e.payload.files.length && e.payload.files.length <= limit) handlers.current.onConfirm(e.payload.files, epoch);
          else handlers.current.close();
        }
      });
      if (disposed) { stop(); return; }
      const monitor = await currentMonitor();
      if (disposed) return;
      const available = monitor ? { width: monitor.workArea.size.width / monitor.scaleFactor, height: monitor.workArea.size.height / monitor.scaleFactor } : { width: 900, height: 600 };
      const query = new URLSearchParams({ requestId, parent, epoch: String(epoch), limit: String(limit), confirmLabel });
      child = new WebviewWindow(`company-picker-${requestId}`, { url: `index.html#company-picker?${query}`, parent, title: '选择云文件 · 闪语', width: Math.min(900, available.width), height: Math.min(600, available.height), minWidth: Math.min(760, available.width), minHeight: Math.min(480, available.height), decorations: false, resizable: true, center: true, generalAutofillEnabled: false });
      await child.once('tauri://destroyed', () => { if (!disposed) handlers.current.close(); });
      await child.once('tauri://error', e => { if (!disposed) setError(`窗口无法打开：${String(e.payload)}`); });
    })().catch(e => { if (!disposed) setError(String(e)); });
    return () => { disposed = true; stop?.(); void child?.close().catch(() => {}); };
  }, [loaded, epoch, limit, confirmLabel]);
  return error ? <div role="alert" className="validation-error">{error}<Button variant="ghost" onClick={close}>关闭</Button></div> : null;
}

export function CompanyPickerWindow() {
  const { status } = useTeams(); const [selected, setSelected] = useState<CompanyFile[]>([]), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  const pending = useRef(false), delivered = useRef(false);
  useCloseGuard({ busy: () => pending.current, dirty: () => !delivered.current && selected.length > 0 });
  useEditorEscape(closeWindow);
  const params = new URLSearchParams(location.hash.split('?')[1]);
  const epoch = Number(params.get('epoch')), limit = Math.min(50, Math.max(1, Number(params.get('limit')) || 1));
  const valid = status.sessionEpoch === epoch && !!status.user && !status.offline;
  async function finish() {
    if (!valid || !selected.length || pending.current) return;
    pending.current = true; setBusy(true);
    try { const latest = await call<{ sessionEpoch: number }>('get_team_status'); if (latest.sessionEpoch !== epoch) throw new Error('账号已变化，请重新选择。'); await emitTo(params.get('parent')!, 'company-picker-result', { requestId: params.get('requestId'), files: selected, epoch }); delivered.current = true; pending.current = false; await closeWindow(); }
    catch (e) { setError(String(e)); } finally { pending.current = false; setBusy(false); }
  }
  return <section className="client-window file-association-window"><Titlebar minimize maximize title="选择云文件" /><CloudFileTable status={status} limit={limit} selected={selected} onChange={files => { if (!pending.current) setSelected(files); }} />{error && <p role="alert" className="validation-error">{error}</p>}<footer className="association-footer"><span>已选择 {selected.length}/{limit}</span><span className="toolbar-spacer" /><Button disabled={busy} onClick={() => void closeWindow()}>取消</Button><Button variant="primary" disabled={!valid || busy || !selected.length} onClick={() => void finish()}>{params.get('confirmLabel') || '添加文件'}</Button></footer></section>;
}
