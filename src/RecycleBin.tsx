import { useEffect, useRef, useState, type ReactNode } from 'react';
import { listen } from '@tauri-apps/api/event';
import { convertFileSrc } from '@tauri-apps/api/core';
import { call, closeWindow, useTeams } from './api';
import { Feedback, Icon, Titlebar } from './ui';
import { TeamSelector } from './TeamSelector';
import { textOf, type Library, type RecycleItem, type RecycleRecord, type TeamRecycle, type TeamInfo, type TeamStatus, type TeamView } from './types';

export const recycleLabel = (kind: RecycleItem['kind']) => kind === 'phrase' ? '话术' : kind === 'primary-category' ? '话术一级分类' : '话术二级分类';
const timestamp = (value: number) => new Date(value).toLocaleString('zh-CN', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });

/** 删除窗口固定绑定内容主键；复用时接收新的删除目标，提交期间不切换对象。
 * 默认回收，只有用户明确取消勾选后才永久删除，中文提示随选项更新。 */
export function DeletionWindow({ library, id, kind }: { library: Library; id: string; kind: string }) {
  const [context, setContext] = useState({ id, kind }); const [recycle, setRecycle] = useState(true); const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const category = context.kind === 'category' ? library.categories.find(c => c.id === context.id && c.kind === 'phrase') : undefined;
  const phrase = context.kind === 'phrase' ? library.phrases.find(p => p.id === context.id) : undefined;
  const item = category || phrase;
  const title = category ? `删除${category.parentId ? '二级' : '一级'}分类` : '删除话术';
  useEffect(() => {
    let disposed = false; let off: (() => void) | undefined;
    void listen<{ id: string; kind: string }>('deletion-navigation', e => { if (!busyRef.current) { setContext(e.payload); setRecycle(true); setError(''); } }).then(fn => disposed ? fn() : off = fn);
    return () => { disposed = true; off?.(); };
  }, []);
  async function submit() {
    if (!item || busyRef.current) return; busyRef.current = true; setBusy(true); setError('');
    try {
      if (category) await call('delete_category', { id: category.id, recycle });
      else await call('delete_item', { id: context.id, kind: 'phrase', recycle });
      await closeWindow();
    } catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  return <section className="client-window deletion-window"><Titlebar title={title} minimize /><form onSubmit={e => { e.preventDefault(); void submit(); }}>
    <h2>删除提示</h2><p>{!item ? '该内容已不存在，请关闭窗口后刷新。' : category ? `确定要删除「${category.name}」及其包含的${category.parentId ? '所有话术' : '二级分类和所有话术'}吗？` : '确定要删除该话术吗？'}{item && (recycle ? '移除到回收站后可以还原。' : '删除后无法恢复。')}</p>
    {error && <p className="validation-error" role="alert">{error}</p>}
    <footer><label><input type="checkbox" checked={recycle} disabled={busy} onChange={e => setRecycle(e.target.checked)} />移除到回收站</label><span /><button type="button" className="secondary-button" disabled={busy} onClick={() => void closeWindow()}>取消</button><button className="primary-button" disabled={busy || !item}>确定</button></footer>
  </form></section>;
}

/** 一个回收站窗口按来源切换。来源、账号和生命周期共同决定内容实例，
 * 切号或撤权立即卸载旧详情和确认框；窗口导航不会打断正在提交的操作。 */
export function RecycleWindow({ library, initialTeamId = '' }: { library: Library; initialTeamId?: string }) {
  const { status, version, loaded } = useTeams();
  const [requestedTeam, setRequestedTeam] = useState(initialTeamId);
  const team = status.teams.find(t => t.teamId === requestedTeam && t.editable);
  const account = `${status.server}:${status.user?.id || ''}:${status.sessionEpoch ?? ''}`;
  const sourceKey = `${library.libraryId || ''}:${account}:${team?.teamId || ''}:${team?.lifecycleVersion || ''}:${team?.membershipVersion || ''}:${status.offline}:${status.user?.mustChangePassword || false}`;
  const sourceRef = useRef(sourceKey); sourceRef.current = sourceKey;
  const busyRef = useRef(''); const [busyKey, setBusyKey] = useState('');
  const navigateRef = useRef<(id: string) => void>(() => {});
  navigateRef.current = id => { if (busyRef.current !== sourceKey) setRequestedTeam(status.teams.some(t => t.teamId === id && t.editable) ? id : ''); };
  useEffect(() => {
    let disposed = false; let off: (() => void) | undefined;
    void listen<{ teamId: string }>('recycle-navigation', e => navigateRef.current(e.payload.teamId)).then(fn => disposed ? fn() : off = fn);
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => { if (loaded && requestedTeam && !team) setRequestedTeam(''); }, [loaded, requestedTeam, team]);
  return <section className="client-window recycle-window" onContextMenu={e => e.preventDefault()}><Titlebar title="回收站" minimize />
    <div className="recycle-source"><div className="scope-bar" aria-label="回收站来源"><button type="button" className={`scope-name${!team ? ' active' : ''}`} aria-pressed={!team} disabled={busyKey === sourceKey || !loaded} onClick={() => navigateRef.current('')}>个人话术</button><TeamSelector status={{ ...status, teams: status.teams.filter(t => t.editable) }} teamId={team?.teamId || ''} disabled={busyKey === sourceKey || !loaded || !status.teams.some(t => t.editable)} showManagement={false} onChange={id => navigateRef.current(id)} onError={() => {}} /></div></div>
    {!loaded ? <p className="recycle-empty">正在读取来源…</p> : <RecycleContent key={sourceKey} library={library} team={team} status={status} version={version} onBusy={value => { if (sourceRef.current === sourceKey) { busyRef.current = value ? sourceKey : ''; setBusyKey(value ? sourceKey : ''); } }} />}
  </section>;
}

type RecycleRow = RecycleItem & { teamRecord?: TeamRecycle };
/** 私人记录使用本机路径，共享记录只读显示素材名；不要推断历史共享记录的删除类型。
 * 清空的 ID 清单在确认前读取，提交后新增记录不属于本次删除范围。 */
function RecycleContent({ library, team, status, version, onBusy }: { library: Library; team?: TeamInfo; status: TeamStatus; version: number; onBusy: (value: boolean) => void }) {
  const [items, setItems] = useState<RecycleRow[]>([]); const [selected, setSelected] = useState(''); const [detail, setDetail] = useState<RecycleRecord | null>(null);
  const [input, setInput] = useState(''); const [query, setQuery] = useState(''); const [page, setPage] = useState(0); const [loading, setLoading] = useState(true); const [message, setMessage] = useState(''); const [readError, setReadError] = useState(''); const [revision, setRevision] = useState(0);
  const [action, setAction] = useState<'restore' | 'remove' | 'empty' | null>(null); const [actionItem, setActionItem] = useState<RecycleRow | null>(null); const [clearIds, setClearIds] = useState<string[]>([]);
  const [withoutShortcuts, setWithoutShortcuts] = useState(false); const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false); const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const source = team ? `共享话术：${team.name}` : '个人话术';
  const blocked = !!team && (status.offline || !!status.user?.mustChangePassword);
  const binding = team ? { teamId: team.teamId, sessionEpoch: status.sessionEpoch, expectedLifecycleVersion: team.lifecycleVersion } : { libraryId: library.libraryId };
  async function read(search: string): Promise<RecycleRow[]> {
    if (!team) return call<RecycleItem[]>('get_recycle_bin', { ...binding, query: search });
    const records = await call<TeamRecycle[]>('get_team_recycle', { ...binding, query: search });
    return records.map(record => ({ id: record.id, label: record.label, kind: 'phrase' as const, location: '', deletedAt: record.deletedAt * 1000, phraseCount: record.phrases.length, teamRecord: record }));
  }
  useEffect(() => {
    let active = true; setLoading(true); setReadError(''); setItems([]); setDetail(null);
    if (blocked) { setLoading(false); return; }
    void read(query).then(rows => { if (active) { setItems(rows); setSelected(previous => rows.some(r => r.id === previous) ? previous : rows[0]?.id || ''); } }).catch(e => { if (active) setReadError(String(e)); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [library, team?.teamId, query, revision, version, blocked]);
  const pageSize = 10; const pages = Math.max(1, Math.ceil(items.length / pageSize)); const current = Math.min(page, pages - 1); const visible = items.slice(current * pageSize, (current + 1) * pageSize); const item = items.find(i => i.id === selected);
  useEffect(() => { const index = items.findIndex(i => i.id === selected); if (index >= 0) setPage(Math.floor(index / pageSize)); }, [selected, items]);
  useEffect(() => {
    let active = true; setDetail(null);
    if (!team && selected && item) void call<RecycleRecord>('get_recycle_detail', { ...binding, id: selected }).then(value => { if (active) setDetail(value); }).catch(e => { if (active) setMessage(String(e)); });
    return () => { active = false; };
  }, [selected, items]);
  async function begin(value: 'restore' | 'remove' | 'empty') {
    if (busyRef.current || blocked || loading || readError) return;
    setActionItem(item || null); setWithoutShortcuts(false); setError('');
    if (value !== 'empty') { setAction(value); return; }
    busyRef.current = true; setBusy(true); onBusy(true);
    try { const rows = await read(''); if (alive.current) { setClearIds(rows.map(r => r.id)); setAction('empty'); } }
    catch (e) { if (alive.current) setMessage(String(e)); }
    finally { if (alive.current) { busyRef.current = false; setBusy(false); onBusy(false); } }
  }
  async function submit() {
    if (busyRef.current || blocked || !action || (action !== 'empty' && !actionItem)) return;
    busyRef.current = true; setBusy(true); onBusy(true); setError('');
    try {
      let count = 0;
      if (team) {
        if (action === 'restore') { const view = await call<TeamView>('get_team_view', { teamId: team.teamId }); if (!alive.current) return; await call('restore_team_record', { ...binding, id: actionItem!.id, expectedVersion: view.team.version }); }
        else if (action === 'remove') await call('purge_team_record', { ...binding, id: actionItem!.id });
        else count = await call<number>('empty_team_recycle', { ...binding, ids: clearIds });
      } else {
        if (action === 'restore') await call('restore_recycled', { ...binding, ids: [actionItem!.id], withoutShortcuts });
        else if (action === 'remove') await call('remove_recycled', { ...binding, ids: [actionItem!.id] });
        else count = await call<number>('empty_recycle', { ...binding, ids: clearIds });
      }
      if (alive.current) { setAction(null); setRevision(v => v + 1); setMessage(action === 'restore' ? `已还原到${source}` : action === 'remove' ? '已永久删除所选记录' : `已删除 ${count} 条回收记录`); }
    } catch (e) { if (alive.current) setError(String(e)); }
    finally { if (alive.current) { busyRef.current = false; setBusy(false); onBusy(false); } }
  }
  const primary = detail?.kind === 'primary-category' ? detail.label : detail?.kind === 'secondary-category' ? detail.location : detail?.categories.find(c => !c.parentId)?.name;
  const secondary = detail?.kind === 'secondary-category' ? detail.label : detail?.phrases[0]?.groupId ? detail.categories.find(c => c.id === detail.phrases[0].groupId)?.name : '';
  const p = detail?.kind === 'phrase' ? detail.phrases[0] : undefined;
  const label = (row: RecycleRow) => row.teamRecord ? '回收记录' : recycleLabel(row.kind);
  const restoreLocation = actionItem?.teamRecord ? actionItem.teamRecord.categories.map(c => c.name).join(' / ') : actionItem?.kind === 'primary-category' ? actionItem.label : [actionItem?.location, actionItem?.kind === 'secondary-category' ? actionItem.label : ''].filter(Boolean).join(' / ');
  const available = !!item && !busy && !loading && !readError && !blocked;
  return <><div className="recycle-layout"><aside className="recycle-sidebar"><form className="recycle-search" onSubmit={e => { e.preventDefault(); if (busyRef.current) return; setQuery(input.trim()); setPage(0); setSelected(''); setRevision(v => v + 1); }}><Icon name="search" /><input aria-label="搜索回收站" placeholder="输入拼音或关键词，按回车搜索" disabled={busy || blocked} value={input} onChange={e => setInput(e.target.value)} onKeyDown={e => { if ((e.nativeEvent.isComposing || e.keyCode === 229) && e.key === 'Enter') e.preventDefault(); }} /><button type="button" aria-label="刷新回收站" disabled={busy || blocked} onClick={() => setRevision(v => v + 1)}><Icon name="refresh" /></button></form>
      <div className="recycle-list" role="listbox" aria-label="回收站记录">{visible.map(row => <button key={row.id} role="option" aria-selected={selected === row.id} disabled={busy} title={row.label} onClick={() => setSelected(row.id)}><strong>{row.label || '图片或附件话术'}</strong><time dateTime={new Date(row.deletedAt).toISOString()}>{timestamp(row.deletedAt)}</time><small>{label(row)} · {row.phraseCount} 条话术</small></button>)}{blocked ? <p className="recycle-empty">{status.offline ? '共享话术的回收记录需要联网查看，可以切换到个人话术。' : '请先修改初始密码。'}</p> : loading ? <p className="recycle-empty">正在读取回收站…</p> : readError ? <p className="recycle-empty" role="alert">{readError}</p> : !items.length && (query ? <p className="recycle-empty">没有匹配的回收记录</p> : <div className="recycle-empty recycle-empty-illustrated"><img className="empty-illustration" src="/illustrations/recycle-empty.png" width="120" height="120" alt="" draggable={false} /><span>回收站为空</span></div>)}</div>
      <footer className="recycle-pagination"><button className="recycle-clear" disabled={busy || loading || !!readError || blocked} onClick={() => void begin('empty')}>清空当前回收站</button><span /><button className="secondary-button" disabled={current === 0 || busy} onClick={() => { setPage(current - 1); setSelected(items[(current - 1) * pageSize]?.id || ''); }}>上一页</button><span className="recycle-page-number">{current + 1}／{pages}</span><button className="secondary-button" disabled={current + 1 >= pages || busy} onClick={() => { setPage(current + 1); setSelected(items[(current + 1) * pageSize]?.id || ''); }}>下一页</button></footer>
    </aside><main className="recycle-detail"><header><span>详情</span><button className="primary-button" disabled={!available} onClick={() => void begin('restore')}>还原</button></header>
      {(detail || item?.teamRecord) && <p>来源：<span>{source}</span></p>}
      {item?.teamRecord && <><p>类型：回收记录</p><p>原分类：{item.teamRecord.categories.map(c => c.name).join(' / ') || '无分类信息'}</p><p>包含话术：{item.phraseCount} 条</p><div className="recycle-content">{item.teamRecord.phrases.map((phrase, index) => <div className="recycle-segment" key={index}><strong>{phrase.title || '图片或附件话术'}</strong>{phrase.segments.map((segment, n) => <div key={n}>{phrase.segments.length > 1 && <small>第 {n + 1} 段</small>}{segment.blocks.map((block, i) => <p key={i}>{block.kind === 'text' ? block.text : `${block.kind === 'image' ? '图片' : '附件'}：${block.name}`}</p>)}</div>)}</div>)}</div></>}
      {detail && <><p>{detail.kind === 'primary-category' ? '一级分类名称：' : '所属一级分类：'}{detail.kind !== 'primary-category' && primary}</p>{detail.kind === 'primary-category' ? <input aria-label="一级分类名称" value={primary || ''} readOnly /> : secondary && <p>所属二级分类：{secondary}</p>}{detail.kind === 'secondary-category' && <label>二级分类名称：<input aria-label="二级分类名称" value={detail.label} readOnly /></label>}
        {p ? <><label>话术标题：<div className="recycle-title"><input aria-label="回收话术标题" value={p.title} readOnly /><span className="recycle-color" style={{ background: p.background || undefined }} title={p.background || '默认背景色'}>背景色</span></div></label><label>话术内容：</label><div className="recycle-content" aria-label="回收话术内容">{p.segments.map((segment, index) => <div className="recycle-segment" key={index}>{p.segments.length > 1 && <small>第 {index + 1} 段</small>}{segment.blocks.map((block, i) => block.kind === 'text' ? <p key={i}>{block.text}</p> : block.kind === 'image' ? <img key={i} src={convertFileSrc(block.path)} alt={block.name} /> : <p className="recycle-attachment" key={i} title={block.path}>附件：{block.name}</p>)}</div>)}</div>{p.shortcut && <p>快捷键：{p.shortcut}</p>}</> : <><p>包含话术：{detail.phrases.length} 条</p><div className="recycle-category-content">{detail.phrases.map(phrase => <p key={phrase.id} title={textOf(phrase)}>{phrase.title || textOf(phrase).slice(0, 35) || '图片或附件话术'}</p>)}</div></>}
      </>}
      {(detail || item?.teamRecord) && <button className="recycle-permanent" disabled={!available} onClick={() => void begin('remove')}>永久删除该记录</button>}
    </main></div>
    {action && <RecycleAction title={action === 'restore' ? `还原${actionItem ? label(actionItem) : '话术'}` : action === 'remove' ? '永久删除' : '清空当前回收站'} danger={action !== 'restore'} busy={busy} error={error} close={() => { if (!busyRef.current) setAction(null); }} submit={submit}><p>来源：{source}</p>{action === 'restore' ? <><p>还原「{actionItem?.label}」到原分类。</p><p>原分类：{restoreLocation || '无分类信息'}</p><p className="recycle-action-hint">{team ? '原分类归属冲突、版本变化或素材缺失时不执行还原，请刷新后重试。' : '原分类不存在或二级分类已移动时重建原位置，保留已有分类的当前设置。'}</p>{!team && <label><input type="checkbox" checked={withoutShortcuts} disabled={busy} onChange={e => setWithoutShortcuts(e.target.checked)} />恢复时不恢复快捷键</label>}</> : <p>{action === 'empty' ? `确定删除此来源的 ${clearIds.length} 条回收记录？包含搜索结果之外的记录，确认后新增的记录保留。` : `确定永久删除「${actionItem?.label || ''}」？`}删除后无法恢复，外部附件源文件保留。</p>}</RecycleAction>}
    <Feedback message={message} clear={() => setMessage('')} />
  </>;
}

/** 危险配色由当前操作决定，不靠标题文字推断；还原与删除继续共用原有提交及关闭保护。 */
function RecycleAction({ title, children, danger, busy, error, close, submit }: { title: string; children: ReactNode; danger: boolean; busy: boolean; error: string; close: () => void; submit: () => Promise<void> }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    ref.current?.showModal();
    return () => { if (previous?.isConnected) previous.focus(); };
  }, []);
  return <dialog ref={ref} aria-label={title} className={`recycle-action${danger ? ' danger-dialog' : ''}`} onCancel={e => { e.preventDefault(); close(); }}><header><img className="app-symbol" src="/shanyu.png" alt="" draggable={false} /><span>{title}</span><button aria-label="关闭确认窗口" disabled={busy} onClick={close}><Icon name="close" /></button></header><form onSubmit={e => { e.preventDefault(); void submit(); }}><div>{children}{error && <p className="validation-error" role="alert">{error}</p>}</div><footer><button type="button" className="secondary-button" disabled={busy} onClick={close}>取消</button><button className="primary-button" disabled={busy}>确定</button></footer></form></dialog>;
}
