import { useEffect, useMemo, useRef, useState } from "react";
import {
  Add20Regular, AppsList20Regular, ArrowDown20Regular, ArrowRight20Regular, ArrowUp20Regular,
  BatteryCharge20Regular, Checkmark20Regular, ChevronDown20Regular, ChevronUp20Regular,
  Chat20Regular, ChatBubblesQuestion20Regular, Clock20Regular, Delete20Regular,
  Dismiss20Regular, Document20Regular, Edit20Regular, Flash20Regular, Folder20Regular,
  Globe20Regular, MoreHorizontal20Regular, Pin20Regular, Search20Regular, Send20Regular,
  Settings20Regular, Speaker2Regular, Square20Regular, Star20Filled, Star20Regular,
  Subtract20Regular, WeatherPartlyCloudyDay20Regular, Wifi120Regular, WindowApps20Regular,
} from "@fluentui/react-icons";
import {
  appAdapters, categoryOptions, initialPhrases, searchPhrases,
} from "./data";

const sceneLabels = [
  { id: "library", label: "话术库" },
  { id: "launcher", label: "Quick Launcher" },
  { id: "editor", label: "编辑话术" },
  { id: "settings", label: "设置" },
];
const quickShortcutOptions = Array.from({ length: 9 }, (_, index) => `Alt + ${index + 1}`);
const defaultSettings = { launchOnStartup: true, minimizeToTray: true, autoSend: false, clipboardMode: true, hotkey: "Alt + Space" };

function IconButton({ label, onClick, children, className = "" }) {
  return <button type="button" className={`icon-button ${className}`} aria-label={label} title={label} onClick={onClick}>{children}</button>;
}
function KeyHint({ children }) { return <span className="key-hint">{children}</span>; }
function WindowTitle({ icon, title, subtitle, onClose, actions }) {
  return <div className="window-titlebar"><div className="window-title-icon">{icon}</div><div className="window-title-copy"><strong>{title}</strong>{subtitle ? <span>{subtitle}</span> : null}</div><div className="window-title-actions">{actions}{onClose ? <IconButton label="关闭" onClick={onClose}><Dismiss20Regular /></IconButton> : null}</div></div>;
}

/**
 * 原型控制层：统一管理四个场景、Launcher 状态和演示器状态入口。
 * 这里的快捷键、应用识别与插入均为会话内模拟，不触碰真实操作系统接口。
 */
export function App() {
  const [scene, setScene] = useState("library");
  const previousScene = useRef("library");
  const [phrases, setPhrases] = useState(initialPhrases);
  const [selectedId, setSelectedId] = useState("factory-reset");
  const [activeFilter, setActiveFilter] = useState("全部话术");
  const [libraryQuery, setLibraryQuery] = useState("");
  const [launcherQuery, setLauncherQuery] = useState("hfcc");
  const [launcherIndex, setLauncherIndex] = useState(0);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [launcherContext, setLauncherContext] = useState({ status: "recognized", appName: "企业微信", recipient: "张先生" });
  const [hotkeyStatus, setHotkeyStatus] = useState("available");
  const [prototypeState, setPrototypeState] = useState("normal");
  const [onboardingOpen, setOnboardingOpen] = useState(false);
  const [toast, setToast] = useState("");
  const [settings, setSettings] = useState(defaultSettings);
  const [shortcutsPaused, setShortcutsPaused] = useState(false);
  const [trayOpen, setTrayOpen] = useState(false);
  const [draft, setDraft] = useState(() => ({ ...initialPhrases[0], tags: [...initialPhrases[0].tags] }));
  const [deletePrompt, setDeletePrompt] = useState(false);
  const [newCategory, setNewCategory] = useState(false);
  const [categoryName, setCategoryName] = useState("");
  const launcherInputRef = useRef(null);
  const libraryInputRef = useRef(null);
  const toastTimer = useRef(null);

  const selectedPhrase = phrases.find((phrase) => phrase.id === selectedId) || phrases[0];
  const launcherResults = useMemo(() => searchPhrases(phrases, launcherQuery), [phrases, launcherQuery]);
  const libraryResults = useMemo(() => searchPhrases(phrases, libraryQuery), [phrases, libraryQuery]);
  const filteredLibrary = useMemo(() => {
    const source = libraryQuery ? libraryResults : phrases;
    if (activeFilter === "收藏") return source.filter((phrase) => phrase.favorite);
    if (activeFilter === "最近使用") return [...source].sort((a, b) => b.usageCount - a.usageCount);
    if (categoryOptions.includes(activeFilter)) return source.filter((phrase) => phrase.category === activeFilter);
    return source;
  }, [activeFilter, libraryQuery, libraryResults, phrases]);

  useEffect(() => { setDraft({ ...selectedPhrase, tags: [...selectedPhrase.tags] }); }, [selectedId]);
  useEffect(() => { setLauncherIndex(0); }, [launcherQuery]);
  useEffect(() => {
    if (scene === "launcher") window.setTimeout(() => launcherInputRef.current?.focus(), 50);
    if (scene === "library") window.setTimeout(() => libraryInputRef.current?.focus(), 50);
  }, [scene]);
  useEffect(() => {
    const onKeyDown = (event) => {
      if (event.altKey && event.code === "Space") {
        event.preventDefault();
        if (shortcutsPaused) { showToast("快捷键已暂停"); return; }
        if (scene === "launcher") closeLauncher(); else openLauncher();
      }
      if (event.ctrlKey && event.key.toLowerCase() === "k" && scene === "library") {
        event.preventDefault();
        libraryInputRef.current?.focus();
        showToast("已聚焦主窗口搜索");
      }
      if (event.key === "Escape" && scene === "launcher") closeLauncher();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [scene, shortcutsPaused]);
  useEffect(() => () => window.clearTimeout(toastTimer.current), []);

  const showToast = (message) => { setToast(message); window.clearTimeout(toastTimer.current); toastTimer.current = window.setTimeout(() => setToast(""), 2800); };
  const openScene = (nextScene) => { setTrayOpen(false); setToast(""); setScene(nextScene); };
  const openLauncher = () => { previousScene.current = scene === "launcher" ? previousScene.current : scene; setScene("launcher"); };
  const closeLauncher = () => { setPreviewOpen(false); setScene(previousScene.current || "library"); };
  const selectPhrase = (phrase) => { if (!phrase) return; setSelectedId(phrase.id); setDraft({ ...phrase, tags: [...phrase.tags] }); };
  const toggleFavorite = (id) => setPhrases((current) => current.map((phrase) => phrase.id === id ? { ...phrase, favorite: !phrase.favorite } : phrase));

  // 插入路径只更新最近使用与反馈；unsupported 状态明确降级为复制提示。
  const performInsert = (phrase, send = false) => {
    setSelectedId(phrase.id);
    setPhrases((current) => current.map((item) => item.id === phrase.id ? { ...item, usageCount: item.usageCount + 1, lastUsed: "刚刚" } : item));
    closeLauncher();
    if (launcherContext.status === "unsupported") showToast(`已复制「${phrase.title}」到剪贴板，请按 Ctrl + V 粘贴`);
    else showToast(send ? `已插入并发送「${phrase.title}」` : `已插入「${phrase.title}」`);
  };
  const insertWithContext = (phrase, send = false) => {
    if (!phrase) { showToast("没有找到可插入的话术"); return; }
    performInsert(phrase, send);
  };

  const createPhrase = (seed = "") => {
    const id = `phrase-${Date.now()}`;
    const newPhrase = { id, title: seed.trim() || "新建话术", body: "", category: categoryOptions[0], tags: ["客服"], keywords: seed.trim() ? [seed.trim()] : [], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 0, lastUsed: "未使用" };
    setPhrases((current) => [newPhrase, ...current]); setSelectedId(id); setDraft({ ...newPhrase, tags: [...newPhrase.tags] }); openScene("editor"); showToast("已创建新话术，请继续编辑");
  };
  // 保存前校验高频槽位唯一性，避免两个话术同时占用同一个 Alt+数字。
  const saveDraft = () => {
    if (!draft.title.trim() || !draft.body.trim()) { showToast("请先填写话术标题和正文"); return; }
    const conflict = draft.shortcut && phrases.find((phrase) => phrase.id !== draft.id && phrase.shortcut === draft.shortcut);
    if (conflict) { showToast(`${draft.shortcut} 已被「${conflict.title}」占用`); return; }
    setPhrases((current) => current.map((phrase) => phrase.id === draft.id ? { ...phrase, ...draft, title: draft.title.trim(), body: draft.body.trim() } : phrase)); openScene("library"); showToast("话术已保存");
  };
  const deleteSelectedPhrase = () => {
    if (!deletePrompt) { setDeletePrompt(true); return; }
    const remaining = phrases.filter((phrase) => phrase.id !== selectedId); setPhrases(remaining); setDeletePrompt(false); if (remaining[0]) setSelectedId(remaining[0].id); openScene("library"); showToast("话术已删除");
  };
  const updateDraft = (key, value) => setDraft((current) => ({ ...current, [key]: value }));
  const createCategory = () => { if (!categoryName.trim()) { showToast("请输入分类名称"); return; } showToast(`已创建分类「${categoryName.trim()}」`); setCategoryName(""); setNewCategory(false); };
  const applyPrototypeState = (value) => {
    setPrototypeState(value);
    setHotkeyStatus(value === "conflict" ? "conflict" : "available");
    setLauncherContext((current) => ({ ...current, status: value === "unsupported" ? "unsupported" : "recognized" }));
    if (value === "first-run") { setOnboardingOpen(true); setScene("library"); } else setOnboardingOpen(false);
  };
  const tryFirstRun = () => { setOnboardingOpen(false); setPrototypeState("normal"); setLauncherContext((current) => ({ ...current, status: "recognized" })); setLauncherQuery("hf"); openLauncher(); };

  return <main className={`desktop-stage scene-${scene}`}>
    <div className="desktop-topbar"><div className="system-brand"><span className="brand-mark"><ChatBubblesQuestion20Regular /></span><span>闪语</span></div><div className="system-caption">Windows 效率工具 · 本地工作区</div><div className="window-controls"><Subtract20Regular /><Square20Regular /><Dismiss20Regular /></div></div>
    <div className="prototype-switcher"><span>原型场景</span>{sceneLabels.map((item) => <button key={item.id} type="button" className={scene === item.id ? "is-active" : ""} onClick={() => openScene(item.id)}>{item.label}</button>)}<label className="prototype-state"><span>状态</span><select aria-label="原型状态" value={prototypeState} onChange={(event) => applyPrototypeState(event.target.value)}><option value="normal">正常</option><option value="unsupported">目标未识别</option><option value="conflict">快捷键冲突</option><option value="first-run">首次使用</option></select></label></div>
    {scene === "library" ? <LibraryScene {...{ activeFilter, setActiveFilter, libraryQuery, setLibraryQuery, libraryInputRef, filteredLibrary, phrases, selectedPhrase, selectedId, selectPhrase, toggleFavorite, createPhrase, openScene, newCategory, setNewCategory, categoryName, setCategoryName, createCategory }} /> : null}
    {scene === "launcher" ? <LauncherScene {...{ launcherInputRef, launcherQuery, setLauncherQuery, launcherResults, launcherIndex, setLauncherIndex, previewOpen, setPreviewOpen, insertWithContext, closeLauncher, createPhrase, launcherContext }} /> : null}
    {scene === "editor" ? <EditorScene {...{ draft, updateDraft, saveDraft, deletePrompt, setDeletePrompt, deleteSelectedPhrase, openScene, phrases }} /> : null}
    {scene === "settings" ? <SettingsScene {...{ settings, setSettings, appAdapters, openScene, showToast, hotkeyStatus, setHotkeyStatus }} /> : null}
    <DesktopTaskbar {...{ openLauncher, trayOpen, setTrayOpen, createPhrase, shortcutsPaused, setShortcutsPaused, openScene }} />
    <div className="desktop-shortcuts"><span>快速插入</span><span className="shortcut-separator" /><span><KeyHint>Alt + Space</KeyHint> 独立呼出</span><span><KeyHint>Ctrl + K</KeyHint> 主窗口搜索</span></div>
    {onboardingOpen ? <OnboardingOverlay onTry={tryFirstRun} onClose={() => { setOnboardingOpen(false); setPrototypeState("normal"); }} /> : null}
    {toast ? <div className="toast"><Checkmark20Regular /><span>{toast}</span></div> : null}
  </main>;
}

function LibraryScene({ activeFilter, setActiveFilter, libraryQuery, setLibraryQuery, libraryInputRef, filteredLibrary, phrases, selectedPhrase, selectedId, selectPhrase, toggleFavorite, createPhrase, openScene, newCategory, setNewCategory, categoryName, setCategoryName, createCategory }) {
  const categories = categoryOptions.map((category) => ({ name: category, count: phrases.filter((phrase) => phrase.category === category).length }));
  return <section className="app-window library-window"><WindowTitle icon={<AppsList20Regular />} title="话术库" subtitle={`${phrases.length} 条本地话术`} actions={<button type="button" className="button primary small" onClick={createPhrase}><Add20Regular /> 新建话术</button>} /><div className="library-layout"><aside className="library-sidebar"><div className="sidebar-heading">我的话术</div><SidebarItem icon={<Document20Regular />} label="全部话术" count={phrases.length} active={activeFilter === "全部话术"} onClick={() => setActiveFilter("全部话术")} /><SidebarItem icon={<Star20Regular />} label="收藏" count={phrases.filter((phrase) => phrase.favorite).length} active={activeFilter === "收藏"} onClick={() => setActiveFilter("收藏")} /><SidebarItem icon={<Clock20Regular />} label="最近使用" count={phrases.length} active={activeFilter === "最近使用"} onClick={() => setActiveFilter("最近使用")} /><div className="sidebar-divider" /><div className="sidebar-heading category-heading">分类</div>{categories.map((category) => <SidebarItem key={category.name} icon={<Folder20Regular />} label={category.name} count={category.count} active={activeFilter === category.name} onClick={() => setActiveFilter(category.name)} />)}<button type="button" className="new-category" onClick={() => setNewCategory(true)}><Add20Regular /> 新建分类</button>{newCategory ? <div className="new-category-form"><input autoFocus value={categoryName} onChange={(event) => setCategoryName(event.target.value)} onKeyDown={(event) => event.key === "Enter" && createCategory()} placeholder="分类名称" /><button type="button" onClick={createCategory}>添加</button></div> : null}</aside><div className="library-list"><div className="library-list-head"><div><span className="section-kicker">{activeFilter}</span><h1>{activeFilter === "全部话术" ? "全部话术" : activeFilter}</h1></div><span className="result-count">{filteredLibrary.length} 条结果</span></div><label className="library-search"><Search20Regular /><input ref={libraryInputRef} value={libraryQuery} onChange={(event) => setLibraryQuery(event.target.value)} aria-label="主窗口搜索话术" placeholder="搜索标题、正文或拼音..." /><KeyHint>Ctrl K</KeyHint></label><div className="phrase-list">{filteredLibrary.map((phrase) => <button type="button" key={phrase.id} className={`phrase-row ${selectedId === phrase.id ? "is-selected" : ""}`} onClick={() => selectPhrase(phrase)}><span className="phrase-row-icon"><Document20Regular /></span><span className="phrase-row-main"><strong>{phrase.title}</strong><small>{phrase.body}</small><span className="row-meta">{phrase.tags.map((tag) => <i key={tag}>{tag}</i>)}<em>{phrase.shortcut || "未设置"}</em></span></span><span className="phrase-row-side">{phrase.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}<small>{phrase.lastUsed}</small></span></button>)}{!filteredLibrary.length ? <div className="library-empty"><Search20Regular /><strong>没有找到话术</strong><span>尝试清除搜索或切换分类</span></div> : null}</div></div><aside className="phrase-preview"><div className="preview-kicker">话术预览</div><div className="preview-title-line"><h2>{selectedPhrase.title}</h2><Star20Filled className="favorite-icon" /></div><p className="preview-body">{selectedPhrase.body || "这条话术还没有正文，点击编辑补充内容。"}</p><div className="preview-fields"><div><span>分类</span><strong>{selectedPhrase.category}</strong></div><div><span>标签</span><strong>{selectedPhrase.tags.join(" / ") || "未添加"}</strong></div><div><span>快捷键</span><KeyHint>{selectedPhrase.shortcut || "未设置"}</KeyHint></div></div><div className="preview-divider" /><div className="preview-stats"><span>使用 {selectedPhrase.usageCount} 次</span><span>最近 {selectedPhrase.lastUsed}</span></div><button type="button" className="button secondary preview-edit" onClick={() => openScene("editor")}><Edit20Regular /> 编辑话术</button></aside></div></section>;
}
function SidebarItem({ icon, label, count, active, onClick }) { return <button type="button" className={`sidebar-item ${active ? "is-active" : ""}`} onClick={onClick}>{icon}<span>{label}</span><small>{count}</small></button>; }

function LauncherScene({ launcherInputRef, launcherQuery, setLauncherQuery, launcherResults, launcherIndex, setLauncherIndex, previewOpen, setPreviewOpen, insertWithContext, closeLauncher, createPhrase, launcherContext }) {
  const selected = launcherResults[launcherIndex] || launcherResults[0];
  const resultClass = launcherResults.length > 4 ? "is-expanded" : launcherResults.length === 0 ? "is-empty" : "";
  return <section className="chat-scene"><div className="chat-window"><div className="chat-titlebar"><div className="chat-app-icon"><Chat20Regular /></div><div><strong>企业微信</strong><span>与 张先生 的对话</span></div><div className="chat-actions"><MoreHorizontal20Regular /></div></div><div className="chat-content"><div className="chat-date">今天 10:30</div><div className="chat-message incoming"><span>张先生</span><p>请问设备要怎么恢复出厂设置？</p><time>10:29</time></div><div className="chat-message outgoing"><p>您好，我帮您确认一下。</p><time>10:30</time></div><div className="chat-composer"><span>输入消息...</span><Send20Regular /></div></div></div><div className="launcher-backdrop" /><section className={`launcher-window ${resultClass}`}><WindowTitle icon={<ChatBubblesQuestion20Regular />} title="QuickPhrase 快速启动" subtitle="当前插入位置" onClose={closeLauncher} actions={<span className={`target-app ${launcherContext.status === "unsupported" ? "is-unsupported" : ""}`}><Chat20Regular /> {launcherContext.appName} · {launcherContext.recipient} <i>{launcherContext.status === "unsupported" ? "!" : ""}</i></span>} /><div className="launcher-body"><label className="launcher-search"><Search20Regular /><input ref={launcherInputRef} value={launcherQuery} onChange={(event) => setLauncherQuery(event.target.value)} onKeyDown={(event) => { if (event.key === "ArrowDown") { event.preventDefault(); setLauncherIndex((index) => Math.min(index + 1, Math.max(launcherResults.length - 1, 0))); } else if (event.key === "ArrowUp") { event.preventDefault(); setLauncherIndex((index) => Math.max(index - 1, 0)); } else if (event.key === "Tab") { event.preventDefault(); setPreviewOpen((open) => !open); } else if (event.key === "Enter") { event.preventDefault(); insertWithContext(selected, event.ctrlKey); } else if (event.key === "Escape") { event.preventDefault(); closeLauncher(); } }} aria-label="搜索话术、拼音或首字母" placeholder="搜索话术、拼音或首字母..." /><button type="button" aria-label="清空搜索" onClick={() => setLauncherQuery("")}><Dismiss20Regular /></button></label><div className="launcher-target-line"><span>插入到</span><strong>{launcherContext.appName} · {launcherContext.recipient}</strong><span className={`online-dot ${launcherContext.status === "unsupported" ? "is-offline" : ""}`} /></div>{launcherContext.status === "unsupported" ? <div className="unsupported-banner"><span>当前应用暂不支持自动插入</span><small>选择后将复制到剪贴板，请按 Ctrl + V 粘贴</small></div> : null}{launcherResults.length ? <><div className="result-label">最佳匹配 · {launcherResults.length} 条</div><div className="launcher-results">{launcherResults.slice(0, 8).map((phrase, index) => <button key={phrase.id} type="button" className={`launcher-result ${index === launcherIndex ? "is-selected" : ""}`} onMouseEnter={() => setLauncherIndex(index)} onClick={() => insertWithContext(phrase)}><span className="result-copy"><strong>{phrase.title}</strong><small>{phrase.body}</small><span className="result-meta"><i>{phrase.category}</i><i>{phrase.tags[0]}</i><em>{phrase.shortcut || "—"}</em></span></span>{phrase.favorite ? <Star20Filled className="favorite-icon" /> : <Star20Regular className="muted-icon" />}</button>)}</div>{previewOpen ? <div className="launcher-preview"><span>预览</span><strong>{selected?.title}</strong><p>{selected?.body}</p></div> : null}</> : <div className="empty-results"><Search20Regular /><strong>没有找到“{launcherQuery}”</strong><span>尝试其他关键词，或直接创建一条新话术</span><button type="button" className="button secondary" onClick={() => createPhrase(launcherQuery)}><Add20Regular /> 新建话术“{launcherQuery}”</button></div>}</div><div className="launcher-keybar"><span><KeyHint><ArrowUp20Regular /></KeyHint><KeyHint><ArrowDown20Regular /></KeyHint> 选择</span><span className="key-action-primary"><KeyHint>Enter</KeyHint> 插入</span><span><KeyHint>Ctrl + Enter</KeyHint> 插入并发送</span><span><KeyHint>Tab</KeyHint> 预览</span><span><KeyHint>Esc</KeyHint> 关闭</span></div></section></section>;
}

function EditorScene({ draft, updateDraft, saveDraft, deletePrompt, setDeletePrompt, deleteSelectedPhrase, openScene, phrases }) {
  const currentConflict = draft.shortcut && phrases.find((phrase) => phrase.id !== draft.id && phrase.shortcut === draft.shortcut);
  return <section className="editor-scene"><div className="editor-backdrop-copy"><span className="scene-eyebrow">PHRASE EDITOR</span><h1>把一条回复，变成<br />下一次的快捷入口。</h1><p>普通话术无需记快捷键，高频内容可以选择 Alt+1～9，其余交给 Launcher 搜索。</p></div><section className="editor-window"><WindowTitle icon={<Edit20Regular />} title="编辑话术" subtitle="快速编辑 · 仅当前会话" onClose={() => openScene("library")} actions={<IconButton label="更多操作"><MoreHorizontal20Regular /></IconButton>} /><div className="editor-form"><div className="field"><label htmlFor="phrase-title">话术标题</label><input id="phrase-title" value={draft.title} onChange={(event) => updateDraft("title", event.target.value)} /></div><div className="field"><label htmlFor="phrase-body">话术正文</label><textarea id="phrase-body" value={draft.body} onChange={(event) => updateDraft("body", event.target.value)} rows="5" placeholder="输入这条话术的正文..." /><span className="field-count">{draft.body.length}/1000</span></div><div className="form-columns"><div className="field"><label htmlFor="phrase-category">分类</label><div className="select-wrap"><select id="phrase-category" value={draft.category} onChange={(event) => updateDraft("category", event.target.value)}>{categoryOptions.map((category) => <option key={category}>{category}</option>)}</select><ChevronDown20Regular /></div></div><div className="field shortcut-field"><label>独立快捷键 <small>可选高级能力</small></label><div className="shortcut-mode-row"><select aria-label="快捷键模式" value={draft.shortcutMode || "none"} onChange={(event) => { const mode = event.target.value; updateDraft("shortcutMode", mode); updateDraft("shortcut", mode === "quick" ? quickShortcutOptions[0] : mode === "none" ? null : ""); }}><option value="none">不设置</option><option value="quick">高频槽位 Alt+1～9</option><option value="custom">自定义</option></select>{draft.shortcutMode === "quick" ? <select aria-label="高频快捷键" value={draft.shortcut || quickShortcutOptions[0]} onChange={(event) => updateDraft("shortcut", event.target.value)}>{quickShortcutOptions.map((shortcut) => <option key={shortcut}>{shortcut}</option>)}</select> : null}{draft.shortcutMode === "custom" ? <input aria-label="自定义快捷键" value={draft.shortcut || ""} placeholder="输入组合键" onChange={(event) => updateDraft("shortcut", event.target.value)} /> : null}</div>{currentConflict ? <small className="field-error">{draft.shortcut} 已被「{currentConflict.title}」占用，请改选空闲槽位。</small> : null}</div></div><div className="field"><label>标签</label><div className="tag-input">{draft.tags.map((tag) => <span className="tag" key={tag}>{tag}<button type="button" onClick={() => updateDraft("tags", draft.tags.filter((item) => item !== tag))}><Dismiss20Regular /></button></span>)}<input aria-label="添加标签" placeholder="添加标签后按 Enter" onKeyDown={(event) => { if (event.key === "Enter" && event.currentTarget.value.trim()) { updateDraft("tags", [...draft.tags, event.currentTarget.value.trim()]); event.currentTarget.value = ""; } }} /></div></div><div className="send-mode-note"><Send20Regular /><span><strong>发送方式</strong><small>Enter 插入；Ctrl + Enter 在支持的应用中插入并发送，不支持时自动降级为复制。</small></span></div>{deletePrompt ? <div className="delete-confirm"><span>确定删除这条话术？</span><button type="button" onClick={deleteSelectedPhrase}>确认删除</button><button type="button" onClick={() => setDeletePrompt(false)}>取消</button></div> : null}</div><div className="editor-footer"><button type="button" className="icon-delete" onClick={deleteSelectedPhrase}><Delete20Regular /> 删除</button><div><button type="button" className="button secondary" onClick={() => openScene("library")}>取消</button><button type="button" className="button primary" disabled={Boolean(currentConflict)} onClick={saveDraft}><Checkmark20Regular /> 保存话术</button></div></div></section></section>;
}

function SettingsScene({ settings, setSettings, appAdapters, openScene, showToast, hotkeyStatus, setHotkeyStatus }) {
  return <section className="settings-scene"><section className="settings-window"><WindowTitle icon={<Settings20Regular />} title="设置" subtitle="QuickPhrase 工作区" onClose={() => openScene("library")} /><div className="settings-layout"><nav className="settings-nav"><button type="button" className="is-active"><Settings20Regular /> 通用</button><button type="button"><Flash20Regular /> 快捷键</button><button type="button"><Send20Regular /> 发送行为</button><button type="button"><Globe20Regular /> 应用适配</button></nav><div className="settings-content"><div className="settings-heading"><span className="scene-eyebrow">PREFERENCES</span><h1>设置</h1><p>保持 QuickPhrase 安静地工作在后台。</p></div><div className="settings-section"><div className="settings-section-title">通用</div><SettingToggle label="开机启动" checked={settings.launchOnStartup} onChange={() => setSettings((current) => ({ ...current, launchOnStartup: !current.launchOnStartup }))} /><SettingToggle label="关闭窗口后驻留托盘" checked={settings.minimizeToTray} onChange={() => setSettings((current) => ({ ...current, minimizeToTray: !current.minimizeToTray }))} /></div><div className="settings-section"><div className="settings-section-title">快捷键</div>{hotkeyStatus === "conflict" ? <div className="hotkey-conflict"><strong>Alt + Space 已被其他应用占用</strong><span>重新设置后，QuickPhrase 才能在聊天窗口中被呼出。</span><button type="button" onClick={() => { setHotkeyStatus("available"); showToast("快捷键冲突已解决"); }}>重新设置</button></div> : null}<div className="settings-hotkey"><span>Quick Launcher 全局呼出</span><KeyHint>{settings.hotkey}</KeyHint><span className="setting-state">{hotkeyStatus === "conflict" ? "冲突" : "可用"}</span></div><div className="settings-hotkey"><span>主窗口内部搜索</span><KeyHint>Ctrl + K</KeyHint><span className="setting-state">可用</span></div></div><div className="settings-section"><div className="settings-section-title">发送行为</div><SettingToggle label="Enter 插入后关闭 Launcher" checked={!settings.autoSend} onChange={() => setSettings((current) => ({ ...current, autoSend: !current.autoSend }))} /><SettingToggle label="剪贴板兼容模式" checked={settings.clipboardMode} onChange={() => setSettings((current) => ({ ...current, clipboardMode: !current.clipboardMode }))} /></div><div className="settings-section"><div className="settings-section-title">应用适配</div><div className="adapter-grid">{appAdapters.map((adapter) => <div className="adapter-row" key={adapter.name}><span className="adapter-icon"><Globe20Regular /></span><span><strong>{adapter.name}</strong><small>{adapter.hint}</small></span><em className={adapter.tone}>{adapter.status}</em></div>)}</div></div></div></div><div className="settings-footer"><span>设置仅保存于当前会话</span><button type="button" className="button primary" onClick={() => { openScene("library"); showToast("设置已应用"); }}>完成</button></div></section></section>;
}
function SettingToggle({ label, checked, onChange }) { return <label className="setting-toggle"><span>{label}</span><input type="checkbox" checked={checked} onChange={onChange} /><span className="toggle-track"><span /></span></label>; }

function OnboardingOverlay({ onTry, onClose }) {
  return <div className="onboarding-scrim"><section className="onboarding-card"><div className="onboarding-mark"><ChatBubblesQuestion20Regular /></div><span className="scene-eyebrow">WELCOME TO QUICKPHRASE</span><h1>把常用回复，<br />放在手边。</h1><p>按下 Alt + Space，在微信、企业微信、QQ 或浏览器里快速搜索并插入话术。</p><div className="onboarding-hotkey"><KeyHint>Alt + Space</KeyHint><span>快速呼出 Launcher</span></div><button type="button" className="button primary" onClick={onTry}>试一下 <ArrowRightIcon /></button><button type="button" className="onboarding-skip" onClick={onClose}>先看看话术库</button></section></div>;
}
function ArrowRightIcon() { return <ArrowRight20Regular aria-hidden="true" />; }

function DesktopTaskbar({ openLauncher, trayOpen, setTrayOpen, createPhrase, shortcutsPaused, setShortcutsPaused, openScene }) {
  return <div className="taskbar"><div className="weather"><WeatherPartlyCloudyDay20Regular /><span><strong>24°C</strong><small>多云</small></span></div><div className="taskbar-center"><button type="button" className="task-icon start-icon" aria-label="开始"><WindowApps20Regular /></button><button type="button" className="task-search" onClick={openLauncher}><Search20Regular /><span>搜索</span></button><button type="button" className="task-icon"><Folder20Regular /></button><button type="button" className="task-icon"><Globe20Regular /></button><button type="button" className="task-icon quick-task-icon" onClick={openLauncher} aria-label="QuickPhrase"><ChatBubblesQuestion20Regular /></button><button type="button" className="task-icon"><AppsList20Regular /></button></div><div className="taskbar-right"><ChevronUp20Regular /><Wifi120Regular /><Speaker2Regular /><BatteryCharge20Regular /><span className="clock">10:30<br /><small>2024/05/15</small></span></div><div className="tray-wrap"><button type="button" className="tray-hotspot" onClick={() => setTrayOpen((open) => !open)} aria-label="QuickPhrase 托盘菜单"><ChatBubblesQuestion20Regular /></button>{trayOpen ? <div className="tray-menu"><strong>QuickPhrase</strong><button type="button" onClick={openLauncher}>快速搜索 <KeyHint>Alt + Space</KeyHint></button><button type="button" onClick={createPhrase}>新建话术</button><button type="button" onClick={() => setShortcutsPaused((paused) => !paused)}>{shortcutsPaused ? "恢复快捷键" : "暂停快捷键"}</button><button type="button" onClick={() => openScene("settings")}>设置</button><button type="button" onClick={() => setTrayOpen(false)}>退出</button></div> : null}</div></div>;
}

export default App;
