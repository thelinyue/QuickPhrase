import { useCallback, useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { confirm, open, save } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { FileIcon, FormDialog, Icon, Titlebar, type DialogSpec } from './ui';
import type { CompanyFile, CompanyFilePermissions, CompanyTransfer, CompanyView, TeamStatus, TeamUser } from './types';

export const fileSize = (size: number) => size < 1024 ? `${size} B` : size < 1024 * 1024 ? `${(size / 1024).toFixed(1)} KB` : `${(size / 1024 / 1024).toFixed(1)} MB`;
const noPermissions: CompanyFilePermissions = { canUpload: false, canRecycle: false, canRestore: false, version: 0, uploadVersion: 0 };
export function companyPermissions(status: TeamStatus): CompanyFilePermissions {
  const rights = status.companyFilePermissions ? status.user?.companyFilePermissions || noPermissions : noPermissions;
  return status.user?.admin ? { ...rights, canUpload: true, canRecycle: true, canRestore: true } : rights;
}
function unavailable(status: TeamStatus) {
  return !status.user ? '请在账号与同步中登录。' : status.user.mustChangePassword ? '请先修改初始密码。' : !status.companyFiles ? '当前 Hub 尚不支持云文件，请升级服务端。' : status.offline ? '当前离线，请联网后刷新。' : '';
}
/** 请求世代与筛选条件一起核验，旧账号或旧搜索结果不能覆盖当前目录。 */
function useCompanyCatalog(status: TeamStatus, recycled: boolean, query: string, enabled = true) {
  const [view, setView] = useState<CompanyView | null>(null), [error, setError] = useState(''); const sequence = useRef(0);
  const epoch = status.sessionEpoch || 0, userId = status.user?.id || '', blocked = unavailable(status);
  const current = useRef({ epoch, recycled, query, enabled }); current.current = { epoch, recycled, query, enabled };
  const refresh = useCallback(async () => {
    const filter = current.current;
    if (filter.epoch !== epoch || filter.recycled !== recycled || filter.query !== query || filter.enabled !== enabled) return;
    const request = ++sequence.current;
    const valid = () => request === sequence.current && current.current.epoch === epoch && current.current.recycled === recycled && current.current.query === query && current.current.enabled === enabled;
    if (!enabled || !userId || blocked) { setView(null); setError(''); return; }
    try { const next = await call<CompanyView>('get_company_files', { sessionEpoch: epoch, recycled, query }); if (valid()) { setView(next); setError(''); } }
    catch (e) { if (valid()) { setView(null); setError(String(e)); } }
  }, [epoch, userId, blocked, recycled, query, enabled]);
  useEffect(() => { setView(null); void refresh(); let disposed = false, stop: (() => void) | undefined;
    void listen<number>('company-files-changed', e => { if (!disposed && e.payload === epoch) void refresh(); }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; ++sequence.current; stop?.(); };
  }, [refresh, epoch]);
  return { view, error, refresh };
}
type Page = 'files' | 'permissions' | 'recycled' | 'upload' | 'download';
const date = (seconds: number | null) => seconds == null ? '—' : new Date(seconds * 1000).toLocaleString('zh-CN', { hour12: false });
const transferKey = (task: CompanyTransfer) => `${task.direction}:${task.assetId}`;
/** 事件与快照可能交错，只接受较新进度；读取快照不会重新启动任务。 */
function mergeTransfers(previous: Record<string, CompanyTransfer>, records: CompanyTransfer[]) {
  const next = { ...previous };
  for (const task of records) { const key = transferKey(task); if (!next[key] || (task.revision || 0) >= (next[key].revision || 0)) next[key] = task; }
  return next;
}

/** 窗口仅负责导航和草稿；授权、引用检查与传输状态由 Rust／Hub 决定。 */
export function CompanyFilesWindow() {
  const { status, version } = useTeams(); const epoch = status.sessionEpoch || 0, admin = !!status.user?.admin, rights = companyPermissions(status);
  const blocked = unavailable(status), canViewRecycle = admin || rights.canRecycle || rights.canRestore;
  const [page, setPage] = useState<Page>('files'), [query, setQuery] = useState(''), [descending, setDescending] = useState(true);
  const { view, error, refresh } = useCompanyCatalog(status, page === 'recycled', query, page === 'files' || (page === 'recycled' && canViewRecycle));
  const [message, setMessage] = useState(''), [busy, setBusy] = useState(false), [dialog, setDialog] = useState<{ spec: DialogSpec; epoch: number } | null>(null);
  const [selected, setSelected] = useState<string[]>([]), [menu, setMenu] = useState(''); const menuRef = useRef<HTMLDivElement>(null);
  const [offset, setOffset] = useState(0), [pageSize, setPageSize] = useState(10), [jump, setJump] = useState('1');
  const [users, setUsers] = useState<TeamUser[]>([]), [drafts, setDrafts] = useState<Record<string, CompanyFilePermissions>>({});
  const [transfers, setTransfers] = useState<Record<string, CompanyTransfer>>({});
  const busyRef = useRef(false), dirtyRef = useRef(false), draftRef = useRef(drafts); draftRef.current = drafts; dirtyRef.current = Object.keys(drafts).length > 0;
  const currentEpoch = useRef(epoch); currentEpoch.current = epoch; const statusRef = useRef(status); statusRef.current = status;
  const taskLocks = useRef(new Set<string>()), transferSequence = useRef(0), usersSequence = useRef(0);
  const dialogAllowed = useRef<() => boolean>(() => true); const allCheck = useRef<HTMLInputElement>(null);
  useEffect(() => { ++transferSequence.current; ++usersSequence.current; busyRef.current = false; setBusy(false); setTransfers({}); setUsers([]); setDrafts({}); setMessage(''); setDialog(null); setPage('files'); setQuery(''); setSelected([]); setMenu(''); setOffset(0); }, [epoch]);
  useEffect(() => { if ((page === 'permissions' && !admin) || (page === 'recycled' && !canViewRecycle)) { setPage('files'); setQuery(''); setSelected([]); setDrafts({}); setUsers([]); } }, [page, admin, canViewRecycle]);
  useEffect(() => { if (dialog && (blocked || !dialogAllowed.current())) setDialog(null); }, [dialog, blocked, rights.canUpload, rights.canRecycle, rights.canRestore, admin]);
  useEffect(() => { setSelected([]); setMenu(''); setOffset(0); }, [query, descending, pageSize]);
  useEffect(() => { if (!view) return; const available = new Set(view.files.map(f => f.assetId)); setSelected(previous => previous.filter(id => available.has(id))); }, [view]);
  useEffect(() => {
    if (!menu) return;
    menuRef.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    const outside = (e: PointerEvent) => { if (!menuRef.current?.contains(e.target as Node)) setMenu(''); };
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); menuRef.current?.querySelector<HTMLButtonElement>('[aria-haspopup]')?.focus(); setMenu(''); } };
    document.addEventListener('pointerdown', outside); document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };
  }, [menu]);
  async function discardAllowed() { return !dirtyRef.current || await confirm('放弃未保存的云文件权限调整？', { title: '未保存的权限调整', kind: 'warning' }); }
  async function navigate(next: Page) {
    const scope = epoch; if (next === page || busyRef.current || !await discardAllowed() || currentEpoch.current !== scope) return;
    setDrafts({}); dirtyRef.current = false; setPage(next); setQuery(''); setSelected([]); setOffset(0); setMenu(''); setMessage('');
  }
  useEffect(() => { let disposed = false, stop: (() => void) | undefined;
    void getCurrentWindow().onCloseRequested(async e => { if (!dirtyRef.current && !busyRef.current) return; e.preventDefault(); if (!busyRef.current && await discardAllowed()) { dirtyRef.current = false; await getCurrentWindow().close(); } }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; stop?.(); };
  }, []);
  async function loadTransfers() {
    const request = ++transferSequence.current;
    try { const records = await call<CompanyTransfer[]>('get_company_transfers', { sessionEpoch: epoch }); if (currentEpoch.current === epoch && request === transferSequence.current) setTransfers(previous => mergeTransfers(previous, records)); }
    catch (e) { if (currentEpoch.current === epoch && request === transferSequence.current) setMessage(String(e)); }
  }
  useEffect(() => { if (!status.user || !status.companyFiles) return; let disposed = false, stop: (() => void) | undefined;
    void listen<CompanyTransfer>('company-transfer', e => { if (!disposed && e.payload.sessionEpoch === epoch) setTransfers(previous => mergeTransfers(previous, [e.payload])); }).then(fn => { if (disposed) fn(); else { stop = fn; void loadTransfers(); } });
    return () => { disposed = true; ++transferSequence.current; stop?.(); };
  }, [epoch, status.user?.id, status.companyFiles]);
  async function loadUsers() {
    const request = ++usersSequence.current;
    try { const records = await call<TeamUser[]>('team_admin', { action: 'users', sessionEpoch: epoch }); if (request === usersSequence.current && currentEpoch.current === epoch && statusRef.current.user?.admin) setUsers(records); }
    catch (e) { if (request === usersSequence.current && currentEpoch.current === epoch) setMessage(String(e)); }
  }
  useEffect(() => { if (page === 'permissions' && admin && status.companyFilePermissions && !blocked) void loadUsers(); }, [page, admin, status.companyFilePermissions, blocked, epoch, version]);
  async function run(action: () => Promise<unknown>) {
    if (busyRef.current) return; busyRef.current = true; setBusy(true); setMessage('');
    try { await action(); if (currentEpoch.current === epoch) await refresh(); }
    catch (e) { if (currentEpoch.current === epoch) { setMessage(String(e)); if (page === 'permissions') await loadUsers(); await refresh(); } }
    finally { if (currentEpoch.current === epoch) { busyRef.current = false; setBusy(false); } }
  }
  async function transfer(file: { assetId: string; name: string }, direction: 'upload' | 'download', queryOnly = false) {
    const key = `${epoch}:${direction}:${file.assetId}`; if (currentEpoch.current !== epoch || taskLocks.current.has(key)) return '';
    taskLocks.current.add(key);
    try { await call(queryOnly ? 'query_company_upload' : direction === 'upload' ? 'upload_company_file' : 'download_company_file', { sessionEpoch: epoch, id: file.assetId }); return ''; }
    catch (e) { return String(e); }
    finally { taskLocks.current.delete(key); if (currentEpoch.current === epoch) { await loadTransfers(); await refresh(); } }
  }
  async function startTransfer(file: { assetId: string; name: string }, direction: 'upload' | 'download', queryOnly = false) { setMessage(''); const error = await transfer(file, direction, queryOnly); if (currentEpoch.current === epoch) setMessage(error || (queryOnly ? '原任务已登记，上传结果已核对。' : '')); }
  async function upload() {
    const path = await open({ directory: false, multiple: false, title: '上传新文件（最多128MB）' });
    if (!path || Array.isArray(path) || currentEpoch.current !== epoch) return;
    const task = await call<{ id: string; name: string }>('prepare_company_upload', { sessionEpoch: epoch, path });
    if (currentEpoch.current !== epoch) return; setPage('upload'); setQuery(''); setOffset(0); setSelected([]);
    void startTransfer({ assetId: task.id, name: task.name }, 'upload');
  }
  function showDialog(spec: DialogSpec, allowed: () => boolean) { dialogAllowed.current = allowed; setMenu(''); setDialog({ spec, epoch }); }
  function canManage(operation: 'recycle' | 'restore' | 'purge') { const current = companyPermissions(statusRef.current); return currentEpoch.current === epoch && !unavailable(statusRef.current) && (operation === 'purge' ? !!statusRef.current.user?.admin : operation === 'recycle' ? current.canRecycle : current.canRestore); }
  function manage(file: CompanyFile, operation: 'recycle' | 'restore' | 'purge') {
    showDialog({ title: operation === 'purge' ? '永久删除云文件' : operation === 'recycle' ? '删除云文件' : '恢复云文件', fields: <><p>{operation === 'recycle' ? '将所选文件移入回收站？已有话术继续可用，不能再新增引用。' : operation === 'purge' ? '所有共享库及话术回收记录均无引用时才能永久删除。个人副本继续保留。' : '恢复原文件及目录入口。'}</p><p>{file.name}</p>{operation === 'purge' && <label>输入完整文件名<input name="confirmName" autoComplete="off" required /></label>}</>, onSubmit: async form => { if (!canManage(operation)) throw new Error('操作权限已变化，请刷新后重试。'); await call('manage_company_file', { sessionEpoch: epoch, id: file.assetId, operation, confirmName: String(form.get('confirmName') || '') }); if (currentEpoch.current === epoch) await refresh(); } }, () => canManage(operation));
  }
  async function saveAs(file: CompanyFile) { const path = await save({ title: '另存云文件', defaultPath: file.name }); if (path && currentEpoch.current === epoch) await call('save_company_file_as', { sessionEpoch: epoch, id: file.assetId, path }); }
  function generate(file: CompanyFile) {
    showDialog({ title: '生成文件话术', className: 'account-dialog', fields: <><p className="company-dialog-file"><FileIcon path={file.name} />{file.name}</p><label>生成话术到<select name="target" defaultValue=""><option value="">我的话术</option>{status.teams.filter(t => t.editable).map(t => <option key={t.teamId} value={t.teamId}>{t.name}</option>)}</select></label></>, submit: '下一步', onSubmit: async form => { if (currentEpoch.current !== epoch) throw new Error('账号已变化，请重新选择文件。'); await openWindow('editor', '', '', `cloud:${epoch}:${file.assetId}`, '', '', String(form.get('target') || '')); } }, () => currentEpoch.current === epoch);
  }
  const files = [...(view?.files || [])].sort((a, b) => { const diff = page === 'recycled' ? (a.deletedAt || 0) - (b.deletedAt || 0) : a.createdAt - b.createdAt; return (descending ? -diff : diff) || a.assetId.localeCompare(b.assetId); });
  const term = query.trim().toLowerCase(), filteredUsers = users.filter(u => `${u.username} ${u.displayName}`.toLowerCase().includes(term));
  const taskRows = Object.values(transfers).filter(t => t.direction === page);
  const count = page === 'permissions' ? filteredUsers.length : page === 'upload' || page === 'download' ? taskRows.length : files.length;
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
  async function performBatch(operation: 'download' | 'recycle' | 'restore', targets: CompanyFile[]) {
    const action = async () => {
      const results: { name: string; error: string }[] = [];
      if (operation === 'download') { const responses = await Promise.all(targets.map(f => transfer(f, 'download'))); responses.forEach((error, i) => results.push({ name: targets[i].name, error })); }
      else for (const file of targets) { if (currentEpoch.current !== epoch) break; try { await call('manage_company_file', { sessionEpoch: epoch, id: file.assetId, operation, confirmName: '' }); results.push({ name: file.name, error: '' }); } catch (e) { results.push({ name: file.name, error: String(e) }); } }
      if (currentEpoch.current === epoch) { const failures = results.filter(r => r.error); setMessage(`成功 ${results.length - failures.length} 个，失败 ${failures.length} 个：${results.map(r => `${r.name}（${r.error || '成功'}）`).join('；')}`); setSelected([]); }
    };
    // 批量下载使用 Rust 的两个许可，界面仍可进入下载管理并停止任务。
    if (operation === 'download') { setMessage(''); await action(); }
    else await run(action);
  }
  async function savePermissions(user: TeamUser) {
    const draft = draftRef.current[user.id]; if (!draft || currentEpoch.current !== epoch) return;
    await run(async () => {
      await call('team_admin', { action: 'update-user', id: user.id, sessionEpoch: epoch, request: { companyFilePermissions: { canUpload: draft.canUpload, canRecycle: draft.canRecycle, canRestore: draft.canRestore, expectedVersion: draft.version } } });
      if (currentEpoch.current !== epoch) return; setDrafts(previous => { const next = { ...previous }; delete next[user.id]; return next; }); await loadUsers(); setMessage(`${user.username} 的云文件权限已保存。`);
    });
  }
  const search = <div className="company-search"><input aria-label={page === 'permissions' ? '搜索用户名或昵称' : '搜索云文件'} placeholder={page === 'permissions' ? '输入用户名或昵称搜索' : '输入文件名称搜索'} value={query} onChange={e => setQuery(e.target.value)} /><Icon name="search" /></div>;
  const refreshButton = <button className="company-button" disabled={busy} onClick={() => void run(async () => { await call('team_refresh'); if (currentEpoch.current !== epoch) return; if (page === 'permissions') await loadUsers(); else if (page === 'upload' || page === 'download') await loadTransfers(); else await refresh(); })}><Icon name="refresh" />刷新</button>;
  function moreMenu(file: CompanyFile) { return <div className="company-menu" ref={menu === file.assetId ? menuRef : undefined}><button className="company-row-button" aria-label="更多" title="更多" disabled={busy || !!blocked} aria-haspopup="menu" aria-expanded={menu === file.assetId} onClick={() => setMenu(menu === file.assetId ? '' : file.assetId)}><Icon name="more" /></button>{menu === file.assetId && <div className="company-menu-popup" role="menu"><button role="menuitem" onClick={() => { setMenu(''); void run(() => saveAs(file)); }}>另存为</button><button role="menuitem" onClick={() => generate(file)}>生成话术</button></div>}</div>; }
  const pagination = <div className="company-pagination"><span>共{pageCount}页 {count}条</span><button aria-label="上一页" disabled={currentPage === 0} onClick={() => changePage(currentPage - 1)}>‹</button><span className="company-page-number">{currentPage + 1}</span><button aria-label="下一页" disabled={currentPage === pageCount - 1} onClick={() => changePage(currentPage + 1)}>›</button><form onSubmit={e => { e.preventDefault(); const value = Number(jump); if (Number.isInteger(value) && value >= 1 && value <= pageCount) changePage(value - 1); }}><label>到第<input aria-label="跳转页码" inputMode="numeric" value={jump} onChange={e => setJump(e.target.value)} />页</label><button type="submit">跳转</button></form><select aria-label="每页条数" value={pageSize} onChange={e => setPageSize(Number(e.target.value))}>{[10, 20, 50].map(n => <option key={n} value={n}>{n}条/页</option>)}</select></div>;

  return <section className="client-window company-window"><Titlebar title="云文件管理中心" minimize maximize />
    <div className="company-body"><nav className="company-sidebar" aria-label="云文件导航"><button className={page === 'files' || page === 'upload' || page === 'download' ? 'active' : ''} onClick={() => void navigate('files')}><Icon name="folder" />云文件管理</button><div className="company-nav-spacer" />{admin && <button className={page === 'permissions' ? 'active' : ''} onClick={() => void navigate('permissions')}><Icon name="shield" />云文件权限管理</button>}{canViewRecycle && <button className={page === 'recycled' ? 'active' : ''} onClick={() => void navigate('recycled')}><Icon name="recycle" />云文件回收站</button>}<div className="company-account" title={status.user?.displayName}>{status.user?.username || status.username}<small>单文件最多128MB</small></div></nav>
    <main className="company-content">{page === 'files' ? <div className="company-toolbar">{search}{refreshButton}<button className="company-button" onClick={() => void navigate('download')}><Icon name="download" />下载管理</button><button className="company-button" onClick={() => void navigate('upload')}><Icon name="upload" />上传管理</button>{rights.canUpload && <button className="company-button company-primary" disabled={busy || !!blocked} onClick={() => void run(upload)}><Icon name="upload" />上传新文件</button>}</div> : page === 'upload' || page === 'download' ? <div className="company-toolbar"><button className="company-button" onClick={() => void navigate('files')}><Icon name="back" />返回</button><span className="company-toolbar-spacer">{page === 'upload' ? '上传管理' : '下载管理'}</span><button className="company-button" disabled={busy || !status.user || !status.companyFiles} onClick={() => void run(async () => { ++transferSequence.current; const records = await call<CompanyTransfer[]>('clear_company_transfers', { sessionEpoch: epoch, direction: page }); if (currentEpoch.current === epoch) setTransfers(previous => mergeTransfers(Object.fromEntries(Object.entries(previous).filter(([, r]) => r.direction !== page || r.status === '传输中' || (r.direction === 'upload' && r.status !== '已完成'))), records)); })} title="清空已结束记录，保留正在传输和结果待核对的任务"><Icon name="trash" />清空</button></div> : <div className="company-toolbar"><strong>{page === 'permissions' ? '云文件权限管理' : '云文件回收站'}</strong>{search}{refreshButton}</div>}
    {(blocked || error || message) && <p className="company-message" role={blocked || error ? 'alert' : 'status'}>{message || error || blocked}</p>}
    {(page === 'files' || page === 'recycled') && <div className="company-selection-toolbar"><div className="company-menu company-batch-menu" ref={menu === 'batch' ? menuRef : undefined}><button className="company-button" disabled={!chosen.length || busy || !!blocked || (page === 'recycled' && !rights.canRestore)} aria-haspopup="menu" aria-expanded={menu === 'batch'} onClick={() => setMenu(menu === 'batch' ? '' : 'batch')}><Icon name="menu" />批量操作</button>{menu === 'batch' && <div className="company-menu-popup" role="menu">{page === 'files' && <button role="menuitem" onClick={() => void batch('download')}>批量下载</button>}{page === 'files' && rights.canRecycle && <button role="menuitem" onClick={() => void batch('recycle')}>批量删除</button>}{page === 'recycled' && rights.canRestore && <button role="menuitem" onClick={() => void batch('restore')}>批量恢复</button>}</div>}</div><span aria-live="polite">已选 {chosen.length} 项</span></div>}
    {page === 'permissions' && !status.companyFilePermissions ? <div className="company-empty"><Icon name="shield" /><p>当前 Hub 使用固定管理员权限，升级后可按用户名授权。</p></div> : page === 'permissions' ? <><p className="company-hint">用户均可查看和使用云文件。永久删除和权限调整仅服务管理员可执行。</p><div className="company-list"><table><thead><tr><th>用户名</th><th>昵称</th><th>状态</th><th>上传文件</th><th>删除文件</th><th>恢复文件</th><th>操作</th></tr></thead><tbody>{rows(filteredUsers).map(user => { const base = user.companyFilePermissions || noPermissions, draft = drafts[user.id], permissions = user.admin ? { ...base, canUpload: true, canRecycle: true, canRestore: true } : draft || base; const conflict = !!draft && draft.version !== base.version; return <tr key={user.id}><td>{user.username}{conflict && <small className="company-permission-conflict" role="alert">权限已变更，草稿已保留</small>}</td><td>{user.displayName || '—'}</td><td>{user.disabled ? '已停用' : user.admin ? '服务管理员' : '已启用'}</td>{(['canUpload', 'canRecycle', 'canRestore'] as const).map((key, i) => <td key={key}><input type="checkbox" aria-label={`${user.username}的${['上传', '删除', '恢复'][i]}权限`} checked={permissions[key]} disabled={busy || !!blocked || user.admin} onChange={e => setDrafts(previous => ({ ...previous, [user.id]: { ...(previous[user.id] || base), [key]: e.target.checked } }))} /></td>)}<td>{user.admin ? <span className="company-row-button company-muted" role="img" aria-label="固定全部权限" title="固定全部权限"><Icon name="shield" /></span> : <div className="company-permission-actions"><button className="company-row-button" aria-label="保存" title="保存" disabled={busy || !!blocked || !draft || conflict} onClick={() => void savePermissions(user)}><Icon name="save" /></button>{draft && <button className="company-row-button" aria-label={conflict ? '采用最新' : '取消'} title={conflict ? '采用最新' : '取消'} disabled={busy} onClick={() => setDrafts(previous => { const next = { ...previous }; delete next[user.id]; return next; })}><Icon name={conflict ? 'refresh' : 'close'} /></button>}</div>}</td></tr>; })}</tbody></table>{!filteredUsers.length && <div className="company-empty"><Icon name="shield" /><p>{query ? '没有匹配的用户名' : '暂无用户名'}</p></div>}</div></> : page === 'upload' || page === 'download' ? <div className="company-list"><table className="company-transfer-table"><thead><tr><th>文件名</th><th>{page === 'upload' ? '上传进度' : '下载进度'}</th><th>状态</th><th>操作</th></tr></thead><tbody>{rows(taskRows).map(task => <tr key={transferKey(task)}><td><span className="company-file-name"><FileIcon path={task.name} /><span title={task.name}>{task.name}</span></span></td><td><div className="company-progress"><progress value={task.done} max={task.total || 1} /><span>{task.total ? Math.floor(task.done / task.total * 100) : 0}%</span></div><small>{fileSize(task.done)} / {fileSize(task.total)}</small></td><td><span className={task.status === '已完成' ? 'company-success' : ''}>{task.status}</span></td><td><div className="company-actions">{task.status === '传输中' ? <button className="company-row-button" aria-label="停止" title="停止" onClick={() => void call('cancel_company_transfer', { sessionEpoch: epoch, id: task.assetId }).catch(e => { if (currentEpoch.current === epoch) setMessage(String(e)); })}><Icon name="stop" /></button> : task.direction === 'upload' && task.status !== '已完成' ? <><button className="company-row-button" aria-label="查询结果" title="查询结果" disabled={!!blocked} onClick={() => void startTransfer(task, 'upload', true)}><Icon name="search" /></button>{rights.canUpload && <button className="company-row-button" aria-label="继续原任务" title="继续原任务" disabled={!!blocked} onClick={() => void startTransfer(task, 'upload')}><Icon name="play" /></button>}</> : task.direction === 'download' && task.status !== '已完成' ? <button className="company-row-button" aria-label="重新下载" title="重新下载" disabled={!!blocked} onClick={() => void startTransfer(task, 'download')}><Icon name="download" /></button> : null}</div></td></tr>)}</tbody></table>{!taskRows.length && <div className="company-empty"><Icon name={page} /><p>暂无{page === 'upload' ? '上传' : '下载'}记录</p></div>}</div> : <div className="company-list"><table className={page === 'recycled' ? 'company-recycle-table' : ''}><thead><tr><th className="company-check"><input ref={allCheck} type="checkbox" aria-label="选择当前页全部文件" checked={!!visibleFiles.length && chosen.length === visibleFiles.length} disabled={!visibleFiles.length || busy} onChange={e => setSelected(e.target.checked ? visibleFiles.map(f => f.assetId) : [])} /></th><th>文件名</th><th>大小</th><th>{page === 'files' ? <button className="company-sort-button" onClick={() => setDescending(v => !v)} aria-label={`上传时间${descending ? '倒序' : '正序'}`}>上传时间 {descending ? '↓' : '↑'}</button> : '上传时间'}</th><th>上传者</th>{page === 'recycled' && <><th><button className="company-sort-button" onClick={() => setDescending(v => !v)}>删除时间 {descending ? '↓' : '↑'}</button></th><th>删除者</th></>}<th>操作</th></tr></thead><tbody>{visibleFiles.map(file => <tr key={file.assetId} className={selected.includes(file.assetId) ? 'selected' : ''}><td className="company-check"><input type="checkbox" aria-label={`选择 ${file.name}`} checked={selected.includes(file.assetId)} disabled={busy} onChange={e => setSelected(previous => e.target.checked ? [...previous, file.assetId] : previous.filter(id => id !== file.assetId))} /></td><td><span className="company-file-name"><FileIcon path={file.name} /><span title={file.name}>{file.name}</span></span><small className="company-cache-status">{file.cached ? '已下载' : '未下载'}</small></td><td>{fileSize(file.size)}</td><td className="company-date">{date(file.createdAt)}</td><td title={file.uploaderUsername || file.uploaderName}>{file.uploaderUsername || file.uploaderName || '原上传人'}</td>{page === 'recycled' && <><td className="company-date">{date(file.deletedAt)}</td><td title={file.deleterUsername || '未知'}>{file.deleterUsername || '未知'}</td></>}<td><div className="company-actions">{page === 'files' ? <><button className="company-row-button" aria-label="下载" title="下载" disabled={busy || !!blocked || transfers[`download:${file.assetId}`]?.status === '传输中'} onClick={() => void startTransfer(file, 'download')}><Icon name="download" /></button>{rights.canRecycle && <button className="company-row-button" aria-label="删除" title="删除" disabled={busy || !!blocked} onClick={() => manage(file, 'recycle')}><Icon name="trash" /></button>}{moreMenu(file)}</> : <>{rights.canRestore && <button className="company-row-button" aria-label="恢复" title="恢复" disabled={busy || !!blocked} onClick={() => manage(file, 'restore')}><Icon name="restore" /></button>}{admin && <button className="company-row-button company-danger" aria-label="永久删除" title="永久删除" disabled={busy || !!blocked} onClick={() => manage(file, 'purge')}><Icon name="trash" /></button>}</>}</div></td></tr>)}</tbody></table>{view && !files.length && <div className="company-empty"><Icon name={page === 'recycled' ? 'recycle' : 'folder'} /><p>{query ? '没有匹配的文件' : page === 'recycled' ? '回收站为空' : '暂无云文件'}</p></div>}</div>}
    {!(page === 'permissions' && !status.companyFilePermissions) && pagination}</main></div>
    {dialog && <FormDialog key={dialog.epoch} spec={dialog.spec} close={() => { if (currentEpoch.current === dialog.epoch) setDialog(previous => previous === dialog ? null : previous); }} />}
  </section>;
}

export function CompanyFilePicker({ limit, onConfirm, close }: { limit: number; onConfirm: (files: CompanyFile[], epoch: number) => void; close: () => void }) {
  const { status } = useTeams(); const [query, setQuery] = useState(''), [selected, setSelected] = useState<CompanyFile[]>([]); const ref = useRef<HTMLDialogElement>(null);
  const { view, error } = useCompanyCatalog(status, false, query); const blocked = unavailable(status), epoch = status.sessionEpoch || 0;
  useEffect(() => { ref.current?.showModal(); }, []); useEffect(() => setSelected([]), [epoch]);
  // 保存已选文件的元数据，搜索只改变可见行；保存时 Rust 再次核验权限和回收状态。
  return <dialog ref={ref} className="company-picker small-dialog" aria-label="选择云文件" onCancel={close}><h2>选择云文件</h2><input aria-label="搜索云文件" placeholder="搜索文件名" value={query} onChange={e => setQuery(e.target.value)} />{(error || blocked) && <p className="validation-error" role="alert">{error || blocked}</p>}<div className="company-picker-list">{view?.files.map(file => { const checked = selected.some(item => item.assetId === file.assetId); return <label key={file.assetId}><input type="checkbox" checked={checked} disabled={!checked && selected.length >= limit} onChange={e => setSelected(previous => e.target.checked ? [...previous, file] : previous.filter(item => item.assetId !== file.assetId))} /><FileIcon path={file.name} /><span>{file.name}<small>{fileSize(file.size)} · {file.uploaderUsername || file.uploaderName} · {date(file.createdAt)}</small></span></label>; })}</div><div className="dialog-actions"><span>已选择 {selected.length}/{limit}</span><button className="secondary-button" onClick={close}>取消</button><button className="primary-button" disabled={!selected.length || !!blocked} onClick={() => { ref.current?.close(); onConfirm(selected, epoch); }}>添加文件</button></div></dialog>;
}
