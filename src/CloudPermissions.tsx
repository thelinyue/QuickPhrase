import { useCallback, useEffect, useRef, useState } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call } from './api';
import { Icon } from './ui';
import type { CloudCommandPermissions, CompanyFilePermissions, TeamStatus, TeamUser } from './types';

const filesDefault: CompanyFilePermissions = { canUpload: false, canRecycle: false, canRestore: false, version: 0, uploadVersion: 0 };
const commandsDefault: CloudCommandPermissions = { canUse: false, canManage: false, version: 0 };
type Drafts = Record<string, { files?: CompanyFilePermissions; commands?: CloudCommandPermissions }>;

/** 草稿保留初次编辑的版本；冲突后须明确采用最新，不能自动替换版本再覆盖他人授权。 */
export function CloudPermissions({ status, version, close }: { status: TeamStatus; version: number; close: () => void }) {
  const epoch = status.sessionEpoch || 0;
  const ref = useRef<HTMLDialogElement>(null), trigger = useRef(document.activeElement as HTMLElement | null);
  const [module, setModule] = useState<'files' | 'commands'>('files'), [query, setQuery] = useState('');
  const [users, setUsers] = useState<TeamUser[]>([]), [drafts, setDrafts] = useState<Drafts>({}), [error, setError] = useState(''), [message, setMessage] = useState(''), [busy, setBusy] = useState('');
  const sequence = useRef(0), currentEpoch = useRef(epoch), busyRef = useRef(false), draftRef = useRef(drafts); currentEpoch.current = epoch; draftRef.current = drafts;
  const currentStatus = useRef(status); currentStatus.current = status;
  const dirty = () => Object.keys(draftRef.current).length > 0;
  const available = !!status.user?.admin && !status.offline && !status.user.mustChangePassword;
  const capability = module === 'files' ? status.companyFilePermissions : status.cloudCommands;
  const load = useCallback(async () => {
    const request = ++sequence.current;
    if (!available || !(status.companyFilePermissions || status.cloudCommands)) { setUsers([]); return; }
    try { const next = await call<TeamUser[]>('team_admin', { action: 'users', sessionEpoch: epoch }); if (request === sequence.current && currentEpoch.current === epoch) { setUsers(next); } }
    catch (e) { if (request === sequence.current && currentEpoch.current === epoch) { setUsers([]); setError(String(e)); } }
  }, [available, epoch, status.companyFilePermissions, status.cloudCommands]);
  useEffect(() => { void load(); return () => { ++sequence.current; }; }, [load, version]);
  async function discardAllowed() { return !dirty() || await confirm('放弃未保存的闪云权限调整？', { title: '未保存的权限调整', kind: 'warning' }); }
  async function requestClose() { if (!busyRef.current && await discardAllowed()) { draftRef.current = {}; close(); } }
  useEffect(() => {
    ref.current?.showModal(); let disposed = false, stop: (() => void) | undefined;
    void getCurrentWindow().onCloseRequested(async e => { if (!dirty() && !busyRef.current) return; e.preventDefault(); if (!busyRef.current && await discardAllowed()) { draftRef.current = {}; await getCurrentWindow().close(); } }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; ++sequence.current; stop?.(); if (trigger.current?.isConnected) trigger.current.focus(); };
  }, []);
  async function save(user: TeamUser) {
    const draft = draftRef.current[user.id]?.[module];
    if (!draft || busyRef.current || !available || !capability) return;
    busyRef.current = true; setBusy(user.id); setError(''); setMessage('');
    const request = module === 'files' ? { companyFilePermissions: { canUpload: (draft as CompanyFilePermissions).canUpload, canRecycle: (draft as CompanyFilePermissions).canRecycle, canRestore: (draft as CompanyFilePermissions).canRestore, expectedVersion: draft.version } } : { cloudCommandPermissions: { canUse: (draft as CloudCommandPermissions).canUse, canManage: (draft as CloudCommandPermissions).canManage, expectedVersion: draft.version } };
    try {
      await call('team_admin', { action: 'update-user', id: user.id, sessionEpoch: epoch, request });
      if (currentEpoch.current !== epoch || !currentStatus.current.user?.admin) return;
      setDrafts(previous => { const next = { ...previous, [user.id]: { ...previous[user.id] } }; delete next[user.id][module]; if (!Object.keys(next[user.id]).length) delete next[user.id]; return next; });
      setMessage(`${user.username} 的${module === 'files' ? '云文件' : '云命令'}权限已保存。`);
    } catch (e) { if (currentEpoch.current === epoch) setError(String(e)); }
    finally { if (currentEpoch.current === epoch) { if (currentStatus.current.user?.admin && !currentStatus.current.offline) await load(); busyRef.current = false; setBusy(''); } }
  }
  const filtered = users.filter(u => `${u.username}\n${u.displayName}`.toLowerCase().includes(query.toLowerCase().trim()));
  return <dialog ref={ref} className="small-dialog cloud-permissions" aria-labelledby="cloud-permissions-title" onCancel={e => { e.preventDefault(); void requestClose(); }}>
    <h2 id="cloud-permissions-title">权限管理</h2><div className="cloud-segments" role="tablist" aria-label="授权模块">{(['files','commands'] as const).map(m => <button role="tab" aria-selected={m === module} key={m} disabled={!!busy} onClick={() => setModule(m)}>{m === 'files' ? '云文件' : '云命令'}</button>)}</div>
    <div className="company-toolbar"><div className="company-search"><input aria-label="搜索用户名或昵称" placeholder="搜索用户名或昵称" value={query} onChange={e => setQuery(e.target.value)} /><Icon name="search" /></div><button className="company-button" disabled={!!busy || !available} onClick={() => void load()}>刷新账号</button></div>
    <p className="company-hint">{module === 'files' ? '所有已完成改密的账号均可查看云文件；管理操作按账号授权。' : '普通账号默认关闭。维护包含使用，取消使用会同时取消维护；保存后立即生效。'}服务管理员固定拥有全部权限。</p>
    {!available && <p role="alert" className="validation-error">请联网并使用已完成改密的服务管理员账号。</p>}
    {!capability && <p role="alert" className="validation-error">当前闪云版本不支持此项授权，请升级服务端。</p>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert" className="validation-error">{error}</p>}
    <div className="cloud-permissions-table"><table><thead><tr><th>账号／昵称</th><th>状态</th>{module === 'files' ? <><th>上传文件</th><th>删除文件</th><th>恢复文件</th></> : <><th>使用命令</th><th>维护命令</th></>}<th>操作</th></tr></thead><tbody>{capability && filtered.map(user => {
      const base = module === 'files' ? user.companyFilePermissions || filesDefault : user.cloudCommandPermissions || commandsDefault;
      const draft = drafts[user.id]?.[module], p = draft || base, conflict = !!draft && draft.version !== base.version;
      const keys = module === 'files' ? ['canUpload','canRecycle','canRestore'] as const : ['canUse','canManage'] as const;
      const labels: Record<string,string> = { canUpload: '上传', canRecycle: '删除', canRestore: '恢复', canUse: '使用命令', canManage: '维护命令' };
      return <tr key={user.id}><td>{user.username}<small>{user.displayName || '—'}</small>{conflict && <small className="company-permission-conflict" role="alert">权限已变更，草稿已保留</small>}</td><td>{user.disabled ? '已停用' : user.admin ? '服务管理员' : '已启用'}</td>{keys.map(key => <td key={key}><input type="checkbox" aria-label={`${user.username}的${labels[key]}权限`} checked={user.admin || !!(p as unknown as Record<string, unknown>)[key]} disabled={!!busy || !available || user.admin} onChange={e => {
        const checked = e.target.checked;
        setDrafts(previous => { const next = { ...base, ...previous[user.id]?.[module], [key]: checked };
          if (module === 'commands' && key === 'canManage' && checked) Object.assign(next, { canUse: true });
          if (module === 'commands' && key === 'canUse' && !checked) Object.assign(next, { canManage: false });
          return { ...previous, [user.id]: { ...previous[user.id], [module]: next } };
        });
      }} /></td>)}<td>{user.admin ? <span role="img" aria-label="固定全部权限" title="固定全部权限"><Icon name="shield" /></span> : <div className="company-permission-actions"><button className="company-row-button" aria-label={`保存${user.username}权限`} title="保存" disabled={!!busy || !available || !draft || conflict} onClick={() => void save(user)}><Icon name="save" /></button>{draft && <button className="company-row-button" aria-label={conflict ? '采用最新' : '取消调整'} title={conflict ? '采用最新' : '取消调整'} disabled={!!busy} onClick={() => setDrafts(previous => { const next = { ...previous, [user.id]: { ...previous[user.id] } }; delete next[user.id][module]; if (!Object.keys(next[user.id]).length) delete next[user.id]; return next; })}><Icon name={conflict ? 'refresh' : 'close'} /></button>}</div>}</td></tr>;
    })}</tbody></table>{!filtered.length && <div className="company-empty">没有匹配的账号</div>}</div>
    <div className="dialog-actions"><span className="toolbar-spacer">{dirty() ? '有未保存的权限调整' : '按行保存，两个模块独立授权'}</span><button className="secondary-button" disabled={!!busy} onClick={() => void requestClose()}>关闭权限管理</button></div>
  </dialog>;
}
