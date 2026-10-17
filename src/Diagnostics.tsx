import { useEffect, useRef, useState } from 'react';
import { save } from '@tauri-apps/plugin-dialog';
import { call, closeWindow } from './api';

type Category = 'input' | 'network' | 'window' | 'performance';
interface Status {
  running: boolean; remainingSeconds: number; categories: Category[]; records: number;
  dropped: number; overwritten: number; diskFailed: boolean; panicRecorded: boolean;
}
const categories: { id: Category; label: string; hint: string }[] = [
  { id: 'input', label: '快捷键与输入法', hint: '快捷键失效、误触或无法输入' },
  { id: 'network', label: '账号与同步', hint: '登录、共享或下载异常' },
  { id: 'window', label: '窗口与显示', hint: '吸附、跨屏或尺寸异常' },
  { id: 'performance', label: '卡顿与响应', hint: '操作缓慢或无响应' },
];

/** 记录由原生服务计时；关闭设置或重新打开都不会延长采集，导出取消也不丢证据。 */
export function Diagnostics() {
  const [selected, setSelected] = useState<Category[]>(['input']);
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const active = useRef(true), working = useRef(false), revision = useRef(0);
  useEffect(() => {
    active.current = true;
    let pending = false;
    const refresh = async () => {
      if (pending || working.current) return;
      pending = true;
      const current = revision.current;
      try {
        const value = await call<Status>('diagnostics_status');
        if (active.current && current === revision.current) {
          setStatus(value);
          if (value.running) setSelected(value.categories);
        }
      } catch (e) { if (active.current && current === revision.current) setError(String(e)); }
      finally { pending = false; }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), 1000);
    return () => { active.current = false; window.clearInterval(timer); };
  }, []);
  async function run(work: () => Promise<void>) {
    if (working.current) return;
    working.current = true; ++revision.current; setBusy(true); setError(''); setMessage('');
    try { await work(); } catch (e) { if (active.current) setError(String(e)); }
    finally { working.current = false; if (active.current) setBusy(false); }
  }
  async function start() {
    await run(async () => {
      const value = await call<Status>('diagnostics_start', { categories: selected });
      setStatus(value); setMessage('记录已开始，请返回原窗口复现问题。');
      // 使用正常关闭请求，沿用设置页的保存等待与失败留窗保护。
      await closeWindow();
    });
  }
  async function exportReport() {
    await run(async () => {
      if (status?.running) setStatus(await call<Status>('diagnostics_stop'));
      const path = await save({ title: '保存诊断包', defaultPath: `闪语诊断-${new Date().toISOString().replace(/[:.]/g, '-')}.zip`, filters: [{ name: '诊断包', extensions: ['zip'] }] });
      if (!path) return;
      await call('diagnostics_export', { path });
      setMessage('已导出。请发给开发者，并附上复现时间和步骤。');
    });
  }
  return <div className="diagnostics-settings">
    <h2 className="settings-heading">问题诊断</h2>
    <p className="settings-hint">开始后返回原窗口复现问题，再回此页导出日志。</p>
    <fieldset className="diagnostics-options" disabled={busy || !status || status.running}>
      <legend>选择问题 · 可多选</legend>
      {categories.map(c => <label key={c.id}><input type="checkbox" checked={selected.includes(c.id)} onChange={e => setSelected(old => e.target.checked ? [...old, c.id] : old.filter(id => id !== c.id))} /><span>{c.label}<small>{c.hint}</small></span></label>)}
    </fieldset>
    <p role="status" className="diagnostics-status">{!status ? '正在读取…' : status.running ? `记录中 · 剩余 ${Math.floor(status.remainingSeconds / 60)}:${String(status.remainingSeconds % 60).padStart(2, '0')}` : '未在记录'}<span className="settings-hint">5 分钟后自动停止</span></p>
    {status?.diskFailed && <p role="alert">本地记录保存失败。当前内存记录仍可尝试导出，重启可能丢失。</p>}
    <div className="setting-buttons">
      {status?.running ? <><button className="primary-button" disabled={busy} onClick={() => void exportReport()}>停止并导出</button><button className="secondary-button" disabled={busy} onClick={() => void run(closeWindow)}>返回复现</button></>
        : <><button className="primary-button" disabled={busy || !status || !selected.length} onClick={() => void start()}>开始记录</button><button className="secondary-button" disabled={busy || !status || (!status.records && !status.panicRecorded)} onClick={() => void exportReport()}>导出日志</button></>}
    </div>
    {message && <p role="status">{message}</p>}{error && <p role="alert">{error}</p>}
    <details className="diagnostics-details"><summary>记录说明</summary>
      <p>不记录实际按键、话术、聊天、剪贴板正文或账号密码，不自动上传。</p>
      <p>平时仅保留少量运行摘要。详细记录按所选问题开启，重启后仍可导出已保存的记录。</p>
      <p>设置打开时快捷键暂停，请返回原窗口复现。日志不能覆盖所有问题，也不包含截图或内存转储。</p>
      {status?.panicRecorded && <p>已有异常退出线索，导出时会一并附带。</p>}
      {status && <p>已保存 {status.records} 条；覆盖 {status.overwritten} 条，丢弃 {status.dropped} 条。</p>}
      <button className="secondary-button" disabled={busy || !status || status.running} onClick={() => void run(async () => { setStatus(await call<Status>('diagnostics_clear')); setMessage('本地记录已清除。'); })}>清除本地记录</button>
    </details>
  </div>;
}
