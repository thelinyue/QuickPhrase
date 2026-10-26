import { useDefaultTeam } from './useDefaultTeam';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { call, openWindow, useTeams } from './api';
import { PrivateSyncSettings } from './PrivateSyncSettings';
import { MemberSyncSettings } from './MemberSyncSettings';
import { AccountNotice, PasswordDialog, PasswordField } from './AccountControls';
import { FormDialog } from './ui';

/** 账号页提供连接、本人同步和成员分享；管理及改密仍使用独立窗口或浮层。 */
export function TeamSettings() {
  const { status } = useTeams();
  const [connection, setConnection] = useState(false);
  const [syncPaused, setSyncPaused] = useState(false);
  const [server, setServer] = useState(''); const [username, setUsername] = useState('');
  const defaultTeam = useDefaultTeam(status);
  const [message, setMessage] = useState(''); const [failed, setFailed] = useState(false); const [password, setPassword] = useState(false);
  const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const account = `${status.server}:${status.user?.id || ''}:${status.sessionEpoch ?? ''}`; const accountRef = useRef(account); accountRef.current = account;
  const manageable = !!status.user && !status.user.mustChangePassword && !status.offline;
  useEffect(() => { if (status.server) setServer(status.server); if (status.username) setUsername(status.username); }, [status.server, status.username]);
  useEffect(() => { setPassword(false); setConnection(false); setMessage(''); }, [account]);
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
  return <div className="team-settings account-settings"><h2 className="settings-heading">账号与共享</h2>
    {!status.user ? <><form className="team-connection" onSubmit={login}><fieldset disabled={busy} className="dialog-fields"><label>服务器地址<input value={server} onChange={e => setServer(e.target.value)} placeholder="http://192.168.1.20:7823" required autoComplete="off" /></label><label>用户名<input value={username} onChange={e => setUsername(e.target.value)} required maxLength={64} autoComplete="username" /></label><PasswordField label="密码" name="password" current /><p className="account-hint">局域网可用 HTTP，公网建议 HTTPS。</p></fieldset><button className="primary-button" disabled={busy}>{busy ? '连接中…' : '登录'}</button></form><p className="account-hint">登录后可开启个人同步、使用共享话术。</p></> : <>
      <section className="account-section account-identity"><div className="account-line"><strong>当前账号</strong><span className="account-user"><span className="account-user-name" title={status.user.displayName ? `${status.user.username} · ${status.user.displayName}` : status.user.username}>{status.user.username}{status.user.displayName && ` · ${status.user.displayName}`}</span><small>· {status.user.admin ? '服务管理员' : '用户'}</small></span><span className={`account-status${status.offline ? ' offline' : ''}`}>{status.offline ? '已断开连接' : '已连接'}</span></div><div className="account-line"><strong>服务器</strong><span className="account-server" title={status.server}>{status.server}</span></div><div className="setting-buttons"><button className="secondary-button" disabled={busy} onClick={() => setConnection(true)}>修改连接</button><button className="secondary-button" disabled={busy || status.offline} onClick={() => setPassword(true)}>修改密码</button>{status.offline && <button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_refresh'))}>重连</button>}<button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_logout'))}>退出登录</button></div>{status.user.mustChangePassword && <p className="account-warning">请先修改初始密码。</p>}</section>
    </>}<section className="account-section sync-sharing-settings"><h3>同步与共享</h3><PrivateSyncSettings key={account} account={status} onPaused={setSyncPaused} /><MemberSyncSettings key={`member:${account}`} account={status} syncPaused={syncPaused} /></section>{status.user && <>
      <section className="account-section shared-library-settings"><label className="default-team-setting">默认共享话术<select aria-label="默认共享话术" disabled={defaultTeam.saving} value={defaultTeam.id} onChange={e => void defaultTeam.save(e.target.value)}><option value="">未设置</option>{defaultTeam.id && !status.teams.some(t => t.teamId === defaultTeam.id) && <option value={defaultTeam.id} disabled>原默认库已不可访问</option>}{status.teams.map(t => <option key={t.teamId} value={t.teamId}>{t.name}</option>)}</select></label>{defaultTeam.error && <p role="alert">{defaultTeam.error}</p>}<div className="sync-row"><strong>共享话术</strong><span className="sync-summary">{status.user.admin ? '可管理' : '已加入'}：{status.teams.length}</span><div className="setting-buttons"><button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('team-management'))}>管理话术</button>{status.user.admin && <button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('user-management'))}>账号管理</button>}</div></div>{status.offline && <p className="account-hint">离线使用已缓存内容，重新连接后可管理。</p>}</section>
    </>}<AccountNotice message={message} error={failed} clear={() => setMessage('')} />{password && status.user && !status.offline && <PasswordDialog key={account} first={status.user.mustChangePassword} close={() => setPassword(false)} changed={() => { if (accountRef.current === account) { setFailed(false); setMessage('密码已修改'); } }} />}
    {connection && status.user && <FormDialog key={`connection:${account}`} close={() => { if (accountRef.current === account) setConnection(false); }} spec={{ title: '修改连接', submit: '连接', className: 'account-dialog', fields: <>
      <label>服务器地址<input name="server" defaultValue={status.server} required autoComplete="off" autoFocus /></label>
      <label>用户名<input name="username" defaultValue={status.username} required maxLength={64} autoComplete="username" /></label>
      <PasswordField label="密码" name="password" current />
      <p className="account-hint">请输入目标账号密码。更换服务器或账号后，分别保留各自的个人内容。</p>
    </>, onSubmit: async form => {
      // 复用登录路径：目标认证成功后才切换，旧账号缓存和未完成任务按已有规则隔离。
      await call('team_login', { server: String(form.get('server')).trim(), username: String(form.get('username')).trim(), password: String(form.get('password')) });
    } }} />}
  </div>;
}
