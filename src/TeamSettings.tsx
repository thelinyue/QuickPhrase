import { useEffect, useRef, useState, type FormEvent } from 'react';
import { call, openWindow, useTeams } from './api';
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
    {!status.user ? <><form className="team-connection" onSubmit={login}><fieldset disabled={busy} className="dialog-fields"><label>服务器地址<input value={server} onChange={e => setServer(e.target.value)} placeholder="http://192.168.1.20:7823" required autoComplete="off" /></label><label>用户名<input value={username} onChange={e => setUsername(e.target.value)} required maxLength={64} autoComplete="username" /></label><PasswordField label="密码" name="password" current /><p className="account-hint">局域网可用 HTTP，公网建议 HTTPS。</p></fieldset><button className="primary-button" disabled={busy}>{busy ? '连接中…' : '登录'}</button></form><p className="account-hint">登录后可开启个人同步、使用共享话术。</p></> : <>
      <section className="account-section account-identity"><div className="account-line"><strong>当前账号</strong><span className="account-user">{status.user.username}{status.user.displayName && ` · ${status.user.displayName}`}<small>{status.user.admin ? '服务管理员' : '用户'}</small></span><span className={`account-status${status.offline ? ' offline' : ''}`}>{status.offline ? '离线' : '已连接'}</span></div><div className="account-line"><strong>服务器</strong><span className="account-server" title={status.server}>{status.server}</span></div><div className="setting-buttons"><button className="secondary-button" disabled={busy || status.offline} onClick={() => setPassword(true)}>修改密码</button><button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_refresh'))}>刷新连接</button><button className="secondary-button" disabled={busy} onClick={() => void run(() => call('team_logout'))}>退出登录</button></div>{status.user.mustChangePassword && <p className="account-warning">请先修改初始密码。</p>}</section>
    </>}<PrivateSyncSettings key={account} account={status} />{status.user && <>
      <section className="account-section"><div className="account-line"><strong>共享话术</strong><span>{status.user.admin ? '可管理' : '已加入'}：{status.teams.length}</span></div><div className="setting-buttons"><button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('team-management'))}>共享话术管理</button>{status.user.admin && <button className="secondary-button" disabled={busy || !manageable} onClick={() => void run(() => openWindow('user-management'))}>账号管理</button>}</div><p className="account-hint">在共享话术管理中搜索库、查看最近同步时间。</p><p className="account-hint">离线可用已缓存内容；联网后核验权限。</p></section>
    </>}<AccountNotice message={message} error={failed} clear={() => setMessage('')} />{password && status.user && !status.offline && <PasswordDialog key={account} first={status.user.mustChangePassword} close={() => setPassword(false)} changed={() => { if (accountRef.current === account) { setFailed(false); setMessage('密码已修改'); } }} />}
  </div>;
}
