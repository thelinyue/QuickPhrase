import { orderSearchResults, SearchResults } from './SearchResults';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import { Menu } from '@tauri-apps/api/menu';
import { LogicalPosition } from '@tauri-apps/api/dpi';
import { listen } from '@tauri-apps/api/event';
import { useCommonMenu } from './CommonMenu';
import { call, closeWindow, openWindow, useTeams } from './api';
import { Icon, Titlebar, useEnterSubmit } from './ui';
import { PhraseRow } from './PhraseRow';
import { SearchHistoryBar, useSearchHistory, type SearchUsage } from './SearchHistory';
import { useMembers } from './useMembers';
import { useNotices } from './notices';
import { useDeliveryProgress } from './useDeliveryProgress';
import { usePendingDeliveryNotices } from './usePendingDeliveryNotices';
import { searchHitKey, searchDeliveryKey, categoryLabel, type CommonPhrase, type DeliveryReport, type Library, type SearchHit, type TargetContext } from './types';

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
 * 管理菜单由独立窗口承载，避免被窄底栏裁切。
 * 两处均单击插入；双击的第二次 click 和 dblclick 不再投递。 */
export function CommonPhraseBar({ library, compact = false, busy, onDeliver, onError, onHide }: {
  library: Library; compact?: boolean; busy: boolean;
  onDeliver: (id: string, mode: 'insert' | 'send', triggerKey?: number) => void; onError: (message: string) => void; onHide?: () => void;
}) {
  const [selected, setSelected] = useState(''); const barRef = useRef<HTMLElement>(null);
  const openMenu = useCommonMenu(library, onError);
  const send = (id: string, mode: 'insert' | 'send', triggerKey?: number) => { const key = library.libraryId ? `private:${library.libraryId}:common:${id}:${library.privateRevisions?.[`common:${id}`] || 0}` : id; if (triggerKey === undefined) onDeliver(key, mode); else onDeliver(key, mode, triggerKey); };
  const enterSubmit = useEnterSubmit();
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
    const trigger = e.currentTarget as HTMLElement;
    const box = trigger.getBoundingClientRect();
    const anchored = e.type === 'click' || (e.clientX === 0 && e.clientY === 0);
    await openMenu(trigger, anchored ? box.left : e.clientX, anchored ? box.bottom : e.clientY, phrase);
  }
  return <section ref={barRef} className={`common-phrases${compact ? ' compact' : ''}`} style={{ maxHeight: compact ? Math.min(150, library.settings.dockCommonRows ? library.settings.dockCommonRows * 27 + 1 : 150) : library.settings.commonHeight || 144 }} aria-label="常用短语" tabIndex={-1} onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget) || !e.relatedTarget?.closest('[data-common]')) setSelected(''); }} onContextMenu={e => run(() => popup(e))}>
    {compact && <button className="common-dock-toggle" title="隐藏短语" aria-label="隐藏短语" disabled={busy || !onHide} onClick={onHide}><Icon name="eye" /></button>}
    {compact && <button className="common-label" aria-label="常用短语菜单" title="常用短语菜单" onClick={e => run(() => popup(e))}>常用短语<span className="common-menu-arrow" /></button>}
    <div className="common-phrase-list" role="listbox" aria-label="常用短语列表">{library.commonPhrases.map(phrase => <button key={phrase.id} role="option" aria-selected={selected === phrase.id} data-common={phrase.id} title={phrase.text} disabled={busy}
      onClick={e => { setSelected(phrase.id); if (!busy && e.detail < 2) send(phrase.id, 'insert'); }} onContextMenu={e => run(() => popup(e, phrase))}
      onFocus={() => setSelected(phrase.id)} onKeyDown={e => {
        if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
          e.preventDefault(); const box = e.currentTarget.getBoundingClientRect();
          const trigger = e.currentTarget; run(() => openMenu(trigger, box.left, box.bottom, phrase)); return;
        }
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
  const { status: teamStatus } = useTeams();
  const members = useMembers(teamStatus);
  const enterSubmit = useEnterSubmit();
  const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const notices = useNotices(); const setMessage = notices.error;
  const deliveryProgress = useDeliveryProgress('common-dock');
  const noticeScope = JSON.stringify([library.libraryId, teamStatus.server, teamStatus.user?.id, teamStatus.sessionEpoch]);
  const noticeScopeRef = useRef(noticeScope); const previousNoticeScope = useRef(noticeScope); noticeScopeRef.current = noticeScope;
  useEffect(() => { if (previousNoticeScope.current !== noticeScope) { previousNoticeScope.current = noticeScope; notices.clear(); } }, [noticeScope]);
  const pendingDeliveries = usePendingDeliveryNotices(noticeScope, 'common-dock');
  const pendingDelivery = pendingDeliveries.items[0];
  const showPending = !!pendingDelivery && (pendingDelivery.status === 'interrupted' || (!pendingDeliveries.problem && notices.current?.kind !== 'warning'));
  const showDeliveryProblem = !showPending && !!pendingDeliveries.problem && notices.current?.kind !== 'warning';
  const showProgress = !showPending && !showDeliveryProblem && !!deliveryProgress.waiting && (!notices.current || notices.current.kind === 'success' || notices.current.kind === 'progress');
  const activeNotice = showPending ? { message: pendingDelivery.message, kind: pendingDelivery.status === 'interrupted' ? 'warning' : 'error', title: pendingDelivery.status === 'interrupted' ? '请先检查目标，操作已中断' : undefined } : showDeliveryProblem ? { message: pendingDeliveries.problem, kind: 'warning', title: '请先检查目标，投递结果暂不可用' } : showProgress ? undefined : notices.current;
  const [noticeExpanded, setNoticeExpanded] = useState(false); const noticeRef = useRef<HTMLDivElement>(null);
  const history = useSearchHistory(library.libraryId, setMessage);
  const [searchVersion, repeatSearch] = useState(0); const searchRef = useRef<HTMLInputElement>(null);
  const searchUsage = (): SearchUsage | undefined => library.libraryId ? { query, libraryId: library.libraryId } : undefined;
  const content = useRef<HTMLElement>(null);
  const [query, setQuery] = useState(''); const [storedResults, setResults] = useState<SearchHit[]>([]); const [index, setIndex] = useState(0); const [searching, setSearching] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const [searchActive, setSearchActive] = useState(false);
  const upperRef = useRef<HTMLDivElement>(null);
  const commonRef = useRef<HTMLDivElement>(null); const resultMenuOpen = useRef(false);
  const overlayOpen = searchActive && library.settings.dockSearch && !collapsed;
  const composing = useRef(false); const [compositionVersion, endComposition] = useState(0); const menuRef = useRef<Menu | null>(null);
  // 状态通知不等于内容更新；只比较内容与已有版本，成员同步时间变化不清空列表。
  const searchScope = JSON.stringify([library.libraryId, teamStatus.server, teamStatus.user?.id, teamStatus.sessionEpoch, teamStatus.user?.mustChangePassword, teamStatus.memberPhraseSync, members?.subscription, teamStatus.teams.map(t => [t.teamId, t.membershipVersion, t.lifecycleVersion])]);
  const teamContent = JSON.stringify(teamStatus.teams.map(t => [t.teamId, t.version]));
  const searchStamp = useMemo(() => JSON.stringify([library.settings.searchMode, library.phrases, library.categories, library.files, members?.phrases, members?.categories, teamContent]), [library.settings.searchMode, library.phrases, library.categories, library.files, members?.phrases, members?.categories, teamContent]);
  const [searchFailed, setSearchFailed] = useState(false);
  const [searchSource, setSearchSource] = useState<{ query: string; scope: string; source: string; valid: boolean } | null>(null);
  const searchPending = searching || composing.current || searchSource?.query !== query || searchSource.scope !== searchScope || searchSource.source !== searchStamp || !searchSource.valid;
  const searchGuard = useRef({ query, scope: searchScope, source: searchStamp, pending: searchPending }); searchGuard.current = { query, scope: searchScope, source: searchStamp, pending: searchPending };
  const results = searchSource?.query === query && searchSource.scope === searchScope ? storedResults.filter(hit => (!hit.teamId || teamStatus.teams.some(t => t.teamId === hit.teamId))) : [];
  const searchSelection = useRef<SearchHit | undefined>(undefined); searchSelection.current = results[index];
  const [hiding, setHiding] = useState(false); const hidingRef = useRef(false);
  const clearSearch = () => { setQuery(''); setResults([]); setIndex(0); setSearching(false); };
  const chooseHistory = (value: string) => { composing.current = false; setQuery(value); setResults([]); setIndex(0); setSearching(true); repeatSearch(v => v + 1); searchRef.current?.focus(); };
  const previousLibrary = useRef(library.libraryId);
  useEffect(() => { if (previousLibrary.current !== library.libraryId) { previousLibrary.current = library.libraryId; clearSearch(); } }, [library.libraryId]);
  useEffect(() => { if (!library.settings.dockSearch) { clearSearch(); setCollapsed(false); setSearchActive(false); } }, [library.settings.dockSearch]);
  useEffect(() => {
    const blur = () => { if (!resultMenuOpen.current) setSearchActive(false); };
    // OS 切窗保留 DOM activeElement，再点原输入框时不一定重新触发 focusin。
    const focus = () => { if (document.activeElement?.closest('.dock-search-upper,.dock-search-bar')) setSearchActive(true); };
    window.addEventListener('blur', blur); window.addEventListener('focus', focus);
    return () => { window.removeEventListener('blur', blur); window.removeEventListener('focus', focus); };
  }, []);
  useEffect(() => () => { void menuRef.current?.close().catch(() => {}); }, []);
  useEffect(() => {
    if (collapsed || !library.settings.dockSearch || !query.trim() || composing.current) { if (!query.trim()) setResults([]); return; }
    let active = true; setSearching(true); setSearchFailed(false);
    const timer = setTimeout(() => { void call<SearchHit[]>('search_scoped_content', { query, scope: 'all' }).then(hits => {
      const found = orderSearchResults(hits);
      if (!active) return;
      const selected = searchSelection.current;
      setIndex(Math.max(0, found.findIndex(hit => !!selected && searchHitKey(hit) === searchHitKey(selected))));
      setResults(previous => JSON.stringify(previous) === JSON.stringify(found) ? previous : found);
      setSearchSource({ query, scope: searchScope, source: searchStamp, valid: true }); setSearching(false);
    }).catch(e => { if (active) { setSearchSource(previous => previous ? { ...previous, valid: false } : null); setMessage(String(e)); setSearching(false); setSearchFailed(true); } }); }, 100);
    return () => { active = false; clearTimeout(timer); };
  }, [query, searchScope, searchStamp, library.settings.dockSearch, compositionVersion, searchVersion, collapsed]);
  const memberIds = JSON.stringify(results.filter(h => h.phrase?.contentRef?.source === 'member').map(h => h.id));
  useEffect(() => { const ids = JSON.parse(memberIds) as string[]; if (ids.length && !teamStatus.offline) void call('download_member_materials', { ids, attachments: false }).catch(() => {}); }, [memberIds, teamStatus.offline]);
  useLayoutEffect(() => {
    const element = content.current?.querySelector('.common-dock-content'); if (!element) return;
    // 两层占同一个网格：只把浮层超出短语的部分报告为向上展开量，搜索输入的屏幕坐标不变。
    const measure = () => {
      const height = Math.max(28, Math.min(420, Math.ceil(element.getBoundingClientRect().height)));
      const overlayHeight = Math.max(0, (upperRef.current?.getBoundingClientRect().height || 0) - (commonRef.current?.getBoundingClientRect().height || 0));
      const aboveHeight = Math.max(0, Math.min(height - 28, Math.ceil(overlayHeight + (noticeRef.current?.getBoundingClientRect().height || 0))));
      void call('set_common_dock_height', { height, aboveHeight }).catch(e => setMessage(String(e)));
    };
    const observer = new ResizeObserver(measure);
    observer.observe(element); if (upperRef.current) observer.observe(upperRef.current); if (commonRef.current) observer.observe(commonRef.current);
    measure();
    return () => observer.disconnect();
  }, []);
  useEffect(() => { let disposed = false; let off: (() => void) | undefined;
    void listen<boolean>('delivery-busy', e => { busyRef.current = e.payload; setBusy(e.payload); }).then(un => { if (disposed) un(); else off = un; });
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => {
    let disposed = false; let off: (() => void) | undefined;
    void listen<string>('ui-action', e => { if (e.payload === 'common') { setSearchActive(false); (content.current?.querySelector<HTMLElement>('[data-common][aria-selected="true"]') || content.current?.querySelector<HTMLElement>('[data-common],.add-common'))?.focus(); } }).then(un => { if (disposed) un(); else off = un; });
    return () => { disposed = true; off?.(); };
  }, []);
  useEffect(() => { setNoticeExpanded(false); }, [pendingDelivery?.id, pendingDeliveries.problem, notices.current?.id]);
  async function deliver(id: string, mode: 'insert' | 'send', kind = 'common', triggerKey?: number, usage?: SearchUsage) {
    if (kind !== 'common' && (searchGuard.current.pending || searchGuard.current.query !== query || searchGuard.current.scope !== searchScope || searchGuard.current.source !== searchStamp)) return;
    if (busyRef.current) return; busyRef.current = true; setBusy(true); const scope = noticeScopeRef.current;
    try {
      const target = await call<TargetContext | null>('get_target');
      if (!target) throw new Error('请先聚焦目标输入区，再使用话术。');
      const report = await call<DeliveryReport>('deliver_content', { id, kind, mode, target, ...(triggerKey ? { triggerKey } : {}) });
      if (scope !== noticeScopeRef.current) return;
      if (report.noticeStored) void pendingDeliveries.refresh();
      if (report.status === 'submitted' && library.settings.exitSearch) clearSearch();
      if (!report.noticeStored && report.status === 'interrupted') notices.show(report.error || '请先检查目标输入区和已发送内容，已执行的内容不会自动重发。', 'warning', '请先检查目标，操作已中断');
      else if (!report.noticeStored && report.status !== 'submitted') setMessage(report.error || '操作已停止');
      if (report.status === 'submitted' && usage) await history.record(usage);
    } catch (e) { if (scope === noticeScopeRef.current) setMessage(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  async function resultMenu(e: MouseEvent, hit: SearchHit) {
    e.preventDefault(); e.stopPropagation();
    if (searchGuard.current.pending) return;
    const usage = searchUsage();
    await menuRef.current?.close();
    const menu = await Menu.new({ items: [{ text: '添加到输入区', enabled: !busy, action: () => void deliver(searchDeliveryKey(hit), 'insert', hit.kind, undefined, usage) }, { text: '直接发送', enabled: !busy, action: () => void deliver(searchDeliveryKey(hit), 'send', hit.kind, undefined, usage) }, ...(hit.phrase?.contentRef?.source === 'member' ? [{ text: '下载全部素材', enabled: !busy && !teamStatus.offline, action: () => { void call('download_member_materials', { ids: [searchDeliveryKey(hit)], attachments: true }).catch(e => setMessage(String(e))); } }] : [])] }); menuRef.current = menu;
    resultMenuOpen.current = true;
    try { await menu.popup(new LogicalPosition(e.clientX, e.clientY)); }
    finally { resultMenuOpen.current = false; if (!document.hasFocus()) setSearchActive(false); }
  }
  async function hideCommon() {
    if (hidingRef.current) return; hidingRef.current = true; setHiding(true);
    try {
      // 关闭整条短语区域并同步设置；没有底部搜索时，原生跟随服务同时隐藏整个窗口。
      // 恢复入口使用界面设置中的“显示底部吸附常用短语”，不留下空横条或新增按钮。
      await call('patch_settings', { patch: { dockCommon: false } });
    } catch (e) { setMessage(String(e)); } finally { hidingRef.current = false; setHiding(false); }
  }
  function toggleSearch() {
    composing.current = false;
    if (collapsed) { setResults([]); setIndex(0); setSearching(!!query.trim()); }
    setCollapsed(value => !value);
    setSearchActive(collapsed);
    if (collapsed) requestAnimationFrame(() => searchRef.current?.focus());
  }
  return <section className="client-window common-dock" ref={content} onContextMenu={e => e.preventDefault()} onFocusCapture={e => {
    setSearchActive(!!(e.target as Element).closest('.dock-search-upper,.dock-search-bar'));
  }} onBlurCapture={e => {
    if (e.relatedTarget && !(e.relatedTarget as Element).closest('.dock-search-upper,.dock-search-bar')) setSearchActive(false);
  }} onPointerDownCapture={e => {
    const target = e.target as Element;
    if (!target.closest('.dock-search-upper,.dock-search-bar')) setSearchActive(false);
    else { setSearchActive(!collapsed); if (!target.closest('button,input,[tabindex]')) e.preventDefault(); }
  }}><div className="common-dock-content">
    {activeNotice && <div className={`dock-notice dock-notice-${activeNotice.kind}`} ref={noticeRef}>
      <div className="dock-notice-row" role="alert" title={activeNotice.message}><span className="dock-notice-mark" aria-hidden="true">!</span><span className="dock-notice-summary">{activeNotice.title || (noticeExpanded ? '提示详情' : activeNotice.message)}</span>{activeNotice.title && <span className="visually-hidden">{activeNotice.message}</span>}{pendingDeliveries.items.length + notices.count + (pendingDeliveries.problem ? 1 : 0) > 1 && <small>{pendingDeliveries.items.length + notices.count + (pendingDeliveries.problem ? 1 : 0)} 条</small>}<button type="button" aria-expanded={noticeExpanded} onClick={() => setNoticeExpanded(value => !value)}>{noticeExpanded ? '收起' : '查看'}</button><button type="button" aria-label="关闭提示" title="关闭提示" onClick={() => showPending ? void pendingDeliveries.dismiss(pendingDelivery!.id).catch(e => setMessage(String(e))) : showDeliveryProblem ? pendingDeliveries.dismissProblem() : notices.current && notices.dismiss(notices.current.id)}>×</button></div>
      {noticeExpanded && <div className="dock-notice-detail">{activeNotice.message}</div>}
    </div>}
    {!activeNotice && deliveryProgress.waiting && <div className="dock-notice dock-notice-progress" ref={noticeRef}><div className="dock-notice-row" role="status"><span className="dock-notice-summary">正在等待剪贴板释放…</span><button type="button" onClick={() => void call('cancel_delivery', { operationId: deliveryProgress.waiting!.operation }).catch(e => setMessage(String(e)))}>停止</button></div></div>}
    <div className="dock-common-slot" ref={commonRef}>
      {library.settings.dockCommon && <CommonPhraseBar library={library} compact busy={busy || hiding} onDeliver={(id, mode, triggerKey) => { setSearchActive(false); void deliver(id, mode, 'common', triggerKey); }} onError={setMessage} onHide={() => void hideCommon()} />}
    </div>
    <div className="dock-search-upper" ref={upperRef} id="dock-search-panel" hidden={!overlayOpen}>
    {overlayOpen && query.trim() && <section className="dock-result-panel" aria-label="搜索结果">
      <header><span>搜索结果</span><button aria-label="关闭底部搜索结果" onClick={() => { clearSearch(); searchRef.current?.focus(); }}>×</button></header>
      <div className="dock-search-results" id="dock-search-results" role="listbox" aria-label="底部搜索结果" aria-busy={searchPending && !searchFailed}>{results.length ? <SearchResults results={results} renderRow={(hit, i) => <PhraseRow key={searchHitKey(hit)} phrase={hit.phrase || undefined} file={hit.file || undefined} index={i} settings={library.settings} selected={i === index} query={query} busy={busy || searchPending} onSelect={() => setIndex(i)} onDeliver={(mode, triggerKey) => void deliver(searchDeliveryKey(hit), mode, hit.kind, triggerKey, searchUsage())} onContext={e => void resultMenu(e, hit).catch(e => setMessage(String(e)))} />} /> : <div className="no-results">{searchFailed ? '搜索失败，请重新输入关键词' : searchPending ? '正在搜索…' : '没有找到匹配的内容'}</div>}</div>
    </section>}
    {overlayOpen && !query.trim() && <SearchHistoryBar label="历史搜索" items={history.items} clearing={history.clearing} onSelect={chooseHistory} onClear={() => void history.clear()} />}
    </div>
    {library.settings.dockSearch && <div className="search-bar dock-search-bar"><button className="dock-search-toggle" aria-label={collapsed ? '展开底部搜索' : '收起底部搜索'} title={collapsed ? '展开底部搜索' : '收起底部搜索'} aria-expanded={!collapsed} aria-controls="dock-search-panel" onClick={toggleSearch}><Icon name={collapsed ? 'eye-off' : 'eye'} /></button>{!collapsed && <><Icon name="search" /><input ref={searchRef} data-shortcut-search aria-label="底部搜索话术、文件" placeholder="搜索话术、文件" value={query} onChange={e => { setSearchActive(true); setQuery(e.target.value); setIndex(0); setSearching(!!e.target.value.trim()); }} onCompositionStart={() => { composing.current = true; setSearching(true); }} onCompositionEnd={e => { composing.current = false; setQuery(e.currentTarget.value); endComposition(v => v + 1); }} onKeyDown={e => {
      if (composing.current || e.nativeEvent.isComposing || e.keyCode === 229) return;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setIndex(i => Math.max(0, Math.min(results.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))); requestAnimationFrame(() => document.querySelector('.dock-search-results .selected')?.scrollIntoView({ block: 'nearest' })); }
      if (e.key === 'Enter' && query.trim() && !searchPending && results[index] && enterSubmit()) { e.preventDefault(); const hit = results[index]; void deliver(searchDeliveryKey(hit), 'insert', hit.kind, 13, searchUsage()); }
      if (e.key === 'Escape') { e.preventDefault(); clearSearch(); setSearchActive(false); }
    }} />{query && <button className="dock-search-clear" aria-label="清空底部搜索" onClick={() => { clearSearch(); searchRef.current?.focus(); }}>×</button>}</>}</div>}
    </div>
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
