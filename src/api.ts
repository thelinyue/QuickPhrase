import { invoke } from '@tauri-apps/api/core';
import { emitTo, listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { WebviewWindow } from '@tauri-apps/api/webviewWindow';
import { useEffect, useState } from 'react';
import type { Library } from './types';

export async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  try { return await invoke<T>(command, args); }
  catch (error) { const message = String(error); void invoke('report_error', { message }).catch(() => {}); throw new Error(message); }
}

export function useLibrary() {
  const [library, setLibrary] = useState<Library | null>(null);
  const [error, setError] = useState('');
  const refresh = async () => { try { setLibrary(await call<Library>('get_library')); setError(''); } catch (e) { setError(String(e)); } };
  useEffect(() => {
    let disposed = false; let unlisten: (() => void) | undefined;
    void refresh();
    void listen('library-changed', () => { if (!disposed) void refresh(); }).then(fn => { if (disposed) fn(); else unlisten = fn; });
    return () => { disposed = true; unlisten?.(); };
  }, []);
  return { library, error, refresh };
}

/** 独立编辑窗口不会清空或覆盖主窗口记录的外部目标。一个用途保持一个窗口。 */
export async function openWindow(route: 'editor' | 'file-editor' | 'settings' | 'common-editor' | 'common-sort' | 'category-sort' | 'file-sort' | 'recycle-bin' | 'delete-confirm', id = '', afterId = '', tab = '', categoryId = '', groupId = '') {
  const existing = await WebviewWindow.getByLabel(route);
  if (existing) {
    // 设置页复用现有窗口时也更新入口上下文，尤其是同名分类必须按所点击的ID预选。
    if (route === 'settings') await emitTo(route, 'settings-navigation', { tab, categoryId });
    if (route === 'delete-confirm') await emitTo(route, 'deletion-navigation', { id, kind: tab });
    await existing.show(); await existing.setFocus(); return;
  }
  const query = new URLSearchParams({ id, afterId, tab, categoryId, groupId });
  const sort = route === 'common-sort' || route === 'category-sort' || route === 'file-sort';
  const deleting = route === 'delete-confirm', recycling = route === 'recycle-bin';
  const window = new WebviewWindow(route, {
    url: `index.html#${route}?${query}`, title: deleting ? '删除确认 · 闪语' : recycling ? '回收站 · 闪语' : route === 'file-sort' ? '文件快速排序 · 闪语' : route === 'editor' ? '话术编辑 · 闪语' : route === 'settings' ? '设置 · 闪语' : route === 'common-editor' ? '常用短语 · 闪语' : route === 'category-sort' ? '分类排序 · 闪语' : route === 'common-sort' ? '常用短语排序 · 闪语' : '文件关联 · 闪语',
    width: deleting ? 384 : recycling ? 830 : route === 'file-editor' ? 510 : route === 'common-editor' ? 380 : sort ? 550 : 594, height: deleting ? 256 : recycling ? 500 : route === 'file-editor' ? 250 : route === 'common-editor' ? 160 : sort ? 580 : 620,
    minWidth: deleting ? 384 : recycling ? 640 : route === 'file-editor' ? 450 : route === 'common-editor' ? 360 : sort ? 380 : 480, minHeight: deleting ? 256 : recycling ? 400 : route === 'file-editor' ? 230 : route === 'common-editor' ? 160 : sort ? 350 : 480,
    decorations: false, resizable: route !== 'common-editor' && !deleting, center: true,
    // 在 WebView2 层关闭所有动态窗口的“保存的信息”建议，autocomplete="off" 不能可靠禁止它。
    generalAutofillEnabled: false,
  });
  await new Promise<void>((resolve, reject) => {
    void window.once('tauri://created', () => resolve());
    void window.once('tauri://error', e => reject(new Error(`窗口无法打开：${String(e.payload)}`)));
  });
}
export const closeWindow = () => getCurrentWindow().close();
