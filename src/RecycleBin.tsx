import { useEffect, useRef, useState, type ReactNode } from 'react';
import { listen } from '@tauri-apps/api/event';
import { convertFileSrc } from '@tauri-apps/api/core';
import { call, closeWindow } from './api';
import { Feedback, Icon, Titlebar } from './ui';
import { textOf, type Library, type RecycleItem, type RecycleRecord } from './types';

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

/** 回收站按参考图显示时间列表与只读详情。搜索按 Enter 提交，恢复只提交记录 ID，
 * 内容由 Rust 事务恢复；取消、失败及快捷键冲突均不会移除回收记录。 */
export function RecycleWindow({ library }: { library: Library }) {
  const [items, setItems] = useState<RecycleItem[]>([]); const [selected, setSelected] = useState(''); const [detail, setDetail] = useState<RecycleRecord | null>(null);
  const [input, setInput] = useState(''); const [query, setQuery] = useState(''); const [page, setPage] = useState(0); const [loading, setLoading] = useState(true); const [message, setMessage] = useState('');
  const [readError, setReadError] = useState(false);
  const [action, setAction] = useState<'restore' | 'remove' | 'empty' | null>(null); const [withoutShortcuts, setWithoutShortcuts] = useState(false); const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const [actionItem, setActionItem] = useState<RecycleItem | null>(null);
  const pageSize = 10; const pages = Math.max(1, Math.ceil(items.length / pageSize)); const current = Math.min(page, pages - 1); const visible = items.slice(current * pageSize, (current + 1) * pageSize); const item = items.find(i => i.id === selected);
  useEffect(() => {
    let active = true; setLoading(true); setReadError(false);
    // 读取失败保留原有错误提示，不把失败伪装成“回收站为空”的插画状态。
    void call<RecycleItem[]>('get_recycle_bin', { query }).then(rows => { if (active) { setItems(rows); setSelected(previous => rows.some(r => r.id === previous) ? previous : rows[0]?.id || ''); } }).catch(e => { if (active) { setReadError(true); setMessage(String(e)); } }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [library, query]);
  useEffect(() => { const index = items.findIndex(i => i.id === selected); if (index >= 0) setPage(Math.floor(index / pageSize)); }, [selected, items]);
  useEffect(() => {
    let active = true; setDetail(null);
    if (selected) void call<RecycleRecord>('get_recycle_detail', { id: selected }).then(value => { if (active) setDetail(value); }).catch(e => { if (active) setMessage(String(e)); });
    return () => { active = false; };
  }, [selected, items]);
  const begin = (value: 'restore' | 'remove' | 'empty') => { if (busyRef.current) return; setActionItem(item || null); setWithoutShortcuts(false); setError(''); setAction(value); };
  async function submit() {
    if (busyRef.current || !action || (action !== 'empty' && !actionItem)) return; busyRef.current = true; setBusy(true); setError('');
    try {
      if (action === 'restore') await call('restore_recycled', { ids: [actionItem!.id], withoutShortcuts });
      else if (action === 'remove') await call('remove_recycled', { ids: [actionItem!.id] });
      else await call('empty_recycle');
      setAction(null); setMessage(action === 'restore' ? '已还原到我的话术' : action === 'remove' ? '已永久删除所选记录' : '回收站已清空');
    } catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  const primary = detail?.kind === 'primary-category' ? detail.label : detail?.kind === 'secondary-category' ? detail.location : detail?.categories.find(c => !c.parentId)?.name;
  const secondary = detail?.kind === 'secondary-category' ? detail.label : detail?.phrases[0]?.groupId ? detail.categories.find(c => c.id === detail.phrases[0].groupId)?.name : '';
  const p = detail?.kind === 'phrase' ? detail.phrases[0] : undefined;
  return <section className="client-window recycle-window" onContextMenu={e => e.preventDefault()}><Titlebar title="回收站" minimize />
    <div className="recycle-layout"><aside className="recycle-sidebar"><form className="recycle-search" onSubmit={e => { e.preventDefault(); const next = input.trim(); setSelected(next === query ? items[0]?.id || '' : ''); setQuery(next); setPage(0); }}><Icon name="search" /><input aria-label="搜索回收站" placeholder="输入拼音或关键词，按下回车键进行搜索" value={input} onChange={e => setInput(e.target.value)} onKeyDown={e => { if (e.nativeEvent.isComposing || e.keyCode === 229) { if (e.key === 'Enter') e.preventDefault(); } }} /></form>
      <div className="recycle-list" role="listbox" aria-label="回收站记录">{visible.map(row => <button key={row.id} role="option" aria-selected={selected === row.id} disabled={busy} title={row.label} onClick={() => setSelected(row.id)}><time dateTime={new Date(row.deletedAt).toISOString()}>{timestamp(row.deletedAt)}</time><span>{recycleLabel(row.kind)}</span></button>)}{loading ? <p className="recycle-empty">正在读取回收站…</p> : !items.length && !readError && (query ? <p className="recycle-empty">没有匹配的回收记录</p> : <div className="recycle-empty recycle-empty-illustrated"><img className="empty-illustration" src="/illustrations/recycle-empty.png" width="120" height="120" alt="" draggable={false} /><span>回收站为空</span></div>)}</div>
      <footer className="recycle-pagination"><button className="recycle-clear" disabled={!items.length || busy} onClick={() => begin('empty')}>清空回收站</button><span /><button className="secondary-button" disabled={current === 0 || busy} onClick={() => { setPage(current - 1); setSelected(items[(current - 1) * pageSize]?.id || ''); }}>上一页</button><button className="secondary-button" disabled={current + 1 >= pages || busy} onClick={() => { setPage(current + 1); setSelected(items[(current + 1) * pageSize]?.id || ''); }}>下一页</button></footer>
    </aside><main className="recycle-detail"><header><span>详情</span><button className="primary-button" disabled={!item || busy} onClick={() => begin('restore')}>还原</button></header>
      {detail && <><p>所属类型：<span>我的话术</span></p><p>{detail.kind === 'primary-category' ? '一级分类名称：' : '所属一级分类：'}{detail.kind !== 'primary-category' && <span>{primary}</span>}</p>{detail.kind === 'primary-category' ? <input aria-label="一级分类名称" value={primary || ''} readOnly /> : secondary && <p>所属二级分类：<span>{secondary}</span></p>}
        {detail.kind === 'secondary-category' && <label>二级分类名称：<input aria-label="二级分类名称" value={detail.label} readOnly /></label>}
        {p ? <><label>话术标题：<div className="recycle-title"><input aria-label="回收话术标题" value={p.title} readOnly /><span className="recycle-color" style={{ background: p.background || undefined }} title={p.background || '默认背景色'}>背景色</span></div></label><label>话术内容：</label><div className="recycle-content" aria-label="回收话术内容">{p.segments.map((segment, index) => <div className="recycle-segment" key={index}>{p.segments.length > 1 && <small>第 {index + 1} 段</small>}{segment.blocks.map((block, i) => block.kind === 'text' ? <p key={i}>{block.text}</p> : block.kind === 'image' ? <img key={i} src={convertFileSrc(block.path)} alt={block.name} /> : <p className="recycle-attachment" key={i} title={block.path}>附件：{block.name}</p>)}</div>)}</div>{p.shortcut && <p>快捷键：<span>{p.shortcut}</span></p>}</> : <><p>包含话术：<span>{detail.phrases.length} 条</span></p><div className="recycle-category-content">{detail.phrases.map(phrase => <p key={phrase.id} title={textOf(phrase)}>{phrase.title || textOf(phrase).slice(0, 35) || '图片或附件话术'}</p>)}</div></>}
        <button className="recycle-permanent" disabled={busy} onClick={() => begin('remove')}>永久删除该记录</button>
      </>}
    </main></div>
    {action && <RecycleAction title={action === 'restore' ? `还原${actionItem ? recycleLabel(actionItem.kind) : '话术'}` : action === 'remove' ? '永久删除' : '清空回收站'} busy={busy} error={error} close={() => { if (!busyRef.current) setAction(null); }} submit={submit}>{action === 'restore' ? <><label>还原到 <input type="checkbox" checked disabled />我的话术</label><p className="recycle-action-hint">按原分类还原；原分类不存在或二级分类已移动时重建原位置，保留已有分类的当前设置。</p><label><input type="checkbox" checked={withoutShortcuts} disabled={busy} onChange={e => setWithoutShortcuts(e.target.checked)} />恢复时不恢复快捷键</label></> : <p>{action === 'empty' ? '确定清空整个回收站？包括搜索结果以外的所有记录。' : `确定永久删除「${actionItem?.label || ''}」？`}删除后无法恢复，外部附件源文件保留。</p>}</RecycleAction>}
    <Feedback message={message} clear={() => setMessage('')} />
  </section>;
}

function RecycleAction({ title, children, busy, error, close, submit }: { title: string; children: ReactNode; busy: boolean; error: string; close: () => void; submit: () => Promise<void> }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); }, []);
  return <dialog ref={ref} className="recycle-action" onCancel={e => { e.preventDefault(); close(); }}><header><img className="app-symbol" src="/shanyu.png" alt="" draggable={false} /><span>{title}</span><button aria-label="关闭确认窗口" disabled={busy} onClick={close}><Icon name="close" /></button></header><form onSubmit={e => { e.preventDefault(); void submit(); }}><div>{children}{error && <p className="validation-error" role="alert">{error}</p>}</div><footer><button type="button" className="secondary-button" disabled={busy} onClick={close}>取消</button><button className="primary-button" disabled={busy}>确定</button></footer></form></dialog>;
}
