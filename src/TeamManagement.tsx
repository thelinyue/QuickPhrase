import { useEffect, useRef, useState } from 'react';
import { emitTo, listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { confirm, save } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { FormDialog, Titlebar, type DialogSpec } from './ui';
import { AccountNotice, PasswordDialog, PasswordField } from './AccountControls';
import { syncTime, teamRoleLabel, type DeletedTeam, type TeamAssetCleanup, type TeamInfo, type TeamMember, type TeamMemberIdentity, type TeamMembers, type TeamRole, type TeamUser } from './types';

type Page = 'list' | 'members' | 'deleted';

/** 管理窗口只维护表单草稿，服务端负责授权和事务；账号切换后旧人员响应与表单全部失效。 */
export function TeamManagementWindow({ mode, tab = '', teamId: initialTeam = '' }: { mode?: 'users' | 'teams'; tab?: string; teamId?: string }) {
  const { status, version } = useTeams(); const user = status.user;
  const available = !!user && !user.mustChangePassword && !status.offline;
  const usersPage = mode === 'users' || (!mode && tab === 'users');
  const [page, setPage] = useState<Page>(tab === 'deleted' ? 'deleted' : initialTeam ? 'members' : 'list');
  const [teamId, setTeamId] = useState(initialTeam); const team = status.teams.find(t => t.teamId === teamId);
  const manageable = !!team && (user?.admin || team.role === 'manager');
  const [users, setUsers] = useState<TeamUser[]>([]); const [deleted, setDeleted] = useState<DeletedTeam[]>([]); const [cleanup, setCleanup] = useState<TeamAssetCleanup[]>([]);
  const [base, setBase] = useState<TeamMembers | null>(null); const [draft, setDraft] = useState<TeamMember[]>([]); const [latest, setLatest] = useState<TeamMembers | null>(null);
  const [dirty, setDirty] = useState(false); const dirtyRef = useRef(false); dirtyRef.current = dirty;
  const [busy, setBusy] = useState(false); const busyRef = useRef(false); const [message, updateMessage] = useState(''); const [failed, setFailed] = useState(false); const [password, setPassword] = useState(false);
  const setMessage = (text: string, error = false) => { setFailed(error); updateMessage(text); }; const [dialog, setDialog] = useState<DialogSpec | null>(null);
  const [revision, setRevision] = useState(0); const [query, setQuery] = useState(''); const [filter, setFilter] = useState('all'); const [offset, setOffset] = useState(0);
  const [groupFilter, setGroupFilter] = useState('managed'); const [more, setMore] = useState(false); const [loaded, setLoaded] = useState(false);
  const navigationRef = useRef<(tab: string, id: string) => Promise<void>>(async () => {});
  const account = `${status.server}:${user?.id || ''}:${status.sessionEpoch ?? ''}`; const accountRef = useRef(account); accountRef.current = account;
  const statusRef = useRef(status); statusRef.current = status;
  const dialogAllowed = useRef<() => boolean>(() => true); const moreRef = useRef<HTMLDivElement>(null);
  const lastAccount = useRef('');
  const pendingCreate = useRef(tab === 'create' && !usersPage);
  const baseRef = useRef(base); baseRef.current = base;
  const online = () => !!statusRef.current.user && !statusRef.current.user.mustChangePassword && !statusRef.current.offline;
  const mayManage = (selected: TeamInfo) => statusRef.current.teams.some(t => t.teamId === selected.teamId && t.lifecycleVersion === selected.lifecycleVersion && (statusRef.current.user?.admin || t.role === 'manager'));
  async function discardAllowed() { return !dirtyRef.current || await confirm('放弃未保存的成员调整？', { title: '未保存的成员调整', kind: 'warning' }); }
  async function navigate(next: Page, id = teamId) {
    if (next === page && id === teamId) return;
    const scope = account;
    if (busyRef.current || !await discardAllowed()) return;
    if (accountRef.current !== scope) return;
    dirtyRef.current = false; setDirty(false); setBase(null); setDraft([]); setLatest(null); setDialog(null); setPage(next); setTeamId(id); setQuery(''); setOffset(0);
  }
  navigationRef.current = async (tab, id) => {
    await navigate(tab === 'deleted' ? 'deleted' : id ? 'members' : 'list', id);
    if (tab === 'create' && !busyRef.current && !dirtyRef.current && online() && (statusRef.current.user?.admin || statusRef.current.user?.canCreateTeams)) createOrRename();
  };
  useEffect(() => {
    let disposed = false; const stops: (() => void)[] = [];
    void listen<{ tab: string; teamId: string }>('team-management-navigation', e => void navigationRef.current(e.payload.tab, e.payload.teamId)).then(fn => { if (disposed) fn(); else stops.push(fn); });
    void getCurrentWindow().onCloseRequested(async e => {
      if (!dirtyRef.current && !busyRef.current) return;
      e.preventDefault();
      if (!busyRef.current && await discardAllowed()) { dirtyRef.current = false; await getCurrentWindow().close(); }
    }).then(fn => { if (disposed) fn(); else stops.push(fn); });
    return () => { disposed = true; stops.forEach(fn => fn()); };
  }, []);
  useEffect(() => {
    // 首次授权到达后才使用入口共享话术；切号或退出后不能恢复上个账号的入口上下文。
    const first = !lastAccount.current && !!user;
    setUsers([]); setDeleted([]); setCleanup([]); setBase(null); setDraft([]); setLatest(null); setDialog(null); setDirty(false); dirtyRef.current = false; setPage(first && initialTeam ? 'members' : first && tab === 'deleted' ? 'deleted' : 'list'); setTeamId(first ? initialTeam : ''); if (user) lastAccount.current = account; setLoaded(false); setPassword(false); setMore(false); setMessage(''); setQuery(''); setOffset(0);
  }, [account]);
  useEffect(() => { if (!user?.admin && page === 'deleted') setPage('list'); }, [user?.admin, page]);
  useEffect(() => {
    if (user && page === 'members' && (!team || !manageable)) { setTeamId(''); setPage('list'); setDialog(null); setBase(null); setDraft([]); setLatest(null); setDirty(false); dirtyRef.current = false; }
  }, [status.teams, teamId, page, manageable, team, user]);
  useEffect(() => {
    setBase(null); setDraft([]); setLatest(null); setDirty(false); dirtyRef.current = false; setDialog(null);
  }, [teamId, team?.lifecycleVersion]);
  useEffect(() => { if (dialog && (!online() || !dialogAllowed.current())) setDialog(null); }, [status, dialog]);
  useEffect(() => {
    if (!more) return;
    const close = (event: PointerEvent) => { if (!moreRef.current?.contains(event.target as Node)) setMore(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); setMore(false); moreRef.current?.querySelector('button')?.focus(); } };
    moreRef.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    document.addEventListener('pointerdown', close); document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape); };
  }, [more]);
  useEffect(() => {
    if (!available) { setDialog(null); return; }
    let active = true;
    if (user?.admin && (usersPage || page === 'members')) void call<TeamUser[]>('team_admin', { action: 'users' }).then(value => { if (active) { setUsers(value); setLoaded(true); } }).catch(e => { if (active) setMessage(String(e), true); });
    if (user?.admin && page === 'deleted') {
      void Promise.all([call<DeletedTeam[]>('team_admin', { action: 'deleted-teams' }), call<TeamAssetCleanup[]>('team_admin', { action: 'asset-cleanup' })]).then(([items, pending]) => { if (active) { setDeleted(items); setCleanup(pending); } }).catch(e => { if (active) setMessage(String(e), true); });
    }
    return () => { active = false; };
  }, [account, user?.admin, available, page, usersPage, revision, version]);
  useEffect(() => {
    let active = true;
    if (usersPage || page !== 'members' || !available || !manageable || !team) { setBase(null); setDraft([]); setDirty(false); dirtyRef.current = false; return; }
    void call<TeamMembers>('team_admin', { action: 'members', teamId }).then(value => {
      if (!active) return;
      if (dirtyRef.current) { if (baseRef.current?.membershipVersion !== value.membershipVersion) setLatest(value); }
      else { setBase(value); setDraft(value.members); setLatest(null); }
    }).catch(e => { if (active) setMessage(String(e), true); });
    return () => { active = false; };
  }, [account, available, page, usersPage, teamId, manageable, team?.membershipVersion, team?.lifecycleVersion, revision]);
  async function submit<T>(action: string, request?: unknown, targetTeam = teamId, id?: string): Promise<T> {
    const scope = account;
    if (accountRef.current !== scope || !online()) throw new Error('账号或连接已变化，请重新操作。');
    const result = await call<T>('team_admin', { action, teamId: targetTeam || undefined, id, request });
    if (accountRef.current !== scope) throw new Error('账号已变化，请重新操作。');
    setRevision(v => v + 1); return result;
  }
  async function run(work: () => Promise<void>) {
    if (busyRef.current || !available) return; busyRef.current = true; setBusy(true);
    const scope = account;
    try { await work(); } catch (e) { if (accountRef.current === scope) setMessage(String(e), true); } finally { busyRef.current = false; setBusy(false); }
  }
  function profile(person?: TeamUser) {
    showDialog({ title: person ? `修改资料 · ${person.username}` : '新建账号', submit: person ? '保存' : '创建', className: 'account-dialog', fields: <>
      <label>用户名<input name="username" required maxLength={64} defaultValue={person?.username || ''} autoFocus /></label>
      <label>昵称<span className="optional">（可选）</span><input name="displayName" maxLength={50} defaultValue={person?.displayName || ''} /></label>
      <label>联系方式<span className="optional">（可选）</span><input name="contact" maxLength={100} defaultValue={person?.contact || ''} /></label>
      {!person && <><PasswordField label="初始密码" name="password" generate /><p>首次登录需改密；创建共享话术权限默认关闭。</p></>}
    </>, onSubmit: async f => {
      const request = { username: String(f.get('username')), displayName: String(f.get('displayName')), contact: String(f.get('contact')), ...(!person ? { password: String(f.get('password')) } : {}) };
      await submit(person ? 'update-user' : 'create-user', request, '', person?.id);
      setMessage(person ? '账号资料已更新' : '账号已创建，需要共享时可加入共享话术');
    } }, () => !!statusRef.current.user?.admin);
  }
  function resetPassword(person: TeamUser) {
    showDialog({ title: `重置密码 · ${person.username}`, submit: '重置', className: 'account-dialog', fields: <><p>重置后需重新登录并改密。</p><PasswordField label="新初始密码" name="password" autoFocus generate /></>, onSubmit: async f => { await submit('update-user', { password: String(f.get('password')) }, '', person.id); setMessage('初始密码已重置'); } }, () => !!statusRef.current.user?.admin);
  }
  function createOrRename(rename = false, selected = team) {
    showDialog({ title: rename ? '重命名共享话术' : '新建共享话术', submit: rename ? '保存' : '创建', className: 'account-dialog', fields: <><label>共享话术名称<input name="name" maxLength={40} required defaultValue={rename ? selected?.name : ''} autoFocus /></label>{!rename && !user?.admin && <p>创建后你是管理员，可添加已有用户名。</p>}</>, onSubmit: async f => {
      const id = await submit<string>(rename ? 'update-team' : 'create-team', { name: String(f.get('name')), ...(rename ? { expectedLifecycleVersion: selected?.lifecycleVersion } : {}) }, rename ? selected?.teamId : '');
      if (!rename) { setPage('members'); setTeamId(id); setQuery(''); setOffset(0); await emitTo('main', 'team-scope-navigation', { teamId: id }); }
      setMessage(rename ? '共享话术名称已更新' : '共享话术已创建，请添加成员');
    } }, () => rename ? !!selected && mayManage(selected) : !!(statusRef.current.user?.admin || statusRef.current.user?.canCreateTeams));
  }
  useEffect(() => {
    if (!pendingCreate.current || !available) return;
    pendingCreate.current = false;
    if (user?.admin || user?.canCreateTeams) createOrRename();
  }, [account, available]);
  function changeMember(id: string, role: TeamRole | '') {
    setDraft(rows => role ? rows.map(m => m.userId === id ? { ...m, role } : m) : rows.filter(m => m.userId !== id)); setDirty(true); dirtyRef.current = true;
  }
  function addIdentity(identity: TeamMemberIdentity) {
    if (draft.some(m => m.userId === identity.userId)) { setMessage('该用户名已在本库或待保存列表中'); return; }
    setDraft(rows => [...rows, { ...identity, teamId, role: 'member' }]); setDirty(true); dirtyRef.current = true;
  }
  async function saveMembers() {
    if (!base || !team) return;
    const scope = account;
    const valid = () => accountRef.current === scope && online() && mayManage(team);
    try {
      await submit('set-members', { expectedMembershipVersion: base.membershipVersion, expectedLifecycleVersion: base.lifecycleVersion, members: draft.map(({ userId, teamId, role }) => ({ userId, teamId, role })) });
      if (!valid()) return;
      dirtyRef.current = false; setDirty(false); setRevision(v => v + 1); setMessage('成员与角色已保存');
    } catch (e) {
      if (!valid()) return;
      setMessage(String(e), true);
      try { const value = await call<TeamMembers>('team_admin', { action: 'members', teamId }); if (valid() && value.membershipVersion !== base.membershipVersion) setLatest(value); } catch { /* 保留草稿，权限失效由状态通知撤下管理入口。 */ }
    }
  }
  function transfer() {
    if (!team || !base) return;
    const candidates = base.members.filter(m => !m.disabled && m.role !== 'manager');
    if (!candidates.length) { setMessage('请先添加并保存一名在用成员'); return; }
    showDialog({ title: `移交管理员 · ${team.name}`, submit: '确认移交', className: 'account-dialog', fields: <><label>接收人<select name="target" required>{candidates.map(m => <option key={m.userId} value={m.userId}>{m.username}{m.displayName ? ` · ${m.displayName}` : ''}</option>)}</select></label><p>移交后，你将成为查看者。</p></>, onSubmit: async f => {
      const target = candidates.find(m => m.userId === f.get('target'));
      if (!target) throw new Error('请选择有效接收人。');
      await submit('transfer-manager', { targetUserId: target.userId, expectedMembershipVersion: base.membershipVersion, expectedLifecycleVersion: base.lifecycleVersion }); setMessage('已移交，你现在是查看者');
    } }, () => mayManage(team));
  }
  async function leave(selected: TeamInfo) {
    if (!await confirm(`退出「${selected.name}」？将清理本库缓存，再次加入需管理员添加。`, { title: '退出共享话术', kind: 'warning' })) return;
    await submit('leave-team', { expectedMembershipVersion: selected.membershipVersion, expectedLifecycleVersion: selected.lifecycleVersion }, selected.teamId); setMessage('已退出共享话术');
  }
  async function deleteTeam(selected: TeamInfo) {
    const counts = await call<TeamMembers>('team_admin', { action: 'members', teamId: selected.teamId });
    if (accountRef.current !== account || !online() || !mayManage(selected)) return;
    if (!await confirm(`将「${selected.name}」移入已删除共享话术？\n成员 ${counts.memberCount} 人，话术 ${counts.phraseCount} 条（含回收记录）。仅服务管理员可恢复。`, { title: '删除整组共享话术', kind: 'warning' })) return;
    await submit('delete-team', { expectedLifecycleVersion: counts.lifecycleVersion }, selected.teamId); setMessage('整组共享话术已删除，可由服务管理员恢复');
  }
  function purge(item: DeletedTeam) {
    showDialog({ title: '永久删除整组共享话术', submit: '永久删除', className: 'account-dialog danger-dialog', fields: <><p>将永久删除「{item.name}」及其成员关系、全部话术和素材，不能还原。</p><label>输入完整共享话术名称<input name="confirmName" required autoComplete="off" autoFocus /></label></>, onSubmit: async f => {
      const result = await submit<TeamAssetCleanup>('purge-team', { expectedLifecycleVersion: item.lifecycleVersion, confirmName: String(f.get('confirmName')) }, item.teamId);
      setMessage(result.pendingAssets ? `已永久删除，${result.pendingAssets} 个素材待清理` : '共享话术及受控素材已永久删除');
    } }, () => !!statusRef.current.user?.admin);
  }
  // 提交期间同时锁住父窗口导航，避免另一入口复用窗口时把表单草稿换到其他共享话术。
  function showDialog(spec: DialogSpec, allowed: () => boolean = () => true) {
    const scope = account;
    dialogAllowed.current = allowed;
    setDialog({ ...spec, onSubmit: async form => {
      if (!online() || !allowed() || accountRef.current !== scope) throw new Error('账号或权限已变化，请重新操作。');
      if (busyRef.current) return;
      busyRef.current = true; setBusy(true);
      try { await spec.onSubmit(form); } finally { busyRef.current = false; setBusy(false); }
    } });
  }
  function addMember() {
    if (!base || !team) return;
    showDialog({ title: '添加成员', submit: '添加', className: 'account-dialog', fields: user?.admin ? <label>用户名<select name="user" required defaultValue=""><option value="" disabled>选择已有用户名</option>{users.filter(u => !u.admin && !u.disabled && !draft.some(m => m.userId === u.id)).map(u => <option key={u.id} value={u.id}>{u.username}{u.displayName ? ` · ${u.displayName}` : ''}</option>)}</select></label> : <label>用户名<input name="username" placeholder="输入完整用户名" maxLength={64} required autoFocus /></label>, onSubmit: async form => {
      const person = users.find(u => u.id === form.get('user'));
      const identity = user?.admin ? person && { userId: person.id, username: person.username, displayName: person.displayName, disabled: person.disabled } : await call<TeamMemberIdentity>('team_admin', { action: 'lookup-member', teamId, request: { username: String(form.get('username')).trim() } });
      if (!identity || identity.disabled) throw new Error('请选择已启用的用户名。');
      if (accountRef.current !== account || !online() || !mayManage(team)) throw new Error('账号或权限已变化，请重新操作。');
      if (draft.some(m => m.userId === identity.userId)) throw new Error('该用户名已在本库。');
      addIdentity(identity);
    } }, () => mayManage(team));
  }
  async function backup() {
    setMore(false);
    const path = await save({ title: '保存共享数据备份', defaultPath: '闪语共享数据备份.zip', filters: [{ name: '共享数据备份', extensions: ['zip'] }] });
    if (accountRef.current !== account || !online() || !statusRef.current.user?.admin) return;
    if (path) { await call('backup_team_server', { path }); setMessage('共享数据备份已保存'); }
  }
  const term = query.trim().toLowerCase();
  const hasManaged = status.teams.some(t => t.role === 'manager');
  const filteredUsers = users.filter(u => `${u.username} ${u.displayName} ${u.contact}`.toLowerCase().includes(term) && (filter === 'all' || (filter === 'disabled' ? u.disabled : !u.disabled)));
  const filteredTeams = status.teams.filter(t => t.name.toLowerCase().includes(term) && (user?.admin || !hasManaged || groupFilter === 'all' || t.role === 'manager'));
  const filteredMembers = draft.filter(m => `${m.username} ${m.displayName}`.toLowerCase().includes(term));
  const filteredDeleted = deleted.filter(t => t.name.toLowerCase().includes(term));
  const count = usersPage ? filteredUsers.length : page === 'members' ? filteredMembers.length : page === 'deleted' ? filteredDeleted.length : filteredTeams.length;
  const lastPage = Math.max(0, Math.ceil(count / 20) - 1), currentPage = Math.min(offset, lastPage);
  const rows = <T,>(items: T[]) => items.slice(currentPage * 20, currentPage * 20 + 20);
  const pagination = <div className="management-pagination"><span>共 {count} {usersPage ? '个账号' : page === 'members' ? '名成员' : '组共享话术'}</span><button className="secondary-button" disabled={currentPage === 0} onClick={() => setOffset(Math.max(0, currentPage - 1))}>上一页</button><span>{currentPage + 1}／{lastPage + 1}</span><button className="secondary-button" disabled={currentPage === lastPage} onClick={() => setOffset(currentPage + 1)}>下一页</button></div>;
  const search = (label: string, placeholder: string) => <input type="search" aria-label={label} placeholder={placeholder} value={query} onChange={e => { setQuery(e.target.value); setOffset(0); }} />;
  const sameAccount = lastAccount.current === account;
  const blocked = !available || !sameAccount || (usersPage && !user?.admin);
  return <section className={`client-window team-management ${usersPage ? 'user-management' : 'group-management'}`}><Titlebar title={usersPage ? '账号管理' : '共享话术管理'} minimize /><div className="management-account"><span>{user ? `${user.admin ? '服务管理员' : '用户名'}：${user.username}` : '未登录'}</span><span title={status.server}>{status.offline ? '离线' : status.server}</span></div>
    {blocked ? <div className="management-content"><p className="management-notice">{!user ? '请先登录。' : !sameAccount ? '正在读取账号…' : user.mustChangePassword ? '请先修改初始密码。' : status.offline ? '离线，管理暂不可用。' : '账号管理仅服务管理员可用。'}</p></div> : <>
      {usersPage ? <><div className="management-toolbar">{search('搜索用户名', '搜索用户名、昵称、联系方式')}<select aria-label="用户名状态筛选" value={filter} onChange={e => { setFilter(e.target.value); setOffset(0); }}><option value="all">全部状态</option><option value="active">已启用</option><option value="disabled">已停用</option></select><button className="primary-button" disabled={busy} onClick={() => profile()}>新建账号</button><div className="management-more" ref={moreRef}><button className="secondary-button" aria-haspopup="menu" aria-expanded={more} disabled={busy} onClick={() => setMore(value => !value)}>更多</button>{more && <div role="menu" className="management-more-menu"><button role="menuitem" onClick={() => void run(backup)}>共享数据备份</button><p>账号、共享话术、云文件及回收记录，不含个人内容。</p></div>}</div></div>
        <div className="management-content user-cards">{rows(filteredUsers).map(u => <article className="user-card" key={u.id}><div className="user-card-heading"><strong>{u.username}</strong><span>{u.displayName || '未填昵称'}</span>{u.id === user?.id && <small className="user-self">本人</small>}<div className="management-row-actions"><button disabled={busy} onClick={() => profile(u)}>修改资料</button>{u.id === user?.id ? <button disabled={busy} onClick={() => setPassword(true)}>修改密码</button> : !u.admin && <button disabled={busy} onClick={() => resetPassword(u)}>重置密码</button>}</div></div><div className="user-card-details"><span>联系方式：{u.contact || '未填写'}</span>{u.admin ? <span>服务管理员</span> : <label className="account-switch"><input type="checkbox" role="switch" aria-label={`${u.username}的启用状态`} checked={!u.disabled} disabled={busy} onChange={() => void run(async () => { if (u.disabled || await confirm(`停用用户名 ${u.username}？将终止其全部登录。`, { title: '停用用户名', kind: 'warning' })) { await submit('update-user', { disabled: !u.disabled }, '', u.id); setMessage('账号状态已更新'); } })} /><i aria-hidden="true" />{u.disabled ? '已停用' : u.mustChangePassword ? '待改密' : '已启用'}</label>}</div><div className="user-card-groups"><span className="account-label">共享话术</span>{u.admin ? <span>全部共享话术</span> : u.memberships?.length ? u.memberships.map(m => <button key={m.teamId} className="membership-link" disabled={busy} onClick={() => void openWindow('team-management', '', '', 'teams', '', '', m.teamId)}>{m.name} · {teamRoleLabel(m.role)}</button>) : <span>尚未加入共享话术</span>}{!u.admin && <button className="text-button" disabled={busy || u.disabled} onClick={() => void openWindow('team-management')}>加入共享话术</button>}</div><div className="user-card-permission"><span className="account-label">创建权限</span>{u.admin ? <span>始终可创建</span> : <label><input type="checkbox" aria-label={`${u.username}的创建共享话术权限`} checked={u.canCreateTeams} disabled={busy} onChange={e => { const checked = e.target.checked; void run(async () => { await submit('update-user', { canCreateTeams: checked }, '', u.id); setMessage(checked ? '已授予创建共享话术权限' : '已取消创建共享话术权限'); }); }} />允许创建共享话术</label>}</div></article>)}{!filteredUsers.length && <p className="management-empty">{loaded ? '没有匹配的用户名' : '正在读取用户名…'}</p>}</div>{pagination}</> : page === 'list' ? <><div className="management-toolbar">{search('搜索共享话术', '搜索共享话术')}{!user?.admin && hasManaged && <select aria-label="共享话术范围" value={groupFilter} onChange={e => { setGroupFilter(e.target.value); setOffset(0); }}><option value="managed">可管理的共享话术</option><option value="all">全部已加入共享话术</option></select>}{(user?.admin || user?.canCreateTeams) && <button className="primary-button" disabled={busy} onClick={() => createOrRename()}>新建共享话术</button>}{user?.admin && <button className="secondary-button" disabled={busy} onClick={() => void navigate('deleted')}>已删除共享话术</button>}</div><div className="management-content"><table className="team-table management-groups"><thead><tr><th>共享话术名称</th><th>成员数</th><th>我的角色</th><th>操作</th></tr></thead><tbody>{rows(filteredTeams).map(t => <tr key={t.teamId}><td>{t.name}<small>最近同步：{syncTime(status.teamSyncTimes?.[t.teamId])}</small></td><td>{t.memberCount ?? '—'}</td><td>{user?.admin ? '服务管理员' : teamRoleLabel(t.role)}</td><td><div className="management-row-actions">{user?.admin || t.role === 'manager' ? <><button disabled={busy} onClick={() => void navigate('members', t.teamId)}>管理成员</button><button disabled={busy} onClick={() => createOrRename(true, t)}>重命名</button><button className="danger" disabled={busy} onClick={() => void run(() => deleteTeam(t))}>删除整组共享话术</button></> : <button disabled={busy} onClick={() => void run(() => leave(t))}>退出共享话术</button>}</div></td></tr>)}</tbody></table>{!filteredTeams.length && <p className="management-empty">{term ? '没有匹配的共享话术' : '暂无共享话术'}</p>}</div>{pagination}</> : page === 'members' && team && manageable ? <><div className="management-toolbar"><button className="secondary-button" disabled={busy} onClick={() => void navigate('list')}>返回</button><strong className="management-current-team" title={team.name}>{team.name}</strong>{search('搜索成员', '搜索用户名、昵称')}<button className="primary-button" disabled={busy || !base} onClick={addMember}>添加成员</button></div><div className="management-content"><div className="management-member-summary"><span>{base ? `${base.memberCount} 名成员` : '正在读取成员…'}</span>{!user?.admin && <><button className="text-button" disabled={busy || dirty || !base} onClick={transfer}>移交管理员</button><button className="text-button" disabled title="请先移交管理员，再退出">退出共享话术</button><span className="account-hint">先移交，再退出。</span></>}</div><table className="team-table management-members"><thead><tr><th>用户名</th><th>昵称</th><th>角色</th><th>状态</th><th>操作</th></tr></thead><tbody>{rows(filteredMembers).map(m => <tr key={m.userId}><td>{m.username}{m.userId === user?.id && <small>本人</small>}</td><td>{m.displayName || '—'}</td><td><select aria-label={`${m.username}的共享话术角色`} value={m.role} disabled={busy || (!user?.admin && m.role === 'manager')} onChange={e => changeMember(m.userId, e.target.value as TeamRole)}><option value="member">查看者</option><option value="editor">编辑者</option>{(user?.admin || m.role === 'manager') && <option value="manager">库管理员</option>}</select></td><td>{m.disabled ? '已停用' : '已启用'}</td><td><button className="danger" disabled={busy || (!user?.admin && m.role === 'manager')} onClick={() => changeMember(m.userId, '')}>移出</button></td></tr>)}</tbody></table>{base && !filteredMembers.length && <p className="management-empty">{term ? '没有匹配的成员' : '尚未分配成员'}</p>}{latest && <div className="management-conflict" role="alert"><p>成员已变更，草稿已保留。最新：{latest.members.map(m => `${m.username}（${teamRoleLabel(m.role)}）`).join('、') || '无'}</p><button className="secondary-button" disabled={busy} onClick={() => { setBase(latest); setDraft(latest.members); setLatest(null); setDirty(false); dirtyRef.current = false; }}>采用最新</button></div>}</div>{pagination}<div className="management-save"><span>{dirty ? '有未保存的调整' : '尚未修改'}</span><button className="secondary-button" disabled={busy || !dirty} onClick={() => { setDraft(base?.members || []); setDirty(false); dirtyRef.current = false; }}>取消修改</button><button className="primary-button" disabled={busy || !dirty || !base || !!latest} onClick={() => void run(saveMembers)}>保存</button></div></> : page === 'deleted' && user?.admin ? <><div className="management-toolbar"><button className="secondary-button" disabled={busy} onClick={() => void navigate('list')}>返回</button><strong>已删除共享话术</strong>{search('搜索已删除共享话术', '搜索共享话术')}<button className="secondary-button" disabled={busy} onClick={() => setRevision(v => v + 1)}>刷新</button></div><div className="management-content"><table className="team-table management-deleted"><thead><tr><th>共享话术</th><th>删除者／时间</th><th>成员／话术</th><th>操作</th></tr></thead><tbody>{rows(filteredDeleted).map(item => <tr key={item.teamId}><td>{item.name}</td><td>{item.deletedBy}<small>{new Date(item.deletedAt * 1000).toLocaleString()}</small></td><td>{item.memberCount} 人／{item.phraseCount} 条</td><td><div className="management-row-actions"><button disabled={busy} onClick={() => void run(async () => { if (await confirm(`恢复整组共享话术「${item.name}」及原成员角色？`, { title: '恢复整组共享话术' })) { await submit('restore-team', { expectedLifecycleVersion: item.lifecycleVersion }, item.teamId); setMessage('整组共享话术已恢复'); } })}>恢复整组共享话术</button><button className="danger" disabled={busy} onClick={() => purge(item)}>永久删除整组共享话术</button></div></td></tr>)}</tbody></table>{!filteredDeleted.length && <p className="management-empty">没有已删除共享话术</p>}{cleanup.length > 0 && <div className="management-conflict"><strong>素材待清理</strong>{cleanup.map(item => <p key={item.teamId}>{item.name} · {item.pendingAssets} 个素材，将自动重试。</p>)}</div>}</div>{pagination}</> : <div className="management-content" />}
    </>}<AccountNotice message={message} error={failed} clear={() => setMessage('')} />{dialog && sameAccount && available && (!usersPage || user?.admin) && <FormDialog spec={dialog} windowClose close={() => setDialog(null)} />}{password && sameAccount && available && user?.admin && <PasswordDialog windowClose close={() => setPassword(false)} changed={() => { if (accountRef.current === account) setMessage('密码已修改'); }} />}
  </section>;
}
