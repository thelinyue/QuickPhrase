import { useCallback, useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call } from './api';
import { AccountNotice } from './AccountControls';
import { FormDialog, Icon, type DialogSpec } from './ui';
import { useNotices } from './notices';
import type { CloudCommand, CloudCommandDraft, CloudCommandSummary, CloudCommandsView, TeamStatus } from './types';

export const canUseCloudCommands = (s: TeamStatus) => !!s.cloudCommands && !!s.user && !s.user.disabled && !s.user.mustChangePassword && (!!s.user.admin || !!s.user.cloudCommandPermissions?.canUse);
const canManage = (s: TeamStatus) => canUseCloudCommands(s) && (!!s.user?.admin || !!s.user?.cloudCommandPermissions?.canManage);
const blank: CloudCommandDraft = { name: '', category: '', description: '', body: '', expectedRevision: null };
const date = (seconds: number) => new Date(seconds * 1000).toLocaleString('zh-CN', { hour12: false });

export function CloudCommands({ status }: { status: TeamStatus }) {
  const epoch = status.sessionEpoch || 0, permissionVersion = status.user?.cloudCommandPermissions?.version || 0;
  const [query, setQuery] = useState(''), [category, setCategory] = useState<string | null>(null), [recycled, setRecycled] = useState(false);
  const [offset, setOffset] = useState(0), [size, setSize] = useState(10), [view, setView] = useState<CloudCommandsView | null>(null);
  const [selected, setSelected] = useState(''), [detail, setDetail] = useState<CloudCommand | null>(null), [mobileDetail, setMobileDetail] = useState(false);
  const [error, setError] = useState(''), [loading, setLoading] = useState(false), [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<{ id: string | null; draft: CloudCommandDraft; changed: boolean } | null>(null), [action, setAction] = useState<DialogSpec | null>(null);
  const [detailRevision, setDetailRevision] = useState(0);
  const catalogSequence = useRef(0), detailSequence = useRef(0), busyRef = useRef(false), listRef = useRef<HTMLDivElement>(null), scrollTop = useRef(0);
  const latest = useRef({ epoch, status, permissionVersion }); latest.current = { epoch, status, permissionVersion };
  const editorRef = useRef(editing); editorRef.current = editing;
  const notices = useNotices(), online = canUseCloudCommands(status) && !status.offline, manage = canManage(status);
  const scope = `${epoch}:${permissionVersion}:${status.user?.admin}`;
  const currentScope = useRef(scope); currentScope.current = scope;
  useEffect(() => { busyRef.current = false; setBusy(false); editorRef.current = null; setEditing(null); setAction(null); notices.clear(); }, [scope]);
  const valid = () => currentScope.current === scope && canUseCloudCommands(latest.current.status) && !latest.current.status.offline;
  const filter = JSON.stringify([scope, recycled, query, category, offset, size]);
  const currentFilter = useRef(filter); currentFilter.current = filter;
  const loaded = useRef('');
  const refresh = useCallback(async () => {
    const request = ++catalogSequence.current;
    if (!online) { setView(null); setDetail(null); setLoading(false); return; }
    setLoading(true); setError('');
    try {
      const next = await call<CloudCommandsView>('get_cloud_commands', { sessionEpoch: epoch, query: { q: query, category, recycled, offset, limit: size } });
      if (request !== catalogSequence.current || currentFilter.current !== filter) return;
      loaded.current = filter; setView(next);
      if (offset && offset >= next.total) { setOffset(Math.max(0, Math.ceil(next.total / size) - 1) * size); return; }
      setSelected(previous => next.commands.some(c => c.id === previous) ? previous : next.commands[0]?.id || '');
      setDetailRevision(n => n + 1);
    } catch (e) { if (request === catalogSequence.current && currentFilter.current === filter) { setView(null); setDetail(null); setError(String(e)); } }
    finally { if (request === catalogSequence.current && currentFilter.current === filter) setLoading(false); }
  }, [online, epoch, filter, recycled, query, category, offset, size]);
  useEffect(() => { setDetail(null); void refresh(); return () => { ++catalogSequence.current; }; }, [refresh]);
  useEffect(() => {
    let disposed = false, stop: (() => void) | undefined;
    void listen<number>('cloud-commands-changed', e => { if (!disposed && e.payload === epoch) void refresh(); }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; stop?.(); };
  }, [epoch, refresh]);
  useEffect(() => {
    const request = ++detailSequence.current; setDetail(null);
    if (!online || !selected) return;
    void call<CloudCommand>('get_cloud_command', { sessionEpoch: epoch, id: selected }).then(next => {
      if (request === detailSequence.current && currentScope.current === scope) setDetail(next);
    }).catch(e => { if (request === detailSequence.current && currentScope.current === scope) { setDetail(null); setView(null); setError(String(e)); } });
    return () => { ++detailSequence.current; };
  }, [online, epoch, scope, selected, detailRevision, filter]);
  useEffect(() => { if (!canUseCloudCommands(status) || !manage) { setEditing(null); setAction(null); } if (!online) { ++catalogSequence.current; ++detailSequence.current; setView(null); setDetail(null); setAction(null); } }, [online, manage, scope]);
  async function discard() { return !editorRef.current?.changed || await confirm('放弃未保存的云命令草稿？', { title: '未保存的命令', kind: 'warning' }); }
  useEffect(() => {
    let disposed = false, stop: (() => void) | undefined;
    void getCurrentWindow().onCloseRequested(async e => { if (!editorRef.current && !busyRef.current) return; e.preventDefault(); if (!busyRef.current && await discard()) { editorRef.current = null; await getCurrentWindow().close(); } }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; ++catalogSequence.current; ++detailSequence.current; stop?.(); };
  }, []);
  async function run(work: () => Promise<void>) {
    if (busyRef.current || !valid()) return;
    busyRef.current = true; setBusy(true); notices.clear();
    try { await work(); }
    catch (e) { if (currentScope.current === scope) { notices.error(String(e)); setDetail(null); setView(null); } }
    finally { if (currentScope.current === scope) { busyRef.current = false; setBusy(false); } }
  }
  function edit(command?: CloudCommand) {
    if (!manage || !online) return;
    setEditing({ id: command?.id || null, draft: command ? { name: command.name, category: command.category, description: command.description, body: command.body, expectedRevision: command.revision } : { ...blank }, changed: false });
  }
  function change(command: CloudCommand, operation: 'recycle' | 'restore' | 'purge') {
    setAction({ title: operation === 'purge' ? '永久删除云命令' : operation === 'restore' ? '恢复云命令' : '删除云命令', fields: <><p>{command.name}</p><p>{operation === 'purge' ? '永久删除后无法恢复，已复制到他处的文本无法收回。' : operation === 'restore' ? '恢复后，有使用权限的账号可立即查看和复制。' : '移入命令回收站，维护者可以恢复；已经复制的文本无法收回。'}</p></>, onSubmit: async () => {
      if (!valid() || !canManage(latest.current.status) || (operation === 'purge' && !latest.current.status.user?.admin)) throw new Error('权限或连接已变化，请刷新后重试。');
      await call('manage_cloud_command', { sessionEpoch: epoch, id: command.id, operation, request: { expectedRevision: command.revision, confirmName: operation === 'purge' ? command.name : null } });
      if (valid()) { setDetail(null); await refresh(); }
    } });
  }
  const catalog = online && loaded.current === filter ? view : null, visibleDetail = online && catalog && detail?.id === selected ? detail : null;
  const pages = Math.max(1, Math.ceil((catalog?.total || 0) / size)), page = Math.floor(offset / size);
  function resetFilter(work: () => void) { ++detailSequence.current; setDetail(null); setMobileDetail(false); setOffset(0); scrollTop.current = 0; work(); }
  return <main className="company-content commands-content">
    <div className="company-toolbar"><div className="company-search"><input aria-label="搜索云命令" placeholder="搜索名称或说明" maxLength={200} value={query} onChange={e => resetFilter(() => setQuery(e.target.value))} /><Icon name="search" /></div>
      <select aria-label="命令分类" value={category == null ? '*' : `=${category}`} onChange={e => resetFilter(() => setCategory(e.target.value === '*' ? null : e.target.value.slice(1)))}><option value="*">全部分类</option>{[...new Set([...(category == null ? [] : [category]), ...(catalog?.categories || [])])].map(c => <option key={c} value={`=${c}`}>{c || '未分类'}</option>)}</select>
      <button className="company-button" disabled={loading || busy} onClick={() => { if (busyRef.current) return; busyRef.current = true; setBusy(true); void call('team_refresh').then(() => { if (valid()) return refresh(); }).catch(e => { if (currentScope.current === scope) setError(String(e)); }).finally(() => { busyRef.current = false; setBusy(false); }); }}><Icon name="refresh" />刷新</button>
      {manage && <><button className="company-button company-primary" disabled={!online || busy || recycled} onClick={() => edit()}>新建命令</button><details className="cloud-module-more"><summary aria-label="云命令更多操作"><Icon name="more" /></summary><div><button disabled={!online || busy} onClick={e => { e.currentTarget.closest('details')?.removeAttribute('open'); resetFilter(() => { setRecycled(!recycled); setCategory(null); }); }}>{recycled ? '返回云命令' : '命令回收站'}</button></div></details></>}
    </div>
    {recycled && <div className="commands-recycle-heading"><button className="company-button" onClick={() => resetFilter(() => { setRecycled(false); setCategory(null); })}><Icon name="back" />返回云命令</button><strong>命令回收站</strong></div>}
    {!online && <p className="company-message" role="alert">云命令仅在线可用，连接中断后不显示旧目录或复制正文。请重连后刷新。</p>}
    {error && <p className="company-message" role="alert">{error}</p>}
    <AccountNotice message={notices.current?.message || ''} error={notices.current?.kind !== 'success'} clear={() => notices.current && notices.dismiss(notices.current.id)} />
    <div className={`commands-workspace${mobileDetail && visibleDetail ? ' commands-show-detail' : ''}`}>
      <div className="commands-list" ref={listRef} aria-label="命令列表" onScroll={e => { scrollTop.current = e.currentTarget.scrollTop; }}>
        {catalog?.commands.map(c => <button className={`command-list-row${selected === c.id ? ' selected' : ''}`} key={c.id} aria-pressed={selected === c.id} onClick={() => { setSelected(c.id); setMobileDetail(true); }}><span><strong>{c.name}</strong><small>{c.category || '未分类'}</small></span><p>{c.description || '暂无说明'}</p></button>)}
        {!catalog?.commands.length && <div className="company-empty"><Icon name="document" /><p>{loading ? '正在加载命令…' : error ? '加载失败，请刷新重试' : !online ? '联网后可查看云命令' : query || category != null ? '没有匹配的命令' : recycled ? '命令回收站为空' : '暂无云命令'}</p></div>}
      </div>
      <article className="command-detail" aria-label="命令详情">{visibleDetail ? <>
        <button className="company-button command-back" onClick={() => { const top = scrollTop.current; setMobileDetail(false); requestAnimationFrame(() => { if (listRef.current) listRef.current.scrollTop = top; }); }}><Icon name="back" />返回列表</button>
        <header><div><small>{visibleDetail.category || '未分类'}</small><h1>{visibleDetail.name}</h1></div>{manage && <div className="company-actions">{!recycled && <button className="company-button" disabled={busy} onClick={() => edit(visibleDetail)}><Icon name="pencil" />编辑</button>}<details className="cloud-module-more"><summary aria-label="命令管理操作"><Icon name="more" /></summary><div>{recycled ? <><button disabled={busy} onClick={() => change(visibleDetail, 'restore')}>恢复命令</button>{status.user?.admin && <button disabled={busy} onClick={() => change(visibleDetail, 'purge')}>永久删除</button>}</> : <button disabled={busy} onClick={() => change(visibleDetail, 'recycle')}>删除命令</button>}</div></details></div>}</header>
        <p className="command-description">{visibleDetail.description || '暂无说明'}</p>
        <div className="command-code"><div><span>命令正文</span><button className="company-button company-primary" disabled={busy || recycled} onClick={() => void run(async () => { await call('copy_cloud_command', { sessionEpoch: epoch, id: visibleDetail.id, expectedRevision: visibleDetail.revision }); if (valid()) notices.success('命令已原样复制，尚未执行。'); })}>复制命令</button></div><pre tabIndex={0}><code>{visibleDetail.body}</code></pre></div>
        <p className="command-risk">授权不代表脚本经过安全审查。请核对来源和参数，再自行在目标终端运行；已复制的文本无法收回。</p>
        <footer>维护人：{visibleDetail.maintainerName || '未知'}<span>更新于 {date(visibleDetail.updatedAt)}</span></footer>
      </> : <div className="company-empty"><Icon name="document" /><p>{loading ? '正在读取…' : '选择命令，阅读详情后复制'}</p></div>}</article>
    </div>
    <div className="company-pagination"><span>共 {catalog?.total || 0} 条 · 第 {Math.min(page + 1, pages)} / {pages} 页</span><button aria-label="命令上一页" disabled={!catalog || page === 0} onClick={() => setOffset(offset - size)}>‹</button><button aria-label="命令下一页" disabled={!catalog || page + 1 >= pages} onClick={() => setOffset(offset + size)}>›</button><select aria-label="命令每页条数" value={size} onChange={e => resetFilter(() => setSize(Number(e.target.value)))}>{[10,20,50].map(n => <option key={n} value={n}>{n}条/页</option>)}</select></div>
    {editing && <CommandEditor key={`${editing.id}:${epoch}`} draft={editing.draft} categories={catalog?.categories || []} online={online && manage} close={async () => { if (!busyRef.current && await discard()) { editorRef.current = null; setEditing(null); } }} changed={draft => setEditing(previous => previous && { ...previous, draft, changed: true })} save={async draft => {
      if (!valid() || !canManage(latest.current.status)) throw new Error('当前离线或权限已变化，草稿仅保留在本窗口。');
      busyRef.current = true; setBusy(true);
      try { const saved = await call<CloudCommand>('save_cloud_command', { sessionEpoch: epoch, id: editing.id, request: draft }); if (valid()) { editorRef.current = null; setEditing(null); setSelected(saved.id); await refresh(); } }
      finally { busyRef.current = false; setBusy(false); }
    }} />}
    {action && <FormDialog spec={action} close={() => setAction(null)} />}
  </main>;
}

function CommandEditor({ draft, categories, online, close, changed, save }: { draft: CloudCommandDraft; categories: string[]; online: boolean; close: () => Promise<void>; changed: (draft: CloudCommandDraft) => void; save: (draft: CloudCommandDraft) => Promise<void> }) {
  const ref = useRef<HTMLDialogElement>(null), busyRef = useRef(false), trigger = useRef(document.activeElement as HTMLElement | null);
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  useEffect(() => { ref.current?.showModal(); return () => { trigger.current?.isConnected && trigger.current.focus(); }; }, []);
  const field = (key: keyof CloudCommandDraft, value: string) => changed({ ...draft, [key]: value });
  return <dialog className="small-dialog cloud-command-editor" ref={ref} aria-labelledby="command-editor-title" onCancel={e => { e.preventDefault(); if (!busyRef.current) void close(); }}><form onSubmit={async e => { e.preventDefault(); if (busyRef.current || !online) return; busyRef.current = true; setBusy(true); setError(''); try { await save(draft); } catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); } }}>
    <h2 id="command-editor-title">{draft.expectedRevision == null ? '新建命令' : '编辑命令'}</h2><div className="command-editor-scroll"><fieldset className="dialog-fields" disabled={busy}>
      <label>名称<input autoFocus required maxLength={100} value={draft.name} onChange={e => field('name', e.target.value)} /></label>
      <label>分类<input list="command-categories" maxLength={50} placeholder="未分类" value={draft.category} onChange={e => field('category', e.target.value)} /><datalist id="command-categories">{categories.filter(Boolean).map(c => <option key={c} value={c} />)}</datalist></label>
      <label>说明<textarea maxLength={5000} rows={3} value={draft.description} onChange={e => field('description', e.target.value)} /></label>
      <label>命令正文<textarea className="command-body-input" required spellCheck={false} rows={9} value={draft.body} onChange={e => field('body', e.target.value)} /></label>
      <p>只保存文本，不托管或执行脚本；空分类归入“未分类”。</p>{!online && <p role="alert">连接已断开，草稿仅保留在当前窗口，重连核权后才能保存。</p>}{error && <p className="validation-error" role="alert">{error}</p>}
    </fieldset></div><div className="dialog-actions"><button type="button" className="secondary-button" disabled={busy} onClick={() => void close()}>取消</button><button className="primary-button" type="submit" disabled={busy || !online}>{busy ? '保存中…' : '保存命令'}</button></div>
  </form></dialog>;
}
