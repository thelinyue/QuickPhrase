import { useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Icon } from './ui';
import { teamRoleLabel, type TeamUser } from './types';

export type AccountEdit = { userId: string; field: 'displayName' | 'contact'; original: string; value: string };
type Props = {
  person: TeamUser; self: boolean; busy: boolean; edit: AccountEdit | null; expanded: boolean;
  beginEdit: (field: AccountEdit['field'], trigger: HTMLButtonElement) => void;
  changeEdit: (value: string) => void; saveEdit: () => void; cancelEdit: () => void;
  profile: () => void; password: () => void; toggleEnabled: () => void; togglePermission: () => void;
  expand: (open: boolean) => void; openTeam: (id?: string) => void;
};

/** 三行账号卡只负责展示；草稿、权限提交及离开确认由管理窗口集中处理，避免行之间相互覆盖。 */
export function UserAccountCard({ person: u, self, busy, edit, expanded, beginEdit, changeEdit, saveEdit, cancelEdit, profile, password, toggleEnabled, togglePermission, expand, openTeam }: Props) {
  const trigger = useRef<HTMLButtonElement>(null); const input = useRef<HTMLInputElement>(null);
  const composing = useRef(false);
  const memberships = u.memberships || []; const first = memberships[0];
  const activeField = edit?.userId === u.id ? edit.field : undefined;
  useLayoutEffect(() => { if (activeField) { input.current?.focus(); input.current?.select(); } }, [activeField]);
  function field(key: AccountEdit['field'], label: string) {
    const editing = activeField === key;
    return <div className="user-inline-field"><span className="user-field-label">{label}：</span>{editing ? <div className="user-inline-editor">
      <input ref={input} aria-label={`${u.username}的${label}`} value={edit!.value} maxLength={key === 'displayName' ? 50 : 100} disabled={busy} onChange={e => changeEdit(e.target.value)}
        onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
        onKeyDown={e => { if (composing.current || e.nativeEvent.isComposing || e.keyCode === 229) return; if (e.key === 'Enter') { e.preventDefault(); saveEdit(); } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancelEdit(); } }} />
      <button type="button" className="user-inline-action" aria-label={`保存${label}`} title={`保存${label}（Enter）`} disabled={busy} onClick={saveEdit}><Icon name="check" /></button>
      <button type="button" className="user-inline-action" aria-label={`取消修改${label}`} title="取消（Esc）" disabled={busy} onClick={cancelEdit}><Icon name="close" /></button>
    </div> : <button type="button" className={`user-edit-value${u[key] ? '' : ' empty'}`} aria-label={`修改${u.username}的${label}`} title={u[key] || `点击填写${label}`} disabled={busy} onClick={e => beginEdit(key, e.currentTarget)}><span>{u[key] || '点击填写'}</span><Icon name="pencil" /></button>}</div>;
  }
  return <article className="user-card" aria-label={`账号 ${u.username}`}>
    <div className="user-card-heading">
      <div className="user-identity"><span className="user-field-label">账号：</span><strong title={u.username}>{u.username}</strong>{self && <small className="user-self">本人</small>}</div>
      <button className="text-button" disabled={busy} onClick={profile}>修改资料</button>
      {self || !u.admin ? <button className="text-button" disabled={busy} onClick={password}>{self ? '修改密码' : '重置密码'}</button> : <span />}
      {u.admin ? <span className="user-admin-label">服务管理员</span> : <label className="account-switch user-status-switch"><span>{u.disabled ? '已停用' : u.mustChangePassword ? '待改密' : '已启用'}</span><input type="checkbox" role="switch" aria-label={`${u.username}的启用状态`} checked={!u.disabled} disabled={busy} onChange={toggleEnabled} /><i aria-hidden="true" /></label>}
    </div>
    <div className="user-card-details">{field('displayName', '昵称')}{field('contact', '联系方式')}</div>
    <div className="user-card-groups">
      <div className="user-memberships"><span className="user-field-label">共享话术：</span>{u.admin ? <span className="user-membership-empty">全部共享话术</span> : <>
        {first ? <button className="membership-link" aria-label={`${first.name} · ${teamRoleLabel(first.role)}`} title={`${first.name} · ${teamRoleLabel(first.role)}`} disabled={busy} onClick={() => openTeam(first.teamId)}><span>{first.name}</span><small> · {teamRoleLabel(first.role)}</small></button> : <span className="user-membership-empty">未加入</span>}
        {memberships.length > 1 && <button ref={trigger} className="user-membership-more" aria-label={`${u.username}的其他${memberships.length - 1}组共享话术`} aria-haspopup="menu" aria-expanded={expanded} disabled={busy} onClick={() => expand(!expanded)}>+{memberships.length - 1}</button>}
        <button className="text-button user-join" aria-label={`为${u.username}加入共享话术`} disabled={busy || u.disabled} onClick={() => openTeam()}>加入</button>
      </>}</div>
      <label className="account-switch user-permission-switch" title={u.admin ? '管理员始终可创建' : undefined}><span>可创建共享话术</span><input type="checkbox" role="switch" aria-label={`${u.username}的创建共享话术权限`} checked={u.admin || u.canCreateTeams} disabled={busy || u.admin} onChange={togglePermission} /><i aria-hidden="true" /></label>
    </div>
    {expanded && trigger.current && <MembershipMenu person={u} anchor={trigger.current} close={() => expand(false)} openTeam={openTeam} busy={busy} />}
  </article>;
}

/** 浮层挂到窗口根部，按实际可用高度向上或向下展开，避免被账号列表的滚动容器裁剪。 */
function MembershipMenu({ person, anchor, close, openTeam, busy }: { person: TeamUser; anchor: HTMLButtonElement; close: () => void; openTeam: (id: string) => void; busy: boolean }) {
  const menu = useRef<HTMLDivElement>(null); const closeRef = useRef(close); closeRef.current = close;
  const [position, setPosition] = useState({ left: 8, top: 8, maxHeight: 240, width: 320 });
  useLayoutEffect(() => {
    const rect = anchor.getBoundingClientRect(); const width = Math.min(320, window.innerWidth - 16);
    const below = window.innerHeight - rect.bottom - 12, above = rect.top - 12;
    const height = Math.min(240, Math.max(below, above)); const down = below >= Math.min(240, menu.current?.scrollHeight || 240) || below >= above;
    setPosition({ left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)), top: down ? rect.bottom + 4 : Math.max(8, rect.top - Math.min(height, menu.current?.scrollHeight || height) - 4), maxHeight: height, width });
    menu.current?.querySelector<HTMLButtonElement>('button')?.focus({ preventScroll: true });
    const outside = (e: PointerEvent) => { if (!menu.current?.contains(e.target as Node) && !anchor.contains(e.target as Node)) closeRef.current(); };
    const dismiss = () => closeRef.current();
    // 长搜索词失焦会触发输入框自己的横向滚动；只有锚点所在容器滚动才需要撤下浮层。
    const scroll = (e: Event) => { if (e.target instanceof Node && e.target.contains(anchor)) dismiss(); };
    document.addEventListener('pointerdown', outside); window.addEventListener('resize', dismiss); document.addEventListener('scroll', scroll, true);
    return () => { document.removeEventListener('pointerdown', outside); window.removeEventListener('resize', dismiss); document.removeEventListener('scroll', scroll, true); if (anchor.isConnected && (!document.activeElement || document.activeElement === document.body || menu.current?.contains(document.activeElement))) anchor.focus(); };
  }, [anchor]);
  return createPortal(<div ref={menu} role="menu" aria-label={`${person.username}的其他共享话术`} className="user-membership-menu" style={position} onKeyDown={e => {
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); anchor.focus(); }
    else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) {
      e.preventDefault(); const buttons = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') || []);
      const index = buttons.indexOf(document.activeElement as HTMLButtonElement); const next = e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : (index + (e.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
      buttons[next]?.focus();
    } else if (e.key === 'Tab') close();
  }}>{person.memberships?.slice(1).map(m => <button key={m.teamId} role="menuitem" aria-label={`${m.name} · ${teamRoleLabel(m.role)}`} title={`${m.name} · ${teamRoleLabel(m.role)}`} disabled={busy} onClick={() => { close(); openTeam(m.teamId); }}><span>{m.name}</span><small>{teamRoleLabel(m.role)}</small></button>)}</div>, document.body);
}
