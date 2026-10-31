import { cloudRenameDialog } from './cloudRename';
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { open, save } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { FileIcon, FormDialog, Icon, type DialogSpec } from './ui';
import { AccountNotice } from './AccountControls';
import { useNotices } from './notices';
import type { CompanyFile, CompanyFilePermissions, CompanyTransfer, CompanyView, TeamStatus } from './types';

export const fileSize = (size: number) => size < 1024 ? `${size} B` : size < 1024 * 1024 ? `${(size / 1024).toFixed(1)} KB` : `${(size / 1024 / 1024).toFixed(1)} MB`;
const noPermissions: CompanyFilePermissions = { canUpload: false, canRecycle: false, canRestore: false, version: 0, uploadVersion: 0 };
const offlineNotice = '连接已断开，显示最近目录；已缓存文件仍可使用。';
export function companyPermissions(status: TeamStatus): CompanyFilePermissions {
  const rights = status.companyFilePermissions ? status.user?.companyFilePermissions || noPermissions : noPermissions;
  return status.user?.admin ? { ...rights, canUpload: true, canRecycle: true, canRestore: true } : rights;
}
function unavailable(status: TeamStatus) {
  return !status.user ? '请在“设置→账号”中登录。' : status.user.mustChangePassword ? '请先修改初始密码。' : !status.companyFiles ? '当前闪云版本不支持云文件，请升级服务端。' : status.offline ? offlineNotice : '';
}
/** 请求世代与筛选条件一起核验，旧账号或旧搜索结果不能覆盖当前目录。 */
function useCompanyCatalog(status: TeamStatus, recycled: boolean, query: string, enabled = true) {
  const [view, setView] = useState<CompanyView | null>(null), [error, setError] = useState(''); const sequence = useRef(0);
  // 切换管理页或筛选时保留目录断连状态，直到当前账号成功取得在线目录。
  const [connection, setConnection] = useState({ epoch: -1, offline: false });
  const epoch = status.sessionEpoch || 0, userId = status.user?.id || '', blocked = unavailable(status);
  const current = useRef({ epoch, recycled, query, enabled }); current.current = { epoch, recycled, query, enabled };
  const loadedFilter = useRef<typeof current.current | null>(null);
  const refresh = useCallback(async () => {
    const filter = current.current;
    if (filter.epoch !== epoch || filter.recycled !== recycled || filter.query !== query || filter.enabled !== enabled) return;
    const request = ++sequence.current;
    const valid = () => request === sequence.current && current.current.epoch === epoch && current.current.recycled === recycled && current.current.query === query && current.current.enabled === enabled;
    const cachedOfflineView = !!userId && status.offline && !!status.companyFiles && !status.user?.mustChangePassword && !recycled;
    if (!enabled || !userId || (blocked && !cachedOfflineView)) { setView(null); setError(''); return; }
    try { const next = await call<CompanyView>('get_company_files', { sessionEpoch: epoch, recycled, query }); if (valid()) { loadedFilter.current = filter; setView(next); setConnection({ epoch, offline: !!next.offline }); setError(''); } }
    catch (e) { if (valid()) { setView(null); setError(String(e)); } }
  }, [epoch, userId, blocked, status.offline, status.companyFiles, status.user?.mustChangePassword, recycled, query, enabled]);
  useEffect(() => { setView(null); void refresh(); let disposed = false, stop: (() => void) | undefined;
    void listen<number>('company-files-changed', e => { if (!disposed && e.payload === epoch) void refresh(); }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; ++sequence.current; stop?.(); };
  }, [refresh, epoch]);
  const matches = loadedFilter.current?.epoch === epoch && loadedFilter.current.recycled === recycled && loadedFilter.current.query === query && loadedFilter.current.enabled === enabled;
  return { view: matches ? view : null, offline: connection.epoch === epoch && connection.offline, error, refresh };
}
type Page = 'files' | 'recycled' | 'upload' | 'download';
const date = (seconds: number | null) => seconds == null ? '—' : new Date(seconds * 1000).toLocaleString('zh-CN', { hour12: false });
const transferKey = (task: CompanyTransfer) => `${task.direction}:${task.assetId}`;
const activeTransfer = (task: CompanyTransfer) => ['等待上传', '准备中', '传输中'].includes(task.status);
/** 事件与快照可能交错，只接受较新进度；读取快照不会重新启动任务。 */
function mergeTransfers(previous: Record<string, CompanyTransfer>, records: CompanyTransfer[]) {
  const next = { ...previous };
  for (const task of records) { const key = transferKey(task); if (!next[key] || (task.revision || 0) >= (next[key].revision || 0)) next[key] = task; }
  return next;
}

/** 窗口仅负责导航和草稿；授权、引用检查与传输状态由 Rust／服务端 决定。 */
export function CompanyFilesPanel({ active = true }: { active?: boolean }) {
  const { status } = useTeams(); const epoch = status.sessionEpoch || 0, admin = !!status.user?.admin, rights = companyPermissions(status);
  const canViewRecycle = admin || rights.canRecycle || rights.canRestore;
  const [page, setPage] = useState<Page>('files'), [query, setQuery] = useState(''), [descending, setDescending] = useState(true);
  const { view, offline: catalogOffline, error, refresh } = useCompanyCatalog(status, page === 'recycled', query, page === 'files' || (page === 'recycled' && canViewRecycle));
  const offline = status.offline || catalogOffline, blocked = unavailable(status) || (catalogOffline ? offlineNotice : '');
  const [busy, setBusy] = useState(false), [dialog, setDialog] = useState<{ spec: DialogSpec; epoch: number } | null>(null);
  const notices = useNotices(); const setMessage = notices.error;
  const [selected, setSelected] = useState<string[]>([]), [menu, setMenu] = useState(''); const menuRef = useRef<HTMLDivElement>(null);
  const [menuPosition, setMenuPosition] = useState({ x: 0, y: 0, context: false }); const menuTrigger = useRef<HTMLElement | null>(null);
  const [offset, setOffset] = useState(0), [pageSize, setPageSize] = useState(10), [jump, setJump] = useState('1');
  const [transfers, setTransfers] = useState<Record<string, CompanyTransfer>>({});
  const [dragging, setDragging] = useState(false); const uploadZone = useRef<HTMLDivElement>(null);
  const currentPageRef = useRef(page); currentPageRef.current = page;
  const busyRef = useRef(false); const activeRef = useRef(active); activeRef.current = active;
  const currentEpoch = useRef(epoch); currentEpoch.current = epoch; const statusRef = useRef(status); statusRef.current = { ...status, offline };
  const taskLocks = useRef(new Set<string>()), transferSequence = useRef(0);
  const dialogAllowed = useRef<() => boolean>(() => true); const allCheck = useRef<HTMLInputElement>(null);
  useEffect(() => { ++transferSequence.current; busyRef.current = false; setBusy(false); setTransfers({}); notices.clear(); setDialog(null); setPage('files'); setQuery(''); setSelected([]); setMenu(''); setOffset(0); }, [epoch]);
  useEffect(() => { if ((page === 'recycled' && !canViewRecycle)) { setPage('files'); setQuery(''); setSelected([]); } }, [page, admin, canViewRecycle]);
  useEffect(() => { if (dialog && (blocked || !dialogAllowed.current())) setDialog(null); }, [dialog, blocked, rights.canUpload, rights.canRecycle, rights.canRestore, admin]);
  useEffect(() => { if (blocked || !rights.canUpload || (page !== 'files' && page !== 'upload') || busy || dialog) setDragging(false); }, [blocked, rights.canUpload, page, busy, dialog]);
  useEffect(() => {
    let disposed = false, dragEpoch: number | null = null, stop: (() => void) | undefined;
    const allowed = () => activeRef.current && currentEpoch.current === epoch && ['files', 'upload'].includes(currentPageRef.current) && !busyRef.current && !document.querySelector('dialog[open]') && !unavailable(statusRef.current) && companyPermissions(statusRef.current).canUpload;
    const inside = (position: { x: number; y: number }) => { const rect = uploadZone.current?.getBoundingClientRect(); const x = position.x / window.devicePixelRatio, y = position.y / window.devicePixelRatio; return !!rect && x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom; };
    // 只用原生文件路径；拖拽开始时固定会话，拖动中切号不能把原文件交给新账号。
    void getCurrentWindow().onDragDropEvent(e => {
      if (disposed) return;
      const event = e.payload;
      if (event.type === 'enter') dragEpoch = epoch;
      if (event.type === 'leave') { dragEpoch = null; setDragging(false); return; }
      const accepted = dragEpoch === epoch && allowed() && inside(event.position);
      if (event.type === 'drop') { dragEpoch = null; setDragging(false); if (accepted) void run(() => enqueueUploads(event.paths, epoch)); }
      else setDragging(accepted);
    }).then(fn => { if (disposed) fn(); else stop = fn; }).catch(e => { if (!disposed) setMessage(`无法接收拖拽文件：${String(e)}`); });
    return () => { disposed = true; stop?.(); };
  }, [epoch]);
  useEffect(() => { setSelected([]); setMenu(''); setOffset(0); }, [query, descending, pageSize]);
  useEffect(() => { if (!view) return; const available = new Set(view.files.map(f => f.assetId)); setSelected(previous => previous.filter(id => available.has(id))); }, [view]);
  useEffect(() => { setMenu(''); }, [page, blocked, admin, rights.canUpload, rights.canRecycle, rights.canRestore]);
  useEffect(() => { if (!active) { setMenu(''); setDragging(false); } }, [active]);
  useEffect(() => { if (menu && !['batch', 'background'].includes(menu) && !view?.files.some(f => f.assetId === menu)) setMenu(''); }, [menu, view]);
  // 所有云文件菜单共用业务动作；使用实际尺寸避让边缘，不被表格滚动容器裁切。
  useLayoutEffect(() => {
    if (!menu || !menuRef.current) return;
    const rect = menuRef.current.getBoundingClientRect();
    const x = Math.max(2, Math.min(menuPosition.x, innerWidth - rect.width - 2)), y = Math.max(2, Math.min(menuPosition.y, innerHeight - rect.height - 2));
    if (x !== menuPosition.x || y !== menuPosition.y) setMenuPosition(previous => ({ ...previous, x, y }));
  }, [menu, menuPosition]);
  useEffect(() => {
    if (!menu) return;
    (menuRef.current?.querySelector<HTMLElement>('[role="menuitem"]:not(:disabled)') || menuRef.current)?.focus();
    const outside = (e: PointerEvent) => { if (!menuRef.current?.contains(e.target as Node)) setMenu(''); };
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); menuTrigger.current?.focus(); setMenu(''); } };
    const blur = () => setMenu('');
    document.addEventListener('pointerdown', outside); document.addEventListener('keydown', escape); window.addEventListener('blur', blur); window.addEventListener('resize', blur);
    return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); window.removeEventListener('blur', blur); window.removeEventListener('resize', blur); };
  }, [menu]);
  function openMenu(id: string, e: MouseEvent<HTMLElement>, context = false) {
    e.preventDefault(); e.stopPropagation(); menuTrigger.current = e.currentTarget;
    const rect = e.currentTarget.getBoundingClientRect();
    setMenuPosition({ x: context ? e.clientX : rect.left, y: context ? e.clientY : rect.bottom, context }); setMenu(id);
  }
  async function navigate(next: Page) {
    if (next === page || busyRef.current) return;
    setPage(next); setQuery(''); setSelected([]); setOffset(0); setMenu(''); setMessage('');
  }
  async function loadTransfers() {
    const request = ++transferSequence.current;
    try { const records = await call<CompanyTransfer[]>('get_company_transfers', { sessionEpoch: epoch }); if (currentEpoch.current === epoch && request === transferSequence.current) setTransfers(previous => mergeTransfers(previous, records)); }
    catch (e) { if (currentEpoch.current === epoch && request === transferSequence.current) setMessage(String(e)); }
  }
  useEffect(() => { if (!status.user || !status.companyFiles) return; let disposed = false, stop: (() => void) | undefined;
    void listen<CompanyTransfer>('company-transfer', e => { if (!disposed && e.payload.sessionEpoch === epoch) setTransfers(previous => mergeTransfers(previous, [e.payload])); }).then(fn => { if (disposed) fn(); else { stop = fn; void loadTransfers(); } });
    return () => { disposed = true; ++transferSequence.current; stop?.(); };
  }, [epoch, status.user?.id, status.companyFiles]);
  async function run(action: () => Promise<unknown>) {
    if (busyRef.current) return; busyRef.current = true; setBusy(true); setMessage('');
    try { await action(); if (currentEpoch.current === epoch) await refresh(); }
    catch (e) { if (currentEpoch.current === epoch) { setMessage(String(e)); await refresh(); } }
    finally { if (currentEpoch.current === epoch) { busyRef.current = false; setBusy(false); } }
  }
  async function transfer(file: { assetId: string; name: string }, direction: 'upload' | 'download', queryOnly = false) {
    const key = `${epoch}:${direction}:${file.assetId}`; if (currentEpoch.current !== epoch || taskLocks.current.has(key)) return '';
    taskLocks.current.add(key);
    try { await call(queryOnly ? 'query_company_upload' : direction === 'upload' ? 'upload_company_file' : 'download_company_file', { sessionEpoch: epoch, id: file.assetId }); return ''; }
    catch (e) { return String(e); }
    finally { taskLocks.current.delete(key); if (currentEpoch.current === epoch) { await loadTransfers(); await refresh(); } }
  }
  async function startTransfer(file: { assetId: string; name: string }, direction: 'upload' | 'download', queryOnly = false) { const error = await transfer(file, direction, queryOnly); if (currentEpoch.current === epoch) { if (error) setMessage(error); else if (queryOnly) notices.success('原任务已登记，上传结果已核对。'); } }
  async function upload() {
    const paths = await open({ directory: false, multiple: true, title: '上传文件（单文件最多128MB，可多选）' });
    if (paths) await enqueueUploads(Array.isArray(paths) ? paths : [paths], epoch);
  }
  async function enqueueUploads(paths: string[], scope: number) {
    if (!paths.length || currentEpoch.current !== scope || unavailable(statusRef.current) || !companyPermissions(statusRef.current).canUpload) return;
    const records = await call<CompanyTransfer[]>('enqueue_company_uploads', { sessionEpoch: scope, paths });
    if (currentEpoch.current !== scope) return;
    setTransfers(previous => mergeTransfers(previous, records)); setPage('upload'); setQuery(''); setOffset(0); setSelected([]); setMenu('');
    notices.success(`已加入 ${records.length} 个上传任务，可在此查看进度或停止。`);
  }
  function showDialog(spec: DialogSpec, allowed: () => boolean) { dialogAllowed.current = allowed; setMenu(''); setMessage(''); setDialog({ spec, epoch }); }
  function canManage(operation: 'recycle' | 'restore' | 'purge') { const current = companyPermissions(statusRef.current); return currentEpoch.current === epoch && !unavailable(statusRef.current) && (operation === 'purge' ? !!statusRef.current.user?.admin : operation === 'recycle' ? current.canRecycle : current.canRestore); }
  function canRename(file: CompanyFile) { const s = statusRef.current; return currentEpoch.current === epoch && !unavailable(s) && !file.deletedAt && (!!s.user?.admin || (s.user?.id === file.uploadedBy && companyPermissions(s).canUpload)); }
  function rename(file: CompanyFile) { showDialog(cloudRenameDialog(file, epoch, refresh), () => canRename(file)); }
  function manage(file: CompanyFile, operation: 'recycle' | 'restore' | 'purge') {
    showDialog({ title: operation === 'purge' ? '永久删除云文件' : operation === 'recycle' ? '删除云文件' : '恢复云文件', fields: <><p>{operation === 'recycle' ? '将所选文件移入回收站？已有话术继续可用，不能再新增引用。' : operation === 'purge' ? '永久删除云端文件，无法恢复。已缓存到本机的附件和个人副本仍可使用；未缓存或清除缓存的设备将无法再下载，恢复旧话术也不会恢复云端文件。' : '恢复原文件及目录入口。'}</p><p>{file.name}</p></>, onSubmit: async () => { if (!canManage(operation)) throw new Error('操作权限已变化，请刷新后重试。'); await call('manage_company_file', { sessionEpoch: epoch, id: file.assetId, operation, confirmName: operation === 'purge' ? file.name : '' }); if (currentEpoch.current === epoch) await refresh(); } }, () => canManage(operation));
  }
  /** 清理范围取未筛选的回收站快照；逐项沿用服务端引用检查，不碰确认后新增的文件。 */
  async function clearRecycle() {
    if (!canManage('purge')) return;
    const all = await call<CompanyView>('get_company_files', { sessionEpoch: epoch, recycled: true, query: '' });
    if (!canManage('purge') || currentPageRef.current !== 'recycled') return;
    const targets = all.files;
    if (!targets.length) { notices.success('回收站已为空。'); return; }
    showDialog({ title: '清空云文件回收站', submit: '清空回收站', fields: <><p>将尝试永久删除回收站中的 {targets.length} 个文件，不受当前搜索和分页限制。</p><p>包括仍被共享话术及回收记录引用的文件。已缓存到本机的附件和个人副本仍可使用；未缓存或清除缓存的设备将无法再下载。删除后无法恢复。</p></>, onSubmit: async () => { if (!canManage('purge')) throw new Error('管理员权限已变化，请刷新后重试。'); await performBatch('purge', targets); } }, () => canManage('purge'));
  }
  async function saveAs(file: CompanyFile) { const path = await save({ title: '另存云文件', defaultPath: file.name }); if (path && currentEpoch.current === epoch) await call('save_company_file_as', { sessionEpoch: epoch, id: file.assetId, path }); }
  function generate(file: CompanyFile) {
    showDialog({ title: '生成文件话术', className: 'account-dialog', fields: <><p className="company-dialog-file"><FileIcon path={file.name} />{file.name}</p><label>生成话术到<select name="target" defaultValue=""><option value="">个人话术</option>{status.teams.filter(t => t.editable).map(t => <option key={t.teamId} value={t.teamId}>{t.name}</option>)}</select></label></>, submit: '下一步', onSubmit: async form => { if (currentEpoch.current !== epoch) throw new Error('账号已变化，请重新选择文件。'); await openWindow('editor', '', '', `cloud:${epoch}:${file.assetId}`, '', '', String(form.get('target') || '')); } }, () => currentEpoch.current === epoch);
  }
  const files = [...(view?.files || [])].sort((a, b) => { const diff = page === 'recycled' ? (a.deletedAt || 0) - (b.deletedAt || 0) : a.createdAt - b.createdAt; return (descending ? -diff : diff) || a.assetId.localeCompare(b.assetId); });
  const taskRows = Object.values(transfers).filter(t => t.direction === page);
  const count = page === 'upload' || page === 'download' ? taskRows.length : files.length;
  const pageCount = Math.max(1, Math.ceil(count / pageSize)), currentPage = Math.min(offset, pageCount - 1);
  const rows = <T,>(items: T[]) => items.slice(currentPage * pageSize, (currentPage + 1) * pageSize);
  const visibleFiles = rows(files), chosen = visibleFiles.filter(f => selected.includes(f.assetId));
  useEffect(() => { setJump(String(currentPage + 1)); }, [currentPage]);
  useEffect(() => { if (allCheck.current) allCheck.current.indeterminate = chosen.length > 0 && chosen.length < visibleFiles.length; }, [chosen.length, visibleFiles.length]);
  function changePage(next: number) { setOffset(Math.max(0, Math.min(pageCount - 1, next))); setSelected([]); setMenu(''); }
  async function batch(operation: 'download' | 'recycle' | 'restore') {
    setMenu(''); const targets = [...chosen]; if (!targets.length) return;
    if (operation !== 'download') showDialog({ title: operation === 'recycle' ? '批量删除云文件' : '批量恢复云文件', fields: <><p>{operation === 'recycle' ? '将所选文件移入回收站？已有话术继续可用，不能再新增引用。' : '恢复以下文件及目录入口。'}</p><ul className="company-confirm-list">{targets.map(f => <li key={f.assetId}>{f.name}</li>)}</ul></>, onSubmit: async () => { await performBatch(operation, targets); } }, () => canManage(operation));
    else await performBatch(operation, targets);
  }
  async function performBatch(operation: 'download' | 'recycle' | 'restore' | 'purge', targets: CompanyFile[]) {
    const action = async () => {
      const results: { name: string; error: string }[] = [];
      if (operation === 'download') { const responses = await Promise.all(targets.map(f => transfer(f, 'download'))); responses.forEach((error, i) => results.push({ name: targets[i].name, error })); }
      else for (const file of targets) { if (!canManage(operation)) { results.push({ name: file.name, error: '权限或账号已变化，未执行' }); continue; } try { await call('manage_company_file', { sessionEpoch: epoch, id: file.assetId, operation, confirmName: operation === 'purge' ? file.name : '' }); results.push({ name: file.name, error: '' }); } catch (e) { results.push({ name: file.name, error: String(e) }); } }
      if (currentEpoch.current === epoch) { const failures = results.filter(r => r.error); const result = `成功 ${results.length - failures.length} 个，失败 ${failures.length} 个：${results.map(r => `${r.name}（${r.error || '成功'}）`).join('；')}`; if (failures.length) notices.show(result, 'warning'); else notices.success(result); setSelected([]); }
    };
    // 批量下载使用 Rust 的两个许可，界面仍可进入下载管理并停止任务。
    if (operation === 'download') { setMessage(''); await action(); }
    else await run(action);
  }
  const search = <div className="company-search"><input aria-label={'搜索云文件'} placeholder={'输入文件名称搜索'} value={query} onChange={e => setQuery(e.target.value)} /><Icon name="search" /></div>;
  async function refreshAll() { await call('team_refresh'); if (currentEpoch.current !== epoch) return; else if (page === 'upload' || page === 'download') await loadTransfers(); else await refresh(); }
  const refreshButton = <button className="company-button" disabled={busy} onClick={() => void run(refreshAll)}><Icon name="refresh" />刷新</button>;
  function moreMenu(file: CompanyFile) { return <div className="company-menu"><button className="company-row-button" aria-label="更多" title="更多" disabled={busy || !!blocked} aria-haspopup="menu" aria-expanded={menu === file.assetId} onClick={e => menu === file.assetId ? setMenu('') : openMenu(file.assetId, e)}><Icon name="more" /></button></div>; }
  function fileContext(file: CompanyFile, e: MouseEvent<HTMLTableRowElement>) {
    if (busy || (e.target as Element).closest('input:not([type="checkbox"]),textarea,[contenteditable="true"]')) return;
    const ids = selected.includes(file.assetId) ? selected : [file.assetId]; setSelected(ids);
    openMenu(ids.length > 1 ? 'batch' : file.assetId, e, true);
  }
  const menuFile = visibleFiles.find(file => file.assetId === menu);
  type Action = { label: string; action: () => unknown; disabled?: boolean } | null;
  const disabled = busy || !!blocked;
  const batchItems: Action[] = page === 'files' ? [{ label: '批量下载', disabled, action: () => batch('download') }, ...(rights.canRecycle ? [{ label: '批量删除', disabled, action: () => batch('recycle') }] : [])] : rights.canRestore ? [{ label: '批量恢复', disabled, action: () => batch('restore') }] : [];
  const menuItems: Action[] = menu === 'background' ? [{ label: '刷新', disabled: busy, action: () => run(refreshAll) }, ...(rights.canUpload ? [{ label: '上传新文件', disabled, action: () => run(upload) }] : []), null, { label: '下载管理', disabled: busy, action: () => navigate('download') }, { label: '上传管理', disabled: busy, action: () => navigate('upload') }]
    : menu === 'batch' ? batchItems : !menuFile ? [] : page === 'recycled' ? [...(rights.canRestore ? [{ label: '恢复', disabled, action: () => manage(menuFile, 'restore') }] : []), ...(admin ? [{ label: '永久删除', disabled, action: () => manage(menuFile, 'purge') }] : [])]
    : [...(menuPosition.context ? [{ label: '下载', disabled: disabled || transfers[`download:${menuFile.assetId}`]?.status === '传输中', action: () => startTransfer(menuFile, 'download') }] : []), { label: '另存为', disabled, action: () => run(() => saveAs(menuFile)) }, { label: '生成话术', disabled, action: () => generate(menuFile) }, ...(canRename(menuFile) ? [{ label: '重命名云文件', disabled, action: () => rename(menuFile) }] : []), ...(menuPosition.context && rights.canRecycle ? [null, { label: '删除', disabled, action: () => manage(menuFile, 'recycle') }] : [])];
  const pagination = <div className="company-pagination"><span>共{pageCount}页 {count}条</span><button aria-label="上一页" disabled={currentPage === 0} onClick={() => changePage(currentPage - 1)}>‹</button><span className="company-page-number">{currentPage + 1}</span><button aria-label="下一页" disabled={currentPage === pageCount - 1} onClick={() => changePage(currentPage + 1)}>›</button><form onSubmit={e => { e.preventDefault(); const value = Number(jump); if (Number.isInteger(value) && value >= 1 && value <= pageCount) changePage(value - 1); }}><label>到第<input aria-label="跳转页码" inputMode="numeric" value={jump} onChange={e => setJump(e.target.value)} />页</label><button type="submit">跳转</button></form><select aria-label="每页条数" value={pageSize} onChange={e => setPageSize(Number(e.target.value))}>{[10, 20, 50].map(n => <option key={n} value={n}>{n}条/页</option>)}</select></div>;

  return <section className="company-files-panel" onContextMenu={e => { if (!(e.target as Element).closest('input:not([type="checkbox"]),textarea,[contenteditable="true"]')) e.preventDefault(); }}><main className="company-content">{page === 'files' ? <div className="company-toolbar">{search}{refreshButton}<button className="company-button" onClick={() => void navigate('download')}><Icon name="download" />下载管理</button><button className="company-button" onClick={() => void navigate('upload')}><Icon name="upload" />上传管理</button>{rights.canUpload && <button className="company-button company-primary" disabled={busy || !!blocked} onClick={() => void run(upload)}><Icon name="upload" />上传新文件</button>}{canViewRecycle && <details className="cloud-module-more"><summary aria-label="云文件更多操作"><Icon name="more" /></summary><div><button onClick={e => { e.currentTarget.closest('details')?.removeAttribute('open'); void navigate('recycled'); }}>云文件回收站</button></div></details>}</div> : page === 'upload'  || page === 'download' ? <div className="company-toolbar"><button className="company-button" onClick={() => void navigate('files')}><Icon name="back" />返回</button><span className="company-toolbar-spacer">{page === 'upload' ? '上传管理' : '下载管理'}</span><button className="company-button" disabled={busy || !status.user || !status.companyFiles} onClick={() => void run(async () => { ++transferSequence.current; const records = await call<CompanyTransfer[]>('clear_company_transfers', { sessionEpoch: epoch, direction: page }); if (currentEpoch.current === epoch) setTransfers(previous => mergeTransfers(Object.fromEntries(Object.entries(previous).filter(([, r]) => r.direction !== page || activeTransfer(r) || (r.direction === 'upload' && r.status === '待核对'))), records)); })} title="清空已结束记录，保留等待、准备、传输和待核对任务"><Icon name="trash" />清空</button></div> : <div className="company-toolbar"><button className="company-button" onClick={() => void navigate('files')}><Icon name="back" />返回</button><strong>云文件回收站</strong>{search}{refreshButton}{page === 'recycled' && admin && <button className="company-button" disabled={busy || !!blocked} onClick={() => void run(clearRecycle)}>清空回收站</button>}</div>}
    {(blocked || error) && <p className="company-message" role="alert">{error || blocked}</p>}
    <AccountNotice message={notices.current?.message || ''} error={notices.current?.kind !== 'success'} count={notices.count} clear={() => notices.current && notices.dismiss(notices.current.id)} />
    {(page === 'files' || page === 'recycled') && <div className="company-selection-toolbar"><div className="company-menu company-batch-menu"><button className="company-button" disabled={!chosen.length || busy || !!blocked || (page === 'recycled' && !rights.canRestore)} aria-haspopup="menu" aria-expanded={menu === 'batch'} onClick={e => menu === 'batch' ? setMenu('') : openMenu('batch', e)}><Icon name="menu" />批量操作</button></div><span aria-live="polite">已选 {chosen.length} 项</span>{page === 'files' && rights.canUpload && !blocked && <p className="company-upload-hint">可多选文件上传，也可将文件拖到下方列表；单文件最多128MB。</p>}</div>}
    {page === 'upload' || page === 'download' ? <div className="company-list" ref={page === 'upload' ? uploadZone : undefined}><table className="company-transfer-table"><thead><tr><th>文件名</th><th>{page === 'upload' ? '上传进度' : '下载进度'}</th><th>状态</th><th>操作</th></tr></thead><tbody>{rows(taskRows).map(task => <tr key={transferKey(task)}><td><span className="company-file-name"><FileIcon path={task.name} /><span title={task.name}>{task.name}</span></span></td><td><div className="company-progress"><progress value={task.done} max={task.total || 1} /><span>{task.total ? Math.floor(task.done / task.total * 100) : 0}%</span></div><small>{fileSize(task.done)} / {fileSize(task.total)}</small></td><td><span className={task.status === '已完成' ? 'company-success' : ''}>{task.status}</span>{task.message && <small className="company-transfer-error">{task.message}</small>}</td><td><div className="company-actions">{activeTransfer(task) ? <button className="company-row-button" aria-label="停止" title="停止" onClick={() => void call('cancel_company_transfer', { sessionEpoch: epoch, id: task.assetId }).catch(e => { if (currentEpoch.current === epoch) setMessage(String(e)); })}><Icon name="stop" /></button> : task.direction === 'upload' && task.status === '待核对' ? <><button className="company-row-button" aria-label="查询结果" title="查询结果" disabled={!!blocked} onClick={() => void startTransfer(task, 'upload', true)}><Icon name="search" /></button>{rights.canUpload && <button className="company-row-button" aria-label="继续原任务" title="继续原任务" disabled={!!blocked} onClick={() => void startTransfer(task, 'upload')}><Icon name="play" /></button>}</> : task.direction === 'download' && task.status !== '已完成' ? <button className="company-row-button" aria-label="重新下载" title="重新下载" disabled={!!blocked} onClick={() => void startTransfer(task, 'download')}><Icon name="download" /></button> : null}</div></td></tr>)}</tbody></table>{dragging && page === 'upload' && <div className="company-drop-overlay" role="status"><Icon name="upload" /><strong>松开即可上传文件</strong></div>}{!taskRows.length && <div className="company-empty"><Icon name={page} /><p>暂无{page === 'upload' ? '上传' : '下载'}记录</p></div>}</div> : <><div className="company-list" tabIndex={-1} ref={page === 'files' ? uploadZone : undefined} onContextMenu={e => { if (!(e.target as Element).closest('tbody tr,input,textarea,[contenteditable="true"]') && page === 'files') openMenu('background', e, true); }}><table className={page === 'recycled' ? 'company-recycle-table' : ''}><thead><tr><th className="company-check"><input ref={allCheck} type="checkbox" aria-label="选择当前页全部文件" checked={!!visibleFiles.length && chosen.length === visibleFiles.length} disabled={!visibleFiles.length || busy} onChange={e => setSelected(e.target.checked ? visibleFiles.map(f => f.assetId) : [])} /></th><th>文件名</th><th>大小</th><th>{page === 'files' ? <button className="company-sort-button" onClick={() => setDescending(v => !v)} aria-label={`上传时间${descending ? '倒序' : '正序'}`}>上传时间 {descending ? '↓' : '↑'}</button> : '上传时间'}</th><th>上传者</th>{page === 'recycled' && <><th><button className="company-sort-button" onClick={() => setDescending(v => !v)}>删除时间 {descending ? '↓' : '↑'}</button></th><th>删除者</th></>}<th>操作</th></tr></thead><tbody>{visibleFiles.map(file => <tr key={file.assetId} tabIndex={-1} onContextMenu={e => fileContext(file, e)} className={selected.includes(file.assetId) ? 'selected' : ''}><td className="company-check"><input type="checkbox" aria-label={`选择 ${file.name}`} checked={selected.includes(file.assetId)} disabled={busy} onChange={e => setSelected(previous => e.target.checked ? [...previous, file.assetId] : previous.filter(id => id !== file.assetId))} /></td><td><span className="company-file-name"><FileIcon path={file.name} /><span className="company-file-details"><span title={file.name}>{file.name}</span><small className="company-cache-status">{file.cached ? offline ? '离线可用' : '已下载' : '未缓存'}</small></span></span></td><td>{fileSize(file.size)}</td><td className="company-date">{date(file.createdAt)}</td><td title={file.uploaderUsername || file.uploaderName}>{file.uploaderUsername || file.uploaderName || '原上传人'}</td>{page === 'recycled' && <><td className="company-date">{date(file.deletedAt)}</td><td title={file.deleterUsername || '未知'}>{file.deleterUsername || '未知'}</td></>}<td><div className="company-actions">{page === 'files' ? <><button className="company-row-button" aria-label="下载" title="下载" disabled={busy || !!blocked || transfers[`download:${file.assetId}`]?.status === '传输中'} onClick={() => void startTransfer(file, 'download')}><Icon name="download" /></button>{rights.canRecycle && <button className="company-row-button" aria-label="删除" title="删除" disabled={busy || !!blocked} onClick={() => manage(file, 'recycle')}><Icon name="trash" /></button>}{moreMenu(file)}</> : <>{rights.canRestore && <button className="company-row-button" aria-label="恢复" title="恢复" disabled={busy || !!blocked} onClick={() => manage(file, 'restore')}><Icon name="restore" /></button>}{admin && <button className="company-row-button company-danger" aria-label="永久删除" title="永久删除" disabled={busy || !!blocked} onClick={() => manage(file, 'purge')}><Icon name="trash" /></button>}</>}</div></td></tr>)}</tbody></table>{dragging && page === 'files' && <div className="company-drop-overlay" role="status"><Icon name="upload" /><strong>松开即可上传文件</strong></div>}{view && !files.length && <div className="company-empty"><Icon name={page === 'recycled' ? 'recycle' : 'folder'} /><p>{query ? '没有匹配的文件' : page === 'recycled' ? '回收站为空' : '暂无云文件'}</p></div>}</div></>}
    {pagination}</main>
    {menu && menuItems.length > 0 && <div ref={menuRef} className="company-menu-popup company-floating-menu" role="menu" tabIndex={-1} style={{ left: menuPosition.x, top: menuPosition.y }} onKeyDown={e => {
      if (e.key === 'Tab') { setMenu(''); return; }
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
      e.preventDefault(); const buttons = [...e.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')], index = buttons.indexOf(document.activeElement as HTMLButtonElement);
      const next = e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : e.key === 'ArrowDown' ? (index + 1) % buttons.length : index < 0 ? buttons.length - 1 : (index - 1 + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }}>{menuItems.map((item, i) => item ? <button key={i} role="menuitem" disabled={item.disabled} onClick={() => { menuTrigger.current?.focus(); setMenu(''); void Promise.resolve(item.action()).catch(e => setMessage(String(e))); }}>{item.label}</button> : <div key={i} className="menu-separator" role="separator" />)}</div>}
    {dialog && <FormDialog key={dialog.epoch} spec={dialog.spec} close={() => { if (currentEpoch.current === dialog.epoch) setDialog(previous => previous === dialog ? null : previous); }} />}
  </section>;
}

export { CompanyFilePicker } from './CompanyPicker';

/** 独立窗口和文件关联页共用表格；分页、搜索、排序变化均清空当前选择。 */
export function CloudFileTable({ status, limit, selected, onChange }: { status: TeamStatus; limit: number; selected: CompanyFile[]; onChange: (files: CompanyFile[]) => void }) {
  const [query, setQuery] = useState(''), [page, setPage] = useState(0), [size, setSize] = useState(10), [descending, setDescending] = useState(true), [uploading, setUploading] = useState(false);
  const notices = useNotices(); const setMessage = notices.error;
  const { view, offline: catalogOffline, error, refresh } = useCompanyCatalog(status, false, query);
  const offline = status.offline || catalogOffline, blocked = unavailable(status) || (catalogOffline ? offlineNotice : ''), epoch = status.sessionEpoch || 0, currentEpoch = useRef(epoch); currentEpoch.current = epoch;
  const rows = [...(view?.files || [])].sort((a, b) => descending ? b.createdAt - a.createdAt : a.createdAt - b.createdAt);
  const pages = Math.max(1, Math.ceil(rows.length / size)), currentPage = Math.min(page, pages - 1), visible = rows.slice(currentPage * size, (currentPage + 1) * size);
  const change = (action: () => void) => { onChange([]); action(); };
  useEffect(() => { onChange([]); setPage(0); notices.clear(); }, [epoch]);
  useEffect(() => { if (offline) onChange([]); }, [offline]);
  useEffect(() => { if (view) { const valid = selected.map(f => view.files.find(v => v.assetId === f.assetId)).filter((f): f is CompanyFile => !!f); if (valid.length !== selected.length) onChange(valid); } }, [view]);
  async function uploadFiles() {
    setUploading(true); setMessage('');
    try { const paths = await open({ title: '上传新文件', multiple: true, directory: false }); if (paths && currentEpoch.current === epoch) { await call('enqueue_company_uploads', { sessionEpoch: epoch, paths: Array.isArray(paths) ? paths : [paths] }); notices.success('已加入上传队列，完成后会显示在列表中。'); } }
    catch (e) { if (currentEpoch.current === epoch) setMessage(String(e)); }
    finally { if (currentEpoch.current === epoch) setUploading(false); }
  }
  return <div className="cloud-selection">
    <div className="company-toolbar"><input type="text" aria-label="搜索云文件" placeholder="搜索文件名" value={query} onChange={e => change(() => { setQuery(e.target.value); setPage(0); })} /><button type="button" className="secondary-button" onClick={() => void refresh()}>刷新</button><span className="toolbar-spacer" />{companyPermissions(status).canUpload && <button type="button" className="primary-button" disabled={uploading || !!blocked} onClick={() => void uploadFiles()}>上传新文件</button>}</div>
    {(error || blocked) && <p className="validation-error" role="alert">{error || blocked}</p>}
    <AccountNotice message={notices.current?.message || ''} error={notices.current?.kind !== 'success'} count={notices.count} clear={() => notices.current && notices.dismiss(notices.current.id)} />
    <div className="company-table-wrap"><table className="company-table"><thead><tr><th><input type="checkbox" aria-label="选择当前页" disabled={!!blocked || !visible.length} checked={!!visible.length && visible.every(f => selected.some(s => s.assetId === f.assetId))} onChange={e => onChange(e.target.checked ? visible.slice(0, limit) : [])} /></th><th>文件名</th><th>大小</th><th><button type="button" onClick={() => change(() => { setDescending(!descending); setPage(0); })}>上传时间 {descending ? '↓' : '↑'}</button></th><th>上传者</th></tr></thead><tbody>{visible.map(file => { const checked = selected.some(f => f.assetId === file.assetId); return <tr key={file.assetId} className={checked ? 'selected' : ''}><td><input type="checkbox" aria-label={`选择 ${file.name}`} checked={checked} disabled={!!blocked || (!checked && selected.length >= limit)} onChange={e => onChange(e.target.checked ? [...selected, file] : selected.filter(f => f.assetId !== file.assetId))} /></td><td><div className="cloud-file-name"><FileIcon path={file.name} /><span title={file.name}>{file.name}<small>{file.cached ? offline ? '离线可用' : '已下载' : '未缓存'}</small></span></div></td><td>{fileSize(file.size)}</td><td>{date(file.createdAt)}</td><td>{file.uploaderUsername || file.uploaderName}</td></tr>; })}</tbody></table>{!rows.length && <p className="company-empty">{blocked ? '云文件暂不可用' : view ? '没有匹配的文件' : '正在加载…'}</p>}</div>
    <div className="company-pagination"><label>每页 <select aria-label="每页数量" value={size} onChange={e => change(() => { setSize(Number(e.target.value)); setPage(0); })}>{[10,20,50].map(n => <option key={n}>{n}</option>)}</select> 条</label><span className="toolbar-spacer" /><button type="button" disabled={currentPage === 0} onClick={() => change(() => setPage(currentPage - 1))}>上一页</button><span>{currentPage + 1} / {pages}</span><button type="button" disabled={currentPage + 1 === pages} onClick={() => change(() => setPage(currentPage + 1))}>下一页</button></div>
  </div>;
}
