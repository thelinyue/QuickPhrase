import { invoke } from '@tauri-apps/api/core';
import { emitTo, listen } from '@tauri-apps/api/event';
import { getCurrentWindow, currentMonitor } from '@tauri-apps/api/window';
import { WebviewWindow } from '@tauri-apps/api/webviewWindow';
import { useEffect, useRef, useState } from 'react';
import type { Library, Settings, TeamStatus, TeamView } from './types';
import { reportDiagnosticOperation } from './diagnosticsClient';
let activeLibraryId = '';
const privateWrites = new Set(['save_phrase', 'save_category', 'save_common_phrase', 'reorder_common_phrases', 'delete_category', 'move_copy_category', 'reorder_categories', 'delete_item', 'delete_local_content', 'restore_recycled', 'remove_recycled', 'empty_recycle', 'move_copy_phrase', 'sort_item', 'save_settings', 'patch_settings', 'import_images', 'import_pasted_images']);

export async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  if (command === 'save_phrase') {
    const ref = (args?.phrase as { contentRef?: { source: string; libraryId?: string; revision?: number } })?.contentRef;
    if (ref?.source === 'private') args = { ...args, libraryId: ref.libraryId, expectedRevision: args?.expectedRevision ?? ref.revision };
  }
  if (privateWrites.has(command) && !args?.libraryId) {
    const query = new URLSearchParams(location.hash.split('?')[1]); const libraryId = query.get('libraryId') || activeLibraryId;
    if (libraryId) args = { ...args, libraryId };
  }
  const started = performance.now();
  try { const result = await invoke<T>(command, args); reportDiagnosticOperation(command, true, started); return result; }
  catch (error) { reportDiagnosticOperation(command, false, started); const message = String(error); void invoke('report_error', { message }).catch(() => {}); throw new Error(message); }
}

export interface SettingsSnapshot { libraryId: string; settings: Settings; revision: number }
export function useLibrary() {
  const [library, setLibrary] = useState<Library | null>(null);
  const [error, setError] = useState('');
  const sequence = useRef(0); const settingsSnapshots = useRef(new Map<string, SettingsSnapshot>());
  const refresh = async () => {
    const request = ++sequence.current;
    try {
      const next = await call<Library & { settingsRevision?: number }>('get_library');
      if (request !== sequence.current) return;
      activeLibraryId = next.libraryId || '';
      const latest = settingsSnapshots.current.get(next.libraryId || '');
      if (latest && latest.revision > (next.settingsRevision ?? 0)) { next.settings = latest.settings; next.settingsRevision = latest.revision; }
      else settingsSnapshots.current.set(next.libraryId || '', { libraryId: next.libraryId || '', settings: next.settings, revision: next.settingsRevision ?? 0 });
      if (next.libraryId) next.phrases = next.phrases.map(p => ({ ...p, contentRef: { source: 'private' as const, libraryId: next.libraryId!, id: p.id, revision: next.privateRevisions?.[`phrase:${p.id}`] || 0 }, ready: next.privateReady?.[p.id] }));
      setLibrary(next); setError('');
    } catch (e) { if (request === sequence.current) setError(String(e)); }
  };
  useEffect(() => {
    let disposed = false; const stops: (() => void)[] = [];
    const subscribe = async () => {
      for (const [name, handler] of [
        ['company-files-changed', () => { if (!disposed) void refresh(); }],
        ['library-changed', () => { if (!disposed) void refresh(); }],
        ['settings-changed', (event: { payload: SettingsSnapshot }) => {
          if (disposed) return;
          const value = event.payload, old = settingsSnapshots.current.get(value.libraryId);
          if (old && old.revision >= value.revision) return;
          settingsSnapshots.current.set(value.libraryId, value);
          setLibrary(current => current?.libraryId === value.libraryId ? { ...current, settings: value.settings, settingsRevision: value.revision } : current);
        }],
      ] as const) {
        const stop = await listen<SettingsSnapshot>(name, handler); if (disposed) stop(); else stops.push(stop);
      }
      if (!disposed) await refresh();
    };
    void subscribe().catch(e => { if (!disposed) setError(String(e)); });
    return () => { disposed = true; ++sequence.current; stops.forEach(stop => stop()); };
  }, []);
  return { library, error, refresh };
}

/** 独立编辑窗口不会清空或覆盖主窗口记录的外部目标。一个用途保持一个窗口。 */
export async function openWindow(route: 'editor' | 'file-editor' | 'settings' | 'user-management' | 'team-management' | 'company-files' | 'usage-guide' | 'common-editor' | 'common-sort' | 'category-sort' | 'file-sort' | 'recycle-bin' | 'delete-confirm', id = '', afterId = '', tab = '', categoryId = '', groupId = '', teamId = '') {
  // 查询现有窗口会异步让出执行；固定入口的个人库，避免切号后混用新库与旧分类。
  const libraryId = activeLibraryId;
  await call('ensure_update_idle');
  // 旧用户名入口兼容映射，两个窗口分别复用，不互相覆盖成员草稿。
  if (route === 'team-management' && tab === 'users') route = 'user-management';
  const existing = await WebviewWindow.getByLabel(route);
  if (existing) {
    if (route === 'usage-guide') await existing.unminimize();
    if (route === 'editor' && tab.startsWith('cloud:')) { await existing.show(); await existing.setFocus(); throw new Error('请先完成或关闭当前话术编辑窗口，再生成新话术。'); }
    // 设置页复用现有窗口时也更新入口上下文，尤其是同名分类必须按所点击的ID预选。
    if (route === 'settings') await emitTo(route, 'settings-navigation', { tab, categoryId, teamId });
    if (route === 'team-management') await emitTo(route, 'team-management-navigation', { tab, teamId });
    if (route === 'recycle-bin') await emitTo(route, 'recycle-navigation', { teamId });
    if (route === 'delete-confirm') await emitTo(route, 'deletion-navigation', { id, kind: tab });
    await existing.show(); await existing.setFocus(); return;
  }
  const query = new URLSearchParams({ id, afterId, tab, categoryId, groupId, teamId, libraryId });
  const sort = route === 'common-sort' || route === 'category-sort' || route === 'file-sort';
  const deleting = route === 'delete-confirm', recycling = route === 'recycle-bin', managing = route === 'team-management' || route === 'user-management';
  const monitor = route === 'file-editor' ? await currentMonitor() : null;
  const available = monitor ? { width: monitor.workArea.size.width / monitor.scaleFactor, height: monitor.workArea.size.height / monitor.scaleFactor } : { width: 900, height: 600 };
  const window = new WebviewWindow(route, {
    url: `index.html#${route}?${query}`, title: managing ? `${route === 'user-management' ? '账号管理' : '共享话术管理'} · 闪语` : deleting ? '删除确认 · 闪语' : recycling ? '回收站 · 闪语' : route === 'file-sort' ? '文件快速排序 · 闪语' : route === 'editor' ? '话术编辑 · 闪语' : route === 'settings' ? '设置 · 闪语' : route === 'common-editor' ? '常用短语 · 闪语' : route === 'category-sort' ? '分类排序 · 闪语' : route === 'common-sort' ? '常用短语排序 · 闪语' : '文件关联 · 闪语',
    width: route === 'user-management' ? 620 : managing ? 680 : deleting ? 384 : recycling ? 830 : route === 'file-editor' ? 510 : route === 'common-editor' ? 380 : sort ? 550 : 594, height: route === 'user-management' ? 620 : managing ? 600 : deleting ? 256 : recycling ? 500 : route === 'file-editor' ? 250 : route === 'common-editor' ? 160 : sort ? 580 : 620,
    minWidth: route === 'user-management' ? 480 : managing ? 560 : deleting ? 384 : recycling ? 640 : route === 'file-editor' ? 450 : route === 'common-editor' ? 360 : sort ? 380 : 480, minHeight: managing ? 440 : deleting ? 256 : recycling ? 400 : route === 'file-editor' ? 230 : route === 'common-editor' ? 160 : sort ? 350 : 480,
    decorations: false, resizable: route !== 'common-editor' && !deleting, center: true,
    // 在 WebView2 层关闭所有动态窗口的“保存的信息”建议，autocomplete="off" 不能可靠禁止它。
    generalAutofillEnabled: false,
    ...(route === 'file-editor' ? { width: Math.min(900, available.width), height: Math.min(600, available.height), minWidth: Math.min(760, available.width), minHeight: Math.min(480, available.height) } : {}),
    ...(route === 'company-files' ? { title: '云文件管理中心 · 闪语', width: 1000, height: 650, minWidth: 760, minHeight: 480 } : {}),
    ...(route === 'usage-guide' ? { title: '使用指引 · 闪语', width: 760, height: 620, minWidth: 620, minHeight: 440 } : {}),
  });
  await new Promise<void>((resolve, reject) => {
    void window.once('tauri://created', () => resolve());
    void window.once('tauri://error', e => reject(new Error(`窗口无法打开：${String(e.payload)}`)));
  });
}
export const closeWindow = () => getCurrentWindow().close();

/** Rust 负责网络合并；此钩子只读 IPC，并让切换账号/共享话术前发出的旧视图请求失效。 */
export function useTeams() {
  const [status, setStatus] = useState<TeamStatus>({ server: '', username: '', user: null, teams: [], offline: false });
  const [version, setVersion] = useState(0); const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    let disposed = false, notified = false; let unlisten: (() => void) | undefined;
    void listen<TeamStatus>('teams-changed', e => { notified = true; if (!disposed) { setStatus(e.payload); setLoaded(true); setVersion(v => v + 1); } }).then(fn => { if (disposed) fn(); else unlisten = fn; }).catch(() => {});
    void call<TeamStatus>('get_team_status').then(s => { if (!disposed && !notified) setStatus(s); }).catch(() => {}).finally(() => { if (!disposed) setLoaded(true); });
    return () => { disposed = true; unlisten?.(); };
  }, []);
  return { status, version, loaded };
}

export function useTeamView(teamId: string, status: TeamStatus, version: number) {
  const [loaded, setLoaded] = useState<{ teamId: string; userId: string; view: TeamView } | null>(null);
  const [error, setError] = useState('');
  const userId = status.user?.id || '';
  const allowed = status.teams.some(t => t.teamId === teamId);
  useEffect(() => {
    let active = true;
    if (!teamId || !allowed) { setLoaded(null); return; }
    void call<TeamView>('get_team_view', { teamId }).then(view => { if (active) { setLoaded({ teamId, userId, view }); setError(''); } }).catch(e => { if (active) setError(String(e)); });
    return () => { active = false; };
  }, [teamId, userId, allowed, version]);
  const info = status.teams.find(t => t.teamId === teamId);
  return { view: allowed && loaded?.teamId === teamId && loaded.userId === userId && loaded.view.team.lifecycleVersion === info?.lifecycleVersion && loaded.view.team.version >= info.version ? { ...loaded.view, team: { ...info, version: loaded.view.team.version } } : null, error };
}
