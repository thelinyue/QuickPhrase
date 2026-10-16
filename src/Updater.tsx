import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { getVersion } from '@tauri-apps/api/app';
import { call, openWindow } from './api';
import { FormDialog } from './ui';

export interface UpdateConfig { automatic: boolean; route: 'default' | 'backup' | 'custom' | 'direct'; customProxy: string }
export interface UpdateStatus {
  config: UpdateConfig; phase: 'idle' | 'checking' | 'available' | 'current' | 'downloading' | 'ready' | 'installing' | 'error';
  release: { version: string; notes: string } | null; downloaded: number; total: number | null; message: string; lastCheck: number; revision: number;
}
/** 先订阅后读取快照；后端序号防止迟到的查询覆盖下载进度。监听器随窗口销毁清理。 */
function useUpdateStatus() {
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [error, setError] = useState('');
  const revision = useRef(-1);
  const accept = (value: UpdateStatus) => {
    if (value && typeof value.revision === 'number' && value.revision >= revision.current) { revision.current = value.revision; setStatus(value); }
  };
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined;
    void listen<UpdateStatus>('update-state', e => { if (!disposed) accept(e.payload); }).then(async fn => {
      if (disposed) { fn(); return; } stop = fn;
      const value = await call<UpdateStatus>('get_update_status'); if (!disposed) accept(value);
    }).catch(e => { if (!disposed) setError(String(e)); });
    return () => { disposed = true; stop?.(); };
  }, []);
  return { status, accept, error, setError };
}

/** 配置仅在明确保存时写入本机；检查、下载和安装由 Rust 持有，窗口切换不会重启任务。 */
export function UpdateSettings() {
  const { status, accept, error, setError } = useUpdateStatus();
  const [draft, setDraft] = useState<UpdateConfig | null>(null);
  const [pending, setPending] = useState(false); const submitting = useRef(false);
  const [confirm, setConfirm] = useState(false); const [later, setLater] = useState(false);
  const config = draft ?? status?.config;
  const busy = pending || ['checking', 'downloading', 'installing'].includes(status?.phase || '');
  async function run(action: () => Promise<void>) {
    if (submitting.current) return; submitting.current = true; setPending(true); setError('');
    try { await action(); } catch (e) { setError(String(e)); } finally { submitting.current = false; setPending(false); }
  }
  const check = () => run(async () => {
    if (!config) return;
    if (draft) { accept(await call<UpdateStatus>('save_update_config', { config })); setDraft(null); }
    setLater(false); accept(await call<UpdateStatus>('check_update'));
  });
  return <section className="setting-block update-settings" aria-label="软件更新">
    <h2 className="settings-heading">软件更新</h2>
    {config && <fieldset disabled={busy} className="update-config">
      <label><input type="checkbox" checked={config.automatic} onChange={e => setDraft({ ...config, automatic: e.target.checked })} />自动检查更新，手动下载安装</label>
      <label>下载线路<select aria-label="下载线路" value={config.route} onChange={e => setDraft({ ...config, route: e.target.value as UpdateConfig['route'] })}>
        <option value="default">默认代理 · gh-proxy.com</option><option value="backup">备用代理 · ghfast.top</option><option value="custom">自定义代理</option><option value="direct">不使用 GitHub 代理</option>
      </select></label>
      {config.route === 'custom' && <><label>代理地址<input type="url" aria-label="代理地址" value={config.customProxy} placeholder="https://代理地址/" onChange={e => setDraft({ ...config, customProxy: e.target.value })} /></label><p className="settings-hint">使用“代理地址／完整 GitHub URL”的 HTTPS 下载代理。</p></>}
      <div className="setting-buttons"><button className="secondary-button" onClick={() => void check()}>{status?.phase === 'checking' ? '检查中…' : draft ? '保存并检查' : '检查更新'}</button>{config.route !== 'default' && <button className="secondary-button" onClick={() => setDraft({ ...config, route: 'default' })}>恢复默认线路</button>}</div>
    </fieldset>}
    {!status && !error && <p>正在读取更新设置…</p>}
    {status?.release && <><p><strong>新版本 v{status.release.version}</strong></p>{status.release.notes && <div className="update-notes">{status.release.notes}</div>}</>}
    {status?.phase === 'downloading' && <><progress aria-label="更新下载进度" value={status.total ? status.downloaded : undefined} max={status.total || undefined} /><p>{(status.downloaded / 1024 / 1024).toFixed(1)} MB{status.total ? ` / ${(status.total / 1024 / 1024).toFixed(1)} MB` : ''}</p><button className="secondary-button" disabled={pending} onClick={() => void run(async () => { await call('cancel_update'); })}>取消下载</button></>}
    {status?.release && ['available', 'error'].includes(status.phase) && <button className="primary-button" disabled={busy || !!draft} onClick={() => void run(async () => { setLater(false); accept(await call<UpdateStatus>('download_update')); })}>{status.phase === 'error' ? '重新下载' : '下载更新'}</button>}
    {status?.phase === 'ready' && <div className="setting-buttons"><button className="primary-button" disabled={busy} onClick={() => setConfirm(true)}>退出并安装</button><button className="secondary-button" disabled={busy} onClick={() => setLater(true)}>稍后</button></div>}
    {status?.message && <p role="status">{later && status.phase === 'ready' ? '可以继续使用，稍后回到此处安装。' : status.message}</p>}
    {error && <p role="alert" className="validation-error">{error}</p>}
    <p className="settings-hint">公共代理可能暂时不可用，可更换线路或直连重试。下载后会验证发布签名；程序和安装器仍未附带 Windows 代码签名。</p>
    {confirm && <FormDialog close={() => setConfirm(false)} spec={{ title: '退出并安装更新', submit: '退出并安装', fields: <><p>请先保存并关闭编辑、排序和管理窗口，等待发送及传输完成。</p><p>安装版沿用原安装目录；免安装版将转为安装版，原文件夹保留。</p><p>安装和升级保留本地数据。安装向导启动后，闪语将退出。</p></>, onSubmit: async () => { await call('install_update'); } }} />}
  </section>;
}

/** 更新仅在底栏显示角标，悬停展示版本信息；复用已验证状态，点击只打开更新页。 */
export function UpdateBadge() {
  const { status } = useUpdateStatus();
  const [current, setCurrent] = useState('读取中…');
  const [error, setError] = useState('');
  useEffect(() => {
    let disposed = false;
    void getVersion().then(version => { if (!disposed) setCurrent(`v${version}`); }).catch(() => { if (!disposed) setCurrent('读取失败'); });
    return () => { disposed = true; };
  }, []);
  if (!status?.release) return <span className="brand-name">闪语</span>;
  const hint = `当前版本 ${current}\n最新版本 v${status.release.version}\n点击查看并升级`;
  return <span className="brand-update" role="status"><button className="brand-name brand-update-button" title={error || hint} aria-label={error || hint} onClick={() => { setError(''); void openWindow('settings', '', '', '关于').catch(e => setError(String(e))); }}>闪语<span className="brand-update-badge" aria-hidden="true">新</span></button></span>;
}
