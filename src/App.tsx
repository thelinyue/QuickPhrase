import { orderSearchResults, SearchResults } from './SearchResults';
import { cloudRenameDialog } from './cloudRename';
import { CompanyPickerWindow } from './CompanyPicker';
import { useSettingsSave } from './useSettingsSave';
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent, type ReactNode } from 'react';
import { listen } from '@tauri-apps/api/event';
import { Menu as NativeMenu, type MenuItemOptions } from '@tauri-apps/api/menu';
import { LogicalPosition } from '@tauri-apps/api/dpi';
import { confirm } from '@tauri-apps/plugin-dialog';
import { call, openWindow, useLibrary, useTeams, useTeamView } from './api';
import { categoryTextColor, CategoryPicker, Feedback, FormDialog, Icon, useEnterSubmit, type DialogSpec } from './ui';
import { PhraseRow } from './PhraseRow';
import { SearchHistoryConfirm } from './SearchHistoryConfirm';
import { SearchHistoryBar, useSearchHistory, type SearchUsage } from './SearchHistory';
import { dismissPhrasePreview, PhrasePreviewWindow } from './PhrasePreview';
import { ShortcutDialog } from './ShortcutDialog';
import { ShortcutCandidatesWindow } from './ShortcutCandidates';
import { Editor, FileEditor, TeamEditor } from './Editor';
import { TeamManagementWindow } from './TeamManagement';
import { CompanyFilePicker, CompanyFilesWindow } from './CompanyFiles';
import { UsageGuideWindow } from './UsageGuide';
import { useDefaultTeam } from './useDefaultTeam';
import { TeamSelector } from './TeamSelector';
import { useMembers } from './useMembers';
import { SettingsWindow } from './Settings';
import { UpdateBadge } from './Updater';
import { CategorySort, CommonDockWindow, CommonPhraseBar, CommonPhraseEditor, CommonPhraseSort, FileSort } from './CommonPhrases';
import { DeletionWindow, RecycleWindow } from './RecycleBin';
import { useNotices } from './notices';
import { useDeliveryProgress } from './useDeliveryProgress';
import { usePendingDeliveryNotices } from './usePendingDeliveryNotices';
import { categoryLabel, contentKey, searchHitKey, searchDeliveryKey, labelOf, type CompanyView, type Category, type DeliveryReport, type Library, type LinkedFile, type Phrase, type SearchHit, type TargetContext, type TeamView } from './types';

type MenuItem = { label: string; action: () => unknown; enabled?: boolean } | null;
type MainSetting = 'dock' | 'pinned' | 'showCommon' | 'showSearch';
interface Menu { x: number; y: number; bottom?: number; items: MenuItem[]; primary?: boolean; search?: boolean }
// 搜索结果的菜单属于同一次搜索交互，获得焦点不能卸载结果；其他菜单仍按离开搜索处理。
const searchInteractionSelector = '#search-bar,.search-history,.search-panel,[data-search-menu="true"]';
export function App() {
  const { library, error, refresh } = useLibrary(); const route = location.hash.slice(1).split('?')[0];
  const [guideError, setGuideError] = useState('');
  useEffect(() => {
    if (route) return;
    let disposed = false, opening = false, requested = false, stop: (() => void) | undefined;
    const check = async () => {
      // 状态读取期间的新唤起再读一轮，不能因旧快照尚未返回而丢掉首次展示请求。
      requested = true;
      if (disposed || opening) return; opening = true;
      try {
        do {
          requested = false;
          const state = await call<{ autoOpen: boolean }>('get_usage_guide_state');
          if (state.autoOpen && !disposed) await openWindow('usage-guide');
        } while (requested && !disposed);
      }
      catch (e) { if (!disposed) setGuideError(`使用指引暂时无法打开：${String(e)}`); }
      finally { opening = false; }
    };
    // 先订阅再读取快照，覆盖原生窗口先显示和页面先订阅两种顺序。
    void listen<string>('ui-action', e => { if (['launch', 'show', 'search', 'common'].includes(e.payload)) void check(); }).then(fn => { if (disposed) fn(); else { stop = fn; void check(); } }).catch(e => { if (!disposed) setGuideError(String(e)); });
    return () => { disposed = true; stop?.(); };
  }, [route]);
  const params = new URLSearchParams(location.hash.split('?')[1]);
  useEffect(() => {
    if (route === 'phrase-preview' || route === 'shortcut-candidates') return;
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.isComposing) dismissPhrasePreview(); };
    document.addEventListener('pointerdown', dismissPhrasePreview);
    document.addEventListener('scroll', dismissPhrasePreview, true);
    document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', dismissPhrasePreview); document.removeEventListener('scroll', dismissPhrasePreview, true); document.removeEventListener('keydown', escape); };
  }, [route]);
  useEffect(() => {
    document.documentElement.dataset.theme = library?.settings.theme || 'default';
    document.documentElement.style.setProperty('--phrase-size', `${library?.settings.fontSize || 14}px`);
  }, [library?.settings.theme, library?.settings.fontSize]);
  useEffect(() => {
    if (route === 'phrase-preview' || route === 'shortcut-candidates') return;
    // 失焦窗口保留的 DOM activeElement 不能覆盖另一窗口的输入状态；OS 切窗也重新同步。
    const updateInput = () => { const focused = document.hasFocus(); const modal = focused && !!document.querySelector('dialog[open],.context-menu,.team-dropdown'); const input = focused ? document.activeElement?.closest('input,textarea,select,[contenteditable="true"]') : null; const search = !modal && !!input?.hasAttribute('data-shortcut-search'); void call('set_shortcut_input', { active: modal || !!input, search }).catch(() => {}); };
    const changeFocus = () => queueMicrotask(updateInput);
    document.addEventListener('focusin', changeFocus); document.addEventListener('focusout', changeFocus);
    window.addEventListener('focus', changeFocus); window.addEventListener('blur', changeFocus);
    const observer = new MutationObserver(changeFocus); observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['open'] }); updateInput();
    return () => { observer.disconnect(); document.removeEventListener('focusin', changeFocus); document.removeEventListener('focusout', changeFocus); window.removeEventListener('focus', changeFocus); window.removeEventListener('blur', changeFocus); };
  }, [route]);
  if (route === 'search-history-confirm') return <SearchHistoryConfirm />;
  if (route === 'phrase-preview') return <PhrasePreviewWindow />;
  if (route === 'shortcut-candidates') return <ShortcutCandidatesWindow titleColor={library?.settings.titleColor} />;
  if (route === 'usage-guide') return <UsageGuideWindow settings={library?.settings} />;
  if (route === 'company-picker') return <CompanyPickerWindow />;
  if (!library) return <div className="loading-screen"><strong>闪语</strong><span>{error || '正在读取本地话术…'}</span>{error && <button className="secondary-button" onClick={() => void refresh()}>重试</button>}</div>;
  if (route === 'editor') { const props = { library, id: params.get('id') || '', afterId: params.get('afterId') || '', categoryId: params.get('categoryId') || '', groupId: params.get('groupId') || '' }; return params.get('teamId') ? <TeamEditor {...props} teamId={params.get('teamId')!} /> : <Editor {...props} />; }
  if (route === 'file-editor') return <FileEditor library={library} id={params.get('id') || ''} categoryId={params.get('categoryId') || ''} />;
  if (route === 'settings') return <SettingsWindow library={library} initialTab={params.get('tab') || ''} initialCategoryId={params.get('categoryId') || ''} initialTeamId={params.get('teamId') || ''} />;
  if (route === 'team-management') return <TeamManagementWindow mode="teams" tab={params.get('tab') || ''} teamId={params.get('teamId') || ''} />;
  if (route === 'user-management') return <TeamManagementWindow mode="users" />;
  if (route === 'company-files') return <CompanyFilesWindow />;
  if (route === 'common-editor') return <CommonPhraseEditor library={library} id={params.get('id') || ''} />;
  if (route === 'common-dock') return <CommonDockWindow library={library} />;
  if (route === 'common-sort') return <CommonPhraseSort library={library} />;
  if (route === 'category-sort') return <CategorySort library={library} parentId={params.get('categoryId') || null} kind={params.get('tab') === 'file' ? 'file' : 'phrase'} />;
  if (route === 'file-sort') return <FileSort library={library} categoryId={params.get('categoryId') || ''} />;
  if (route === 'recycle-bin') return <RecycleWindow library={library} initialTeamId={params.get('teamId') || ''} />;
  if (route === 'delete-confirm') return <DeletionWindow library={library} id={params.get('id') || ''} kind={params.get('tab') || 'phrase'} />;
  return <><MainWindow library={library} refresh={refresh} /><Feedback message={guideError} clear={() => setGuideError('')} /></>;
}

/** 吸附、置顶与面板开关统一保存设置；快捷键打开搜索时会先展开被隐藏的搜索栏。 */
export function MainWindow({ library: privateLibrary, refresh }: { library: Library; refresh: () => Promise<void> }) {
  const { status: teamStatus, version: teamVersion } = useTeams(); const [teamId, setTeamId] = useState('');
  const defaultTeam = useDefaultTeam(teamStatus);
  const { view: teamView, error: teamError } = useTeamView(teamId, teamStatus, teamVersion);
  const members = useMembers(teamStatus);
  const library = useMemo(() => {
    const files = privateLibrary.files.filter(f => !f.cloud || (f.cloud.server === teamStatus.server && f.cloud.userId === teamStatus.user?.id));
    return teamId ? { ...privateLibrary, files, categories: [...(teamView?.categories || []), ...privateLibrary.categories.filter(c => c.kind === 'file')], phrases: teamView?.phrases || [] } : { ...privateLibrary, files, categories: [...privateLibrary.categories, ...(members?.categories || [])], phrases: [...privateLibrary.phrases, ...(members?.phrases || [])] };
  }, [privateLibrary, teamId, teamView, members, teamStatus.server, teamStatus.user?.id]);
  const scopeCanEdit = !teamId || teamView?.team.editable === true;
  const writableCategory = (id: string) => scopeCanEdit && library.categories.some(c => c.id === id && !c.readOnly);
  const scopeMemory = useRef(new Map<string, { root: string; collapsed: Set<string>; scroll: number }>());
  const pendingScope = useRef<string | null>(null);
  const sequenceGeneration = useRef(Date.now() * 1000);
  const [kind, setKind] = useState<'phrase' | 'file'>('phrase'); const [root, setRoot] = useState(() => library.categories.find(c => c.kind === 'phrase' && !c.parentId)?.id || ''); const [fileCategory, setFileCategory] = useState(() => library.categories.find(c => c.kind === 'file')?.id || '');
  const [selected, setSelected] = useState(''); const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const canEdit = scopeCanEdit && !library.categories.find(c => c.id === root)?.readOnly;
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]); const fileAnchor = useRef('');
  const [query, setQuery] = useState(''); const [storedResults, setResults] = useState<SearchHit[]>([]); const [searchIndex, setSearchIndex] = useState(0); const [searching, setSearching] = useState(false);
  // 比较搜索实际使用的内容，忽略主题、同步时间等状态。身份与内容分开：同一来源更新可保留列表，换源不能保留。
  const searchScope = JSON.stringify([privateLibrary.libraryId, teamStatus.server, teamStatus.user?.id, teamStatus.sessionEpoch, teamStatus.user?.mustChangePassword, teamStatus.memberPhraseSync, members?.subscription, teamStatus.teams.map(t => [t.teamId, t.membershipVersion, t.lifecycleVersion])]);
  const teamContent = JSON.stringify(teamStatus.teams.map(t => [t.teamId, t.version]));
  const searchStamp = useMemo(() => JSON.stringify([privateLibrary.settings.searchMode, privateLibrary.phrases, privateLibrary.categories, privateLibrary.files, members?.phrases, members?.categories, teamContent]), [privateLibrary.settings.searchMode, privateLibrary.phrases, privateLibrary.categories, privateLibrary.files, members?.phrases, members?.categories, teamContent]);
  const [searchFailed, setSearchFailed] = useState(false);
  const [searchSource, setSearchSource] = useState<{ query: string; scope: string; source: string; valid: boolean } | null>(null);
  const results = searchSource?.query === query && searchSource.scope === searchScope ? storedResults : [];
  const searchSelection = useRef<SearchHit | undefined>(undefined); searchSelection.current = results[searchIndex];
  const composing = useRef(false); const [compositionVersion, endComposition] = useState(0); const searchRef = useRef<HTMLInputElement>(null); const libraryRef = useRef(library); libraryRef.current = library;
  const searchPending = searching || composing.current || searchSource?.query !== query || searchSource.scope !== searchScope || searchSource.source !== searchStamp || !searchSource.valid;
  const searchGuard = useRef({ query, scope: searchScope, source: searchStamp, pending: searchPending }); searchGuard.current = { query, scope: searchScope, source: searchStamp, pending: searchPending };
  const [menu, setMenu] = useState<Menu | null>(null); const [dialog, setDialog] = useState<DialogSpec | null>(null);
  const notices = useNotices(); const setMessage = notices.error;
  const deliveryProgress = useDeliveryProgress('main');
  const noticeScope = JSON.stringify([privateLibrary.libraryId, teamStatus.server, teamStatus.user?.id, teamStatus.sessionEpoch]);
  const noticeScopeRef = useRef(noticeScope); const previousNoticeScope = useRef(noticeScope); noticeScopeRef.current = noticeScope;
  useEffect(() => { if (previousNoticeScope.current !== noticeScope) { previousNoticeScope.current = noticeScope; notices.clear(); } }, [noticeScope]);
  const pendingDeliveries = usePendingDeliveryNotices(noticeScope, 'main');
  const pendingDelivery = pendingDeliveries.items[0];
  const showPending = !!pendingDelivery && (pendingDelivery.status === 'interrupted' || (!pendingDeliveries.problem && notices.current?.kind !== 'warning'));
  const showDeliveryProblem = !showPending && !!pendingDeliveries.problem && notices.current?.kind !== 'warning';
  const showProgress = !showPending && !showDeliveryProblem && !!deliveryProgress.waiting && (!notices.current || notices.current.kind === 'success' || notices.current.kind === 'progress');
  const activeNotice = showPending ? { message: pendingDelivery.message, kind: pendingDelivery.status === 'interrupted' ? 'warning' as const : 'error' as const, title: pendingDelivery.status === 'interrupted' ? '操作已中断，请先检查目标' : undefined } : showDeliveryProblem ? { message: pendingDeliveries.problem, kind: 'warning' as const, title: '请先检查目标，投递结果暂不可用' } : showProgress ? undefined : notices.current;
  // 选择云文件时固定账号、库和分类，后续切换不能把附件保存到另一个目标。
  const [cloudTarget, setCloudTarget] = useState<{ epoch: number; server: string; userId: string; libraryId?: string; teamId: string; lifecycle?: number; categoryId: string; groupId: string } | null>(null);
  const cloudAvailable = !!teamStatus.companyFiles && !!teamStatus.user && !teamStatus.offline && !teamStatus.user.mustChangePassword && scopeCanEdit && (!teamId || teamStatus.teams.some(t => t.teamId === teamId && t.editable));
  const cloudContext = useRef({ teamStatus, teamId, library, privateLibrary, cloudAvailable }); cloudContext.current = { teamStatus, teamId, library, privateLibrary, cloudAvailable };
  const menuTrigger = useRef<HTMLElement | null>(null);
  const history = useSearchHistory(privateLibrary.libraryId, setMessage);
  // 历史只在用户进入搜索区域后展示；窗口启动和后台记录刷新不能占用列表空间。
  const [historyActive, setHistoryActive] = useState(false); const [focusSearch, setFocusSearch] = useState(false);
  const [searchVersion, repeatSearch] = useState(0);
  const searchUsage = (): SearchUsage | undefined => privateLibrary.libraryId ? { query, libraryId: privateLibrary.libraryId } : undefined;
  const chooseHistory = (value: string) => { composing.current = false; setQuery(value); setResults([]); setSearchSource(null); setSearchIndex(0); setSearching(true); repeatSearch(v => v + 1); searchRef.current?.focus(); };
  const [shortcutPhrase, setShortcutPhrase] = useState<(Phrase & { shortcutEpoch?: number }) | null>(null);
  useEffect(() => { if (menu) (menu.primary ? document.querySelector<HTMLElement>('.context-menu') : document.querySelector<HTMLElement>('.context-menu button:not(:disabled)') || document.querySelector<HTMLElement>('.context-menu'))?.focus(); }, [menu]);
  useEffect(() => { setMenu(null); void nativeMenuRef.current?.close().catch(() => {}); }, [teamStatus.sessionEpoch, teamStatus.server, teamStatus.user?.id, teamStatus.offline, teamStatus.companyFiles, teamStatus.user?.mustChangePassword, scopeCanEdit, cloudAvailable, teamId, privateLibrary.libraryId]);
  const nativeMenuRef = useRef<NativeMenu | null>(null);
  useEffect(() => () => { void nativeMenuRef.current?.close().catch(() => {}); }, []);
  const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const settingsSave = useSettingsSave(privateLibrary.settings, privateLibrary.libraryId, privateLibrary.settingsRevision);
  const settings = settingsSave.settings;
  libraryRef.current = { ...library, settings };
  useEffect(() => { const error = Object.values(settingsSave.errors).filter(Boolean).join(' '); if (error) setMessage(`设置保存失败：${error}`); }, [settingsSave.errors]);
  const [focusCommon, setFocusCommon] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  useEffect(() => { scopeMemory.current.clear(); }, [teamStatus.server, teamStatus.user?.id]);
  const previousPrivateLibrary = useRef(privateLibrary.libraryId);
  useEffect(() => { if (previousPrivateLibrary.current === privateLibrary.libraryId) return; previousPrivateLibrary.current = privateLibrary.libraryId; setSelected(''); setQuery(''); setResults([]); setSearchSource(null); setRoot(''); scopeMemory.current.clear(); void call('sync_sequence_map', { ids: [], loading: true, generation: ++sequenceGeneration.current }); }, [privateLibrary.libraryId]);
  function changeScope(id: string) {
    scopeMemory.current.set(teamId, { root, collapsed: new Set(collapsed), scroll: listRef.current?.scrollTop || 0 });
    void call('sync_sequence_map', { ids: [], loading: true, generation: ++sequenceGeneration.current });
    setSelected(''); setQuery(''); setResults([]); setSearchSource(null); setMenu(null);
    const remembered = scopeMemory.current.get(id); setRoot(remembered?.root || ''); setCollapsed(remembered?.collapsed || new Set());
    pendingScope.current = id; setTeamId(id);
  }
  const scopeNavigation = useRef(changeScope); scopeNavigation.current = changeScope;
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined;
    void listen<{ teamId: string }>('team-scope-navigation', e => { setKind('phrase'); scopeNavigation.current(e.payload.teamId); }).then(fn => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; stop?.(); };
  }, []);
  useEffect(() => { if (teamId && !teamStatus.teams.some(t => t.teamId === teamId)) changeScope(''); }, [teamStatus]);
  useEffect(() => {
    if (pendingScope.current !== teamId || (teamId && !teamView)) return;
    pendingScope.current = null; const remembered = scopeMemory.current.get(teamId);
    setRoot(remembered && library.categories.some(c => c.id === remembered.root) ? remembered.root : library.categories.find(c => c.kind === 'phrase' && !c.parentId)?.id || '');
    setCollapsed(remembered?.collapsed || (privateLibrary.settings.expand ? new Set() : new Set(library.categories.filter(c => c.parentId).map(c => c.id))));
    requestAnimationFrame(() => { if (listRef.current) listRef.current.scrollTop = remembered?.scroll || 0; });
  }, [teamId, teamView, library.categories]);
  const enterSubmit = useEnterSubmit();
  const roots = library.categories.filter(c => c.kind === 'phrase' && !c.parentId); const fileCats = library.categories.filter(c => c.kind === 'file');
  const hasCategory = kind === 'phrase' ? roots.some(c => c.id === root) : fileCats.some(c => c.id === fileCategory);
  useEffect(() => { setSelectedFiles([]); fileAnchor.current = ''; }, [kind, fileCategory]);
  useEffect(() => { setSelectedFiles(previous => previous.filter(id => library.files.some(f => f.id === id))); }, [library.files]);
  useEffect(() => { if (!roots.some(c => c.id === root)) setRoot(roots[0]?.id || ''); if (!fileCats.some(c => c.id === fileCategory)) setFileCategory(fileCats[0]?.id || ''); }, [library.categories]);
  useEffect(() => { setCollapsed(settings.expand ? new Set() : new Set(library.categories.filter(c => c.parentId).map(c => c.id))); }, [settings.expand]);
  const run = (action: () => unknown) => { try { Promise.resolve(action()).catch(e => setMessage(String(e))); } catch (e) { setMessage(String(e)); } };
  const clearSearch = () => { setQuery(''); setResults([]); setSearchIndex(0); setSearching(false); };
  function changeSettings(key: MainSetting, value?: boolean) {
    void settingsSave.change({ [key]: value ?? !settings[key] });
  }
  useEffect(() => { if (!settings.showSearch) { clearSearch(); setHistoryActive(false); } }, [settings.showSearch]);
  useEffect(() => { if (focusSearch && settings.showSearch) { setHistoryActive(true); searchRef.current?.focus(); setFocusSearch(false); } }, [focusSearch, settings.showSearch]);
  useEffect(() => { const blur = () => setHistoryActive(false); window.addEventListener('blur', blur); return () => window.removeEventListener('blur', blur); }, []);
  useEffect(() => {
    if (focusCommon && settings.showCommon) {
      const selected = document.querySelector<HTMLElement>('.main-window [data-common][aria-selected="true"]');
      (selected || document.querySelector<HTMLElement>('.main-window [data-common],.main-window .add-common'))?.focus();
      setFocusCommon(false);
    }
  }, [focusCommon, settings.showCommon]);
  const deliver = useCallback(async (id: string, kind: string, mode: 'insert' | 'send', triggerKey?: number, usage?: SearchUsage) => {
    if (usage && (searchGuard.current.pending || searchGuard.current.query !== usage.query || searchGuard.current.scope !== searchScope || searchGuard.current.source !== searchStamp)) return;
    if (busyRef.current) return;
    busyRef.current = true; setBusy(true); const scope = noticeScopeRef.current;
    const visibleKey = kind === 'phrase' && !id.startsWith('team:') && !id.startsWith('private:') && !id.startsWith('member:') ? results.find(hit => hit.id === id)?.privateKey || (libraryRef.current.phrases.find(p => p.id === id) ? contentKey(libraryRef.current.phrases.find(p => p.id === id)!) : id) : id;
    try {
      const target = await call<TargetContext | null>('get_target');
      if (!target) throw new Error('请先聚焦目标输入区，再返回闪语使用话术。');
      const report = await call<DeliveryReport>('deliver_content', { id: visibleKey, kind, mode, target, ...(triggerKey ? { triggerKey } : {}) });
      if (scope !== noticeScopeRef.current) return;
      if (report.noticeStored) void pendingDeliveries.refresh();
      if (report.status === 'submitted') { notices.success(mode === 'send' ? `已向目标提交 ${report.completedSegments} 段话术` : '已向目标提交插入操作'); if (settings.exitSearch) clearSearch(); if (usage) await history.record(usage); }
      else if (!report.noticeStored && report.status === 'interrupted') notices.show(report.error || '请先检查目标输入区和已发送内容，已执行的内容不会自动重发。', 'warning', '操作已中断，请先检查目标');
      else if (!report.noticeStored) setMessage(report.error || '操作已停止');
    } catch (e) { if (scope === noticeScopeRef.current) setMessage(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }, [settings.exitSearch, results, history.record, searchScope, searchStamp]);
  useEffect(() => {
    let disposed = false; const unlisteners: (() => void)[] = [];
    const on = <T,>(name: string, fn: (payload: T) => void) => { void listen<T>(name, e => fn(e.payload)).then(un => disposed ? un() : unlisteners.push(un)); };
    on<string>('ui-action', action => { if (action === 'search') { setFocusSearch(true); if (!libraryRef.current.settings.showSearch) void changeSettings('showSearch', true); } if (action === 'show') { clearSearch(); setHistoryActive(false); listRef.current?.focus(); } if (action === 'common') { setHistoryActive(false); setKind('phrase'); setFocusCommon(true); if (!libraryRef.current.settings.showCommon) void changeSettings('showCommon', true); } if (action === 'settings') run(() => openWindow('settings')); });
    on<DeliveryReport>('shortcut-delivery', report => { if (report.noticeStored) void pendingDeliveries.refresh(); if (report.status === 'submitted') { if (libraryRef.current.settings.exitSearch) clearSearch(); } else if (!report.noticeStored && report.status === 'interrupted') notices.show(report.error || '请先检查目标输入区和已发送内容，已执行的内容不会自动重发。', 'warning', '快捷键操作已中断，请先检查目标'); else if (!report.noticeStored) setMessage(report.error || '快捷键操作已停止'); });
    void listen<string>('search-history-focus', e => { if (e.payload === libraryRef.current.libraryId) { setQuery(''); setFocusSearch(true); } }).then(un => disposed ? un() : unlisteners.push(un));
    // Windows 切换前台窗口时 WebView 可能仍报告 DOM 有焦点，以原生窗口失焦补齐收起。
    void listen('tauri://blur', () => setHistoryActive(false), { target: { kind: 'Window', label: 'main' } }).then(un => disposed ? un() : unlisteners.push(un));
    on<string>('native-error', setMessage); on<boolean>('delivery-busy', value => { busyRef.current = value; setBusy(value); });
    void call<string | null>('native_status').then(e => { if (e) setMessage(e); });
    return () => { disposed = true; unlisteners.forEach(un => un()); };
  }, []);
  useEffect(() => {
    if (!query.trim() || composing.current) { if (!query.trim()) setResults([]); return; }
    let active = true; setSearching(true); setSearchFailed(false);
    const timer = setTimeout(() => { void call<SearchHit[]>('search_scoped_content', { query, scope: 'all' }).then(hits => {
      if (!active) return;
      const found = orderSearchResults(hits);
      const selected = searchSelection.current;
      setSearchIndex(Math.max(0, found.findIndex(hit => !!selected && searchHitKey(hit) === searchHitKey(selected))));
      setResults(previous => JSON.stringify(previous) === JSON.stringify(found) ? previous : found);
      setSearchSource({ query, scope: searchScope, source: searchStamp, valid: true }); setSearching(false);
    }).catch(e => { if (active) { setSearchSource(previous => previous ? { ...previous, valid: false } : null); setMessage(String(e)); setSearching(false); setSearchFailed(true); } }); }, 100);
    return () => { active = false; clearTimeout(timer); };
  }, [query, searchScope, searchStamp, compositionVersion, searchVersion]);
  useEffect(() => { const close = (e: globalThis.MouseEvent) => { if (!(e.target as Element).closest('.context-menu')) setMenu(null); }; const escape = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.isComposing && !composing.current) { if (document.querySelector('.context-menu')) { setMenu(null); menuTrigger.current?.focus(); } else { clearSearch(); setHistoryActive(false); } } }; const blur = () => setMenu(null); window.addEventListener('blur', blur); document.addEventListener('pointerdown', close); document.addEventListener('keydown', escape); return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape); window.removeEventListener('blur', blur); }; }, []);
  const popup = (items: MenuItem[], x: number, y: number, primary = false, bottom?: number, search = false) => { menuTrigger.current = document.activeElement as HTMLElement; setMenu({ items, primary, bottom, search, x: Math.max(2, Math.min(x, innerWidth - (primary ? 178 : 190))), y: Math.max(2, Math.min(y, innerHeight - items.reduce((n, i) => n + (i ? 32 : 7), 4))) }); };
  // 原生分类菜单可展开到主窗口外，右键动作始终绑定被点击分类，而不是后来切换的选中项。
  async function categoryPopup(items: MenuItemOptions[], e: MouseEvent) {
    const position = new LogicalPosition(e.clientX, e.clientY); setMenu(null);
    await nativeMenuRef.current?.close();
    const next = await NativeMenu.new({ items }); nativeMenuRef.current = next;
    await next.popup(position);
  }
  const newContent = () => { if (kind === 'phrase' && !writableCategory(root)) return; return openWindow(kind === 'phrase' ? 'editor' : 'file-editor', '', '', '', kind === 'phrase' ? root : fileCategory, '', kind === 'phrase' ? teamId : ''); };
  const chooseCategory = (c: Category) => c.kind === 'phrase' ? setRoot(c.parentId || c.id) : setFileCategory(c.id);
  const newPhrase = (c: Category) => { if (!writableCategory(c.id)) return; return openWindow('editor', '', '', '', c.parentId || c.id, c.parentId ? c.id : '', teamId); };
  /** 空白区域按标题的视口位置归属，折叠不改变边界；菜单打开时捕获分类，避免后续布局改变目标。 */
  function listMenu(e: MouseEvent<HTMLDivElement>) {
    if ((e.target as Element).closest('.content-row,.group-heading')) return;
    e.preventDefault(); e.currentTarget.focus();
    let groupId = '';
    if (kind === 'phrase') for (const heading of e.currentTarget.querySelectorAll<HTMLElement>('[data-group-id]')) {
      if (heading.getBoundingClientRect().top > e.clientY) break;
      groupId = heading.dataset.groupId || '';
    }
    popup(kind === 'phrase' ? [
      { label: '新增话术', enabled: hasCategory && canEdit, action: () => openWindow('editor', '', '', '', root, groupId, teamId) },
      ...cloudItems(hasCategory ? root : '', groupId),
      { label: '新增二级分类', enabled: hasCategory && canEdit, action: () => categoryDialog(undefined, root, 'phrase') },
    ] : [{ label: '关联文件', enabled: hasCategory, action: () => openWindow('file-editor', '', '', '', fileCategory) }, { label: '刷新', action: refresh }], e.clientX, e.clientY);
  }
  function validCloudTarget(target: NonNullable<typeof cloudTarget>) {
    const current = cloudContext.current, status = current.teamStatus;
    return current.cloudAvailable && current.teamId === target.teamId && current.privateLibrary.libraryId === target.libraryId && status.server === target.server && status.user?.id === target.userId && (status.sessionEpoch || 0) === target.epoch
      && (!target.teamId || status.teams.some(t => t.teamId === target.teamId && t.editable && t.lifecycleVersion === target.lifecycle))
      && current.library.categories.some(c => c.kind === 'phrase' && !c.parentId && !c.readOnly && c.id === target.categoryId)
      && (!target.groupId || current.library.categories.some(c => c.kind === 'phrase' && c.parentId === target.categoryId && c.id === target.groupId));
  }
  function newCloudFile(categoryId: string, groupId = '') {
    const target = { epoch: teamStatus.sessionEpoch || 0, server: teamStatus.server, userId: teamStatus.user?.id || '', libraryId: privateLibrary.libraryId, teamId, lifecycle: teamStatus.teams.find(t => t.teamId === teamId)?.lifecycleVersion, categoryId, groupId };
    if (!validCloudTarget(target)) { setMessage('账号、分类或编辑权限已变化，请重新选择。'); return; }
    setCloudTarget(target);
  }
  const cloudItems = (categoryId: string, groupId = ''): MenuItem[] => teamStatus.companyFiles ? [{ label: '从云文件新建话术', enabled: cloudAvailable && writableCategory(categoryId), action: () => newCloudFile(categoryId, groupId) }] : [];
  useEffect(() => { if (cloudTarget && !validCloudTarget(cloudTarget)) { setCloudTarget(null); setMessage('账号、分类或编辑权限已变化，请重新选择云文件。'); } }, [cloudTarget, teamStatus, teamId, privateLibrary.libraryId, library.categories, cloudAvailable]);
  function categoryDialog(c?: Category, parentId: string | null = null, categoryKind = kind) { if (categoryKind === 'phrase' && (!scopeCanEdit || c?.readOnly || (parentId && !writableCategory(parentId)))) return; setDialog({ title: c ? '重命名' : parentId ? '新增二级分类' : '新增一级分类', fields: <label>分类名称<input name="name" type="text" defaultValue={c?.name || ''} maxLength={40} required autoFocus /></label>, onSubmit: async form => { const category = c ? { ...c, name: String(form.get('name')) } : { id: '', name: String(form.get('name')), kind: categoryKind, parentId, position: library.categories.length, background: '' }; if (teamId && categoryKind === 'phrase') { await call('save_team_category', { teamId, expectedVersion: teamView!.team.version, expectedLifecycleVersion: teamView!.team.lifecycleVersion, category }); return; } const id = await call<string>('save_category', { category }); if (!c && !parentId) { if (categoryKind === 'phrase') setRoot(id); else setFileCategory(id); } await refresh(); } }); }
  function sortDialog(id: string, kind: string, current: number, count: number) { setDialog({ title: '排序', fields: <label>分类中的位置<input type="number" name="position" min={1} max={Math.max(1, count)} defaultValue={current} required /></label>, onSubmit: async f => { await call('sort_item', { id, kind, position: Number(f.get('position')) }); await refresh(); } }); }
  async function deleteCategory(c: Category) {
    if (teamId && c.kind === 'phrase') { if (await confirm(`将「${c.name}」及其中的话术移入「${teamView?.team.name}」回收站？`, { title: '删除分类', kind: 'warning' })) await call('delete_team_content', { teamId, request: { expectedVersion: teamView!.team.version, expectedLifecycleVersion: teamView!.team.lifecycleVersion, kind: 'category', id: c.id } }); return; }
    if (c.kind === 'phrase') { await openWindow('delete-confirm', c.id, '', 'category'); return; }
    if (await confirm(`删除「${c.name}」及其中的文件关联记录？原文件会保留。`, { title: '删除分类', kind: 'warning' })) {
      await call('delete_category', { id: c.id }); await refresh();
    }
  }
  function keepCategoryPhrases(c: Category) {
    if (c.parentId) {
      run(async () => { if (await confirm(`删除「${c.name}」，其中的话术移至原一级分类，不保留此分组。`, { title: '删除但保留话术', kind: 'warning' })) { await call('delete_category', { id: c.id, keepPhrases: true }); await refresh(); } });
    } else {
      const targets = roots.filter(v => !v.readOnly && v.id !== c.id);
      setDialog({ title: '删除但保留话术', submit: '删除分类并保留话术', fields: <><p>删除「{c.name}」及二级分类，所有话术移至所选一级分类，不保留分组。</p><label>保留话术到<select name="target" required>{targets.map(v => <option key={v.id} value={v.id}>{categoryLabel(v, library.categories)}</option>)}</select></label></>, onSubmit: async f => { const targetId = String(f.get('target')); await call('delete_category', { id: c.id, keepPhrases: true, targetId }); setRoot(targetId); await refresh(); } });
    }
  }
  function transferCategory(c: Category, copy: boolean) {
    const targets = roots.filter(v => !v.readOnly && v.id !== (c.parentId || c.id));
    setDialog({ title: copy ? '复制到' : '移动到', fields: <><p>将「{c.name}」的话术和二级分组{copy ? '复制' : '移动'}到目标一级分类，同名二级分组合并到列表中第一组。{copy ? '复制的话术不保留快捷键。' : '完成后移除来源分类，保留话术快捷键。'}</p><label>目标一级分类<select name="target" required>{targets.map(v => <option key={v.id} value={v.id}>{categoryLabel(v, library.categories)}</option>)}</select></label></>, onSubmit: async f => { const targetId = String(f.get('target')); const count = await call<number>('move_copy_category', { id: c.id, targetId, copy }); if (!copy) setRoot(targetId); await refresh(); notices.success(`已${copy ? '复制' : '移动'}${count}条话术`); } });
  }
  const categorySort = (parentId: string | null, categoryKind: 'phrase' | 'file' = 'phrase') => openWindow('category-sort', '', '', categoryKind, parentId || '');
  function categoryMenu(c: Category, e: MouseEvent) {
    (e.currentTarget as HTMLElement).focus(); menuTrigger.current = e.currentTarget as HTMLElement;
    if (c.readOnly) { e.preventDefault(); e.stopPropagation(); chooseCategory(c); popup(readOnlyCategoryItems(c), e.clientX, e.clientY); return; }
    if (teamId && c.kind === 'phrase') { e.preventDefault(); popup([{ label: '新增话术', enabled: canEdit, action: () => newPhrase(c) }, ...cloudItems(c.parentId || c.id, c.parentId ? c.id : ''), { label: '重命名', enabled: canEdit, action: () => categoryDialog(c) }, { label: '移入回收站', enabled: canEdit, action: () => deleteCategory(c) }], e.clientX, e.clientY); return; }
    e.preventDefault(); e.stopPropagation(); chooseCategory(c);
    if (c.kind === 'phrase' && !c.parentId) { showPrimaryCategoryMenu(c, e); return; }
    if (c.kind === 'file') { popup([
      { label: '关联文件', action: () => openWindow('file-editor', '', '', '', c.id) },
      { label: '新增分类', action: () => categoryDialog(undefined, null, 'file') },
      { label: '重命名', action: () => categoryDialog(c) },
      { label: '删除', action: () => deleteCategory(c) },
      { label: '快速排序', enabled: fileCats.length > 1, action: () => categorySort(null, 'file') },
    ], e.clientX, e.clientY); return; }
    const targets = roots.filter(v => !v.readOnly && v.id !== (c.parentId || c.id));
    const items: MenuItemOptions[] = [
      { text: '新增话术', action: () => run(() => newPhrase(c)) },
      ...(teamStatus.companyFiles ? [{ text: '从云文件新建话术', enabled: cloudAvailable, action: () => newCloudFile(c.parentId || c.id, c.parentId ? c.id : '') }] : []),
      { text: '新增同级分类', action: () => categoryDialog(undefined, c.parentId, c.kind) },
      { text: '重命名', action: () => categoryDialog(c) },
      { text: '删除', action: () => run(() => deleteCategory(c)) },
      { text: '删除但保留话术', action: () => keepCategoryPhrases(c) },
      { text: '复制到', enabled: targets.length > 0, action: () => transferCategory(c, true) },
      { text: '移动到', enabled: targets.length > 0, action: () => transferCategory(c, false) },
      { text: '快速排序二级分类', enabled: library.categories.filter(v => v.parentId === (c.parentId || c.id)).length > 1, action: () => run(() => categorySort(c.parentId || c.id)) },
    ];
    run(() => categoryPopup(items, e));
  }
  async function phraseMenu(p: Phrase, e: MouseEvent, isSearch = false) { e.preventDefault(); (e.currentTarget as HTMLElement).focus(); setSelected(p.id); const usage = isSearch ? searchUsage() : undefined; const followed = p.contentRef?.source === 'member';
    const guard = { ...searchGuard.current };
    const sourceTeam = p.contentRef?.source === 'team' ? p.contentRef.teamId : '';
    let sourceView: TeamView | null = sourceTeam && sourceTeam === teamId ? teamView : null;
    if (sourceTeam && !sourceView) { try { sourceView = await call<TeamView>('get_team_view', { teamId: sourceTeam }); } catch (error) { setMessage(String(error)); return; } }
    if (searchGuard.current.scope !== guard.scope || (isSearch && (searchGuard.current.pending || searchGuard.current.query !== guard.query || searchGuard.current.source !== guard.source))) return;
    const canEdit = !!sourceView?.team.editable && !teamStatus.offline;
    const library = privateLibrary;
    popup([
    { label: '添加到输入区（双击）', action: () => deliver(contentKey(p), 'phrase', 'insert', undefined, usage) }, { label: '直接发送', action: () => deliver(contentKey(p), 'phrase', 'send', undefined, usage) },
    { label: '复制正文到剪贴板', action: () => call('copy_phrase_text', { id: contentKey(p) }) }, null,
    ...(followed ? [
      { label: '自定义快捷键', action: () => setShortcutPhrase({ ...p, shortcutEpoch: teamStatus.sessionEpoch }) },
      { label: '下载全部素材', enabled: !!teamStatus.user && !teamStatus.offline, action: () => call('download_member_materials', { ids: [contentKey(p)], attachments: true }) },
      { label: '分类只读，由来源成员维护', enabled: false, action: () => {} },
    ] : sourceTeam ? [
      { label: '下载全部素材', action: () => call('prepare_team_phrase', { id: p.id }) },
      { label: '复制到个人话术', action: async () => { if (await confirm(`将「${labelOf(p)}」及其图片、文件复制到个人库？副本之后独立维护。`, { title: '复制到个人话术' })) await call('copy_team_to_private', { id: p.id }); } },
      { label: '新增话术', enabled: canEdit, action: () => openWindow('editor', '', '', '', p.rootId, p.groupId || '', sourceTeam) }, ...(isSearch ? [] : cloudItems(p.rootId, p.groupId || '')),
      { label: '编辑', enabled: canEdit, action: () => openWindow('editor', p.id, '', '', '', '', sourceTeam) },
      { label: '移入回收站', enabled: canEdit, action: async () => { if (sourceView?.phrases.find(item => item.contentRef?.id === p.contentRef?.id)?.contentRef?.revision !== p.contentRef?.revision) throw new Error('话术已更新，请重新搜索后操作。'); if (await confirm(`将「${labelOf(p)}」移入「${sourceView?.team.name}」的回收站？`, { title: '删除话术', kind: 'warning' })) await call('delete_team_content', { teamId: sourceTeam, request: { expectedVersion: sourceView!.team.version, expectedLifecycleVersion: sourceView!.team.lifecycleVersion, kind: 'phrase', id: p.contentRef!.id } }); } },
    ] : [
    ...(p.ready === false ? [{ label: '下载素材', enabled: !!teamStatus.user && !teamStatus.offline, action: async () => { await call('download_private_materials', { ids: [p.id], attachments: true }); notices.success('下载中，完成后重新投递'); } }] : []),
    { label: '复制到共享话术', enabled: teamStatus.teams.some(t => t.editable), action: () => publishToTeam(p.id, 'phrase') },
    { label: '新增话术', action: () => openWindow('editor', '', '', '', p.rootId, p.groupId || '', '') }, ...(isSearch ? [] : cloudItems(p.rootId, p.groupId || '')), { label: '插入一条话术', action: () => openWindow('editor', '', p.id) }, { label: '编辑', action: () => openWindow('editor', p.id) },
    { label: '标题背景色', action: () => setDialog({ title: '标题背景色', fields: <><label>选择颜色<input name="color" type="color" defaultValue={p.background || '#fff2bc'} /></label><label><span><input name="reset" type="checkbox" />恢复默认背景</span></label></>, onSubmit: async f => { await call('save_phrase', { phrase: { ...p, background: f.has('reset') ? '' : String(f.get('color')) }, afterId: null }); } }) },
    { label: '自定义快捷键', action: () => setShortcutPhrase(p) },
    { label: '排序', action: () => { const siblings = library.phrases.filter(v => v.rootId === p.rootId && v.groupId === p.groupId); sortDialog(p.id, 'phrase', siblings.findIndex(v => v.id === p.id) + 1, siblings.length); } },
    ...[false, true].map(copy => ({ label: copy ? '复制到其他分类' : '移动到其他分类', action: () => setDialog({ title: copy ? '复制话术' : '移动话术', fields: <CategoryPicker categories={library.categories.filter(c => !c.readOnly)} rootId={p.rootId} groupId={p.groupId} />, onSubmit: async f => { await call('move_copy_phrase', { id: p.id, rootId: f.get('root'), groupId: f.get('group') || null, copy }); await refresh(); } }) })),
    { label: '删除', action: () => openWindow('delete-confirm', p.id, '', 'phrase') },
    ]),
  ], e.clientX, e.clientY, false, undefined, isSearch); }
  function publishToTeam(id: string, contentKind: 'phrase' | 'category') {
    const phrases = privateLibrary.phrases.filter(p => contentKind === 'phrase' ? p.id === id : p.rootId === id);
    setDialog({ title: '复制到共享话术', submit: '确认复制', fields: <><p>将{contentKind === 'phrase' ? `「${labelOf(phrases[0])}」` : `该一级分类的 ${phrases.length} 条话术`}连同图片、附件复制到目标共享话术，新建独立分类。成员将可以看到这些内容。</p><label>目标共享话术<select name="team" required>{teamStatus.teams.filter(t => t.editable).map(t => <option key={t.teamId} value={t.teamId}>{t.name}</option>)}</select></label><div className="publish-preview">{phrases.slice(0, 20).map(p => <p key={p.id}>{labelOf(p)} · 图片 {p.segments.flatMap(s => s.blocks).filter(b => b.kind === 'image').length} · 文件 {p.segments.flatMap(s => s.blocks).filter(b => b.kind === 'file').length}</p>)}</div></>, onSubmit: async f => { await call('publish_private_to_team', { teamId: String(f.get('team')), id, kind: contentKind }); notices.success('已复制到共享话术，私人原件独立保留'); } });
  }
  /** 文件右键绑定点击时的主键集合；多选复制、移动与移除均由 Rust 事务处理。 */
  function fileMenu(f: LinkedFile, e: MouseEvent, isSearch = false) {
    e.preventDefault(); (e.currentTarget as HTMLElement).focus(); const ids = !query && selectedFiles.includes(f.id) ? selectedFiles : [f.id]; setSelectedFiles(ids); setSelected(f.id);
    const single = ids.length === 1; const targets = fileCats.filter(c => c.id !== f.categoryId);
    const usage = isSearch ? searchUsage() : undefined;
    popup([
      { label: '发到输入区（双击）', enabled: single && !busy && !f.missing, action: () => deliver(f.id, 'file', 'insert', undefined, usage) },
      { label: '直接发送', enabled: single && !busy && !f.missing, action: () => deliver(f.id, 'file', 'send', undefined, usage) },
      { label: '复制文件', enabled: !busy, action: async () => { await call('copy_linked_files', { ids }); notices.success(`已复制${ids.length}个文件，可在目标程序粘贴`); } }, null,
      { label: '新增', action: () => openWindow('file-editor', '', '', '', f.categoryId) },
      { label: '修改显示名', enabled: single, action: () => setDialog({ title: '修改显示名', fields: <><label>显示名<input type="text" name="name" defaultValue={f.name} maxLength={200} required autoFocus /></label><p>只修改列表显示名，源文件名称保持不变；云端名称也不会修改。</p></>, onSubmit: async form => { await call('rename_linked_file', { id: f.id, name: String(form.get('name')) }); } }) },
      ...(f.cloud && f.canRename ? [{ label: '重命名云文件', enabled: single, action: async () => {
        const epoch = teamStatus.sessionEpoch || 0, view = await call<CompanyView>('get_company_files', { sessionEpoch: epoch, recycled: false, query: '' });
        const file = view.files.find(item => item.assetId === f.cloud!.assetId); if (!file) throw new Error('云文件已不可用，请刷新。');
        setDialog(cloudRenameDialog(file, epoch, refresh));
      } }] : []),
      { label: '移除关联', action: async () => { if (await confirm(`确定移除${ids.length > 1 ? `选中的${ids.length}个文件` : `「${f.name}」`}的关联？源文件会保留。`, { title: '移除文件关联', kind: 'warning' })) await call('remove_linked_files', { ids }); } }, null,
      { label: '快速排序文件', enabled: library.files.filter(v => v.categoryId === f.categoryId).length > 1, action: () => openWindow('file-sort', '', '', '', f.categoryId) },
      { label: '移动到其它分类', enabled: targets.length > 0, action: () => setDialog({ title: '移动到其它分类', fields: <><p>移动{ids.length}条文件关联，源文件路径保持不变。</p><label>目标分类<select name="target" required>{targets.map(c => <option key={c.id} value={c.id}>{categoryLabel(c, library.categories)}</option>)}</select></label></>, onSubmit: async form => { const targetId = String(form.get('target')); await call('move_linked_files', { ids, categoryId: targetId }); setFileCategory(targetId); } }) },
      { label: '打开文件', enabled: single && !f.missing, action: () => call('open_linked_file', { id: f.id }) },
      { label: '打开文件位置', enabled: single, action: () => call('open_linked_file', { id: f.id, reveal: true }) },
      { label: '重新关联文件', enabled: single, action: () => openWindow('file-editor', f.id) },
      { label: '刷新文件关联状态', action: refresh }, null,
      { label: '如何批量操作？', action: () => setDialog({ title: '文件批量操作', submit: '知道了', fields: <><p>按住 Ctrl 单击可多选，Shift 单击可选择连续文件，Ctrl+A 选择当前分类全部文件。</p><p>右键可批量复制、移动分类或移除关联。发送、修改显示名和打开文件使用单项选择。</p><p>新增窗口可一次关联多个文件；快速排序支持 Ctrl 多选拖动，确定后保存。</p><p>所有关联管理操作均保留磁盘上的源文件。</p></>, onSubmit: async () => {} }) },
    ], e.clientX, e.clientY, false, undefined, isSearch);
  }
  function selectFile(f: LinkedFile, e: MouseEvent) {
    const siblings = library.files.filter(v => v.categoryId === f.categoryId).map(v => v.id);
    if (e.shiftKey && siblings.includes(fileAnchor.current)) { const a = siblings.indexOf(fileAnchor.current), b = siblings.indexOf(f.id); setSelectedFiles(siblings.slice(Math.min(a, b), Math.max(a, b) + 1)); }
    else if (e.ctrlKey || e.metaKey) { setSelectedFiles(previous => previous.includes(f.id) ? previous.filter(id => id !== f.id) : [...previous, f.id]); fileAnchor.current = f.id; }
    else { setSelectedFiles([f.id]); fileAnchor.current = f.id; }
    setSelected(f.id);
  }
  function exportCategory(c: Category) { void openWindow('settings', '', '', '导出数据', c.id, '', teamId); }
  const managementMenu = () => [
    { label: '账号', action: () => openWindow('settings', '', '', '账号') },
    ...(teamStatus.companyFiles ? [{ label: '云文件', enabled: !!teamStatus.user && !teamStatus.user.mustChangePassword, action: () => openWindow('company-files') }] : []),
    ...(teamStatus.user?.admin ? [{ label: '账号管理', enabled: !teamStatus.offline && !teamStatus.user.mustChangePassword, action: () => openWindow('user-management') }] : []),
    ...(teamStatus.user ? [{ label: '共享话术管理', enabled: !teamStatus.offline && !teamStatus.user.mustChangePassword, action: () => openWindow('team-management', '', '', 'teams', '', '', teamId) }] : []),
  ];
  const readOnlyCategoryItems = (c: Category): MenuItem[] => [
    { label: `来自${c.sourceLabel} · 分类只读`, enabled: false, action: () => {} },
    { label: '成员话术', action: () => openWindow('settings', '', '', '账号') },
    { label: '新增本人一级分类', action: () => categoryDialog(undefined, null, 'phrase') },
  ];
  function showPrimaryCategoryMenu(c: Category | undefined, e: MouseEvent) {
    // 分类菜单只承载分类相关操作，全局账号和管理入口集中在主菜单。
    (e.currentTarget as HTMLElement).focus();
    if (c?.readOnly) { popup(readOnlyCategoryItems(c), e.clientX, e.clientY); return; }
    if (teamId) { popup([
      { label: '新增话术', enabled: canEdit && !!c, action: () => c && newPhrase(c) }, ...cloudItems(c?.id || ''), { label: '新增一级分类', enabled: canEdit, action: () => categoryDialog() }, { label: '新增二级分类', enabled: canEdit && !!c, action: () => c && categoryDialog(undefined, c.id, 'phrase') },
      { label: '重命名', enabled: canEdit && !!c, action: () => c && categoryDialog(c) }, { label: '移入回收站', enabled: canEdit && !!c, action: () => c && deleteCategory(c) },
      { label: '回收站', enabled: canEdit, action: () => openWindow('recycle-bin', '', '', '', '', '', teamId) },
      { label: '导入数据', enabled: canEdit, action: () => openWindow('settings', '', '', '导入数据', '', '', teamId) }, { label: '导出数据', action: () => openWindow('settings', '', '', '导出数据', c?.id || '', '', teamId) },
    ], e.clientX, e.clientY); return; }
    const transfer = roots.filter(c => !c.readOnly).length > 1;
    const anchor = e.currentTarget.getBoundingClientRect();
    popup([
      { label: '新增话术', enabled: !!c, action: () => c && newPhrase(c) }, ...cloudItems(c?.id || ''),
      { label: '新增二级分类', enabled: !!c, action: () => c && categoryDialog(undefined, c.id, 'phrase') },
      { label: '新增同级分类', action: () => categoryDialog(undefined, null, 'phrase') },
      { label: '重命名', enabled: !!c, action: () => c && categoryDialog(c) },
      { label: '删除', enabled: !!c, action: () => c && deleteCategory(c) },
      { label: '快速排序一级分类', enabled: roots.filter(c => !c.readOnly).length > 1, action: () => categorySort(null) },
      { label: '复制到', enabled: !!c && transfer, action: () => c && transferCategory(c, true) },
      { label: '移动到', enabled: !!c && transfer, action: () => c && transferCategory(c, false) },
      { label: '导出该一级分类的话术', enabled: !!c, action: () => c && exportCategory(c) },
      { label: '复制到共享话术', enabled: !!c && teamStatus.teams.some(t => t.editable), action: () => c && publishToTeam(c.id, 'category') },
      { label: '设置一级分类背景色', enabled: !!c, action: () => c && openWindow('settings', '', '', '界面设置', c.id) },
      { label: '设置一级分类区域高度', action: () => setDialog({ title: '设置一级分类区域高度', fields: <><label>最小高度（31—200像素）<input name="height" type="number" min={31} max={200} defaultValue={settings.categoryHeight} required autoFocus /></label><p>一级分类自动换行，超过200像素可滚动。</p></>, onSubmit: async f => { await call('patch_settings', { patch: { categoryHeight: Number(f.get('height')) } }); } }) },
    ], e.clientX || anchor.left, e.clientY || anchor.bottom, true);
  }
  const topCategoryMenu = (e: MouseEvent) => showPrimaryCategoryMenu(roots.find(v => v.id === root), e);
  function mainMenu(e: MouseEvent) {
    // 左下菜单固定在触发按钮上方；小窗口缩短可见高度并滚动，始终露出工具栏。
    const anchor = e.currentTarget.getBoundingClientRect();
    popup([
    ...(kind === 'file' ? [{ label: '新增关联文件', action: newContent, enabled: hasCategory }, { label: '刷新文件关联状态', action: refresh }, null] : []),
    ...['常用设置', '界面设置', '吸附设置', '导入数据', '导出数据', '快捷键', '缓存管理', '关于'].map(tab => ({ label: tab, action: () => openWindow('settings', '', '', tab, kind === 'phrase' ? root : fileCategory, '', kind === 'phrase' ? teamId : '') })), null,
    { label: '使用指引', action: () => openWindow('usage-guide') }, null,
    { label: '回收站', action: () => openWindow('recycle-bin', '', '', '', '', '', teamId) }, null, ...managementMenu(),
  ], anchor.left, anchor.top, false, innerHeight - anchor.top + 4); }
  const row = (p: Phrase | null, f: LinkedFile | null, index: number, isSearch = false, hit?: SearchHit) => {
    const id = (p?.id || f?.id)!;
    return <PhraseRow key={hit ? searchHitKey(hit) : id} phrase={p || undefined} file={f || undefined} index={index} settings={settings} selected={isSearch ? index === searchIndex : f ? selectedFiles.includes(id) : selected === id} query={isSearch ? query : undefined} busy={busy || (isSearch && searchPending)}
      onSelect={e => { if (f && !isSearch) selectFile(f, e); else setSelected(id); if (isSearch) setSearchIndex(index); }} onDeliver={(mode, triggerKey) => { if (isSearch && searchGuard.current.pending) return; void deliver(p ? hit ? searchDeliveryKey(hit) : contentKey(p) : id, f ? 'file' : 'phrase', mode, triggerKey, isSearch ? searchUsage() : undefined); }} onContext={e => { if (isSearch && searchPending) { e.preventDefault(); return; } p ? phraseMenu(p, e, isSearch) : fileMenu(f!, e, isSearch); }} />;
  };
  let sequence = 0; const content: ReactNode[] = []; const visible: { id: string; kind: 'phrase' | 'file'; privateKey?: string }[] = [];
  const addRow = (p: Phrase | null, f: LinkedFile | null) => { visible.push({ id: (p?.id || f?.id)!, kind: p ? 'phrase' : 'file', privateKey: p ? contentKey(p) : undefined }); content.push(row(p, f, sequence++)); };
  if (kind === 'phrase') {
    library.phrases.filter(p => p.rootId === root && !p.groupId).forEach(p => addRow(p, null));
    library.categories.filter(c => c.parentId === root).forEach(c => {
      const isCollapsed = collapsed.has(c.id); content.push(<div className={`group-heading${isCollapsed ? ' collapsed' : ''}`} key={c.id} data-group-id={c.id} tabIndex={-1} onContextMenu={e => categoryMenu(c, e)}><button className="group-toggle" aria-expanded={!isCollapsed} onClick={() => setCollapsed(previous => { const next = new Set(previous); if (next.has(c.id)) next.delete(c.id); else next.add(c.id); return next; })}><span className="group-arrow" />{c.name}</button>{writableCategory(c.id) && <button className="group-edit" aria-label={`编辑分类 ${c.name}`} onClick={() => categoryDialog(c)}><Icon name="pencil" /></button>}</div>);
      const phrases = library.phrases.filter(p => p.rootId === root && p.groupId === c.id); if (!isCollapsed) phrases.forEach(p => addRow(p, null));
    });
  } else library.files.filter(f => f.categoryId === fileCategory).forEach(f => addRow(null, f));
  const sequenceIds = JSON.stringify((settings.showSearch && historyActive && query.trim() ? results.map(hit => hit.kind === 'phrase' ? searchDeliveryKey(hit) : null) : visible.map(item => item.kind === 'phrase' ? item.privateKey || item.id : null)).slice(0, 10));
  const sequenceLoading = !!(teamId && !teamView) || pendingScope.current !== null || !!(settings.showSearch && historyActive && query.trim() && searchPending);
  useEffect(() => { void call('sync_sequence_map', { ids: JSON.parse(sequenceIds), loading: sequenceLoading, generation: ++sequenceGeneration.current }).catch(e => setMessage(String(e))); }, [sequenceIds, sequenceLoading]);
  useEffect(() => { if (!teamId || !teamView) return; const ids = visible.filter(v => v.kind === 'phrase').map(v => v.id); const visibleRows = [...(listRef.current?.querySelectorAll<HTMLElement>('[data-row]') || [])].filter(e => { const rect = e.getBoundingClientRect(); return rect.bottom > 0 && rect.top < innerHeight; }).map(e => e.dataset.row!); void call('preload_team_images', { teamId, ids: [...visibleRows, ...ids.filter(id => !visibleRows.includes(id))] }).catch(() => {}); }, [teamId, root, collapsed, sequenceIds]);
  useEffect(() => {
    if (teamId || teamStatus.offline) return;
    const ids = [...new Set([...(settings.showSearch && query.trim() ? results.filter(h => h.phrase?.contentRef?.source === 'member').map(h => h.id) : []), ...visible.filter(v => v.id.startsWith('member:')).map(v => v.id)])];
    if (ids.length) void call('download_member_materials', { ids, attachments: false }).catch(() => {});
  }, [teamId, root, collapsed, sequenceIds, teamStatus.offline]);
  useEffect(() => { if (teamId || !privateLibrary.libraryId) return; const visibleRows = [...(listRef.current?.querySelectorAll<HTMLElement>('[data-row]') || [])].filter(e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight; }).map(e => e.dataset.row!); const visibleIds = [...visibleRows, ...visible.filter(item => item.kind === 'phrase').map(item => item.id)]; const background = privateLibrary.phrases.filter(p => p.rootId === root).map(p => p.id); void call('download_private_materials', { ids: [...new Set([...visibleIds, ...background])].filter(id => !id.startsWith('member:')) }).catch(() => {}); }, [teamId, root, sequenceIds, privateLibrary.libraryId]);
  function listKey(event: React.KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.keyCode === 229 || (event.target as Element).closest('.row-send,.group-toggle,.group-edit')) return;
    if (kind === 'file' && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') { event.preventDefault(); setSelectedFiles(visible.map(item => item.id)); return; }
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault(); const categories = kind === 'phrase' ? roots : fileCats; const current = categories.findIndex(c => c.id === (kind === 'phrase' ? root : fileCategory)); const next = categories[Math.max(0, Math.min(categories.length - 1, current + (event.key === 'ArrowRight' ? 1 : -1)))];
      if (next) { chooseCategory(next); setSelected(''); setSelectedFiles([]); listRef.current?.focus(); } return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault(); const current = visible.findIndex(item => item.id === selected); const next = visible[Math.max(0, Math.min(visible.length - 1, current < 0 ? event.key === 'ArrowDown' ? 0 : visible.length - 1 : current + (event.key === 'ArrowDown' ? 1 : -1)))];
      if (next) { setSelected(next.id); if (kind === 'file') setSelectedFiles([next.id]); listRef.current?.focus(); requestAnimationFrame(() => listRef.current?.querySelector<HTMLElement>(`[data-row="${next.id}"]`)?.scrollIntoView({ block: 'nearest' })); }
    }
    if (event.key === 'Enter' && enterSubmit()) { event.preventDefault(); const item = visible.find(item => item.id === selected) || visible[0]; if (item) void deliver(item.id, item.kind, 'insert', 13); }
  }
  return <section className="client-window main-window" onFocusCapture={e => setHistoryActive(!!(e.target as Element).closest(searchInteractionSelector))} onBlurCapture={e => {
    if (e.relatedTarget && !(e.relatedTarget as Element).closest(searchInteractionSelector)) setHistoryActive(false);
  }} onPointerDownCapture={e => { if (!(e.target as Element).closest('.search-backdrop')) setHistoryActive(!!(e.target as Element).closest(searchInteractionSelector)); }} onClickCapture={e => { if (!(e.target as Element).closest('.content-row,.context-menu,dialog')) setSelected(''); }} onContextMenu={e => { if (!(e.target as Element).closest('input,textarea')) e.preventDefault(); }}>
    <header className="main-titlebar"><img className="app-symbol" src="/shanyu.png" alt="闪语" draggable={false} /><nav className="content-tabs">{(settings.filesFirst ? ['file', 'phrase'] as const : ['phrase', 'file'] as const).map(tab => <button key={tab} className={kind === tab ? 'active' : ''} onClick={() => { setKind(tab); clearSearch(); }}>{tab === 'phrase' ? '话术' : '文件'}</button>)}</nav><span className="titlebar-spacer" data-tauri-drag-region /><div className="titlebar-actions"><button aria-label="吸附" title="吸附" aria-pressed={settings.dock} disabled={settingsSave.busy('dock')} aria-busy={settingsSave.busy('dock')} onClick={() => changeSettings('dock')}><Icon name={settings.dock ? 'dock' : 'dock-off'} /></button><button aria-label="置顶" title="置顶" aria-pressed={settings.pinned} disabled={settingsSave.busy('pinned')} aria-busy={settingsSave.busy('pinned')} onClick={() => changeSettings('pinned')}><Icon name="pin" /></button><button aria-label="最小化" title="最小化" onClick={() => run(() => call('window_action', { action: 'minimize' }))}><Icon name="minus" /></button></div></header>
    <div className="main-search-content">
    {kind === 'phrase' && <div className="scope-bar">{(settings.teamsFirst ? ['team', 'private'] : ['private', 'team']).map(scope => scope === 'private' ? <button key={scope} className={`scope-name${!teamId ? ' active' : ''}`} onClick={() => changeScope('')}>个人话术</button> : <TeamSelector key={scope} status={teamStatus} teamId={teamId} onChange={changeScope} onError={setMessage} />)}<button id="top-menu" aria-label="一级分类菜单" onClick={topCategoryMenu}><Icon name="menu" /></button></div>}
    <nav className={`category-tabs${kind === 'file' ? ' files-tabs' : ' auto-wrapping'}${kind === 'phrase' || settings.fileCategoryHeight > 31 ? ' wrapping' : ''}`} style={kind === 'phrase' ? { minHeight: settings.categoryHeight } : { height: settings.fileCategoryHeight }} aria-label="一级分类">{(kind === 'phrase' ? roots : fileCats).map(c => { const background = c.background || (c.kind === 'file' ? settings.fileCategoryColor : settings.phraseCategoryColor); return <button key={c.id} title={c.readOnly ? `${c.name} · 来自${c.sourceLabel} · 只读` : categoryLabel(c, library.categories)} className={(kind === 'phrase' ? root : fileCategory) === c.id ? 'active' : ''} style={{ backgroundColor: background || undefined, color: background ? categoryTextColor(background) : undefined }} onClick={() => chooseCategory(c)} onContextMenu={e => categoryMenu(c, e)}>{c.name}{c.readOnly && <small className="member-source">{c.sourceLabel} · 只读</small>}</button>; })}<button className="add-category" aria-label="新增一级分类" disabled={kind === 'phrase' && !scopeCanEdit} onClick={() => categoryDialog()}>＋</button></nav>
    <div className="list-area"><div ref={listRef} tabIndex={0} className="content-list" role="listbox" aria-label={kind === 'phrase' ? '话术列表' : '文件列表'} aria-multiselectable={kind === 'file'} onKeyDown={listKey} onContextMenu={listMenu}>{content.length ? content : <div className="empty-state"><img className="empty-illustration" src={`/illustrations/${kind === 'phrase' ? 'phrase' : 'files'}-empty.png`} width="96" height="96" alt="" draggable={false} /><span>{teamId && kind === 'phrase' && !teamView ? teamError || '正在加载共享话术…' : !hasCategory ? '还没有一级分类' : kind === 'phrase' ? '这个分类还没有话术' : '还没有关联文件'}</span><button disabled={kind === 'phrase' && !canEdit} onClick={() => hasCategory ? run(newContent) : categoryDialog()}>{!hasCategory ? '新增一级分类' : kind === 'phrase' ? '新增话术' : '新增关联文件'}</button></div>}</div>
    </div>
    {settings.showCommon && <CommonPhraseBar library={library} busy={busy} onDeliver={(id, mode, triggerKey) => void deliver(id, 'common', mode, triggerKey)} onError={setMessage} />}
    {settings.showSearch && historyActive && <div className="search-backdrop" onPointerDown={e => { e.preventDefault(); e.stopPropagation(); }} onClick={() => setHistoryActive(false)} />}
    {settings.showSearch && historyActive && query.trim() && <section className="search-panel" aria-label="搜索结果"><header><span>搜索结果</span><button aria-label="关闭搜索" onClick={clearSearch}>×</button></header><div id="search-results" aria-busy={searchPending && !searchFailed}>{results.length ? <SearchResults results={results} renderRow={(hit, i) => row(hit.phrase, hit.file, i, true, hit)} /> : <div className="no-results">{searchFailed ? '搜索失败，请重新输入关键词' : searchPending ? '正在搜索…' : '没有找到匹配的内容'}</div>}</div></section>}
    {settings.showSearch && historyActive && !query.trim() && <SearchHistoryBar presentation="main" items={history.items} clearing={history.clearing} onSelect={chooseHistory} onClear={() => run(() => call('open_search_history_confirm', { libraryId: privateLibrary.libraryId }))} />}
    </div>
    {settings.showSearch && <div className="search-bar" id="search-bar"><Icon name="search" /><input ref={searchRef} data-shortcut-search aria-label="搜索话术、文件" placeholder="搜索话术、文件" value={query} onChange={e => { setHistoryActive(true); setQuery(e.target.value); setSearchIndex(0); setSearching(!!e.target.value.trim()); }} onCompositionStart={() => { composing.current = true; setSearching(true); }} onCompositionEnd={e => { composing.current = false; setQuery(e.currentTarget.value); endComposition(v => v + 1); }} onKeyDown={e => {
      if (composing.current || e.nativeEvent.isComposing || e.keyCode === 229) return;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setSearchIndex(i => Math.max(0, Math.min(results.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))); requestAnimationFrame(() => document.querySelector('#search-results .selected')?.scrollIntoView({ block: 'nearest' })); }
      if (e.key === 'Enter' && historyActive && query.trim() && !sequenceLoading && results[searchIndex] && enterSubmit()) { e.preventDefault(); const item = results[searchIndex]; void deliver(searchDeliveryKey(item), item.kind, 'insert', 13, searchUsage()); }
      if (e.key === 'Escape') { e.preventDefault(); clearSearch(); e.currentTarget.blur(); listRef.current?.focus(); }
    }} />{query && <button aria-label="清空搜索" onClick={clearSearch}>×</button>}</div>}
    <footer className="main-footer"><button aria-label="主菜单" onClick={mainMenu}><Icon name="menu" /></button><button aria-label={settings.showCommon ? '隐藏常用短语' : '显示常用短语'} title={settings.showCommon ? '隐藏常用短语' : '显示常用短语'} aria-pressed={settings.showCommon} disabled={settingsSave.busy('showCommon')} aria-busy={settingsSave.busy('showCommon')} onClick={() => changeSettings('showCommon')}><Icon name={settings.showCommon ? 'eye' : 'eye-off'} /></button><button aria-label={settings.showSearch ? '隐藏搜索框' : '显示搜索框'} title={settings.showSearch ? '隐藏搜索框' : '显示搜索框'} aria-controls="search-bar" aria-expanded={settings.showSearch} aria-pressed={settings.showSearch} disabled={settingsSave.busy('showSearch')} aria-busy={settingsSave.busy('showSearch')} onClick={() => changeSettings('showSearch')}><Icon name={settings.showSearch ? 'eye' : 'eye-off'} /></button>{busy && <button className="stop-delivery" onClick={() => run(() => call('cancel_delivery'))}>停止</button>}<span className="footer-spacer" data-tauri-drag-region /><UpdateBadge /><button aria-label="设置" title="设置" className="settings-button" onClick={() => run(() => openWindow('settings'))}><Icon name="settings" /></button></footer>
    {menu && <div className={`context-menu${menu.primary ? ' category-context-menu' : ''}`} data-search-menu={menu.search} role="menu" tabIndex={-1} style={menu.bottom === undefined ? { left: menu.x, top: menu.y } : { left: menu.x, bottom: menu.bottom, maxHeight: Math.max(0, innerHeight - menu.bottom - 2) }} onKeyDown={e => {
      if (e.key === 'Tab') { setMenu(null); return; }
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
      e.preventDefault();
      const buttons = [...e.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')]; const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
      const next = e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : e.key === 'ArrowDown' ? (index + 1) % buttons.length : index < 0 ? buttons.length - 1 : (index - 1 + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }}>{menu.items.map((item, i) => item ? <button key={i} role="menuitem" disabled={item.enabled === false} onClick={() => { setMenu(null); run(item.action); }}>{item.label}</button> : <div className="menu-separator" key={i} />)}</div>}
    {cloudTarget && <CompanyFilePicker limit={1} confirmLabel="下一步" close={() => { setCloudTarget(null); menuTrigger.current?.focus(); }} onConfirm={(files, epoch) => { const target = cloudTarget; setCloudTarget(null); run(async () => { if (epoch !== target.epoch || !validCloudTarget(target)) throw new Error('账号、分类或编辑权限已变化，请重新选择云文件。'); await openWindow('editor', '', '', `cloud:${epoch}:${files[0].assetId}`, target.categoryId, target.groupId, target.teamId); }); }} />}{dialog && <FormDialog key={dialog.title} spec={dialog} close={() => setDialog(null)} />}{shortcutPhrase && <ShortcutDialog id={shortcutPhrase.id} value={shortcutPhrase.shortcut} onConfirm={async shortcut => { const latest = [...privateLibrary.phrases, ...(members?.phrases || [])].find(p => p.id === shortcutPhrase.id); if (!latest) throw new Error('话术已被删除，无法设置快捷键。'); if (shortcutPhrase.id.startsWith('member:')) await call('save_member_shortcut', { id: shortcutPhrase.id, shortcut, sessionEpoch: shortcutPhrase.shortcutEpoch }); else await call('save_phrase', { phrase: { ...latest, shortcut }, afterId: null }); }} close={() => setShortcutPhrase(null)} />}
    <Feedback message={activeNotice?.message || (deliveryProgress.waiting ? '正在等待剪贴板释放…' : '')} kind={activeNotice?.kind || 'progress'} title={activeNotice?.title} count={pendingDeliveries.items.length + notices.count + (pendingDeliveries.problem ? 1 : 0)} clear={() => showPending ? void pendingDeliveries.dismiss(pendingDelivery!.id).catch(e => setMessage(String(e))) : showDeliveryProblem ? pendingDeliveries.dismissProblem() : showProgress ? deliveryProgress.dismissWaiting() : notices.current ? notices.dismiss(notices.current.id) : deliveryProgress.dismissWaiting()} stop={!activeNotice && deliveryProgress.waiting ? () => run(() => call('cancel_delivery', { operationId: deliveryProgress.waiting!.operation })) : undefined} />
  </section>;
}
