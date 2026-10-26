import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call } from './api';
import { syncTime, type TeamStatus } from './types';
import { AccountModal } from './AccountControls';

interface Status { libraryId: string; username: string; enabled: boolean; state: string; pending: number; conflicts: number; settingsPending: number; message: string; sameAccount: boolean; initialized: boolean; lastSuccessfulSyncAt: number | null; syncIntervalMinutes: number }
interface Choice { key: string; label: string; local: string; server: string; setting: boolean }
interface Preview { id: string; categories: number; phrases: number; commonPhrases: number; conflicts: Choice[]; resume: boolean; restore: boolean }
interface Conflict { kind: string; id: string; label: string; local: string; server: string; serverVersion: number; localRevision: number }
interface SettingChange { key: string; current: unknown; next: unknown }
const names: Record<string, string> = { enter: '发送按键', interval: '分段间隔', phraseDoubleClick: '话术双击', commonSingleClick: '旧版短语单击（不再生效）', searchHotkey: '搜索快捷键', windowHotkey: '窗口快捷键', commonHotkey: '短语快捷键', customHotkeys: '个人快捷键', globalPhraseHotkeys: '全局话术快捷键', sequenceHotkeys: '编号快捷键', sequenceDirectSend: '编号直接发送', sequenceBindings: '编号绑定', theme: '主题', fontSize: '字号', showNumbers: '显示编号', showPreview: '显示内容', showTitle: '显示标题', showSearch: '显示搜索', showCommon: '显示常用短语', exitSearch: '投递后退出搜索', searchMode: '搜索方式', expand: '展开分类', categoryHeight: '话术分类高度', fileCategoryHeight: '文件分类高度', commonHeight: '短语区域高度', titleColor: '标题颜色', phraseCategoryColor: '话术分类颜色', fileCategoryColor: '文件分类颜色', filesFirst: '文件优先' };
const format = (value: unknown) => typeof value === 'boolean' ? value ? '开' : '关' : Array.isArray(value) ? value.join('，') : String(value ?? '');

/** 云端配置与本机生效状态分开展示；确认按钮只应用本机，不会反写未修改的云端值。 */
export function PrivateSyncSettings({ account, onPaused }: { account: TeamStatus; onPaused?: (paused: boolean) => void }) {
  const [status, setStatus] = useState<Status | null>(null); const [message, setMessage] = useState(''); const [busy, setBusy] = useState(false);
  const [intervalBusy, setIntervalBusy] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null); const [choices, setChoices] = useState<Record<string, string>>({}); const [conflicts, setConflicts] = useState<Conflict[] | null>(null); const [settings, setSettings] = useState<SettingChange[] | null>(null); const [page, setPage] = useState(0);
  const sequence = useRef(0); const busyRef = useRef(false); const [panelLibrary, setPanelLibrary] = useState('');
  const owner = `${account.server}:${account.user?.id || ''}:${account.sessionEpoch ?? ''}`; const ownerRef = useRef(owner); ownerRef.current = owner;
  const libraryRef = useRef(status?.libraryId); libraryRef.current = status?.libraryId;
  async function refresh() { const request = ++sequence.current, scope = owner; try { const result = await call<Status>('get_private_status'); if (ownerRef.current === scope && request === sequence.current) setStatus(result); } catch { /* 旧测试宿主或尚未就绪的窗口不影响连接表单。 */ } }
  useEffect(() => {
    let disposed = false; const stops: (() => void)[] = [];
    for (const event of ['private-sync-changed', 'library-changed', 'teams-changed']) void listen(event, () => { if (!disposed) void refresh(); }).then(stop => disposed ? stop() : stops.push(stop));
    return () => { disposed = true; sequence.current++; stops.forEach(stop => stop()); };
  }, []);
  async function run(work: () => Promise<unknown>) { if (busyRef.current) return; busyRef.current = true; setBusy(true); setMessage(''); const scope = owner; try { await work(); if (ownerRef.current === scope) await refresh(); } catch (e) { if (ownerRef.current === scope) setMessage(String(e)); } finally { busyRef.current = false; setBusy(false); } }
  async function readPanel<T>(command: string, apply: (value: T) => void) { const scope = owner, library = libraryRef.current; const value = await call<T>(command); if (ownerRef.current === scope && libraryRef.current === library) apply(value); }
  useEffect(() => { sequence.current++; setStatus(null); setPreview(null); setConflicts(null); setSettings(null); setMessage(''); void refresh(); }, [owner]);
  useEffect(() => { if (panelLibrary && status?.libraryId !== panelLibrary) { setConflicts(null); setSettings(null); } setPreview(null); }, [status?.libraryId]);
  useEffect(() => { onPaused?.(!!status?.sameAccount && !!status.initialized && !status.enabled); }, [status?.sameAccount, status?.initialized, status?.enabled, onPaused]);
  const online = !!account.user && !account.offline && !account.user.mustChangePassword && account.privateSync === true;
  const bound = !!status?.username;
  const label = !bound ? '仅保存在本机' : !account.user ? '未登录，内容保留在本机' : !status.sameAccount ? '内容属于其他账号' : !status.initialized ? '未开启' : !status.enabled ? '已暂停' : status.state;
  async function saveInterval(minutes: number) {
    if (intervalBusy) return;
    setIntervalBusy(true); setMessage('');
    const scope = owner;
    try { await call('set_private_sync_interval', { minutes }); if (ownerRef.current === scope) await refresh(); }
    catch (e) { if (ownerRef.current === scope) setMessage(String(e)); }
    finally { setIntervalBusy(false); }
  }
  return <div className="sync-setting private-sync-settings"><div className="sync-row"><strong>多设备同步</strong><span role="status" className="sync-summary">{label}</span>
    <div className="setting-buttons">
      {account.privateSync && <label className="sync-interval" title="本机定时检查间隔，内容变更仍即时同步">间隔<select aria-label="同步检查间隔" disabled={!status || intervalBusy} value={status?.syncIntervalMinutes || 1} onChange={e => void saveInterval(Number(e.target.value))}>{[1, 3, 5].map(minutes => <option key={minutes} value={minutes}>{minutes}分钟</option>)}</select></label>}
      {status?.enabled ? <button className="secondary-button" disabled={busy} onClick={() => void run(() => call('pause_private_sync'))}>暂停同步</button> : <button className="secondary-button" disabled={busy || !status || !online || (bound && !status.sameAccount)} onClick={() => void run(() => readPanel<Preview>('preview_private_sync', value => { setPreview(value); setChoices({}); setPage(0); }))}>{status?.initialized ? '恢复同步' : '开启同步'}</button>}
      {status?.conflicts ? <button className="secondary-button" disabled={busy} onClick={() => void run(() => readPanel<Conflict[]>('get_private_conflicts', value => { setPanelLibrary(status!.libraryId); setConflicts(value); }))}>处理冲突（{status.conflicts}）</button> : null}
      {status?.settingsPending ? <button className="secondary-button" disabled={busy} onClick={() => void run(() => readPanel<SettingChange[]>('get_private_settings', value => { setPanelLibrary(status!.libraryId); setSettings(value); }))}>设置待确认（{status.settingsPending}）</button> : null}
    </div>
    </div>
    <details className="sync-details"><summary>同步详情</summary>
    {bound && <p className="account-hint">所属账号：{status.username}</p>}
    <p className="account-hint">最近同步：<time dateTime={status?.lastSuccessfulSyncAt != null ? new Date(status.lastSuccessfulSyncAt).toISOString() : undefined}>{syncTime(status?.lastSuccessfulSyncAt)}</time></p>
    <p className="account-hint">暂停后仍可查看和编辑，恢复后会同步期间的改动。</p></details>
    {account.user && account.privateSync === false && <p className="settings-hint">服务端需升级后才能同步个人话术。</p>}
    {preview && <AccountModal title={preview.restore ? '恢复后核对' : preview.resume ? '恢复同步' : '首次合并'} busy={busy} close={() => { void run(async () => { await call('cancel_private_sync_preview'); setPreview(null); setMessage(''); }); }} footer={<><button className="secondary-button" disabled={busy} onClick={() => void run(async () => { await call('cancel_private_sync_preview'); setPreview(null); setMessage(''); })}>取消</button><button className="primary-button" disabled={busy || !online} onClick={() => void run(async () => { await call('confirm_private_sync', { request: { previewId: preview.id, choices } }); setPreview(null); })}>{busy ? '提交中…' : '确认同步'}</button></>}><p>{preview.resume ? '保留离线改动，恢复同步。' : `${preview.categories} 个分类 · ${preview.phrases} 条话术 · ${preview.commonPhrases} 条常用短语`}</p>
      {preview.conflicts.length > 0 && <><p>相同记录请选择版本，默认使用服务器版。</p>{preview.conflicts.slice(page * 20, page * 20 + 20).map(item => <div className="private-choice" key={item.key}><strong>{item.setting ? names[item.key.slice(8)] || '个人设置' : item.label}</strong><p>本机：{item.local}</p><p>服务器：{item.server}</p><select aria-label={`合并 ${item.label}`} value={choices[item.key] || 'server'} onChange={e => setChoices(c => ({ ...c, [item.key]: e.target.value }))}><option value="server">使用服务器</option><option value="local">使用本机</option>{!item.setting && <option value="both">保留两份</option>}</select></div>)}{preview.conflicts.length > 20 && <div className="setting-buttons"><button disabled={page === 0} onClick={() => setPage(p => p - 1)}>上一页</button><span>{page + 1} / {Math.ceil(preview.conflicts.length / 20)}</span><button disabled={(page + 1) * 20 >= preview.conflicts.length} onClick={() => setPage(p => p + 1)}>下一页</button></div>}</>}
      {message && <p role="alert" className="validation-error">{message}</p>}</AccountModal>}
    {conflicts && <AccountModal title="处理冲突" busy={busy} close={() => { setConflicts(null); setMessage(''); }} footer={<button className="secondary-button" disabled={busy} onClick={() => { setConflicts(null); setMessage(''); }}>关闭</button>}>{conflicts.map(item => <div className="private-choice" key={`${item.kind}:${item.id}`}><strong>{item.kind === 'setting' ? names[item.id] || '个人设置' : item.label}</strong><p>本机：{item.local}</p><p>服务器：{item.server}</p><div className="setting-buttons">{['local', 'server', ...(item.kind === 'setting' ? [] : ['both'])].map(choice => <button className="secondary-button" disabled={busy} key={choice} onClick={() => void run(async () => { if (item.kind === 'category' && !(await confirm('同时处理此分类及关联内容？', { title: '分类冲突' }))) return; await call('resolve_private_conflict', { request: { kind: item.kind, id: item.id, choice, serverVersion: item.serverVersion, localRevision: item.localRevision, libraryId: panelLibrary } }); setPanelLibrary(status!.libraryId); await readPanel<Conflict[]>('get_private_conflicts', setConflicts); })}>{choice === 'local' ? '使用本机' : choice === 'server' ? '使用服务器' : '保留两份'}</button>)}</div></div>)}{message && <p role="alert" className="validation-error">{message}</p>}</AccountModal>}
    {settings && <AccountModal title="应用设置" busy={busy} close={() => { setSettings(null); setMessage(''); }} footer={<><button className="secondary-button" disabled={busy} onClick={() => { setSettings(null); setMessage(''); }}>稍后</button><button className="primary-button" disabled={busy || !online} onClick={() => void run(async () => { if (await confirm('应用这些发送和快捷键设置？', { title: '应用设置' })) { await call('apply_private_settings', { libraryId: panelLibrary }); setSettings(null); } })}>应用本机</button></>}>{settings.map(item => <p key={item.key}>{names[item.key] || '个人设置'}：{format(item.current)} → {format(item.next)}</p>)}{message && <p role="alert" className="validation-error">{message}</p>}</AccountModal>}
    {status?.message && <p role="status" className="validation-error">{status.message}</p>}{message && !preview && !conflicts && !settings && <p role="alert" className="validation-error">{message}</p>}
  </div>;
}
