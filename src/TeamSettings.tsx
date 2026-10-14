import { useEffect, useRef, useState, type FormEvent } from 'react';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useTeams } from './api';
import { Feedback, Titlebar } from './ui';
import { teamRoleLabel, type TeamRecycle, type TeamView } from './types';
import { PrivateSyncSettings } from './PrivateSyncSettings';
import { AccountNotice, PasswordDialog, PasswordField } from './AccountControls';

/** 账号页只展示当前连接、个人同步及共享入口；管理窗口分别复用，改密不撑长页面。 */
export function TeamSettings() {
  const { status } = useTeams();
  const [server, setServer] = useState(''); const [username, setUsername] = useState('');
  const [message, setMessage] = useState(''); const [failed, setFailed] = useState(false); const [password, setPassword] = useState(false);
  const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const account = `${status.server}:${status.user?.id || ''}:${status.sessionEpoch ?? ''}`; const accountRef = useRef(account); accountRef.current = account;
  const manageable = !!status.user && !status.user.mustChangePassword && !status.offline;
  useEffect(() => { if (status.server) setServer(status.server); if (status.username) setUsername(status.username); }, [status.server, status.username]);
  useEffect(() => { setPassword(false); setMessage(''); }, [account]);
  useEffect(() => { if (status.user?.mustChangePassword && !status.offline) setPassword(true); }, [account, status.user?.mustChangePassword, status.offline]);
  async function run(work: () => Promise<unknown>) {
    if (busyRef.current) return; busyRef.current = true; setBusy(true);
    const owner = account;
    try { await work(); if (accountRef.current === owner) { setFailed(false); setMessage('已完成'); } } catch (e) { if (accountRef.current === owner) { setFailed(true); setMessage(String(e)); } } finally { busyRef.current = false; setBusy(false); }
  }
  async function login(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); const form = e.currentTarget; const password = String(new FormData(form).get('password'));
    await run(async () => { await call('team_login', { server, username, password }); form.reset(); });
  }
  return <div className="team-settings account-settings"><h2 className="settings-heading">账号与同步</h2>
    {!status.user ? <><form className="team-connection" onSubmit={login}><fieldset disabled={busy} className="dialog-fields"><label>服务器地址<input value={server} onChange={e => setServer(e.target.value)} placeholder="http://192.168.1.20:7823" required autoComplete="off" /></label><label>用户名<input value={username} onChange={e => setUsername(e.target.value)} required maxLength={64} autoComplete="username" /></label><PasswordField label="密码" name="password" current /><p className="account-hint">局域网可用 HTTP，公网建议 HTTPS。</p></fieldset><button className="primary-button" disabled={busy}>{busy ? '连接中…' : '登录'}</button></form><p className="account-hint">登录后可开启个人同步、使用共享库话术。</p></> : <>
      <section className="account-section account-identity"><div className="account-line"><strong>当前账号</strong><span className="account-user">{status.user.username}{status.user.displayName && ` · ${status.user.displayName}`}<small>{status.user.admin ? '服务管理员' : '用户'}</small></span><span className={`account-status${status.offline ? ' offline' : ''}`}>{status.offline ? '离线' : '已连接'}</span></div><div className="account-line"><strong>服务器</strong><span className="account-server" title={status.server}>{status.server}</span></div><div className="setting-buttons"><button className="secondary-button" disabled={busy || status.offline} onClick={() => setPassword(true)}>修改密码</button><button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_refresh'))}>刷新连接</button><button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_logout'))}>退出登录</button></div>{status.user.mustChangePassword && <p className="account-warning">请先修改初始密码。</p>}</section>
      <PrivateSyncSettings key={account} account={status} />
      <section className="account-section"><div className="account-line"><strong>共享库</strong><span>{status.user.admin ? '可管理' : '已加入'} {status.teams.length} 个共享库</span></div><div className="setting-buttons"><button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('team-management'))}>共享库管理</button>{status.user.admin && <button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('user-management'))}>账号管理</button>}</div><p className="account-hint">离线可用已缓存内容；联网后核验权限。</p></section>
    </>}<AccountNotice message={message} error={failed} clear={() => setMessage('')} />{password && status.user && !status.offline && <PasswordDialog key={account} first={status.user.mustChangePassword} close={() => setPassword(false)} changed={() => { if (accountRef.current === account) { setFailed(false); setMessage('密码已修改'); } }} />}
  </div>;
}

/** 回收历史按共享库授权；还原事务采用当前版本，永久删除必须明确确认。 */
export function TeamRecycleWindow({ teamId }: { teamId: string }) {
  const { status, version } = useTeams(); const [records, setRecords] = useState<TeamRecycle[]>([]); const [message, setMessage] = useState(''); const [busy, setBusy] = useState(false); const [query, setQuery] = useState(''); const [page, setPage] = useState(0);
  const info = status.teams.find(t => t.teamId === teamId); const editable = !!info?.editable && !status.offline;
  const load = async () => setRecords(await call<TeamRecycle[]>('get_team_recycle', { teamId }));
  useEffect(() => { let active = true; setRecords([]); if (editable) void call<TeamRecycle[]>('get_team_recycle', { teamId }).then(r => { if (active) setRecords(r); }).catch(e => { if (active) setMessage(String(e)); }); return () => { active = false; }; }, [teamId, version, editable]);
  async function action(record: TeamRecycle, remove: boolean) {
    if (busy) return; setBusy(true);
    try { if (remove) { if (!await confirm(`永久删除回收记录「${record.label}」？此操作不可还原。`, { title: '永久删除', kind: 'warning' })) return; await call('purge_team_record', { teamId, id: record.id, expectedLifecycleVersion: info?.lifecycleVersion }); } else { const view = await call<TeamView>('get_team_view', { teamId }); await call('restore_team_record', { teamId, id: record.id, expectedVersion: view.team.version, expectedLifecycleVersion: view.team.lifecycleVersion }); } await load(); }
    catch (e) { setMessage(String(e)); } finally { setBusy(false); }
  }
  const filtered = records.filter(r => `${r.label} ${r.phrases.map(p => `${p.title} ${p.segments.flatMap(s => s.blocks).map(b => b.kind === 'text' ? b.text : b.name).join(' ')}`).join(' ')}`.toLowerCase().includes(query.trim().toLowerCase()));
  return <section className="client-window team-recycle"><Titlebar title={`话术回收站 · ${status.teams.find(t => t.teamId === teamId)?.name || ''}`} /><div className="team-recycle-content">{editable ? <><input aria-label="搜索共享库回收站" placeholder="搜索回收话术" value={query} onChange={e => { setQuery(e.target.value); setPage(0); }} />{filtered.slice(page * 20, page * 20 + 20).map(r => <article key={r.id}><strong>{r.label}</strong><small>{new Date(r.deletedAt * 1000).toLocaleString()}</small>{r.phrases.map((p, i) => <div key={i}><b>{p.title}</b><p>{p.segments.flatMap(s => s.blocks).map(b => b.kind === 'text' ? b.text : `${b.kind === 'image' ? '图片' : '文件'}：${b.name}`).join('\n')}</p></div>)}<button disabled={busy} onClick={() => void action(r, false)}>还原</button><button disabled={busy} onClick={() => void action(r, true)}>永久删除</button></article>)}{!filtered.length && <p>回收站为空</p>}<div className="setting-buttons"><button disabled={page === 0} onClick={() => setPage(v => v - 1)}>上一页</button><span>{page + 1}／{Math.max(1, Math.ceil(filtered.length / 20))}</span><button disabled={(page + 1) * 20 >= filtered.length} onClick={() => setPage(v => v + 1)}>下一页</button></div></> : <p>没有该共享库回收站的访问权限。</p>}</div><Feedback message={message} clear={() => setMessage('')} /></section>;
}
