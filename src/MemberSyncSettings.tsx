import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call } from './api';
import { AccountModal } from './AccountControls';
import { useMembers } from './useMembers';
import { categoryLabel, type MemberOverview, type TeamMemberIdentity, type TeamStatus } from './types';

const memberName = (user: TeamMemberIdentity) => user.displayName ? `${user.displayName}（${user.username}）` : user.username;
interface ShareDraft { mode: 'all' | 'selected'; roots: string[]; version: number }

/** 共享一次开放，接收账号自主选择；三项操作各自提交，名单与人数使用同一份响应。 */
export function MemberSyncSettings({ account, syncPaused = false }: { account: TeamStatus; syncPaused?: boolean }) {
  const view = useMembers(account);
  const [overview, setOverview] = useState<MemberOverview | null>(null);
  const [message, setMessage] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const [share, setShare] = useState<ShareDraft | null>(null);
  const [choose, setChoose] = useState(false); const [chosen, setChosen] = useState('');
  const [usersOpen, setUsersOpen] = useState(false); const [loading, setLoading] = useState(false); const [loadError, setLoadError] = useState('');
  const owner = `${account.server}:${account.user?.id || ''}:${account.sessionEpoch ?? ''}`;
  const ownerRef = useRef(owner); ownerRef.current = owner; const sequence = useRef(0);
  const supported = account.memberPhraseSync === true;
  const readable = !!account.user && !account.user.mustChangePassword && supported;
  const online = readable && !account.offline && !overview?.offline;
  const epoch = account.sessionEpoch || 0;
  async function refresh() {
    if (!readable) return;
    const request = ++sequence.current, scope = owner;
    setLoading(true); setLoadError('');
    try {
      const next = await call<MemberOverview>('get_member_overview', { sessionEpoch: epoch });
      if (ownerRef.current === scope && request === sequence.current) setOverview(next);
    } catch (e) { if (ownerRef.current === scope && request === sequence.current) setLoadError(String(e)); }
    finally { if (ownerRef.current === scope && request === sequence.current) setLoading(false); }
  }
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined;
    void refresh();
    void listen('member-content-changed', () => { if (!disposed) void refresh(); }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; sequence.current++; stop?.(); };
  }, [owner, readable, account.offline]);
  useEffect(() => { setOverview(null); setShare(null); setChoose(false); setUsersOpen(false); setMessage(''); }, [owner]);
  async function run(work: (current: () => boolean) => Promise<void>) {
    if (busyRef.current) return;
    busyRef.current = true; setBusy(true); setMessage(''); const scope = owner;
    const current = () => ownerRef.current === scope;
    try { await work(current); if (current()) await refresh(); }
    catch (e) { if (current()) { setMessage(String(e)); await refresh(); } }
    finally { busyRef.current = false; setBusy(false); }
  }
  const sharing = overview?.sharing;
  const currentGrant = overview?.available.find(g => g.id === overview.subscription.grantId);
  const selectedGrant = overview?.available.find(g => g.id === chosen);
  const selectedRoots = share?.roots.filter(id => overview?.categories.some(c => c.id === id)) || [];
  const using = !!(overview?.subscription.grantId || view?.subscription.grantId);
  const usedCategories = sharing?.roots.map(category => categoryLabel(category, sharing.roots)).join('、') || '暂无有效分类';
  const users = [...(overview?.users || [])].sort((a, b) => a.username < b.username ? -1 : a.username > b.username ? 1 : 0);
  const errorNotice = loadError && <p role="alert" className="validation-error">{loadError} <button className="text-button" disabled={loading} onClick={() => void refresh()}>重试</button></p>;
  const offline = overview?.offline || account.offline;
  if (!account.user) return null;
  return <>
    <div className="sync-setting member-sync-settings"><div className="sync-row"><strong>成员话术</strong><span className="sync-summary">{view?.ownerLabel || (currentGrant && memberName(currentGrant.owner))}{(view?.ownerLabel || currentGrant) && ' · '}{view?.state || '未使用'}</span>
      <div className="setting-buttons"><button className="secondary-button" disabled={busy || !online || !overview} onClick={() => { setChosen(overview?.subscription.grantId || ''); setChoose(true); setMessage(''); void refresh(); }}>{using ? '更换成员' : '选择成员'}</button>
        {using && <button className="secondary-button" disabled={busy || !online || !overview?.subscription.grantId} onClick={() => void run(async () => {
          if (await confirm('停止使用并清除本机成员缓存？此设置会同步到同账号的其他设备。', { title: '停止使用' })) await call('set_member_subscription', { sessionEpoch: epoch, request: { grantId: null, expectedVersion: overview!.subscription.version } });
        })}>停止使用</button>}</div></div>
      {view?.message && <p role="status" className="validation-error">{view.message}</p>}
      {!supported && <p className="account-warning">Hub 需升级后才能使用成员话术。</p>}
    </div>
    <div className="sync-setting member-share-settings"><div className="sync-row"><strong>开放共享</strong><span className="sync-summary">{sharing?.enabled ? <><span>{sharing.mode === 'all' ? '全部分类' : `已选 ${sharing.roots.length} 个分类`}</span><span aria-hidden="true"> · </span><button className="text-button member-user-count" onClick={() => { setUsersOpen(true); void refresh(); }}>{users.length} 人使用</button>{offline && <small>离线数据</small>}</> : overview ? '未开启' : loading ? '加载中…' : '暂不可用'}</span>
      <div className="setting-buttons"><button className="secondary-button" disabled={busy || !online || !overview?.sourceReady} onClick={() => { setShare({ mode: sharing?.mode || 'all', roots: sharing?.rootIds || [], version: sharing?.version || 0 }); setMessage(''); }}>{sharing?.enabled ? '调整范围' : '开启共享'}</button>
        {sharing?.enabled && <button className="secondary-button" disabled={busy || !online} onClick={() => void run(async () => {
          if (await confirm('关闭后所有成员停止使用，再次开启需由成员重新选择。离线设备联网后生效，已复制或发送的内容无法收回。', { title: '关闭共享', kind: 'warning' })) await call('save_member_share', { sessionEpoch: epoch, request: { enabled: false, mode: sharing.mode, rootIds: sharing.mode === 'all' ? [] : sharing.rootIds.filter(id => overview?.categories.some(c => c.id === id)), expectedVersion: sharing.version } });
        })}>关闭共享</button>}</div></div>
      {sharing?.enabled && syncPaused && <p className="account-hint">同步已暂停，共享保留最后同步内容。</p>}
      {online && overview && !overview.sourceReady && <p className="account-hint">先开启多设备同步，上传本人话术后即可共享。</p>}
    </div>
    {!usersOpen && errorNotice}
    {usersOpen && <AccountModal title={`使用成员（${users.length}）`} busy={false} close={() => setUsersOpen(false)} footer={<button className="secondary-button" onClick={() => setUsersOpen(false)}>关闭</button>}>
      {loading && <p role="status">加载中…</p>}{offline && <p className="account-hint">离线数据</p>}{errorNotice}
      {!loading && !loadError && !users.length && <p>暂无成员使用</p>}
      {!!users.length && <table className="member-users"><thead><tr><th scope="col">成员</th><th scope="col">使用分类</th></tr></thead><tbody>{users.map(user => <tr key={user.userId}><td>{memberName(user)}</td><td>{usedCategories}</td></tr>)}</tbody></table>}
    </AccountModal>}
    {choose && <AccountModal title="选择成员" busy={busy} close={() => { setChoose(false); setMessage(''); }} footer={<><button className="secondary-button" disabled={busy} onClick={() => setChoose(false)}>取消</button><button className="primary-button" disabled={busy || !online || !selectedGrant || chosen === overview?.subscription.grantId} onClick={() => void run(async current => {
      await call('set_member_subscription', { sessionEpoch: epoch, request: { grantId: chosen, expectedVersion: overview!.subscription.version } }); if (current()) setChoose(false);
    })}>使用话术</button></>}><label>成员<select value={chosen} onChange={e => setChosen(e.target.value)}><option value="">请选择成员</option>{overview?.available.map(grant => <option key={grant.id} value={grant.id}>{memberName(grant.owner)}</option>)}</select></label>
      {overview && !overview.available.length && <p>暂无成员开放共享</p>}
      {selectedGrant && <><p>{selectedGrant.mode === 'all' ? '全部分类（包含今后新增分类）' : '共享分类：'}</p><ul>{selectedGrant.roots.map(c => <li key={c.id}>{categoryLabel(c, selectedGrant.roots)}</li>)}</ul></>}
      <p className="account-hint">分类只读，每个账号可使用一位成员的话术，多设备共用选择。</p>{message && <p role="alert" className="validation-error">{message}</p>}
    </AccountModal>}
    {share && <AccountModal title={sharing?.enabled ? '调整范围' : '开启共享'} busy={busy} close={() => { setShare(null); setMessage(''); }} footer={<><button className="secondary-button" disabled={busy} onClick={() => setShare(null)}>取消</button><button className="primary-button" disabled={busy || !online || (share.mode === 'selected' && !selectedRoots.length)} onClick={() => void run(async current => {
      await call('save_member_share', { sessionEpoch: epoch, request: { enabled: true, mode: share.mode, rootIds: share.mode === 'all' ? [] : selectedRoots, expectedVersion: share.version } }); if (current()) setShare(null);
    })}>确认共享</button></>}>
      <p className="account-hint">开放后，同一 Hub 的成员可直接使用，无需逐人授权。</p>
      <div className="member-root-choices"><label><input type="radio" name="share-mode" checked={share.mode === 'all'} onChange={() => setShare(s => s && { ...s, mode: 'all' })} />全部分类（包含今后新增分类）</label><label><input type="radio" name="share-mode" checked={share.mode === 'selected'} onChange={() => setShare(s => s && { ...s, mode: 'selected' })} />指定分类</label></div>
      {share.mode === 'selected' && <div className="member-root-choices">{overview?.categories.map(category => <label key={category.id}><input type="checkbox" checked={selectedRoots.includes(category.id)} onChange={e => setShare(s => s && { ...s, roots: e.target.checked ? [...selectedRoots, category.id] : selectedRoots.filter(id => id !== category.id) })} />{categoryLabel(category, overview.categories)}</label>)}{!overview?.categories.length && <p>暂无可选分类</p>}</div>}
      <p className="account-hint">所选分类内新增内容自动共享；包含话术、图片和附件。</p>
      {message && <p role="alert" className="validation-error">{message}</p>}
    </AccountModal>}
    {message && !share && !choose && <p role="alert" className="validation-error">{message}</p>}
  </>;
}
