import { useEffect, useRef, useState } from 'react';
import { openWindow } from './api';
import { teamRoleLabel, type TeamStatus } from './types';

/** 共享话术名称和选择入口合为一个控件；键盘、搜索和当前标记共享同一份授权列表。 */
export function TeamSelector({ status, teamId, onChange, onError, disabled = false, showManagement = true }: { status: TeamStatus; teamId: string; onChange: (id: string) => void; onError: (message: string) => void; disabled?: boolean; showManagement?: boolean }) {
  const [open, setOpen] = useState(false); const [query, setQuery] = useState('');
  const root = useRef<HTMLDivElement>(null); const trigger = useRef<HTMLButtonElement>(null); const search = useRef<HTMLInputElement>(null);
  const current = status.teams.find(t => t.teamId === teamId);
  const manage = !!status.user && !status.user.mustChangePassword && !status.offline;
  const teams = status.teams.filter(t => t.name.toLowerCase().includes(query.trim().toLowerCase()));
  function close(focus = false) { setOpen(false); setQuery(''); if (focus) trigger.current?.focus(); }
  useEffect(() => {
    if (!open) return;
    const outside = (e: PointerEvent) => { if (!root.current?.contains(e.target as Node)) close(); };
    document.addEventListener('pointerdown', outside);
    if (status.teams.length > 8) search.current?.focus(); else root.current?.querySelector<HTMLElement>('[role="menuitem"][aria-current="true"],[role="menuitem"]')?.focus();
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);
  useEffect(() => { close(); }, [status.server, status.user?.id, status.sessionEpoch, disabled]);
  const action = (create = false) => { close(); void openWindow(status.user ? 'team-management' : 'settings', '', '', status.user ? (create ? 'create' : 'teams') : '账号与共享').catch(e => onError(String(e))); };
  return <div className="team-selector" ref={root}>
    <button ref={trigger} className={`scope-name${teamId ? ' active' : ''}`} aria-label="共享话术" aria-pressed={!!teamId} disabled={disabled} aria-haspopup="menu" aria-expanded={open} title={current?.name || '共享话术'} onClick={() => setOpen(v => !v)}><span>{current ? `共享话术：${current.name}` : '共享话术'}</span>{status.offline && current && <small>离线</small>}<span aria-hidden="true">▾</span></button>
    {open && <div className="team-dropdown" role="menu" aria-label="选择共享话术" onKeyDown={e => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(true); return; }
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
      const items = Array.from(root.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') || []);
      if (!items.length) return;
      e.preventDefault(); const at = items.indexOf(document.activeElement as HTMLButtonElement); const index = e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1 : at < 0 ? (e.key === 'ArrowDown' ? 0 : items.length - 1) : (at + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length; items[index].focus();
    }}>
      {status.teams.length > 8 && <input ref={search} aria-label="搜索共享话术" placeholder="搜索共享话术" value={query} onChange={e => setQuery(e.target.value)} />}
      <div className="team-options">{teams.map(t => <button key={t.teamId} role="menuitem" aria-current={t.teamId === teamId ? 'true' : undefined} title={`${t.name} · ${status.user?.admin ? '服务管理员' : teamRoleLabel(t.role)}`} onClick={() => { close(); onChange(t.teamId); }}><span className="team-option-check" aria-hidden="true">{t.teamId === teamId ? '✓' : ''}</span><span className="team-option-name">{t.name}</span><small>{status.user?.admin ? '服务管理员' : teamRoleLabel(t.role)}</small></button>)}</div>
      {!teams.length && <p>{status.teams.length ? '没有匹配的共享话术' : status.user ? '尚未加入共享话术' : '请先连接并登录'}</p>}
      {showManagement && <div className="team-dropdown-actions">{status.user ? <><button role="menuitem" disabled={!manage} onClick={() => action()}>共享话术管理</button>{(status.user.admin || status.user.canCreateTeams) && <button role="menuitem" disabled={!manage} onClick={() => action(true)}>新建共享话术</button>}</> : <button role="menuitem" onClick={() => action()}>账号与共享</button>}</div>}
    </div>}
  </div>;
}
