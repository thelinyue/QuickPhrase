import { useEffect, useMemo, useRef, useState } from "react";
import {
  Add20Regular, AppsList20Regular, ArrowLeft20Regular, Checkmark20Regular,
  ChevronDown20Regular, Clock20Regular, Delete20Regular, Dismiss20Regular,
  Document20Regular, Edit20Regular, Flash20Regular, Folder20Regular,
  Globe20Regular, Search20Regular, Send20Regular, Settings20Regular,
  Star20Filled, Star20Regular, ChatBubblesQuestion20Regular, MoreHorizontal20Regular,
} from "@fluentui/react-icons";
import { hasUnsavedChanges, nextSelectionAfterDelete, restoreLibraryContextAfterSave } from "./managementNavigation.js";

const SCENES = new Set(["library", "editor", "settings"]);
const QUICK_SHORTCUTS = Array.from({ length: 9 }, (_, index) => `Alt + ${index + 1}`);
const emptyContext = { activeFilter: "all", query: "", selectedId: null, scrollTop: 0, sort: "manual" };
const emptyDraft = (categoryId = "", title = "") => ({ id: crypto.randomUUID(), version: 0, title, content: "", categoryId, tags: [], favorite: false, colorKey: "default", shortcutMode: "None", shortcut: "" });

function clone(value) { return value == null ? value : structuredClone(value); }
function toDraft(phrase) { return { id: phrase.id, version: phrase.version, title: phrase.title, content: phrase.content, categoryId: phrase.categoryId, tags: (phrase.tags || []).map((tag) => tag.name), favorite: phrase.favorite, colorKey: phrase.colorKey || "default", shortcutMode: phrase.shortcutMode || "None", shortcut: phrase.shortcut?.display || "" }; }
function phraseColor(colorKey) { return ({ red: "#e4574f", orange: "#ef8b32", yellow: "#c99618", green: "#3c9e67", blue: "#3b86c5", purple: "#8b58c9", gray: "#66717d" })[colorKey] || "#5b6675"; }
function formatLastUsed(value) { if (!value) return "未使用"; const timestamp = Date.parse(value); if (Number.isNaN(timestamp)) return value; const elapsed = Date.now() - timestamp; if (elapsed < 3600000) return "刚刚"; if (elapsed < 86400000) return `今天 ${new Date(timestamp).toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" })}`; return new Date(timestamp).toLocaleDateString("zh-CN", { month: "2-digit", day: "2-digit" }); }
function capabilityLabel(value) { return value || "Unverified"; }
function childrenOf(categories, parentId = null) { return categories.filter((category) => (category.parentId || null) === parentId).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, "zh-CN")); }
function categoryDepth(categories, id) { let depth = 0; let current = categories.find((category) => category.id === id); while (current?.parentId) { depth += 1; current = categories.find((category) => category.id === current.parentId); } return depth; }
function categoryPath(categories, id) { const names = []; let current = categories.find((category) => category.id === id); while (current) { names.unshift(current.name); current = current.parentId ? categories.find((category) => category.id === current.parentId) : null; } return names.join(" / "); }
function descendantIds(categories, id) { const ids = new Set([id]); let changed = true; while (changed) { changed = false; for (const category of categories) if (category.parentId && ids.has(category.parentId) && !ids.has(category.id)) { ids.add(category.id); changed = true; } } return ids; }

export function ManagementHostApp() {
  const bridge = window.__quickPhraseBridge;
  const [surface, setSurface] = useState(() => new URLSearchParams(window.location.search).get("surface") === "settings" ? "settings" : "phrase-library");
  const [scene, setScene] = useState(() => new URLSearchParams(window.location.search).get("surface") === "settings" ? "settings" : "library");
  // 话术库贴边入口每次启动默认收起；展开状态只保留在当前管理窗口会话。
  const [libraryExpanded, setLibraryExpanded] = useState(false);
  const [phrases, setPhrases] = useState([]);
  const [searchResults, setSearchResults] = useState(null);
  const [categories, setCategories] = useState([]);
  const [settings, setSettings] = useState(null);
  const [settingsBaseline, setSettingsBaseline] = useState(null);
  const [draft, setDraft] = useState(null);
  const [editorBaseline, setEditorBaseline] = useState(null);
  const [context, setContext] = useState(emptyContext);
  // 使用 ref 保留最新列表上下文，确保保存/删除后的异步导航恢复正确滚动位置。
  const contextRef = useRef(emptyContext);
  contextRef.current = context;
  const [settingsSection, setSettingsSection] = useState("general");
  const [newCategoryName, setNewCategoryName] = useState("");
  const [tagInput, setTagInput] = useState("");
  const [pendingNavigation, setPendingNavigation] = useState(null);
  const [status, setStatus] = useState("正在加载本地话术…");
  const [error, setError] = useState("");
  const [toast, setToast] = useState("");
  const [hotkeyStatus, setHotkeyStatus] = useState(null);
  const [adapterStatus, setAdapterStatus] = useState(null);
  const [adapterCatalog, setAdapterCatalog] = useState([]);
  const [expandedCategories, setExpandedCategories] = useState(() => new Set());
  const [categoryDialog, setCategoryDialog] = useState(null);
  const [phraseMenu, setPhraseMenu] = useState(null);
  const [phraseMoveDialog, setPhraseMoveDialog] = useState(null);
  const loadStarted = useRef(false);
  const secondaryStarted = useRef(false);
  const navigationRef = useRef(null);
  const libraryListRef = useRef(null);

  const categoryName = useMemo(() => Object.fromEntries(categories.map((item) => [item.id, categoryPath(categories, item.id)])), [categories]);
  const sourcePhrases = context.query.trim() ? (searchResults || []) : phrases;
  const visiblePhrases = useMemo(() => {
    const source = sourcePhrases.map((phrase) => ({ ...phrase, categoryName: categoryName[phrase.categoryId] || "未分类" }));
    let filtered = source;
    if (context.activeFilter === "favorite") filtered = source.filter((phrase) => phrase.favorite);
    if (context.activeFilter === "recent") filtered = [...source].sort((a, b) => (b.lastUsedAtUtc || "").localeCompare(a.lastUsedAtUtc || ""));
    if (context.activeFilter.startsWith("category:")) {
      const selectedCategoryId = context.activeFilter.slice(9);
      const included = descendantIds(categories, selectedCategoryId);
      filtered = source.filter((phrase) => included.has(phrase.categoryId));
    }
    if (context.sort === "title") return [...filtered].sort((a, b) => a.title.localeCompare(b.title, "zh-CN"));
    if (context.sort === "recent") return [...filtered].sort((a, b) => (b.lastUsedAtUtc || "").localeCompare(a.lastUsedAtUtc || ""));
    return filtered;
  }, [categories, categoryName, context.activeFilter, context.query, context.sort, searchResults, sourcePhrases]);
  const selectedRaw = phrases.find((phrase) => phrase.id === context.selectedId) || visiblePhrases[0] || null;
  const selected = selectedRaw ? { ...selectedRaw, categoryName: categoryName[selectedRaw.categoryId] || "未分类" } : null;
  const dirty = (scene === "editor" && hasUnsavedChanges(editorBaseline, draft)) || (scene === "settings" && hasUnsavedChanges(settingsBaseline, settings));

  const showToast = (message) => { setToast(message); window.setTimeout(() => setToast((current) => current === message ? "" : current), 2800); };
  const updateContext = (patch) => setContext((current) => {
    const next = { ...current, ...patch };
    contextRef.current = next;
    return next;
  });
  const notifyScene = (nextScene) => bridge?.request("window.sceneChanged", { scene: nextScene }).catch(() => {});

  const applyNavigation = (nextScene, intent = {}) => {
    if (intent.type === "new-editor") { const next = emptyDraft(categories[0]?.id || "", intent.seedTitle || ""); setDraft(next); setEditorBaseline(clone(next)); setTagInput(""); }
    if (intent.type === "edit-editor") { const next = toDraft(intent.phrase); setDraft(next); setEditorBaseline(clone(next)); setTagInput(""); }
    setSurface(nextScene === "settings" ? "settings" : "phrase-library");
    if (nextScene === "library") setLibraryExpanded(true);
    if (nextScene === "settings" && intent.resetSettingsSection) setSettingsSection("general");
    setPendingNavigation(null); setScene(nextScene); notifyScene(nextScene);
    if (nextScene === "library") window.requestAnimationFrame(() => { if (libraryListRef.current) libraryListRef.current.scrollTop = contextRef.current.scrollTop; });
  };

  const requestNavigation = (nextScene, intent = {}) => {
    if (!SCENES.has(nextScene)) return;
    if (nextScene === scene && !intent.type) return;
    if (dirty) { setPendingNavigation({ nextScene, intent }); return; }
    applyNavigation(nextScene, intent);
  };
  navigationRef.current = requestNavigation;

  const refresh = async () => {
    setError("");
    try {
      const [phrasePage, categoryList, storedSettings, catalog] = await Promise.all([bridge.request("phrase.list", { offset: 0, limit: 100 }), bridge.request("category.list"), bridge.request("settings.get"), bridge.request("adapter.catalog")]);
      const items = phrasePage.items || [];
      setPhrases(items); setCategories(categoryList || []); setSettings(storedSettings); setSettingsBaseline(clone(storedSettings)); setAdapterCatalog(catalog?.items || catalog || []);
      setExpandedCategories((current) => {
        const next = new Set(current);
        for (const category of categoryList || []) if (category.parentId && contextRef.current.activeFilter === `category:${category.id}`) next.add(category.parentId);
        return next;
      });
      setContext((current) => ({ ...current, selectedId: current.selectedId && items.some((item) => item.id === current.selectedId) ? current.selectedId : items[0]?.id || null }));
      setStatus("本地数据已就绪");
    } catch (cause) { setError(cause.message || "无法读取本地数据"); setStatus("数据加载失败"); }
  };
  const loadSecondary = async () => {
    if (secondaryStarted.current) return;
    secondaryStarted.current = true;
    try { const [hotkey, adapter] = await Promise.all([bridge.request("hotkey.status"), bridge.request("adapter.status")]); setHotkeyStatus(hotkey); setAdapterStatus(adapter); }
    catch (cause) { setError(cause.message || "无法读取宿主状态"); }
  };

  useEffect(() => {
    if (!bridge) return undefined;
    const unsubscribe = bridge.onEvent((event) => {
      if (event.event !== "navigation.requested") return;
      if (event.data?.scene === "settings") navigationRef.current?.("settings", { resetSettingsSection: true });
      else navigationRef.current?.("editor", { type: "new-editor", seedTitle: event.data?.seedTitle || "" });
    });
    if (!loadStarted.current) { loadStarted.current = true; refresh().finally(() => bridge.request("system.ready", {}, 5000).then(loadSecondary).catch(() => setStatus("管理界面已加载，但宿主握手失败"))); }
    return () => unsubscribe?.();
  }, [bridge]);
  useEffect(() => {
    let cancelled = false;
    if (!context.query.trim()) { setSearchResults(null); return undefined; }
    bridge?.request("phrase.search", { query: context.query, limit: 100 }).then((response) => { if (!cancelled) setSearchResults((response.items || []).map((item) => item.phrase)); }).catch((cause) => { if (!cancelled) setError(cause.message || "搜索失败"); });
    return () => { cancelled = true; };
  }, [bridge, context.query]);

  const openEditor = (phrase = null) => { updateContext({ scrollTop: libraryListRef.current?.scrollTop || 0 }); requestNavigation("editor", phrase ? { type: "edit-editor", phrase } : { type: "new-editor" }); };
  const toggleFavorite = async (phrase) => {
    try { const updated = await bridge.request("phrase.update", { ...toDraft(phrase), favorite: !phrase.favorite, expectedVersion: phrase.version, shortcut: phrase.shortcut?.display || null }); setPhrases((current) => current.map((item) => item.id === updated.id ? updated : item)); updateContext({ selectedId: updated.id }); }
    catch (cause) { setError(cause.message || "收藏状态保存失败"); }
  };
  const insertPhrase = async (phrase) => {
    setPhraseMenu(null);
    try {
      await bridge.request("phrase.insert", { id: phrase.id });
      showToast("已请求安全插入；目标不可用时会降级为复制");
    } catch (cause) { setError(cause.message || "无法插入话术"); }
  };
  const phraseMatchesContext = (phrase, target) => {
    const matchesFilter = target.activeFilter === "all" || target.activeFilter === "recent" || (target.activeFilter === "favorite" && phrase.favorite) || (target.activeFilter.startsWith("category:") && descendantIds(categories, target.activeFilter.slice(9)).has(phrase.categoryId));
    if (!matchesFilter || !target.query.trim()) return matchesFilter;
    return `${phrase.title} ${phrase.content} ${(phrase.tags || []).map((tag) => tag.name).join(" ")}`.toLowerCase().includes(target.query.trim().toLowerCase());
  };
  const saveDraft = async (afterNavigation = null) => {
    if (!draft.title.trim()) { setError("话术标题不能为空。"); return false; }
    if (!draft.content.trim()) { setError("话术正文不能为空。"); return false; }
    if (draft.title.trim().length > 80) { setError("话术标题不能超过 80 个字。"); return false; }
    if (draft.content.trim().length > 4000) { setError("话术正文不能超过 4000 个字。"); return false; }
    const shortcut = draft.shortcut.trim();
    const conflict = shortcut && phrases.some((phrase) => phrase.id !== draft.id && phrase.shortcut?.normalized === shortcut.replace(/\s+/g, "").toLowerCase());
    if (conflict) { setError(`${shortcut} 已被「${conflict.title}」占用，请改选空闲快捷键。`); return false; }
    try {
      const payload = { ...draft, title: draft.title.trim(), content: draft.content.trim(), tags: draft.tags.map((tag) => tag.trim()).filter(Boolean), shortcut: shortcut || null };
      const saved = draft.version ? await bridge.request("phrase.update", { ...payload, expectedVersion: draft.version }) : await bridge.request("phrase.create", payload);
      const visible = phraseMatchesContext(saved, context);
      setPhrases((current) => draft.version ? current.map((item) => item.id === saved.id ? saved : item) : [saved, ...current]);
      const restoredContext = restoreLibraryContextAfterSave(contextRef.current, saved, { isVisible: visible, isNew: !draft.version });
      contextRef.current = restoredContext;
      setContext(restoredContext);
      setDraft(null); setEditorBaseline(null); setStatus("话术已保存"); showToast(visible || !draft.version ? "话术已保存" : "话术已保存，已切换到全部话术查看");
      if (afterNavigation) applyNavigation(afterNavigation.nextScene, afterNavigation.intent); else applyNavigation("library");
      return true;
    } catch (cause) { setError(cause.message || "保存失败"); return false; }
  };
  const deletePhrase = async () => {
    if (!draft?.version) return;
    try { const candidateIds = visiblePhrases.map((phrase) => phrase.id); await bridge.request("phrase.delete", { id: draft.id, expectedVersion: draft.version }); setPhrases((current) => current.filter((item) => item.id !== draft.id)); updateContext({ selectedId: nextSelectionAfterDelete(candidateIds, draft.id) }); setDraft(null); setEditorBaseline(null); showToast("话术已删除"); applyNavigation("library"); }
    catch (cause) { setError(cause.message || "删除失败"); }
  };
  const deletePhraseById = async (phrase) => {
    setPhraseMenu(null);
    if (!window.confirm(`删除话术“${phrase.title}”？`)) return;
    try {
      await bridge.request("phrase.delete", { id: phrase.id, expectedVersion: phrase.version });
      setPhrases((current) => current.filter((item) => item.id !== phrase.id));
      updateContext({ selectedId: contextRef.current.selectedId === phrase.id ? null : contextRef.current.selectedId });
      showToast("话术已删除");
    } catch (cause) { setError(cause.message || "删除失败"); }
  };
  const movePhrase = async (phrase, categoryId) => {
    try {
      const updated = await bridge.request("phrase.update", { ...toDraft(phrase), categoryId, expectedVersion: phrase.version, shortcut: phrase.shortcut?.display || null });
      setPhrases((current) => current.map((item) => item.id === updated.id ? updated : item));
      setPhraseMoveDialog(null); setPhraseMenu(null); updateContext({ selectedId: updated.id }); showToast("话术已移动");
    } catch (cause) { setError(cause.message || "话术移动失败"); }
  };
  const cyclePhraseSort = () => {
    const next = context.sort === "manual" ? "title" : context.sort === "title" ? "recent" : "manual";
    updateContext({ sort: next }); setPhraseMenu(null); showToast(next === "title" ? "已按标题排序" : next === "recent" ? "已按最近使用排序" : "已恢复默认顺序");
  };
  const createCategory = async (parentId = null, dialogName = null) => {
    const name = (dialogName ?? newCategoryName).trim();
    if (!name) { setError("分类名称不能为空。"); return; }
    if (parentId && categoryDepth(categories, parentId) >= 2) { setError("分类最多支持二级，无法在其下再建子分类。"); return; }
    try { const siblings = childrenOf(categories, parentId); const category = await bridge.request("category.create", { id: crypto.randomUUID(), name, parentId, sortOrder: siblings.length }); setCategories((current) => [...current, category]); setNewCategoryName(""); setCategoryDialog(null); if (parentId) setExpandedCategories((current) => new Set([...current, parentId])); updateContext({ activeFilter: `category:${category.id}`, query: "", scrollTop: 0 }); showToast("分类已创建"); }
    catch (cause) { setError(cause.message || "分类创建失败。"); }
  };
  const renameCategory = async (category) => {
    const name = window.prompt("重命名分类", category.name)?.trim();
    if (!name || name === category.name) return;
    try { const updated = await bridge.request("category.rename", { id: category.id, name, expectedVersion: category.version, sortOrder: category.sortOrder }); setCategories((current) => current.map((item) => item.id === updated.id ? updated : item)); showToast("分类已重命名"); }
    catch (cause) { setError(cause.message || "分类重命名失败。"); }
  };
  const moveCategory = async (category, parentId) => {
    if (parentId === category.id || (parentId && descendantIds(categories, category.id).has(parentId))) { setError("不能把分类移动到自己或自己的后代下。"); return; }
    if (parentId && categoryDepth(categories, parentId) + 1 > 2) { setError("移动后会超过二级分类限制。"); return; }
    try { const updated = await bridge.request("category.move", { id: category.id, expectedVersion: category.version, parentId, sortOrder: childrenOf(categories, parentId).filter((item) => item.id !== category.id).length }); setCategories((current) => current.map((item) => item.id === updated.id ? updated : item)); setCategoryDialog(null); showToast("分类已移动"); }
    catch (cause) { setError(cause.message || "分类移动失败。"); }
  };
  const deleteCategory = async (category) => {
    const hasChildren = categories.some((item) => item.parentId === category.id);
    const hasPhrases = phrases.some((phrase) => phrase.categoryId === category.id);
    if (hasChildren || hasPhrases) { setError("分类包含话术或子分类，请先移动内容后再删除。"); return; }
    if (!window.confirm(`删除分类“${categoryPath(categories, category.id)}”？`)) return;
    try { await bridge.request("category.delete", { id: category.id, expectedVersion: category.version }); setCategories((current) => current.filter((item) => item.id !== category.id)); if (context.activeFilter === `category:${category.id}`) updateContext({ activeFilter: "all", selectedId: null }); showToast("分类已删除"); }
    catch (cause) { setError(cause.message || "分类删除失败。"); }
  };
  const saveSettings = async (afterNavigation = null) => {
    try { const saved = await bridge.request("settings.update", settings); setSettings(saved); setSettingsBaseline(clone(saved)); setStatus("设置已保存"); showToast("设置已保存"); if (afterNavigation) applyNavigation(afterNavigation.nextScene, afterNavigation.intent); else applyNavigation("library"); return true; }
    catch (cause) { setError(cause.message || "设置保存失败"); return false; }
  };
  const resolvePendingNavigation = async (choice) => {
    const pending = pendingNavigation;
    if (!pending) return;
    if (choice === "continue") { setPendingNavigation(null); return; }
    if (choice === "discard") { if (scene === "editor") { setDraft(clone(editorBaseline)); setTagInput(""); } if (scene === "settings") setSettings(clone(settingsBaseline)); applyNavigation(pending.nextScene, pending.intent); return; }
    if (scene === "editor") await saveDraft(pending);
    if (scene === "settings") await saveSettings(pending);
  };

  if (!bridge) return <div className="host-fallback">管理界面宿主不可用，请从闪语托盘重新打开。</div>;
  const collapsedLibrary = surface === "phrase-library" && scene === "library" && !libraryExpanded;
  if (collapsedLibrary) return <main className="management-shell phrase-library-collapsed"><button type="button" className="phrase-library-rail" aria-label="展开话术库" onClick={() => setLibraryExpanded(true)}><AppsList20Regular /><span>话术库</span><ChevronDown20Regular /></button></main>;
  return <main className={`management-shell scene-${scene}`}>
    <ManagementHeader surface={surface} scene={scene} onNavigate={(next) => requestNavigation(next, next === "settings" ? { resetSettingsSection: true } : {})} onNew={() => openEditor()} onLauncher={() => bridge.request("launcher.open", { mode: "search" }).then(() => showToast("已打开快速启动器（无目标时仅支持预览或复制）")).catch((cause) => setError(cause.message || "无法打开快速启动器"))} />
    <div className="management-content">
      {error ? <div className="host-error" role="alert">{error}<button type="button" aria-label="关闭提示" onClick={() => setError("")}><Dismiss20Regular /></button></div> : null}
      {surface === "phrase-library" && scene === "library" ? <LibraryTreeView listRef={libraryListRef} phrases={visiblePhrases} allPhrases={phrases} categories={categories} selected={selected} context={context} status={status} newCategoryName={newCategoryName} setNewCategoryName={setNewCategoryName} expandedCategories={expandedCategories} setExpandedCategories={expandedCategories} onContextChange={updateContext} onCreateCategory={createCategory} onRenameCategory={renameCategory} onMoveCategory={moveCategory} onDeleteCategory={deleteCategory} onOpenCategoryDialog={setCategoryDialog} onNew={() => openEditor()} onEdit={openEditor} onFavorite={toggleFavorite} onInsert={insertPhrase} onOpenMenu={(phrase, event) => { event.preventDefault(); updateContext({ selectedId: phrase.id }); setPhraseMenu({ phrase, x: event.clientX, y: event.clientY }); }} /> : null}
      {surface === "phrase-library" && scene === "editor" ? <EditorView draft={draft} categories={categories} phrases={phrases} tagInput={tagInput} setTagInput={setTagInput} setDraft={setDraft} onSave={() => saveDraft()} onDelete={deletePhrase} onCancel={() => requestNavigation("library")} /> : null}
      {surface === "settings" ? <SettingsViewV2 settings={settings} setSettings={setSettings} section={settingsSection} setSection={setSettingsSection} hotkeyStatus={hotkeyStatus} adapterStatus={adapterStatus} adapterCatalog={adapterCatalog} onSave={() => saveSettings()} onCancel={() => requestNavigation("library")} /> : null}
    </div>
    {pendingNavigation ? <NavigationConfirm onChoice={resolvePendingNavigation} /> : null}
    {categoryDialog ? <CategoryDialog categories={categories} mode={categoryDialog.mode} category={categoryDialog.category} onClose={() => setCategoryDialog(null)} onCreate={(parentId, name) => createCategory(parentId, name)} onMove={(parentId) => moveCategory(categoryDialog.category, parentId)} onRename={renameCategory} onDelete={deleteCategory} onOpenMove={(category) => setCategoryDialog({ mode: "move", category })} onOpenCreate={(parentId) => setCategoryDialog({ mode: "create", category: { parentId } })} /> : null}
    {phraseMenu ? <PhraseContextMenu phrase={phraseMenu.phrase} x={phraseMenu.x} y={phraseMenu.y} onClose={() => setPhraseMenu(null)} onInsert={() => insertPhrase(phraseMenu.phrase)} onEdit={() => { setPhraseMenu(null); openEditor(phraseMenu.phrase); }} onFavorite={() => { setPhraseMenu(null); toggleFavorite(phraseMenu.phrase); }} onMove={() => { setPhraseMenu(null); setPhraseMoveDialog(phraseMenu.phrase); }} onSort={cyclePhraseSort} onDelete={() => deletePhraseById(phraseMenu.phrase)} /> : null}
    {phraseMoveDialog ? <PhraseMoveDialog phrase={phraseMoveDialog} categories={categories} onClose={() => setPhraseMoveDialog(null)} onMove={(categoryId) => movePhrase(phraseMoveDialog, categoryId)} /> : null}
    {toast ? <div className="host-toast" role="status">{toast}</div> : null}
  </main>;
}

function ManagementHeader({ surface, scene, onNavigate, onNew, onLauncher }) {
  const phraseLibrary = surface === "phrase-library";
  return <header className={`management-header ${phraseLibrary ? "phrase-library-header" : "settings-only-header"}`}>
    <div className="management-brand"><span className="management-brand-icon"><ChatBubblesQuestion20Regular /></span><span><strong>闪语</strong><small>{phraseLibrary ? "话术库工作区" : "应用设置"}</small></span></div>
    <nav className="global-nav" aria-label={phraseLibrary ? "话术库导航" : "设置导航"}>
      {phraseLibrary ? <button type="button" className={scene !== "settings" ? "is-active" : ""} aria-current={scene !== "settings" ? "page" : undefined} onClick={() => onNavigate("library")}><AppsList20Regular /> 话术库</button> : null}
      {phraseLibrary ? <button type="button" onClick={() => onNavigate("settings")}><Settings20Regular /> 设置</button> : null}
      {!phraseLibrary ? <button type="button" className="is-active" aria-current="page"><Settings20Regular /> 设置</button> : <></>}
      {!phraseLibrary ? <button type="button" onClick={() => onNavigate("library")}><AppsList20Regular /> 话术库</button> : null}
    </nav>
    <div className="management-header-actions">
      {phraseLibrary ? <button type="button" className="button secondary small" onClick={onLauncher}><Flash20Regular /> 打开 Launcher</button> : null}
      {phraseLibrary ? <button type="button" className="button primary small management-new" onClick={onNew}><Add20Regular /> 新建话术</button> : null}
    </div>
  </header>;
}

function LibraryTreeView({ listRef, phrases, allPhrases, categories, selected, context, status, newCategoryName, setNewCategoryName, expandedCategories, setExpandedCategories, onContextChange, onCreateCategory, onRenameCategory, onMoveCategory, onDeleteCategory, onOpenCategoryDialog, onNew, onEdit, onFavorite, onInsert, onOpenMenu }) {
  const filterLabel = context.activeFilter === "all" ? "全部话术" : context.activeFilter === "favorite" ? "收藏" : context.activeFilter === "recent" ? "最近使用" : categoryPath(categories, context.activeFilter.slice(9)) || "全部话术";
  const toggleExpanded = (id) => setExpandedCategories((current) => { const next = new Set(current); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  useEffect(() => {
    if (!context.activeFilter.startsWith("category:")) return;
    const selectedId = context.activeFilter.slice(9);
    const next = new Set(expandedCategories);
    let current = categories.find((category) => category.id === selectedId);
    while (current?.parentId) { next.add(current.parentId); current = categories.find((category) => category.id === current.parentId); }
    if (next.size !== expandedCategories.size) setExpandedCategories(next);
  }, [categories, context.activeFilter]);
  const phraseCount = (id) => { const included = descendantIds(categories, id); return allPhrases.filter((phrase) => included.has(phrase.categoryId)).length; };
  const renderCategory = (category, depth = 0) => { const children = childrenOf(categories, category.id); const expanded = expandedCategories.has(category.id); return <div key={category.id} className="category-tree-node"><div className={`category-tree-row ${context.activeFilter === `category:${category.id}` ? "is-active" : ""}`} style={{ paddingLeft: `${10 + depth * 18}px` }}><button type="button" className="category-expand" aria-label={expanded ? `收起 ${category.name}` : `展开 ${category.name}`} onClick={() => toggleExpanded(category.id)} disabled={!children.length}>{children.length ? <ChevronDown20Regular className={expanded ? "is-expanded" : ""} /> : <span className="category-expand-placeholder" />}</button><button type="button" className="category-tree-select" onClick={() => onContextChange({ activeFilter: `category:${category.id}`, selectedId: null, scrollTop: 0 })}><Folder20Regular /><span title={categoryPath(categories, category.id)}>{category.name}</span><small>{phraseCount(category.id)}</small></button><button type="button" className="category-more" aria-label={`${category.name} 分类操作`} onClick={() => onOpenCategoryDialog({ mode: "menu", category })}><MoreHorizontal20Regular /></button></div>{expanded ? <div className="category-tree-children">{children.map((child) => renderCategory(child, depth + 1))}</div> : null}</div>; };
  return <section className="library-page"><div className="library-breadcrumb">话术库 <span>·</span> {filterLabel}</div><div className="library-layout"><aside className="library-sidebar" aria-label="话术筛选"><div className="sidebar-heading">我的话术</div><SidebarItem icon={<Document20Regular />} label="全部话术" count={allPhrases.length} active={context.activeFilter === "all"} onClick={() => onContextChange({ activeFilter: "all", scrollTop: 0 })} /><SidebarItem icon={<Star20Regular />} label="收藏" count={allPhrases.filter((phrase) => phrase.favorite).length} active={context.activeFilter === "favorite"} onClick={() => onContextChange({ activeFilter: "favorite", scrollTop: 0 })} /><SidebarItem icon={<Clock20Regular />} label="最近使用" count={allPhrases.length} active={context.activeFilter === "recent"} onClick={() => onContextChange({ activeFilter: "recent", scrollTop: 0 })} /><div className="sidebar-divider" /><div className="sidebar-heading category-heading">分类 <button type="button" className="category-add" onClick={() => onOpenCategoryDialog({ mode: "create", category: null })}><Add20Regular /></button></div><div className="category-tree">{childrenOf(categories).map((category) => renderCategory(category))}</div><div className="new-category-row"><input value={newCategoryName} onChange={(event) => setNewCategoryName(event.target.value)} onKeyDown={(event) => event.key === "Enter" && onCreateCategory(null)} placeholder="新建一级分类" aria-label="新建一级分类名称" /><button type="button" onClick={() => onCreateCategory(null)}><Add20Regular /> 新建</button></div></aside><label className="mobile-filter"><span>当前分类</span><select value={context.activeFilter} onChange={(event) => onContextChange({ activeFilter: event.target.value, scrollTop: 0 })}><option value="all">全部话术</option><option value="favorite">收藏</option><option value="recent">最近使用</option>{categories.map((category) => <option key={category.id} value={`category:${category.id}`}>{categoryPath(categories, category.id)}</option>)}</select><ChevronDown20Regular /></label><div ref={listRef} className="library-list" onScroll={(event) => onContextChange({ scrollTop: event.currentTarget.scrollTop })}><div className="library-list-head"><div><span className="section-kicker">{filterLabel}</span><h1>{filterLabel}</h1></div><span className="result-count">{phrases.length} 条结果</span></div><label className="library-search"><Search20Regular /><input value={context.query} onChange={(event) => onContextChange({ query: event.target.value, scrollTop: 0 })} aria-label="主窗口搜索话术" placeholder="搜索标题、正文或拼音..." /><span className="key-hint">Ctrl K</span></label><div className="phrase-list">{phrases.map((phrase) => <div role="button" tabIndex="0" key={phrase.id} className={`phrase-row ${selected?.id === phrase.id ? "is-selected" : ""}`} onClick={() => onContextChange({ selectedId: phrase.id })} onDoubleClick={() => onInsert(phrase)} onContextMenu={(event) => onOpenMenu(phrase, event)} onKeyDown={(event) => event.key === "Enter" && onContextChange({ selectedId: phrase.id })}><span className="phrase-row-icon" style={{ color: phraseColor(phrase.colorKey) }}><Document20Regular /></span><span className="phrase-row-main"><strong style={{ color: phraseColor(phrase.colorKey) }}>{phrase.title}</strong><small>{phrase.content}</small><span className="row-meta">{(phrase.tags || []).slice(0, 2).map((tag) => <i key={tag.id}>{tag.name}</i>)}<em>{phrase.shortcut?.display || "未设置"}</em></span></span><span className="phrase-row-side"><button type="button" aria-label={phrase.favorite ? `取消收藏 ${phrase.title}` : `收藏 ${phrase.title}`} onClick={(event) => { event.stopPropagation(); onFavorite(phrase); }} onDoubleClick={(event) => event.stopPropagation()}>{phrase.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}</button><small>{formatLastUsed(phrase.lastUsedAtUtc)}</small></span></div>)}{!phrases.length ? <div className="library-empty"><Search20Regular /><strong>没有找到话术</strong><span>尝试清除搜索或切换分类</span></div> : null}</div></div><aside className="phrase-preview" aria-label="话术预览">{selected ? <><div className="preview-kicker">话术预览</div><div className="preview-title-line"><h2 style={{ color: phraseColor(selected.colorKey) }}>{selected.title}</h2><button type="button" aria-label="切换收藏" onClick={() => onFavorite(selected)}>{selected.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}</button></div><p className="preview-body">{selected.content || "这条话术还没有正文，点击编辑补充内容。"}</p><div className="preview-fields"><div><span>分类路径</span><strong>{selected.categoryName || "未分类"}</strong></div><div><span>标签</span><strong>{(selected.tags || []).map((tag) => tag.name).join(" / ") || "未添加"}</strong></div><div><span>快捷键</span><span className="key-hint">{selected.shortcut?.display || "未设置"}</span></div></div><div className="preview-divider" /><div className="preview-stats"><span>使用 {selected.usageCount} 次</span><span>最近 {formatLastUsed(selected.lastUsedAtUtc)}</span></div><button type="button" className="button secondary preview-edit" onClick={() => onEdit(selected)}><Edit20Regular /> 编辑话术</button></> : <div className="library-empty"><Document20Regular /><strong>还没有话术</strong><span>点击右上角新建一条标准回复</span></div>}</aside></div><div className="library-status" role="status">{status}</div></section>;
}

function LibraryView({ listRef, phrases, allPhrases, categories, selected, context, status, newCategoryName, setNewCategoryName, onContextChange, onCreateCategory, onNew, onEdit, onFavorite }) {
  const filterLabel = context.activeFilter === "all" ? "全部话术" : context.activeFilter === "favorite" ? "收藏" : context.activeFilter === "recent" ? "最近使用" : categories.find((category) => `category:${category.id}` === context.activeFilter)?.name || "全部话术";
  return <section className="library-page"><div className="library-breadcrumb">话术库 <span>·</span> {filterLabel}</div><div className="library-layout"><aside className="library-sidebar" aria-label="话术筛选"><div className="sidebar-heading">我的话术</div><SidebarItem icon={<Document20Regular />} label="全部话术" count={allPhrases.length} active={context.activeFilter === "all"} onClick={() => onContextChange({ activeFilter: "all", scrollTop: 0 })} /><SidebarItem icon={<Star20Regular />} label="收藏" count={allPhrases.filter((phrase) => phrase.favorite).length} active={context.activeFilter === "favorite"} onClick={() => onContextChange({ activeFilter: "favorite", scrollTop: 0 })} /><SidebarItem icon={<Clock20Regular />} label="最近使用" count={allPhrases.length} active={context.activeFilter === "recent"} onClick={() => onContextChange({ activeFilter: "recent", scrollTop: 0 })} /><div className="sidebar-divider" /><div className="sidebar-heading category-heading">分类</div>{categories.map((category) => <SidebarItem key={category.id} icon={<Folder20Regular />} label={category.name} count={allPhrases.filter((phrase) => phrase.categoryId === category.id).length} active={context.activeFilter === `category:${category.id}`} onClick={() => onContextChange({ activeFilter: `category:${category.id}`, scrollTop: 0 })} />)}<div className="new-category-row"><input value={newCategoryName} onChange={(event) => setNewCategoryName(event.target.value)} onKeyDown={(event) => event.key === "Enter" && onCreateCategory()} placeholder="分类名称" aria-label="新分类名称" /><button type="button" onClick={onCreateCategory}><Add20Regular /> 新建分类</button></div></aside><label className="mobile-filter"><span>当前分类</span><select value={context.activeFilter} onChange={(event) => onContextChange({ activeFilter: event.target.value, scrollTop: 0 })}><option value="all">全部话术</option><option value="favorite">收藏</option><option value="recent">最近使用</option>{categories.map((category) => <option key={category.id} value={`category:${category.id}`}>{category.name}</option>)}</select><ChevronDown20Regular /></label><div ref={listRef} className="library-list" onScroll={(event) => onContextChange({ scrollTop: event.currentTarget.scrollTop })}><div className="library-list-head"><div><span className="section-kicker">{filterLabel}</span><h1>{filterLabel}</h1></div><span className="result-count">{phrases.length} 条结果</span></div><label className="library-search"><Search20Regular /><input value={context.query} onChange={(event) => onContextChange({ query: event.target.value, scrollTop: 0 })} aria-label="主窗口搜索话术" placeholder="搜索标题、正文或拼音..." /><span className="key-hint">Ctrl K</span></label><div className="phrase-list">{phrases.map((phrase) => <div role="button" tabIndex="0" key={phrase.id} className={`phrase-row ${selected?.id === phrase.id ? "is-selected" : ""}`} onClick={() => onContextChange({ selectedId: phrase.id })} onKeyDown={(event) => event.key === "Enter" && onContextChange({ selectedId: phrase.id })}><span className="phrase-row-icon"><Document20Regular /></span><span className="phrase-row-main"><strong>{phrase.title}</strong><small>{phrase.content}</small><span className="row-meta">{(phrase.tags || []).slice(0, 2).map((tag) => <i key={tag.id}>{tag.name}</i>)}<em>{phrase.shortcut?.display || "未设置"}</em></span></span><span className="phrase-row-side"><button type="button" aria-label={phrase.favorite ? `取消收藏 ${phrase.title}` : `收藏 ${phrase.title}`} onClick={(event) => { event.stopPropagation(); onFavorite(phrase); }}>{phrase.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}</button><small>{formatLastUsed(phrase.lastUsedAtUtc)}</small></span></div>)}{!phrases.length ? <div className="library-empty"><Search20Regular /><strong>没有找到话术</strong><span>尝试清除搜索或切换分类</span></div> : null}</div></div><aside className="phrase-preview" aria-label="话术预览">{selected ? <><div className="preview-kicker">话术预览</div><div className="preview-title-line"><h2>{selected.title}</h2><button type="button" aria-label="切换收藏" onClick={() => onFavorite(selected)}>{selected.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}</button></div><p className="preview-body">{selected.content || "这条话术还没有正文，点击编辑补充内容。"}</p><div className="preview-fields"><div><span>分类</span><strong>{selected.categoryName || "未分类"}</strong></div><div><span>标签</span><strong>{(selected.tags || []).map((tag) => tag.name).join(" / ") || "未添加"}</strong></div><div><span>快捷键</span><span className="key-hint">{selected.shortcut?.display || "未设置"}</span></div></div><div className="preview-divider" /><div className="preview-stats"><span>使用 {selected.usageCount} 次</span><span>最近 {formatLastUsed(selected.lastUsedAtUtc)}</span></div><button type="button" className="button secondary preview-edit" onClick={() => onEdit(selected)}><Edit20Regular /> 编辑话术</button></> : <div className="library-empty"><Document20Regular /><strong>还没有话术</strong><span>点击右上角新建一条标准回复</span></div>}</aside></div><div className="library-status" role="status">{status}</div></section>;
}
function PhraseContextMenu({ phrase, x, y, onClose, onInsert, onEdit, onFavorite, onMove, onSort, onDelete }) {
  useEffect(() => {
    const close = () => onClose();
    window.addEventListener("click", close, { once: true });
    return () => window.removeEventListener("click", close);
  }, [onClose]);
  return <div className="phrase-context-menu" role="menu" style={{ left: `${x}px`, top: `${y}px` }} onClick={(event) => event.stopPropagation()}>
    <button type="button" onClick={onInsert}>插入到输入区（双击）</button>
    <button type="button" onClick={() => { navigator.clipboard?.writeText(phrase.content || "").catch(() => {}); onClose(); }}>复制内容到剪贴板</button>
    <button type="button" onClick={onEdit}>编辑</button>
    <button type="button" onClick={onFavorite}>{phrase.favorite ? "取消收藏" : "收藏"}</button>
    <button type="button" onClick={onEdit}>设置颜色</button>
    <button type="button" onClick={onEdit}>设置快捷键</button>
    <button type="button" onClick={onSort}>排序：{phrase.sortLabel || "切换顺序"}</button>
    <button type="button" onClick={onMove}>移动到其他分类</button>
    <button type="button" className="danger-link" onClick={onDelete}>删除</button>
  </div>;
}

function PhraseMoveDialog({ phrase, categories, onClose, onMove }) {
  const [categoryId, setCategoryId] = useState(phrase.categoryId || "");
  return <div className="navigation-scrim"><section className="category-dialog" role="dialog" aria-modal="true" aria-labelledby="phrase-move-title"><h2 id="phrase-move-title">移动话术</h2><p>{phrase.title}</p><label className="field"><span>目标分类</span><select autoFocus value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>{categories.map((category) => <option key={category.id} value={category.id}>{categoryPath(categories, category.id)}</option>)}</select></label><div className="category-dialog-actions"><button type="button" className="button secondary" onClick={onClose}>取消</button><button type="button" className="button primary" disabled={!categoryId || categoryId === (phrase.categoryId || "")} onClick={() => onMove(categoryId)}>移动</button></div></section></div>;
}

function SidebarItem({ icon, label, count, active, onClick }) { return <button type="button" className={`sidebar-item ${active ? "is-active" : ""}`} aria-current={active ? "page" : undefined} onClick={onClick}>{icon}<span>{label}</span><small>{count}</small></button>; }

function CategoryDialog({ categories, mode, category, onClose, onCreate, onMove, onRename, onDelete, onOpenMove, onOpenCreate }) {
  const [parentId, setParentId] = useState(category?.parentId || "");
  const [name, setName] = useState("");
  if (mode === "menu") return <div className="navigation-scrim"><section className="category-dialog" role="dialog" aria-modal="true" aria-labelledby="category-menu-title"><h2 id="category-menu-title">{category.name}</h2><p className="category-dialog-path">{categoryPath(categories, category.id)}</p><div className="category-menu-actions"><button type="button" onClick={() => { onClose(); onOpenCreate(category.id); }} disabled={categoryDepth(categories, category.id) >= 2}>新建子分类</button><button type="button" onClick={onClose}>关闭</button><button type="button" onClick={() => { onClose(); onRename(category); }}>重命名</button><button type="button" onClick={() => { onClose(); onOpenMove(category); }}>移动到...</button><button type="button" className="danger-link" onClick={() => { onClose(); onDelete(category); }}>删除</button></div></section></div>;
  const moveMode = mode === "move";
  const options = categories.filter((item) => !moveMode || (item.id !== category?.id && !descendantIds(categories, category?.id).has(item.id) && categoryDepth(categories, item.id) < 2));
  return <div className="navigation-scrim"><section className="category-dialog" role="dialog" aria-modal="true" aria-labelledby="category-dialog-title"><h2 id="category-dialog-title">{moveMode ? "移动分类" : "新建分类"}</h2>{moveMode ? <p>{categoryPath(categories, category.id)}</p> : <label className="field"><span>分类名称</span><input autoFocus value={name} onChange={(event) => setName(event.target.value)} placeholder="例如：退款" /></label>}<label className="field"><span>{moveMode ? "移动到" : "父分类"}</span><select value={parentId} onChange={(event) => setParentId(event.target.value)}><option value="">一级分类</option>{options.map((item) => <option key={item.id} value={item.id}>{categoryPath(categories, item.id)}</option>)}</select></label><div className="category-dialog-actions"><button type="button" className="button secondary" onClick={onClose}>取消</button><button type="button" className="button primary" disabled={!moveMode && !name.trim()} onClick={() => moveMode ? onMove(parentId || null) : onCreate(parentId || null, name)}>{moveMode ? "移动" : "创建"}</button></div></section></div>;
}

function EditorView({ draft, categories, phrases, tagInput, setTagInput, setDraft, onSave, onDelete, onCancel }) {
  const [deletePrompt, setDeletePrompt] = useState(false);
  if (!draft) return <div className="host-loading">正在准备编辑器…</div>;
  const updateDraft = (key, value) => setDraft((current) => ({ ...current, [key]: value }));
  const addTag = () => { const tag = tagInput.trim(); if (!tag || draft.tags.includes(tag)) return; setDraft((current) => ({ ...current, tags: [...current.tags, tag] })); setTagInput(""); };
  const conflict = draft.shortcut && phrases.find((phrase) => phrase.id !== draft.id && phrase.shortcut?.normalized === draft.shortcut.replace(/\s+/g, "").toLowerCase());
  return <section className="editor-page"><div className="editor-breadcrumb"><button type="button" onClick={onCancel}><ArrowLeft20Regular /> 话术库</button><span>/</span><strong>{draft.version ? "编辑话术" : "新建话术"}</strong></div><div className="editor-heading"><span className="section-kicker">PHRASE EDITOR</span><h1>{draft.version ? "编辑话术" : "新建话术"}</h1><p>把一条回复，变成下一次的快捷入口。</p>{draft.categoryId ? <small className="editor-category-path">分类路径：{categoryPath(categories, draft.categoryId)}</small> : null}</div><section className="editor-card"><div className="editor-form"><Field label="话术标题"><input aria-label="话术标题" value={draft.title} onChange={(event) => updateDraft("title", event.target.value)} /></Field><Field label="话术正文"><textarea aria-label="话术正文" value={draft.content} onChange={(event) => updateDraft("content", event.target.value)} rows="5" placeholder="输入这条话术的正文..." /><span className="field-count">{draft.content.length}/4000</span></Field><div className="field"><span>标题颜色</span><div className="color-picker" role="radiogroup" aria-label="选择话术颜色">{["default", "red", "orange", "yellow", "green", "blue", "purple", "gray"].map((color) => <button type="button" key={color} className={`color-swatch color-${color} ${draft.colorKey === color ? "is-selected" : ""}`} aria-label={color === "default" ? "默认颜色" : color} aria-pressed={draft.colorKey === color} onClick={() => updateDraft("colorKey", color)} />)}</div><small className="field-help">颜色只用于快速识别，不改变话术正文。</small></div><div className="form-columns"><Field label="分类"><select value={draft.categoryId} onChange={(event) => updateDraft("categoryId", event.target.value)}>{categories.map((category) => <option key={category.id} value={category.id}>{categoryPath(categories, category.id)}</option>)}</select></Field><div className="field shortcut-field"><label htmlFor="shortcut-mode">独立快捷键 <small>可选高级能力</small></label><div className="shortcut-mode-row"><select id="shortcut-mode" value={draft.shortcutMode} onChange={(event) => { const mode = event.target.value; updateDraft("shortcutMode", mode); updateDraft("shortcut", mode === "Quick" ? QUICK_SHORTCUTS[0] : mode === "None" ? "" : draft.shortcut); }}><option value="None">不设置</option><option value="Quick">高频槽位 Alt+1～9</option><option value="Custom">自定义</option></select>{draft.shortcutMode === "Quick" ? <select aria-label="高频快捷键" value={draft.shortcut || QUICK_SHORTCUTS[0]} onChange={(event) => updateDraft("shortcut", event.target.value)}>{QUICK_SHORTCUTS.map((shortcut) => <option key={shortcut}>{shortcut}</option>)}</select> : null}{draft.shortcutMode === "Custom" ? <input aria-label="自定义快捷键" value={draft.shortcut} placeholder="输入组合键" onChange={(event) => updateDraft("shortcut", event.target.value)} /> : null}</div>{conflict ? <small className="field-error">{draft.shortcut} 已被「{conflict.title}」占用，请改选空闲槽位。</small> : null}</div></div><div className="field"><label htmlFor="tag-input">标签</label><div className="tag-input">{draft.tags.map((tag) => <span className="tag" key={tag}>{tag}<button type="button" aria-label={`移除标签 ${tag}`} onClick={() => updateDraft("tags", draft.tags.filter((item) => item !== tag))}><Dismiss20Regular /></button></span>)}<input id="tag-input" aria-label="添加标签" value={tagInput} placeholder="添加标签后按 Enter" onChange={(event) => setTagInput(event.target.value)} onKeyDown={(event) => event.key === "Enter" && (event.preventDefault(), addTag())} /></div></div><div className="send-mode-note"><Send20Regular /><span><strong>投递方式</strong><small>双击或 Enter 执行安全插入；未验证目标时只复制，不直接发送。</small></span></div>{deletePrompt ? <div className="delete-confirm" role="alert"><span>确定删除这条话术？</span><button type="button" onClick={onDelete}>确认删除</button><button type="button" onClick={() => setDeletePrompt(false)}>取消</button></div> : null}</div><div className="editor-footer"><button type="button" className="icon-delete" onClick={() => setDeletePrompt(true)} disabled={!draft.version}><Delete20Regular /> 删除</button><div><button type="button" className="button secondary" onClick={onCancel}>取消</button><button type="button" className="button primary" disabled={Boolean(conflict)} onClick={onSave}><Checkmark20Regular /> 保存话术</button></div></div></section></section>;
}

function SettingsViewV2({ settings, setSettings, section, setSection, hotkeyStatus, adapterStatus, adapterCatalog, onSave, onCancel }) {
  if (!settings) return <div className="host-loading">正在准备设置…</div>;
  const update = (key) => setSettings((current) => ({ ...current, [key]: !current[key] }));
  const updateAdapter = (adapterId) => setSettings((current) => ({ ...current, launcherEnabledAdapters: { ...(current.launcherEnabledAdapters || {}), [adapterId]: !(current.launcherEnabledAdapters || {})[adapterId] } }));
  const jump = (next) => { setSection(next); document.getElementById(`settings-${next}`)?.scrollIntoView({ behavior: "smooth", block: "start" }); };
  const catalog = adapterCatalog || [];
  return <section className="settings-page"><div className="settings-heading"><span className="section-kicker">PREFERENCES</span><h1>设置</h1><p>管理本机快捷键与已登记应用的 Launcher 呼出权限。</p></div><div className="settings-layout"><nav className="settings-nav" aria-label="设置分组"><SettingsNavButton icon={<Settings20Regular />} label="通用" active={section === "general"} onClick={() => jump("general")} /><SettingsNavButton icon={<Flash20Regular />} label="快捷键" active={section === "shortcuts"} onClick={() => jump("shortcuts")} /><SettingsNavButton icon={<Send20Regular />} label="发送行为" active={section === "delivery"} onClick={() => jump("delivery")} /><SettingsNavButton icon={<Globe20Regular />} label="应用适配" active={section === "adapters"} onClick={() => jump("adapters")} /></nav><div className="settings-content"><SettingsSection id="settings-general" title="通用"><SettingToggle label="开机启动" checked={settings.launchOnStartup} onChange={() => update("launchOnStartup")} /><SettingToggle label="关闭窗口后驻留托盘" checked={settings.stayInTrayOnClose} onChange={() => update("stayInTrayOnClose")} /></SettingsSection><SettingsSection id="settings-shortcuts" title="快捷键"><div className="settings-hotkey"><span>Quick Launcher 全局呼出</span><span className="key-hint">{settings.launcherShortcutDisplay || "Alt + Space"}</span><span className={`setting-state ${hotkeyStatus?.launcher?.conflict ? "bad" : "good"}`}>{hotkeyStatus?.launcher?.configured ? (hotkeyStatus?.launcher?.registered ? "已注册" : hotkeyStatus?.launcher?.conflict ? "冲突" : "未激活") : "未设置"}</span></div><p className="settings-help">仅在已开启的适配应用中生效；关闭适配后不会吞掉当前应用原有的 Alt + Space。</p><div className="settings-hotkey"><span>主窗口内部搜索</span><span className="key-hint">Ctrl + K</span><span className="setting-state good">可用</span></div></SettingsSection><SettingsSection id="settings-delivery" title="发送行为"><div className="readonly-setting"><span>自动发送</span><strong className="setting-state soft">不支持</strong></div><SettingToggle label="剪贴板兼容模式" checked={settings.clipboardCompatibilityMode} onChange={() => update("clipboardCompatibilityMode")} /><p className="settings-help">自动发送能力由应用适配 Profile 决定，开关不会绕过能力验证。</p></SettingsSection><SettingsSection id="settings-adapters" title="应用适配"><p className="settings-help">只有开发者登记的应用会出现在这里。开启后，前台目标匹配该 Profile 时才允许 Alt + Space 呼出。</p><div className="adapter-grid">{catalog.map((adapter) => { const enabled = Boolean((settings.launcherEnabledAdapters || {})[adapter.adapterId]); return <div className="adapter-card" key={adapter.adapterId}><div className="adapter-row"><span className="adapter-icon"><Globe20Regular /></span><span><strong>{adapter.displayName || adapter.adapterId}</strong><small>支持版本 {adapter.supportedProductVersion || "未声明"} · Profile {adapter.profileVersion || "未声明"}</small></span><label className="adapter-switch"><input type="checkbox" checked={enabled} onChange={() => updateAdapter(adapter.adapterId)} /><span>{enabled ? "已开启" : "已关闭"}</span></label></div><div className="adapter-capabilities"><span>插入：{capabilityLabel(adapter.insertText)}</span><span>验证插入：{capabilityLabel(adapter.verifyInsert)}</span><span>自动发送：{capabilityLabel(adapter.sendText)}</span><span>安全复制：{adapter.fallbackMode === "CopyOnly" ? "可用" : "未声明"}</span></div></div>; })}{!catalog.length ? <div className="library-empty"><Globe20Regular /><strong>暂无已登记应用</strong><span>需要开发者先提供 Adapter/Profile。</span></div> : null}</div></SettingsSection></div></div><div className="settings-footer"><span>关闭应用适配只影响全局 Launcher 呼出，不影响托盘和管理页测试入口。</span><div><button type="button" className="button secondary" onClick={onCancel}>取消</button><button type="button" className="button primary" onClick={onSave}>完成</button></div></div></section>;
}

function SettingsView({ settings, setSettings, section, setSection, hotkeyStatus, adapterStatus, onSave, onCancel }) {
  if (!settings) return <div className="host-loading">正在准备设置…</div>;
  const update = (key) => setSettings((current) => ({ ...current, [key]: !current[key] }));
  const jump = (next) => { setSection(next); document.getElementById(`settings-${next}`)?.scrollIntoView({ behavior: "smooth", block: "start" }); };
  const adapterName = adapterStatus?.adapterId === "WXWork" ? "企业微信" : "当前应用";
  return <section className="settings-page"><div className="settings-heading"><span className="section-kicker">PREFERENCES</span><h1>设置</h1><p>统一管理快捷键、发送行为和应用适配，设置仅保存于本机。</p></div><div className="settings-layout"><nav className="settings-nav" aria-label="设置分组"><SettingsNavButton icon={<Settings20Regular />} label="通用" active={section === "general"} onClick={() => jump("general")} /><SettingsNavButton icon={<Flash20Regular />} label="快捷键" active={section === "shortcuts"} onClick={() => jump("shortcuts")} /><SettingsNavButton icon={<Send20Regular />} label="发送行为" active={section === "delivery"} onClick={() => jump("delivery")} /><SettingsNavButton icon={<Globe20Regular />} label="应用适配" active={section === "adapters"} onClick={() => jump("adapters")} /></nav><div className="settings-content"><SettingsSection id="settings-general" title="通用"><SettingToggle label="开机启动" checked={settings.launchOnStartup} onChange={() => update("launchOnStartup")} /><SettingToggle label="关闭窗口后驻留托盘" checked={settings.stayInTrayOnClose} onChange={() => update("stayInTrayOnClose")} /></SettingsSection><SettingsSection id="settings-shortcuts" title="快捷键"><div className="settings-hotkey"><span>Quick Launcher 全局呼出</span><span className="key-hint">{settings.launcherShortcutDisplay || "Alt + Space"}</span><span className={`setting-state ${hotkeyStatus?.launcher?.conflict ? "bad" : "good"}`}>{hotkeyStatus?.launcher?.conflict ? "冲突" : "可用"}</span></div><div className="settings-hotkey"><span>主窗口内部搜索</span><span className="key-hint">Ctrl + K</span><span className="setting-state good">可用</span></div></SettingsSection><SettingsSection id="settings-delivery" title="发送行为"><SettingToggle label="允许已验证应用自动发送" checked={settings.autoSend} onChange={() => update("autoSend")} /><SettingToggle label="剪贴板兼容模式" checked={settings.clipboardCompatibilityMode} onChange={() => update("clipboardCompatibilityMode")} /><p className="settings-help">自动发送只会在目标、适配器和发送能力均重新验证通过时执行。</p></SettingsSection><SettingsSection id="settings-adapters" title="应用适配"><div className="adapter-grid"><div className="adapter-row"><span className="adapter-icon"><Globe20Regular /></span><span><strong>{adapterName}</strong><small>{adapterStatus?.productVersion || "版本未识别"} · Profile {adapterStatus?.profileVersion || "未识别"}</small></span><em className={adapterStatus?.adapterId === "WXWork" ? "good" : "soft"}>{adapterStatus?.adapterId === "WXWork" ? "已识别" : "未捕获"}</em></div><div className="adapter-capabilities"><span>插入：{capabilityLabel(adapterStatus?.insertText)}</span><span>验证插入：{capabilityLabel(adapterStatus?.verifyInsert)}</span><span>自动发送：{capabilityLabel(adapterStatus?.sendText)}</span><small>未验证能力会安全降级为复制，请按 Ctrl + V 粘贴。</small></div></div></SettingsSection></div></div><div className="settings-footer"><span>未保存的设置不会影响投递行为</span><div><button type="button" className="button secondary" onClick={onCancel}>取消</button><button type="button" className="button primary" onClick={onSave}>完成</button></div></div></section>;
}
function SettingsNavButton({ icon, label, active, onClick }) { return <button type="button" className={active ? "is-active" : ""} aria-current={active ? "page" : undefined} onClick={onClick}>{icon}<span>{label}</span></button>; }
function SettingsSection({ id, title, children }) { return <section id={id} className="settings-section"><h2>{title}</h2>{children}</section>; }
function SettingToggle({ label, checked, onChange }) { return <label className="setting-toggle"><span>{label}</span><input type="checkbox" checked={checked} onChange={onChange} /><span className="toggle-track"><span /></span></label>; }
function Field({ label, children }) { return <label className="field"><span>{label}</span>{children}</label>; }
function NavigationConfirm({ onChoice }) { return <div className="navigation-scrim"><section className="navigation-dialog" role="dialog" aria-modal="true" aria-labelledby="navigation-dialog-title"><div className="navigation-dialog-icon"><Edit20Regular /></div><h2 id="navigation-dialog-title">还有未保存的改动</h2><p>离开当前页面前，先处理这些改动。</p><div className="navigation-dialog-actions"><button type="button" className="button primary" onClick={() => onChoice("save")}>保存并离开</button><button type="button" className="button secondary" onClick={() => onChoice("discard")}>放弃改动</button><button type="button" className="dialog-link" autoFocus onClick={() => onChoice("continue")}>继续编辑</button></div></section></div>; }
