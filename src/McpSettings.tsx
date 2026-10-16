import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { open } from '@tauri-apps/plugin-dialog';
import { call, useTeams } from './api';
import type { Library } from './types';

type Grant = { source: 'private' | 'team'; libraryId: string };
type Root = { id: string; path: string };
type Config = { enabled: boolean; port: number; grants: Grant[]; roots: Root[] };
type Status = { account: string; config: Config; running: boolean; error: string };
/** AI 连接只保存明确勾选的库。单项排队提交，端口草稿及其他选项不随保存重建。 */
export function McpSettings({ library }: { library: Library }) {
  const { status: teams } = useTeams();
  const scope = JSON.stringify([library.libraryId, teams.server, teams.user?.id, teams.sessionEpoch]);
  const owner = useRef(scope); owner.current = scope;
  const [snapshot, setSnapshot] = useState<Status | null>(null);
  const [pending, setPending] = useState<Partial<Config>>({}); const pendingRef = useRef<Partial<Config>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [port, setPort] = useState('17823'); const editingPort = useRef(false);
  const queue = useRef(Promise.resolve()); const [copying, setCopying] = useState(false); const [copied, setCopied] = useState(false);
  const [resetting, setResetting] = useState(false);
  useEffect(() => {
    let disposed = false, sequence = 0; let off: (() => void) | undefined;
    setSnapshot(null); setErrors({}); setPending({}); pendingRef.current = {}; setCopied(false); setCopying(false); setResetting(false); editingPort.current = false;
    const read = async () => { const request = ++sequence; try { const value = await call<Status>('get_mcp_status'); if (!disposed && request === sequence) { setSnapshot(value); if (value.error) setErrors(e => ({ ...e, load: value.error })); } } catch (e) { if (!disposed) setErrors({ load: String(e) }); } };
    void listen('mcp-changed', () => void read()).then(stop => { if (disposed) stop(); else { off = stop; void read(); } });
    return () => { disposed = true; off?.(); };
  }, [scope]);
  useEffect(() => { if (snapshot && !editingPort.current && !('port' in pendingRef.current)) setPort(String(snapshot.config.port)); }, [snapshot]);
  function save(patch: Partial<Config>) {
    if (!snapshot) return;
    const key = Object.keys(patch)[0] as keyof Config;
    if (key in pendingRef.current) return;
    const accountKey = snapshot.account, current = scope;
    pendingRef.current = { ...pendingRef.current, ...patch }; setPending({ ...pendingRef.current });
    setErrors(e => ({ ...e, [key]: '' }));
    queue.current = queue.current.then(async () => {
      if (owner.current !== current) return;
      try { const next = await call<Status>('patch_mcp_settings', { accountKey, patch }); if (owner.current === current) { setSnapshot(next); if (next.error) setErrors(e => ({ ...e, [key]: next.error })); } }
      catch (error) { if (owner.current === current) setErrors(e => ({ ...e, [key]: String(error) })); }
      finally { if (owner.current === current) { delete pendingRef.current[key]; setPending({ ...pendingRef.current }); } }
    });
  }
  const cfg = snapshot ? { ...snapshot.config, ...pending } : null;
  const error = (key: string) => errors[key] ? <p role="alert" className="validation-error">{errors[key]}</p> : null;
  const libraries: (Grant & { name: string })[] = [
    ...(library.libraryId ? [{ source: 'private' as const, libraryId: library.libraryId, name: '个人话术' }] : []),
    ...teams.teams.map(t => ({ source: 'team' as const, libraryId: t.teamId, name: t.name })),
  ];
  return <div className="mcp-settings"><h2 className="settings-heading">AI 连接</h2>{error('load')}{cfg && <>
    <section className="account-section"><label><input type="checkbox" checked={cfg.enabled} disabled={'enabled' in pending} onChange={e => save({ enabled: e.target.checked })} />启用本机 MCP 服务</label><p className="account-hint">{snapshot?.running ? '运行中' : '已停止'} · 仅接受本机 AI 客户端连接</p>{error('enabled')}
      <label className="default-team-setting">端口<input aria-label="MCP 端口" type="number" min={1024} max={65535} value={port} disabled={'port' in pending} onFocus={() => { editingPort.current = true; }} onChange={e => setPort(e.target.value)} onBlur={() => { editingPort.current = false; const value = Number(port); if (!Number.isInteger(value) || value < 1024 || value > 65535) setErrors(e => ({ ...e, port: '端口应为1024—65535。' })); else if (value !== cfg.port) save({ port: value }); }} /></label>{error('port')}
      <div className="setting-buttons"><button disabled={copying} onClick={async () => { const current = scope; setCopying(true); try { const config = await call('get_mcp_connection'); if (owner.current !== current) return; await navigator.clipboard.writeText(JSON.stringify(config, null, 2)); if (owner.current === current) setCopied(true); } catch (e) { if (owner.current === current) setErrors(v => ({ ...v, connection: String(e) })); } finally { if (owner.current === current) setCopying(false); } }}>{copied ? '已复制连接配置' : '复制连接配置'}</button><button disabled={resetting} onClick={async () => { const current = scope; setResetting(true); setCopied(false); try { await call('reset_mcp_token'); } catch (e) { if (owner.current === current) setErrors(v => ({ ...v, connection: String(e) })); } finally { if (owner.current === current) setResetting(false); } }}>重置令牌</button></div>{error('connection')}<p className="account-hint">连接配置包含令牌，请仅交给信任的 AI 客户端。重置后旧连接立即失效。</p>
    </section>
    <section className="account-section"><h3>授权话术库</h3><p className="account-hint">勾选后允许 AI 读取、整理和回收该库的话术。共享库仍受服务器编辑权限约束。</p>{libraries.map(item => <label className="mcp-grant" key={`${item.source}:${item.libraryId}`}><input type="checkbox" checked={cfg.grants.some(g => g.source === item.source && g.libraryId === item.libraryId)} disabled={'grants' in pending} onChange={e => save({ grants: e.target.checked ? [...cfg.grants, { source: item.source, libraryId: item.libraryId }] : cfg.grants.filter(g => g.source !== item.source || g.libraryId !== item.libraryId) })} />{item.source === 'team' ? `共享话术 · ${item.name}` : item.name}</label>)}{error('grants')}</section>
    <section className="account-section"><h3>素材目录</h3><p className="account-hint">仅允许从这些目录导入图片和附件。原文件保留，AI 使用目录标识和相对路径。</p>{cfg.roots.map(root => <div className="mcp-root" key={root.id}><span title={root.path}>{root.path}<small>标识：{root.id}</small></span><button disabled={'roots' in pending} onClick={() => save({ roots: cfg.roots.filter(r => r.id !== root.id) })}>移除</button></div>)}<button disabled={'roots' in pending} onClick={async () => { const current = scope; try { const path = await open({ directory: true, multiple: false, title: '授权 AI 导入素材的目录' }); if (typeof path === 'string' && owner.current === current && !cfg.roots.some(r => r.path === path)) save({ roots: [...cfg.roots, { id: crypto.randomUUID(), path }] }); } catch (e) { if (owner.current === current) setErrors(v => ({ ...v, roots: String(e) })); } }}>添加素材目录</button>{error('roots')}</section>
  </>}</div>;
}
