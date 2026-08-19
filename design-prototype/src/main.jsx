import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AddRegular,
  ArrowLeftRegular,
  CheckmarkCircleRegular,
  ChevronDownRegular,
  CopyRegular,
  DeleteRegular,
  DesktopRegular,
  DismissRegular,
  DocumentTextRegular,
  EditRegular,
  FlashRegular,
  FolderRegular,
  HistoryRegular,
  MoreHorizontalRegular,
  SearchRegular,
  SendRegular,
  SettingsRegular,
  ShieldCheckmarkRegular,
  StarRegular,
  WarningRegular,
  WindowRegular,
} from '@fluentui/react-icons';
import './styles.css';

const initialPhrases = [
  { id: 'sn', title: '请提供设备序列号', body: '请提供设备序列号（SN），方便我们进一步确认设备信息。', category: '信息收集', tags: ['设备', 'SN'], favorite: false, shortcut: '' },
  { id: 'restart', title: '设备重启步骤', body: '请您尝试重启设备：1. 长按电源键10秒；2. 等待30秒后重新开机；3. 观察指示灯。', category: '设备问题', tags: ['设备', '重启'], favorite: false, shortcut: 'Ctrl + 1' },
  { id: 'network', title: '网络连接异常', body: '请检查网络连接是否正常，或重启设备后重试。', category: '网络问题', tags: ['客服', '网络'], favorite: false, shortcut: 'Ctrl + 2' },
  { id: 'order', title: '订单信息确认', body: '为了尽快为您处理，请提供订单号和收货手机号。', category: '订单问题', tags: ['客服', '订单'], favorite: true, shortcut: 'Ctrl + 3' },
  { id: 'after-sale', title: '售后处理说明', body: '如需售后支持，请提供订单号和问题描述。', category: '售后服务 / 退款 / 已发货', tags: ['客服', '售后'], favorite: false, shortcut: '' },
  { id: 'password', title: '密码重置', body: '您可以通过绑定的手机号或邮箱重置密码。', category: '通用话术', tags: ['账号'], favorite: false, shortcut: '' },
];

const categoryTree = [
  { name: '设备问题', children: [] },
  { name: '网络问题', children: [] },
  { name: '账户问题', children: [] },
  { name: '售后服务', children: [{ name: '退款', children: [{ name: '已发货', children: [] }] }, { name: '换货', children: [] }] },
  { name: '订单问题', children: [] },
  { name: '信息收集', children: [] },
  { name: '通用话术', children: [] },
];
const categoryPaths = [];
function flattenCategoryTree(nodes, parent = '') { nodes.forEach(node => { const path = parent ? `${parent} / ${node.name}` : node.name; categoryPaths.push(path); flattenCategoryTree(node.children, path); }); }
flattenCategoryTree(categoryTree);
const categories = ['全部话术', '收藏', '最近使用', ...categoryPaths];

function Icon({ component: Component, size = 18 }) {
  return <Component fontSize={size} aria-hidden="true" />;
}

function Badge({ children, tone = 'neutral' }) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}

function Toast({ toast }) {
  if (!toast) return null;
  const IconComponent = toast.tone === 'warning' ? WarningRegular : CheckmarkCircleRegular;
  return <div className={`toast toast-${toast.tone || 'success'}`}><Icon component={IconComponent} size={18} /><span>{toast.message}</span></div>;
}

function Header({ screen, onNavigate, onNew, onLauncher, onTray }) {
  return (
    <header className="app-header">
      <div className="brand-lockup"><span className="brand-mark">闪</span><div><strong>闪语</strong><span>本地话术工作区</span></div></div>
      <nav className="main-nav" aria-label="主导航">
        <button className={screen === 'library' || screen === 'editor' ? 'active' : ''} onClick={() => onNavigate('library')}><Icon component={DocumentTextRegular} />话术库</button>
        <button className={screen === 'settings' ? 'active' : ''} onClick={() => onNavigate('settings')}><Icon component={SettingsRegular} />设置</button>
      </nav>
      <div className="header-actions">
        <button className="button button-primary button-launcher" onClick={onLauncher}><Icon component={FlashRegular} />快速启动器 <kbd>Alt + Space</kbd></button>
        <button className="button button-secondary" onClick={onNew}><Icon component={AddRegular} />新建话术</button>
        <button className="icon-button header-tray" onClick={onTray} aria-label="托盘菜单"><MoreHorizontalRegular fontSize={19} /></button>
      </div>
    </header>
  );
}

function CategoryNav({ selected, onSelect, onCreateCategory }) {
  const [expanded, setExpanded] = useState(new Set(['售后服务', '售后服务 / 退款']));
  const countFor = (path) => initialPhrases.filter(phrase => phrase.category === path || phrase.category.startsWith(`${path} /`)).length;
  const render = (nodes, parent = '', depth = 0) => nodes.map(node => { const path = parent ? `${parent} / ${node.name}` : node.name; const isExpanded = expanded.has(path); return <div key={path}><div className={`category-tree-item ${selected === path ? 'selected' : ''}`} style={{ paddingLeft: 10 + depth * 16 }}><button className="tree-chevron" disabled={!node.children.length} onClick={() => setExpanded(current => { const next = new Set(current); if (next.has(path)) next.delete(path); else next.add(path); return next; })}><ChevronDownRegular fontSize={14} className={isExpanded ? 'expanded' : ''} /></button><button className="tree-select" onClick={() => onSelect(path)}><FolderRegular fontSize={17} /><span>{node.name}</span><span className="count">{countFor(path)}</span></button><button className="tree-more" onClick={() => onCreateCategory(path)} aria-label={`${path} 分类操作`}><MoreHorizontalRegular fontSize={15} /></button></div>{isExpanded && render(node.children, path, depth + 1)}</div>; });
  return <aside className="category-nav"><div className="section-caption">我的话术</div><button className={`category-item ${selected === '全部话术' ? 'selected' : ''}`} onClick={() => onSelect('全部话术')}><Icon component={DocumentTextRegular} /><span>全部话术</span><span className="count">{initialPhrases.length}</span></button><button className={`category-item ${selected === '收藏' ? 'selected' : ''}`} onClick={() => onSelect('收藏')}><Icon component={StarRegular} /><span>收藏</span><span className="count">{initialPhrases.filter(p => p.favorite).length}</span></button><button className={`category-item ${selected === '最近使用' ? 'selected' : ''}`} onClick={() => onSelect('最近使用')}><Icon component={HistoryRegular} /><span>最近使用</span><span className="count">{initialPhrases.length}</span></button><div className="category-divider" /><div className="section-caption category-caption"><span>分类</span><button className="new-category-icon" onClick={() => onCreateCategory(null)} aria-label="新建一级分类"><AddRegular fontSize={16} /></button></div><div className="category-tree">{render(categoryTree)}</div><button className="new-category" onClick={() => onCreateCategory(null)}><AddRegular fontSize={18} />新建分类</button></aside>;
}

function PhraseList({ phrases, selectedId, onSelect, onToggleFavorite }) {
  return <div className="phrase-list" role="list">
    {phrases.map((phrase) => <button key={phrase.id} className={`phrase-row ${selectedId === phrase.id ? 'selected' : ''}`} onClick={() => onSelect(phrase.id)} role="listitem">
      <span className="phrase-icon"><DocumentTextRegular fontSize={19} /></span>
      <span className="phrase-main"><strong>{phrase.title}</strong><span>{phrase.body}</span><span className="phrase-meta">{phrase.tags.map(tag => <Badge key={tag}>{tag}</Badge>)}{phrase.shortcut && <Badge tone="violet">{phrase.shortcut}</Badge>}</span></span>
      <span className="phrase-side"><button className={`icon-button ${phrase.favorite ? 'favorite' : ''}`} onClick={(event) => { event.stopPropagation(); onToggleFavorite(phrase.id); }} aria-label={phrase.favorite ? '取消收藏' : '收藏'}><StarRegular fontSize={19} /></button><span>今天 10:24</span></span>
    </button>)}
  </div>;
}

function PreviewPanel({ phrase, onEdit, onTestLauncher }) {
  if (!phrase) return <section className="preview-panel empty-preview"><DocumentTextRegular fontSize={32} /><p>选择一条话术查看预览</p></section>;
  return <section className="preview-panel">
    <div className="panel-eyebrow">话术预览</div>
    <div className="preview-heading"><h2>{phrase.title}</h2><button className={`icon-button ${phrase.favorite ? 'favorite' : ''}`}><StarRegular fontSize={20} /></button></div>
    <p className="preview-body">{phrase.body}</p>
    <dl className="preview-details"><div><dt>分类</dt><dd>{phrase.category}</dd></div><div><dt>标签</dt><dd>{phrase.tags.join(' / ')}</dd></div><div><dt>快捷键</dt><dd>{phrase.shortcut || '未设置'}</dd></div></dl>
    <div className="preview-history"><span>使用 44 次</span><span>最近 今天 10:24</span></div>
    <div className="preview-actions"><button className="button button-secondary full" onClick={onTestLauncher}><FlashRegular fontSize={18} />快速启动器测试</button><button className="button button-primary full" onClick={onEdit}><EditRegular fontSize={18} />编辑话术</button></div>
  </section>;
}

function Library({ phrases, selectedId, selectedCategory, search, onSearch, onSelect, onCategory, onToggleFavorite, onEdit, onTestLauncher, onCreateCategory }) {
  const filtered = useMemo(() => phrases.filter(p => (selectedCategory === '全部话术' || (selectedCategory === '收藏' ? p.favorite : selectedCategory === '最近使用' ? true : p.category === selectedCategory || p.category.startsWith(`${selectedCategory} /`))) && `${p.title}${p.body}${p.tags.join('')}`.toLowerCase().includes(search.toLowerCase())), [phrases, selectedCategory, search]);
  const selectedPhrase = phrases.find(p => p.id === selectedId);
  return <main className="workspace library-workspace">
    <CategoryNav selected={selectedCategory} onSelect={onCategory} onCreateCategory={onCreateCategory} />
    <section className="library-content">
      <div className="content-heading"><div><div className="panel-eyebrow">我的话术</div><h1>{selectedCategory}</h1></div><span className="result-count">{filtered.length} 条结果</span></div>
      <label className="search-box"><SearchRegular fontSize={20} /><input value={search} onChange={event => onSearch(event.target.value)} placeholder="搜索标题、正文或拼音..." aria-label="搜索话术" /><kbd>Ctrl K</kbd></label>
      <PhraseList phrases={filtered} selectedId={selectedId} onSelect={onSelect} onToggleFavorite={onToggleFavorite} />
    </section>
    <PreviewPanel phrase={selectedPhrase} onEdit={onEdit} onTestLauncher={onTestLauncher} />
  </main>;
}

function Editor({ phrase, mode, onBack, onSave, onDelete, onTestLauncher }) {
  const [draft, setDraft] = useState(phrase || { title: '', body: '', category: '设备问题', tags: [], shortcut: '' });
  const [dirty, setDirty] = useState(mode === 'new');
  const [conflict, setConflict] = useState(false);
  const update = (key, value) => { setDraft(prev => ({ ...prev, [key]: value })); setDirty(true); };
  return <main className="editor-page"><div className="context-row"><button className="back-button" onClick={() => dirty ? setConflict(true) : onBack()}><ArrowLeftRegular fontSize={18} />话术库</button><span>/</span><span>{mode === 'new' ? '新建话术' : draft.title || '编辑话术'}</span></div>
    <div className="editor-title"><div><div className="panel-eyebrow">PHRASE EDITOR</div><h1>{mode === 'new' ? '新建话术' : '编辑话术'}</h1><p>把一条回复，变成下一次的快捷入口。</p></div>{dirty && <Badge tone="warning">未保存</Badge>}</div>
    <section className="editor-card">
      <label>话术标题<input value={draft.title} onChange={event => update('title', event.target.value)} placeholder="例如：请提供设备序列号" /></label>
      <label>话术正文<textarea value={draft.body} onChange={event => update('body', event.target.value)} placeholder="输入这条话术的正文..." /><span className="character-count">{draft.body.length}/4000</span></label>
      <div className="form-grid"><label>分类<select value={draft.category} onChange={event => update('category', event.target.value)}>{categories.slice(3).map(c => <option key={c}>{c}</option>)}</select></label><label>独立快捷键 <span className="help">可选·提高效率</span><select value={draft.shortcut} onChange={event => update('shortcut', event.target.value)}><option value="">不设置</option><option>Ctrl + 1</option><option>Ctrl + 2</option><option>Ctrl + 3</option></select></label></div>
      <label>标签<input value={draft.tags.join('、')} onChange={event => update('tags', event.target.value.split('、').filter(Boolean))} placeholder="添加标签后按 Enter" /></label>
      <div className="delivery-note"><ShieldCheckmarkRegular fontSize={19} /><div><strong>安全投递提示</strong><span>当前目标为企业微信：插入已验证，自动发送不支持。</span></div></div>
      {conflict && <div className="inline-alert"><WarningRegular fontSize={18} /><span>检测到本地快捷键与其他话术冲突，请更换后再保存。</span><button onClick={() => setConflict(false)}><DismissRegular fontSize={16} /></button></div>}
    </section>
    <footer className="editor-footer"><button className="danger-button" onClick={onDelete}><DeleteRegular fontSize={17} />删除</button><div><button className="button button-secondary" onClick={() => dirty ? setConflict(true) : onBack()}>取消</button><button className="button button-primary" onClick={() => onSave({ ...draft, id: phrase?.id || `phrase-${Date.now()}` })}><CheckmarkCircleRegular fontSize={18} />保存话术</button><button className="button button-secondary" onClick={onTestLauncher}><FlashRegular fontSize={18} />在 Launcher 中试用</button></div></footer>
  </main>;
}

function CapabilityMatrix({ enabled, onToggle }) {
  const rows = [['插入文本', '已验证', 'verified'], ['插入验证', '未确认', 'unverified'], ['自动发送', '不支持', 'unsupported'], ['安全复制', '可用', 'verified']];
  return <div className="capability-card"><div className="capability-heading"><div><div className="panel-eyebrow">DEVELOPER ADAPTER CATALOG</div><h2>企业微信</h2><span className="capability-subtitle">支持版本 WXWork 5.0.9.6065 · Profile v1</span></div><label className="adapter-toggle"><input type="checkbox" checked={enabled} onChange={onToggle} /><span>{enabled ? '允许 Alt + Space' : '已关闭'}</span></label></div><p>只有开发者登记且已开启的应用才会触发全局 Launcher；关闭后不会吞掉企业微信原有快捷键。</p><div className="capability-table">{rows.map(([name, value, tone]) => <div className="capability-row" key={name}><span>{name}</span><Badge tone={tone}>{value}</Badge></div>)}</div><div className="capability-warning"><WarningRegular fontSize={18} /><span>自动发送不支持，固定为安全复制或插入；能力验证不会被开关绕过。</span></div></div>;
}

function Settings({ onBack, onSave, dirty, setDirty, shortcutConflict, onShortcutConflict, adapterEnabled, onAdapterToggle }) {
  const [section, setSection] = useState('通用');
  return <main className="settings-page"><div className="settings-heading"><div><div className="panel-eyebrow">PREFERENCES</div><h1>设置</h1><p>管理本机快捷键与已登记应用的 Launcher 呼出权限。</p></div>{dirty && <Badge tone="warning">有未保存改动</Badge>}</div><div className="settings-layout"><aside className="settings-nav">{['通用', '快捷键', '发送行为', '应用适配'].map((item, index) => <button key={item} className={section === item ? 'selected' : ''} onClick={() => setSection(item)}><Icon component={[SettingsRegular, FlashRegular, SendRegular, DesktopRegular][index]} />{item}</button>)}</aside><section className="settings-content">{section === '应用适配' ? <CapabilityMatrix enabled={adapterEnabled} onToggle={onAdapterToggle} /> : <>
      <div className="settings-section"><h2>{section}</h2><p className="section-description">{section === '快捷键' ? '快捷键只在本机生效，冲突时会阻止保存。' : section === '发送行为' ? '所有投递动作都经过目标复核和能力验证。' : '保持闪语在需要时可用，但不打扰当前工作。'}</p>
      <div className="setting-row"><div><strong>开机启动</strong><span>登录 Windows 后启动闪语</span></div><button className="toggle" aria-label="开机启动" onClick={() => setDirty(true)}><span /></button></div>
      <div className="setting-row"><div><strong>关闭窗口后驻留托盘</strong><span>继续保留 Launcher 和全局快捷键</span></div><button className="toggle on" aria-label="驻留托盘" onClick={() => setDirty(true)}><span /></button></div>
      <div className="setting-row"><div><strong>Quick Launcher 全局呼出</strong><span>仅在已开启的适配应用中生效</span></div><kbd>Alt + Space</kbd><Badge tone="success">企业微信</Badge></div>
      {shortcutConflict && <div className="inline-alert"><WarningRegular fontSize={18} /><span>Alt + Space 已被其他应用占用，请更换快捷键。</span><button onClick={onShortcutConflict}><DismissRegular fontSize={16} /></button></div>}
      {section === '发送行为' && <><div className="setting-row"><div><strong>自动发送</strong><span>当前适配目录没有支持自动发送的应用</span></div><Badge tone="warning">不支持</Badge></div><div className="setting-row"><div><strong>剪贴板兼容模式</strong><span>插入失败时降级为安全复制</span></div><button className="toggle on" aria-label="剪贴板兼容模式" onClick={() => setDirty(true)}><span /></button></div></>}
      </div><div className="settings-save-note"><span>{dirty ? '有未保存的设置更改' : '所有设置已保存到本机'}</span><div><button className="button button-secondary" onClick={onBack}>取消</button><button className="button button-primary" onClick={onSave}>完成</button></div></div>
    </>}</section></div></main>;
}

function Onboarding({ onTry, onLibrary }) {
  return <div className="modal-scrim"><section className="onboarding-card"><div className="onboarding-mark"><FlashRegular fontSize={28} /></div><div className="panel-eyebrow">WELCOME TO QUICKPHRASE</div><h1>让每次回复，都更快一点</h1><p>按下 <kbd>Alt + Space</kbd> 呼出快速启动器。闪语会在目标窗口确认后安全插入；无法验证时只会复制，不会误发。</p><div className="onboarding-steps"><span><b>1</b>选择话术</span><span><b>2</b>确认目标</span><span><b>3</b>安全插入</span></div><div className="onboarding-actions"><button className="button button-primary" onClick={onTry}><FlashRegular fontSize={18} />试一下</button><button className="button button-secondary" onClick={onLibrary}>先看看话术库</button></div></section></div>;
}

function Launcher({ phrases, selectedId, target, onClose, onInsert, onSend }) {
  const [query, setQuery] = useState('');
  const filtered = phrases.filter(p => `${p.title}${p.body}${p.tags.join('')}`.toLowerCase().includes(query.toLowerCase()));
  const selected = phrases.find(p => p.id === selectedId) || filtered[0];
  return <div className="launcher-scrim"><section className="launcher-card"><div className="launcher-topline"><div className="launcher-title"><span className="launcher-mark"><FlashRegular fontSize={18} /></span><strong>快速启动器</strong><Badge tone={target === 'wecom' ? 'success' : 'warning'}>{target === 'wecom' ? '企业微信 · 5.0.9.6065' : '目标未识别 · 仅预览/复制'}</Badge></div><button className="icon-button" onClick={onClose} aria-label="关闭"><DismissRegular fontSize={20} /></button></div><label className="launcher-search"><SearchRegular fontSize={20} /><input autoFocus value={query} onChange={event => setQuery(event.target.value)} placeholder="搜索话术，输入关键词或拼音" /><kbd>Esc</kbd></label><div className="launcher-body"><div className="launcher-results">{filtered.length ? filtered.map(p => <button key={p.id} className={`launcher-result ${selected?.id === p.id ? 'selected' : ''}`} onClick={() => {}}><DocumentTextRegular fontSize={18} /><span><strong>{p.title}</strong><small>{p.body}</small></span><kbd>{p.shortcut || 'Enter'}</kbd></button>) : <div className="launcher-empty"><SearchRegular fontSize={26} /><strong>没有找到匹配话术</strong><span>试试标题、正文或标签</span></div>}</div><div className="launcher-preview">{selected ? <><div className="panel-eyebrow">预览</div><h3>{selected.title}</h3><p>{selected.body}</p><div className="launcher-preview-meta">{selected.tags.map(tag => <Badge key={tag}>{tag}</Badge>)}</div></> : <span>选择一条话术查看预览</span>}</div></div><div className="launcher-footer"><span>{target === 'wecom' ? <><kbd>Enter</kbd> 插入 <kbd>Ctrl + Enter</kbd> 自动发送不支持</> : <>无合格目标：只能预览或安全复制</>}</span><div><button className="button button-secondary" onClick={onClose}>取消</button><button className="button button-secondary" onClick={() => onInsert('copy')}><CopyRegular fontSize={17} />安全复制</button><button className="button button-primary" onClick={() => onInsert('insert')} disabled={target !== 'wecom'}><CheckmarkCircleRegular fontSize={17} />插入</button><button className="button button-disabled" onClick={onSend} disabled><SendRegular fontSize={17} />发送</button></div></div></section></div>;
}

function TrayMenu({ onNavigate, onLauncher, onNew, onOnboarding, onFallback, onClose }) {
  return <div className="tray-menu"><div className="tray-menu-heading"><span className="brand-mark small">闪</span><div><strong>闪语</strong><span>Launcher 已就绪</span></div></div><button onClick={onNavigate}><WindowRegular fontSize={18} />打开管理界面</button><button onClick={onLauncher}><FlashRegular fontSize={18} />快速搜索</button><button onClick={onNew}><AddRegular fontSize={18} />新建话术</button><button onClick={() => onNavigate('settings')}><SettingsRegular fontSize={18} />设置</button><button onClick={onOnboarding}><WindowRegular fontSize={18} />再次查看首次引导</button><button onClick={onFallback}><WarningRegular fontSize={18} />管理界面状态</button><div className="tray-separator" /><button className="tray-exit" onClick={onClose}><DismissRegular fontSize={18} />退出闪语</button></div>;
}

function FallbackPanel({ onLauncher, onClose }) {
  return <div className="modal-scrim"><section className="fallback-card"><div className="fallback-icon"><WarningRegular fontSize={28} /></div><div className="panel-eyebrow">MANAGEMENT UI UNAVAILABLE</div><h1>管理界面暂不可用</h1><p>WebView2 初始化失败。你的话术和 Native Launcher 仍然安全可用，托盘也可以继续操作。</p><div className="fallback-status"><span><CheckmarkCircleRegular fontSize={17} />快速启动器可用</span><span><CheckmarkCircleRegular fontSize={17} />托盘菜单可用</span></div><div className="onboarding-actions"><button className="button button-primary" onClick={onLauncher}><FlashRegular fontSize={18} />打开 Launcher</button><button className="button button-secondary" onClick={onClose}>稍后再试</button></div></section></div>;
}

function App() {
  // 这一层只模拟管理界面与 Native Launcher 的状态联动，不承载真实投递能力。
  const [screen, setScreen] = useState('library');
  const [phrases, setPhrases] = useState(initialPhrases);
  const [selectedId, setSelectedId] = useState('sn');
  const [selectedCategory, setSelectedCategory] = useState('全部话术');
  const [search, setSearch] = useState('');
  const [target, setTarget] = useState('wecom');
  const [adapterEnabled, setAdapterEnabled] = useState(true);
  const [launcher, setLauncher] = useState(false);
  const [tray, setTray] = useState(false);
  const [onboarding, setOnboarding] = useState(false);
  const [fallback, setFallback] = useState(false);
  const [editorMode, setEditorMode] = useState('edit');
  const [toast, setToast] = useState(null);
  const [settingsDirty, setSettingsDirty] = useState(false);
  const searchRef = useRef(null);
  const selectedPhrase = phrases.find(p => p.id === selectedId);
  const notify = (message, tone = 'success') => { setToast({ message, tone }); window.setTimeout(() => setToast(null), 3200); };
  const navigate = (next) => { setScreen(next); setTray(false); };
  const openEditor = (mode = 'edit') => { setEditorMode(mode); setScreen('editor'); setTray(false); };
  const savePhrase = (phrase) => { setPhrases(prev => prev.some(p => p.id === phrase.id) ? prev.map(p => p.id === phrase.id ? phrase : p) : [phrase, ...prev]); setSelectedId(phrase.id); setScreen('library'); notify('话术已保存，列表上下文已恢复'); };
  const deletePhrase = () => { if (!selectedPhrase) return; setPhrases(prev => prev.filter(p => p.id !== selectedPhrase.id)); setScreen('library'); setSelectedId('restart'); notify('话术已删除，已返回原列表', 'warning'); };
  const insert = (mode) => { setLauncher(false); notify(mode === 'copy' ? '已安全复制到剪贴板，未向目标发送' : target === 'wecom' ? '已插入企业微信，未执行自动发送' : '未识别目标，已取消投递', mode === 'copy' || target !== 'wecom' ? 'warning' : 'success'); };
  useEffect(() => {
    const onKey = (event) => { if (event.altKey && event.code === 'Space' && (launcher || (adapterEnabled && target === 'wecom'))) { event.preventDefault(); setLauncher(open => !open); } if (event.ctrlKey && event.key.toLowerCase() === 'k') { event.preventDefault(); navigate('library'); window.setTimeout(() => searchRef.current?.focus(), 0); } if (event.key === 'Escape') { setLauncher(false); setTray(false); } };
    window.addEventListener('keydown', onKey); return () => window.removeEventListener('keydown', onKey);
  }, [adapterEnabled, launcher, target]);
  const createCategory = (parent) => { const name = window.prompt(parent ? `新建 ${parent} 的子分类` : '新建一级分类'); if (name?.trim()) notify(`已创建分类：${parent ? `${parent} / ` : ''}${name.trim()}`); };
  return <div className="app-shell"><Header screen={screen} onNavigate={navigate} onNew={() => openEditor('new')} onLauncher={() => setLauncher(true)} onTray={() => setTray(menu => !menu)} />
    {screen === 'library' && <Library phrases={phrases} selectedId={selectedId} selectedCategory={selectedCategory} search={search} onSearch={setSearch} onSelect={setSelectedId} onCategory={setSelectedCategory} onCreateCategory={createCategory} onToggleFavorite={(id) => setPhrases(prev => prev.map(p => p.id === id ? { ...p, favorite: !p.favorite } : p))} onEdit={() => openEditor('edit')} onTestLauncher={() => setLauncher(true)} />}
    {screen === 'editor' && <Editor phrase={editorMode === 'new' ? null : selectedPhrase} mode={editorMode} onBack={() => navigate('library')} onSave={savePhrase} onDelete={deletePhrase} onTestLauncher={() => setLauncher(true)} />}
    {screen === 'settings' && <Settings onBack={() => navigate('library')} onSave={() => { setSettingsDirty(false); notify('设置已保存'); }} dirty={settingsDirty} setDirty={setSettingsDirty} adapterEnabled={adapterEnabled} onAdapterToggle={() => { setAdapterEnabled(value => !value); setSettingsDirty(true); }} shortcutConflict={false} onShortcutConflict={() => {}} />}
    {launcher && <Launcher phrases={phrases} selectedId={selectedId} target={target} onClose={() => setLauncher(false)} onInsert={insert} onSend={() => notify('当前 Profile 不支持自动发送，已阻止操作', 'warning')} />}
    {tray && <TrayMenu onNavigate={navigate} onLauncher={() => { setTray(false); setLauncher(true); }} onNew={() => openEditor('new')} onOnboarding={() => { setTray(false); setOnboarding(true); }} onFallback={() => { setTray(false); setFallback(true); }} onClose={() => { setTray(false); notify('退出操作仅在正式客户端生效', 'warning'); }} />}
    {onboarding && <Onboarding onTry={() => { setOnboarding(false); setLauncher(true); }} onLibrary={() => setOnboarding(false)} />}
    {fallback && <FallbackPanel onLauncher={() => { setFallback(false); setLauncher(true); }} onClose={() => setFallback(false)} />}
    <Toast toast={toast} />
  </div>;
}

createRoot(document.getElementById('root')).render(<App />);
