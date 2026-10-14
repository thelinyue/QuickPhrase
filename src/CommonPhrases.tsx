import { useEffect, useRef, useState, type MouseEvent } from 'react';
import { Menu, type MenuItemOptions } from '@tauri-apps/api/menu';
import { LogicalPosition } from '@tauri-apps/api/dpi';
import { listen } from '@tauri-apps/api/event';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, closeWindow, openWindow, useTeams } from './api';
import { Icon, Titlebar, useEnterSubmit } from './ui';
import { PhraseRow } from './PhraseRow';
import { SearchHistoryBar, useSearchHistory, type SearchUsage } from './SearchHistory';
import { categoryLabel, type CommonPhrase, type DeliveryReport, type Library, type SearchHit, type TargetContext } from './types';

/** 换行短语按真实布局导航：上下先找相邻行，再选择横向中心最近的项。 */
export function commonNavigationIndex(rects: Pick<DOMRect, 'left' | 'top' | 'width'>[], index: number, key: string) {
  if (key === 'ArrowLeft' || key === 'ArrowRight') return Math.max(0, Math.min(rects.length - 1, index + (key === 'ArrowRight' ? 1 : -1)));
  const current = rects[index]; if (!current) return index;
  const rows = rects.map((rect, i) => ({ rect, i })).filter(({ rect }) => key === 'ArrowDown' ? rect.top > current.top + 1 : rect.top < current.top - 1);
  if (!rows.length) return index;
  const top = rows.reduce((best, { rect }) => Math.abs(rect.top - current.top) < Math.abs(best - current.top) ? rect.top : best, rows[0].rect.top);
  const center = current.left + current.width / 2;
  return rows.filter(({ rect }) => Math.abs(rect.top - top) <= 1).reduce((best, item) => Math.abs(item.rect.left + item.rect.width / 2 - center) < Math.abs(best.rect.left + best.rect.width / 2 - center) ? item : best).i;
}

/** 两种布局共用短语和操作；新增按钮与短语一起自适应换行，空列表不占提示行。
 * 横条使用原生菜单，避免菜单被窄窗口裁切。
 * 默认单击选中、双击插入；用户配置单击插入或发送后，双击不会再重复提交。 */
export function CommonPhraseBar({ library, compact = false, busy, onDeliver, onError, onHide }: {
  library: Library; compact?: boolean; busy: boolean;
  onDeliver: (id: string, mode: 'insert' | 'send', triggerKey?: number) => void; onError: (message: string) => void; onHide?: () => void;
}) {
  const [selected, setSelected] = useState(''); const menuRef = useRef<Menu | null>(null); const barRef = useRef<HTMLElement>(null);
  const send = (id: string, mode: 'insert' | 'send', triggerKey?: number) => { const key = library.libraryId ? `private:${library.libraryId}:common:${id}:${library.privateRevisions?.[`common:${id}`] || 0}` : id; if (triggerKey === undefined) onDeliver(key, mode); else onDeliver(key, mode, triggerKey); };
  const singleClick = library.settings.commonSingleClick || 'select';
  const enterSubmit = useEnterSubmit();
  useEffect(() => () => { void menuRef.current?.close().catch(() => {}); }, []);
  // 选中只属于当前短语栏；点击其他区域或移出焦点后恢复普通背景，避免残留黄色。
  useEffect(() => {
    const clear = (e: globalThis.MouseEvent) => { if (!(e.target as Element).closest('[data-common]') || !barRef.current?.contains(e.target as Node)) setSelected(''); };
    document.addEventListener('click', clear); return () => document.removeEventListener('click', clear);
  }, []);
  const run = (action: () => unknown) => { try { void Promise.resolve(action()).catch(e => onError(String(e))); } catch (e) { onError(String(e)); } };
  const add = () => openWindow('common-editor');
  async function popup(e: MouseEvent, phrase?: CommonPhrase) {
    e.preventDefault(); e.stopPropagation();
    if (phrase) setSelected(phrase.id);
    const items: MenuItemOptions[] = phrase ? [
      { text: '添加到输入区', enabled: !busy, action: () => send(phrase.id, 'insert') },
      { text: '直接发送', enabled: !busy, action: () => send(phrase.id, 'send') },
      { text: '修改这个短语', action: () => run(() => openWindow('common-editor', phrase.id)) },
      { text: '删除这个短语', action: () => run(async () => { if (await confirm(`删除「${phrase.text}」？`, { title: '删除常用短语', kind: 'warning' })) await call('delete_item', { id: phrase.id, kind: 'common' }); }) },
    ] : [];
    items.push({ text: '新增个人短语', action: () => run(add) }, { text: '快速排序', enabled: library.commonPhrases.length > 1, action: () => run(() => openWindow('common-sort')) });
    await menuRef.current?.close();
    const menu = await Menu.new({ items }); menuRef.current = menu;
    await menu.popup(new LogicalPosition(e.clientX, e.clientY));
  }
  return <section ref={barRef} className={`common-phrases${compact ? ' compact' : ''}`} style={{ maxHeight: compact ? Math.min(150, library.settings.dockCommonRows ? library.settings.dockCommonRows * 27 + 6 : 150) : library.settings.commonHeight || 144 }} aria-label="常用短语" onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget) || !e.relatedTarget?.closest('[data-common]')) setSelected(''); }} onContextMenu={e => run(() => popup(e))}>
    {compact && <button className="common-dock-toggle" title="隐藏短语" aria-label="隐藏短语" disabled={busy || !onHide} onClick={onHide}><Icon name="eye" /></button>}
    {compact && <button className="common-label" title="常用短语菜单" onClick={e => run(() => popup(e))}>常用短语<span className="common-menu-arrow" /></button>}
    <div className="common-phrase-list" role="listbox" aria-label="常用短语列表">{library.commonPhrases.map(phrase => <button key={phrase.id} role="option" aria-selected={selected === phrase.id} data-common={phrase.id} title={phrase.text} disabled={busy}
      onClick={e => { setSelected(phrase.id); if (!busy && singleClick !== 'select' && e.detail < 2) send(phrase.id, singleClick); }} onDoubleClick={() => { if (!busy && singleClick === 'select') send(phrase.id, 'insert'); }} onContextMenu={e => run(() => popup(e, phrase))}
      onFocus={() => setSelected(phrase.id)} onKeyDown={e => {
        if (e.nativeEvent.isComposing || e.keyCode === 229 || e.altKey || e.ctrlKey || e.shiftKey || e.metaKey) return;
        if (e.key === 'Enter') { e.preventDefault(); if (!busy && enterSubmit()) send(phrase.id, 'send', 13); }
        if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(e.key)) {
          e.preventDefault(); const buttons = [...barRef.current!.querySelectorAll<HTMLButtonElement>('[data-common]')];
          const next = buttons[commonNavigationIndex(buttons.map(button => button.getBoundingClientRect()), buttons.indexOf(e.currentTarget), e.key)];
          next?.focus(); next?.scrollIntoView?.({ block: 'nearest' });
        }
      }}>{phrase.text}</button>)}
    </div>
    <button className="add-common" aria-label="新增常用短语" title="新增常用短语" onClick={() => run(add)}>＋</button>
  </section>;
}

/** 常用短语只编辑文字，保存通过 Rust 服务完成，两处面板自动同步刷新。 */
export function CommonPhraseEditor({ library, id }: { library: Library; id: string }) {
  const origin = useRef({ libraryId: library.libraryId, revision: library.privateRevisions?.[`common:${id}`] || 0 });
  const phrase = library.commonPhrases.find(p => p.id === id);
  const [text, setText] = useState(phrase?.text || ''); const [error, setError] = useState(''); const [saving, setSaving] = useState(false); const composing = useRef(false);
  const limit = (value: string) => Array.from(value).slice(0, 16).join('');
  return <section className="client-window common-editor"><Titlebar title={id ? '修改短语' : '新增短语'} minimize /><form onSubmit={e => {
    e.preventDefault(); if (saving) return; setSaving(true);
    void call('save_common_phrase', { phrase: { id, text, position: 0 }, ...(origin.current.libraryId ? { libraryId: origin.current.libraryId, expectedRevision: origin.current.revision } : {}) }).then(closeWindow).catch(e => setError(String(e))).finally(() => setSaving(false));
  }}><label>短语内容<input type="text" aria-label="短语内容" placeholder="请输入短语，不超过16个字" value={text} onChange={e => setText(composing.current ? e.target.value : limit(e.target.value))} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={e => { composing.current = false; setText(limit(e.currentTarget.value)); }} required autoFocus /></label>
    {error && <p className="validation-error" role="alert">{error}</p>}
    <div className="dialog-actions"><button type="button" className="secondary-button" disabled={saving} onClick={() => void closeWindow()}>取消</button><button className="primary-button" disabled={saving}>确定</button></div>
  </form></section>;
}

/** 底部搜索和短语共用目标上下文与发送锁。内容高度变化通知 Rust，跟随时不激活聊天窗口。 */
export function CommonDockWindow({ library }: { library: Library }) {
  const { version: teamVersion, status: teamStatus } = useTeams();
  const enterSubmit = useEnterSubmit();
  const [busy, setBusy] = useState(false); const busyRef = useRef(false); const [message, setMessage] = useState('');
  const history = useSearchHistory(library.libraryId, setMessage);
  const [searchVersion, repeatSearch] = useState(0); const searchRef = useRef<HTMLInputElement>(null);
  const searchUsage = (): SearchUsage | undefined => library.libraryId ? { query, libraryId: library.libraryId } : undefined;
  const content = useRef<HTMLElement>(null);
  const [query, setQuery] = useState(''); const [storedResults, setResults] = useState<SearchHit[]>([]); const [index, setIndex] = useState(0); const [searching, setSearching] = useState(false);
  const [collapsed, setCollapsed] = useState(false); const [kind, setKind] = useState<'phrase' | 'file'>('phrase');
  const upperRef = useRef<HTMLDivElement>(null);
  const composing = useRef(false); const [compositionVersion, endComposition] = useState(0); const menuRef = useRef<Menu | null>(null);
  const [hiding, setHiding] = useState(false); const hidingRef = useRef(false);
  const clearSearch = () => { setQuery(''); setResults([]); setIndex(0); setSearching(false); };
  const chooseHistory = (value: string) => { composing.current = false; setQuery(value); setResults([]); setIndex(0); setSearching(true); repeatSearch(v => v + 1); searchRef.current?.focus(); };
  const previousLibrary = useRef(library.libraryId);
  useEffect(() => { if (previousLibrary.current !== library.libraryId) { previousLibrary.current = library.libraryId; clearSearch(); } }, [library.libraryId]);
  useEffect(() => { if (!library.settings.dockSearch) { clearSearch(); setCollapsed(false); } }, [library.settings.dockSearch]);
  useEffect(() => () => { void menuRef.current?.close().catch(() => {}); }, []);
  useEffect(() => {
    if (collapsed || !library.settings.dockSearch || !query.trim() || composing.current) { if (!query.trim()) setResults([]); return; }
    let active = true; setSearching(true);
    const timer = setTimeout(() => { void call<SearchHit[]>('search_scoped_content', { query, scope: 'all' }).then(found => { if (active) { setResults(found); setIndex(i => Math.min(i, Math.max(0, found.length - 1))); setSearching(false); } }).catch(e => { if (active) { setResults([]); setMessage(String(e)); setSearching(false); } }); }, 100);
    return () => { active = false; clearTimeout(timer); };
  }, [query, library, compositionVersion, teamVersion, searchVersion, collapsed]);
  const results = storedResults.filter(hit => hit.kind === kind && (!hit.teamId || teamStatus.teams.some(t => t.teamId === hit.teamId)));
  useEffect(() => {
    const element = content.current?.querySelector('.common-dock-content'); if (!element) return;
    // Flex 先为输入、历史和短语留出空间，结果区消化剩余高度；同时报告向上展开量，原生窗口才能固定输入行。
    const observer = new ResizeObserver(() => {
      const height = Math.max(28, Math.min(420, Math.ceil(element.getBoundingClientRect().height)));
      const aboveHeight = Math.min(height - 28, Math.ceil(upperRef.current?.getBoundingClientRect().height || 0));
      void call('set_common_dock_height', { height, aboveHeight }).catch(e => setMessage(String(e)));
    });
    observer.observe(element); if (upperRef.current) observer.observe(upperRef.current);
    return () => observer.disconnect();
  }, []);
  useEffect(() => { let disposed = false; let off: (() => void) | undefined;
    void listen<boolean>('delivery-busy', e => { busyRef.current = e.payload; setBusy(e.payload); }).then(un => { if (disposed) un(); else off = un; });
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => {
    let disposed = false; let off: (() => void) | undefined;
    void listen<string>('ui-action', e => { if (e.payload === 'common') (content.current?.querySelector<HTMLElement>('[data-common][aria-selected="true"]') || content.current?.querySelector<HTMLElement>('[data-common],.add-common'))?.focus(); }).then(un => { if (disposed) un(); else off = un; });
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => { if (message) { const timer = setTimeout(() => setMessage(''), 6000); return () => clearTimeout(timer); } }, [message]);
  async function deliver(id: string, mode: 'insert' | 'send', kind = 'common', triggerKey?: number, usage?: SearchUsage) {
    if (busyRef.current) return; busyRef.current = true; setBusy(true);
    try {
      const target = await call<TargetContext | null>('get_target');
      if (!target) throw new Error('请先聚焦目标输入区，再使用话术。');
      const report = await call<DeliveryReport>('deliver_content', { id, kind, mode, target, ...(triggerKey ? { triggerKey } : {}) });
      if (report.status === 'submitted' && library.settings.exitSearch) clearSearch();
      setMessage(report.status === 'submitted' ? '' : report.error || '操作已停止');
      if (report.status === 'submitted' && usage) await history.record(usage);
    } catch (e) { setMessage(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  async function resultMenu(e: MouseEvent, hit: SearchHit) {
    e.preventDefault(); e.stopPropagation();
    const usage = searchUsage();
    await menuRef.current?.close();
    const menu = await Menu.new({ items: [{ text: '添加到输入区', enabled: !busy, action: () => void deliver(hit.privateKey || hit.id, 'insert', hit.kind, undefined, usage) }, { text: '直接发送', enabled: !busy, action: () => void deliver(hit.privateKey || hit.id, 'send', hit.kind, undefined, usage) }] }); menuRef.current = menu;
    await menu.popup(new LogicalPosition(e.clientX, e.clientY));
  }
  async function hideCommon() {
    if (hidingRef.current) return; hidingRef.current = true; setHiding(true);
    try {
      // 关闭整条短语区域并同步设置；没有底部搜索时，原生跟随服务同时隐藏整个窗口。
      // 恢复入口使用界面设置中的“显示底部吸附常用短语”，不留下空横条或新增按钮。
      await call('save_settings', { settings: { ...library.settings, dockCommon: false } });
    } catch (e) { setMessage(String(e)); } finally { hidingRef.current = false; setHiding(false); }
  }
  function toggleSearch() {
    composing.current = false;
    if (collapsed) { setResults([]); setIndex(0); setSearching(!!query.trim()); }
    setCollapsed(value => !value);
    if (collapsed) requestAnimationFrame(() => searchRef.current?.focus());
  }
  return <section className="client-window common-dock" ref={content} onContextMenu={e => e.preventDefault()}><div className="common-dock-content">
    <div className="dock-search-upper" ref={upperRef} id="dock-search-panel">
    {library.settings.dockSearch && !collapsed && query.trim() && <>
      <div className="dock-search-tabs" role="tablist" aria-label="底部搜索类型">{(['phrase', 'file'] as const).map(value => <button key={value} role="tab" id={`dock-tab-${value}`} aria-selected={kind === value} aria-controls="dock-search-results" tabIndex={kind === value ? 0 : -1} onClick={() => { setKind(value); setIndex(0); }} onKeyDown={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) { e.preventDefault(); const next = e.key === 'Home' ? 'phrase' : e.key === 'End' ? 'file' : kind === 'phrase' ? 'file' : 'phrase'; setKind(next); setIndex(0); document.getElementById(`dock-tab-${next}`)?.focus(); } }}>{value === 'phrase' ? '话术' : '本地文件'}</button>)}</div>
      <div className="dock-search-results" id="dock-search-results" role="listbox" aria-label="底部搜索结果">{searching ? <div className="no-results">正在搜索…</div> : results.length ? results.map((hit, i) => <PhraseRow key={hit.privateKey || `${hit.teamId || ''}:${hit.kind}:${hit.id}`} sourceLabel={hit.sourceLabel} phrase={hit.phrase || undefined} file={hit.file || undefined} index={i} settings={library.settings} selected={i === index} query={query} busy={busy} onSelect={() => setIndex(i)} onDeliver={(mode, triggerKey) => void deliver(hit.privateKey || hit.id, mode, hit.kind, triggerKey, searchUsage())} onContext={e => void resultMenu(e, hit).catch(e => setMessage(String(e)))} />) : <div className="no-results">没有找到匹配的{kind === 'phrase' ? '话术' : '本地文件'}</div>}</div>
    </>}
    {library.settings.dockSearch && !collapsed && <SearchHistoryBar label="历史搜索" items={history.items} clearing={history.clearing} onSelect={chooseHistory} onClear={() => void history.clear()} />}
    </div>
    {library.settings.dockSearch && <div className="search-bar dock-search-bar"><button className="dock-search-toggle" aria-label={collapsed ? '展开底部搜索' : '收起底部搜索'} title={collapsed ? '展开底部搜索' : '收起底部搜索'} aria-expanded={!collapsed} aria-controls="dock-search-panel" onClick={toggleSearch}><Icon name={collapsed ? 'eye-off' : 'eye'} /></button>{!collapsed && <><Icon name="search" /><input ref={searchRef} data-shortcut-search aria-label="底部搜索话术、本地文件" placeholder="搜索话术、本地文件" value={query} onChange={e => { setQuery(e.target.value); setIndex(0); setSearching(!!e.target.value.trim()); }} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={e => { composing.current = false; setQuery(e.currentTarget.value); endComposition(v => v + 1); }} onKeyDown={e => {
      if (composing.current || e.nativeEvent.isComposing || e.keyCode === 229) return;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setIndex(i => Math.max(0, Math.min(results.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))); requestAnimationFrame(() => document.querySelector('.dock-search-results .selected')?.scrollIntoView({ block: 'nearest' })); }
      if (e.key === 'Enter' && query.trim() && !searching && results[index] && enterSubmit()) { e.preventDefault(); const hit = results[index]; void deliver(hit.privateKey || hit.id, 'insert', hit.kind, 13, searchUsage()); }
      if (e.key === 'Escape') { e.preventDefault(); clearSearch(); }
    }} />{query && <button className="dock-search-clear" aria-label="清空底部搜索" onClick={() => { clearSearch(); searchRef.current?.focus(); }}>×</button>}</>}</div>}
    {library.settings.dockCommon && <CommonPhraseBar library={library} compact busy={busy || hiding} onDeliver={(id, mode, triggerKey) => void deliver(id, mode, 'common', triggerKey)} onError={setMessage} onHide={() => void hideCommon()} />}
    </div>
    {message && <span className="dock-status" role="status" title={message} onClick={() => setMessage('')}>{message}</span>}
  </section>;
}

/** 排序只修改本窗口的顺序草稿，确认后以主键列表事务保存；取消不改数据库。
 * Pointer Events 支持 Ctrl 多选拖动，不依赖 Windows WebView 的文件拖放通道。 */
export function CommonPhraseSort({ library }: { library: Library }) {
  return <QuickSort items={library.commonPhrases} title="常用短语快速排序" listLabel="短语排序列表" empty="还没有常用短语" onSave={ids => call('reorder_common_phrases', { ids })} />;
}

/** 一级与二级分类共用现有排序交互；只提交同级分类主键，不改变分类归属或话术内容。 */
export function CategorySort({ library, parentId, kind }: { library: Library; parentId: string | null; kind: 'phrase' | 'file' }) {
  const items = library.categories.filter(c => c.kind === kind && c.parentId === parentId).map(c => ({ id: c.id, text: categoryLabel(c, library.categories) }));
  const parent = library.categories.find(c => c.id === parentId);
  return <QuickSort items={items} title={kind === 'file' ? '文件分类快速排序' : parentId ? `二级分类快速排序 · ${parent?.name || '分类已删除'}` : '一级分类快速排序'} listLabel="分类排序列表" empty="没有可排序的分类" onSave={ids => call('reorder_categories', { ids, kind, parentId })} />;
}

/** 文件排序仅保存当前分类中关联记录的顺序，不改动源文件。 */
export function FileSort({ library, categoryId }: { library: Library; categoryId: string }) {
  const items = library.files.filter(f => f.categoryId === categoryId).map(f => ({ id: f.id, text: f.name }));
  return <QuickSort items={items} title="文件快速排序" listLabel="文件排序列表" empty="没有可排序的文件" onSave={ids => call('reorder_files', { ids, categoryId })} />;
}

function QuickSort({ items, title, listLabel, empty, onSave }: { items: { id: string; text: string }[]; title: string; listLabel: string; empty: string; onSave: (ids: string[]) => Promise<void> }) {
  const [order, setOrder] = useState(() => items.map(p => p.id)); const [selected, setSelected] = useState(new Set<string>());
  const [drop, setDrop] = useState<{ id: string; after: boolean } | null>(null); const [error, setError] = useState(''); const [saving, setSaving] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{ ids: Set<string>; clicked: string; ctrl: boolean; x: number; y: number; moved: boolean; over: { id: string; after: boolean } | null } | null>(null);
  useEffect(() => {
    const ids = items.map(p => p.id); const available = new Set(ids);
    setOrder(previous => [...previous.filter(id => available.has(id)), ...ids.filter(id => !previous.includes(id))]);
    setSelected(previous => new Set([...previous].filter(id => available.has(id))));
  }, [items]);
  useEffect(() => {
    const move = (e: PointerEvent) => {
      const active = drag.current; if (!active) return;
      if (!active.moved && Math.hypot(e.clientX - active.x, e.clientY - active.y) < 4) return;
      active.moved = true;
      const row = document.elementFromPoint(e.clientX, e.clientY)?.closest<HTMLElement>('[data-sort-id]');
      if (row) { active.over = { id: row.dataset.sortId!, after: e.clientY > row.getBoundingClientRect().top + row.getBoundingClientRect().height / 2 }; }
      else if (listRef.current?.contains(document.elementFromPoint(e.clientX, e.clientY))) {
        const rows = listRef.current.querySelectorAll<HTMLElement>('[data-sort-id]');
        if (rows.length) active.over = { id: rows[rows.length - 1].dataset.sortId!, after: true };
      } else active.over = null;
      setDrop(active.over);
    };
    const end = () => {
      const active = drag.current; drag.current = null; setDrop(null); if (!active) return;
      if (!active.moved && !active.ctrl) setSelected(new Set([active.clicked]));
      if (active.moved && active.over && !active.ids.has(active.over.id)) {
        const over = active.over;
        setOrder(previous => {
          const moving = previous.filter(id => active.ids.has(id)); const remaining = previous.filter(id => !active.ids.has(id));
          const index = remaining.indexOf(over.id) + (over.after ? 1 : 0);
          return [...remaining.slice(0, index), ...moving, ...remaining.slice(index)];
        });
      }
    };
    const cancel = () => { drag.current = null; setDrop(null); };
    document.addEventListener('pointermove', move); document.addEventListener('pointerup', end); document.addEventListener('pointercancel', cancel);
    return () => { document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', end); document.removeEventListener('pointercancel', cancel); };
  }, []);
  function shift(direction: number) {
    setOrder(previous => {
      const next = [...previous];
      if (direction < 0) { for (let i = 1; i < next.length; i++) if (selected.has(next[i]) && !selected.has(next[i - 1])) [next[i - 1], next[i]] = [next[i], next[i - 1]]; }
      else { for (let i = next.length - 2; i >= 0; i--) if (selected.has(next[i]) && !selected.has(next[i + 1])) [next[i], next[i + 1]] = [next[i + 1], next[i]]; }
      return next;
    });
  }
  return <section className="client-window common-sort" onContextMenu={e => e.preventDefault()}><Titlebar title={title} maximize />
    <div ref={listRef} className="common-sort-list" role="listbox" aria-label={listLabel} aria-multiselectable="true">{order.map(id => {
      const phrase = items.find(p => p.id === id); if (!phrase) return null;
      return <button key={id} data-sort-id={id} role="option" aria-selected={selected.has(id)} disabled={saving} className={drop?.id === id ? drop.after ? 'drop-after' : 'drop-before' : ''}
        onPointerDown={e => {
          if (e.button !== 0) return; e.preventDefault(); e.currentTarget.focus();
          const ctrl = e.ctrlKey || e.metaKey; let next: Set<string>;
          if (ctrl) { next = new Set(selected); if (next.has(id)) next.delete(id); else next.add(id); }
          else next = selected.has(id) ? new Set(selected) : new Set([id]);
          setSelected(next);
          if (next.has(id)) { drag.current = { ids: next, clicked: id, ctrl, x: e.clientX, y: e.clientY, moved: false, over: null }; e.currentTarget.setPointerCapture(e.pointerId); }
        }} onKeyDown={e => {
          if (e.altKey && ['ArrowUp', 'ArrowDown'].includes(e.key)) { e.preventDefault(); shift(e.key === 'ArrowUp' ? -1 : 1); }
          else if (e.ctrlKey && e.key.toLowerCase() === 'a') { e.preventDefault(); setSelected(new Set(order)); }
          else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelected(previous => { const next = e.ctrlKey ? new Set(previous) : new Set<string>(); if (e.ctrlKey && next.has(id)) next.delete(id); else next.add(id); return next; }); }
        }}>{phrase.text}</button>;
    })}{!order.length && <span className="common-empty">{empty}</span>}</div>
    {error && <p className="validation-error" role="alert">{error}</p>}<footer className="common-sort-footer"><span>上下拖动排序，Ctrl 可多选；Alt+↑/↓ 可移动选中项</span><button className="secondary-button" disabled={saving} onClick={() => void closeWindow()}>取消</button><button className="primary-button" disabled={saving} onClick={() => {
      if (saving) return; setSaving(true); void onSave(order).then(closeWindow).catch(e => setError(String(e))).finally(() => setSaving(false));
    }}>确定</button></footer>
  </section>;
}
