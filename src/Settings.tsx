import { createContext, useContext, useLayoutEffect, useEffect, useRef, useState, type ReactNode } from 'react';
import { listen } from '@tauri-apps/api/event';
import { getVersion } from '@tauri-apps/api/app';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { open } from '@tauri-apps/plugin-dialog';
import { call } from './api';
import { CategoryColorPicker, Feedback, FormDialog, phraseTitleColor, Titlebar, type DialogSpec } from './ui';
import { categoryLabel, type Library, type Settings } from './types';
import { McpSettings } from './McpSettings';
import { TeamSettings } from './TeamSettings';
import { DataTransfer } from './DataTransfer';
import { UpdateSettings } from './Updater';
import { Diagnostics } from './Diagnostics';
import { useSettingsSave } from './useSettingsSave';

/** 序号只录入一次组合键。草稿显示到保存回执，失败后回到原绑定，不遗留看似已保存的值。 */
function SequenceBinding({ index, value, disabled, save, error }: { index: number; value: string; disabled: boolean; save: (value: string) => Promise<boolean>; error: (message: string) => void }) {
  const [draft, setDraft] = useState<string | null>(null);
  const record = async (value: string) => { setDraft(value); try { await save(value); } finally { setDraft(null); } };
  return <div className="sequence-binding"><label htmlFor={`sequence-${index}`}>发送序号{index + 1}</label><input id={`sequence-${index}`} type="text" value={draft ?? value} placeholder="未绑定" readOnly disabled={disabled} title="点击后按单个组合键录入" onKeyDown={e => {
    if (e.key === 'Tab' || e.key === 'Escape') return;
    e.preventDefault();
    if (e.repeat || e.nativeEvent.isComposing || ['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
    const key = /^(Key[A-Z]|Digit[0-9])$/.test(e.code) ? e.code.replace(/^(Key|Digit)/, '') : e.key.toUpperCase();
    const mods = [[e.ctrlKey, 'Ctrl'], [e.altKey, 'Alt'], [e.shiftKey, 'Shift'], [e.metaKey, 'Win']].filter(([active]) => active).map(([, name]) => name);
    if (!mods.length || !/^[A-Z0-9]$|^F(?:[1-9]|1[0-9]|2[0-4])$/.test(key)) { error('序号快捷键请录入单个组合键，例如 Ctrl+1。'); return; }
    void record([...mods, key].join('+'));
  }} /><button type="button" aria-label={`关闭序号${index + 1}快捷键`} disabled={disabled || !value} onClick={() => void record('')}>关闭</button></div>;
}

const tabs = ['常用设置', '界面设置', '吸附设置', '账号', 'AI 连接', '导入数据', '导出数据', '快捷键', '缓存管理', '问题诊断', '关于'];
const accountTab = (tab: string) => ['共享话术', '账号与同步', '账号与共享'].includes(tab) ? '账号' : tab;
const FieldErrors = createContext<Record<string, string>>({});
function Row({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) {
  const errors = useContext(FieldErrors), ref = useRef<HTMLDivElement>(null); const [message, setMessage] = useState('');
  useLayoutEffect(() => { setMessage([...new Set(Array.from(ref.current?.querySelectorAll<HTMLElement>('[data-setting]') || []).map(el => errors[el.dataset.setting!]).filter(Boolean))].join(' ')); }, [errors]);
  return <div ref={ref} className="settings-row" title={hint}><span className="settings-label">{label}</span><div className="settings-options">{children}</div>{hint && <p className="settings-hint">{hint}</p>}{message && <p role="alert" className="validation-error">{message}</p>}</div>;
}
/** 输入过程不随后台快照重建；未提交文字保留，结束编辑后接受实际生效值。 */
function SettingInput({ value, save, label, ...props }: { value: string; save: (value: string) => Promise<boolean>; label: string } & Omit<React.InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'onBlur'>) {
  const [draft, setDraft] = useState(value); const focused = useRef(false);
  useEffect(() => { if (!focused.current) setDraft(value); }, [value]);
  return <input {...props} aria-label={label} value={draft} onChange={e => setDraft(e.target.value)} onFocus={e => { focused.current = true; props.onFocus?.(e); }} onBlur={() => { focused.current = false; if (draft !== value) void save(draft).then(ok => { if (!ok && !focused.current) setDraft(value); }); }} />;
}

/** 位置示意只读取现有设置，用布局表达侧栏与底部面板的关系，不操作真实聊天窗口。 */
function DockIllustration({ settings }: { settings: Settings }) {
  const side = settings.dockSide === 'left' ? '左侧' : '右侧';
  const bottom = [settings.dockSearch && '搜索', settings.dockCommon && '常用短语'].filter(Boolean).join('与');
  return <figure className="dock-illustration"><figcaption>位置示意</figcaption>
    <div className="dock-preview" data-side={settings.dockSide} role="img" aria-label={`闪语位于聊天窗口${side}${bottom ? `，底部显示${bottom}` : '，底部面板已关闭'}`}>
      <div className="dock-preview-sidebar" aria-hidden="true"><strong>闪语</strong><i /><i /><i /><i /></div>
      <div className="dock-preview-chat" aria-hidden="true"><strong>聊天窗口</strong><div className="dock-preview-messages"><i /><i /></div><span>输入区</span></div>
      {bottom && <div className="dock-preview-bottom" aria-hidden="true">{settings.dockCommon && <div className="dock-preview-common">常用短语</div>}{settings.dockSearch && <div className="dock-preview-search">搜索</div>}</div>}
    </div><p className="settings-hint">底部搜索与常用短语可在“界面设置”中开关。</p>
  </figure>;
}

/** 设置只展示本地可用功能；保存完成后通知各窗口，原生窗口行为也使用同一份设置。
 * 点击行为的新字段使用旧规则作为默认值，升级或恢复旧备份不会自动启用直接发送。 */
export function SettingsWindow({ library, initialTab, initialCategoryId = '', initialTeamId = '' }: { library: Library; initialTab: string; initialCategoryId?: string; initialTeamId?: string }) {
  const [tab, setTab] = useState(tabs.includes(accountTab(initialTab)) ? accountTab(initialTab) : tabs[0]); const [message, setMessage] = useState(''); const [saving, setSaving] = useState(false);
  const { settings, change, errors, busy, pending, isSaving, flush } = useSettingsSave(library.settings, library.libraryId, library.settingsRevision);
  const saveOnClose = useRef({ isSaving, flush }); saveOnClose.current = { isSaving, flush };
  const closing = useRef(false);
  useEffect(() => {
    let disposed = false, stop: (() => void) | undefined;
    // 标题栏和系统关闭共用拦截，保留WebView直到串行保存完成；失败留窗查看就近错误。
    void getCurrentWindow().onCloseRequested(async event => {
      if (!saveOnClose.current.isSaving()) return;
      event.preventDefault();
      if (closing.current) return;
      closing.current = true; setMessage('正在保存设置，完成后关闭…');
      try {
        if (await saveOnClose.current.flush()) await getCurrentWindow().close();
        else if (!disposed) setMessage('部分设置未保存，请检查提示后重试。');
      } catch (error) { if (!disposed) setMessage(`设置窗口关闭失败：${String(error)}`); }
      finally { closing.current = false; }
    }).then(unlisten => { if (disposed) unlisten(); else stop = unlisten; }).catch(error => { if (!disposed) setMessage(`无法监听设置窗口关闭：${String(error)}`); });
    return () => { disposed = true; stop?.(); };
  }, []);
  const [appVersion, setAppVersion] = useState('');
  const [dataDirectory, setDataDirectory] = useState<{ path: string; message: string } | null>(null);
  const [choosingDirectory, setChoosingDirectory] = useState(false);
  const choosingDirectoryRef = useRef(false);
  useEffect(() => {
    if (tab !== '关于') return;
    let disposed = false;
    void call<{ path: string; message: string }>('get_data_directory').then(info => {
      if (!disposed) setDataDirectory(info);
    }).catch(error => { if (!disposed) setMessage(String(error)); });
    return () => { disposed = true; };
  }, [tab]);
  /** 选择不修改目录；确认后才由 Rust 保存迁移请求并重启，前端不复制或删除任何文件。 */
  async function chooseDataDirectory() {
    if (choosingDirectoryRef.current || !dataDirectory?.path) return;
    choosingDirectoryRef.current = true; setChoosingDirectory(true);
    try {
      const path = await open({ directory: true, multiple: false, title: '选择空数据文件夹', defaultPath: dataDirectory.path });
      if (typeof path !== 'string') return;
      setDialog({ title: '更换数据目录', submit: '迁移并重启', fields: <><p>当前目录</p><p className="data-path">{dataDirectory.path}</p><p>新目录</p><p className="data-path">{path}</p><p>迁移全部本地数据并重启。</p><p className="settings-hint">请选择空文件夹，并预留足够空间。</p><p className="settings-hint">请先保存并关闭编辑窗口，完成发送和传输。</p><p className="settings-hint">成功后清理原数据，保留外部源文件。</p></>, onSubmit: async () => {
        await call('change_data_directory', { path });
        setMessage('正在重启并迁移…');
      } });
    } catch (error) { setMessage(String(error)); }
    finally { choosingDirectoryRef.current = false; setChoosingDirectory(false); }
  }
  // 使用 Tauri 实际版本，候选构建不会被两个设置入口误标为稳定版。
  useEffect(() => {
    let disposed = false;
    void getVersion().then(version => { if (!disposed) setAppVersion(version); }).catch(error => { if (!disposed) setMessage(`无法读取应用版本：${String(error)}`); });
    return () => { disposed = true; };
  }, []);
  const savingRef = useRef(false);
  const [dialog, setDialog] = useState<DialogSpec | null>(null);
  const [transferTeam, setTransferTeam] = useState(initialTeamId); const [transferCategory, setTransferCategory] = useState(initialCategoryId);
  const [cache, setCache] = useState(0);
  const [colorCategory, setColorCategory] = useState(initialCategoryId || library.categories.find(c => !c.parentId)?.id || '');
  const category = library.categories.find(c => c.id === colorCategory && !c.parentId);
  // 标题与一级分类统一暖色；恢复默认仍保存旧标记或空值，不改写用户配色。
  const defaultTitleColor = 'var(--phrase-title-color)';
  const defaultCategoryColor = 'var(--category-surface)';
  // 菜单入口既支持首次打开，也支持已有设置窗口切换页面和分类，不重新创建窗口。
  useEffect(() => {
    let disposed = false; let unlisten: (() => void) | undefined;
    void listen<{ tab: string; categoryId: string; teamId?: string }>('settings-navigation', e => {
      if (tabs.includes(accountTab(e.payload.tab))) setTab(accountTab(e.payload.tab));
      if (e.payload.categoryId) setColorCategory(e.payload.categoryId);
      if (['导入数据', '导出数据'].includes(e.payload.tab)) { setTransferTeam(e.payload.teamId || ''); setTransferCategory(e.payload.categoryId || ''); }
    }).then(fn => disposed ? fn() : unlisten = fn).catch(e => setMessage(String(e)));
    return () => { disposed = true; unlisten?.(); };
  }, []);
  useEffect(() => { if (!library.categories.some(c => c.id === colorCategory && !c.parentId)) setColorCategory(library.categories.find(c => !c.parentId)?.id || ''); }, [library.categories, colorCategory]);
  useEffect(() => { if (tab === '缓存管理') void call<number>('cache_info').then(setCache).catch(e => setMessage(String(e))); }, [tab]);
  async function categoryColor(background: string) { if (savingRef.current || !category) return; savingRef.current = true; setSaving(true); try { await call('save_category', { category: { ...category, background } }); } catch (e) { setMessage(String(e)); } finally { savingRef.current = false; setSaving(false); } }
  const checkbox = (key: keyof Settings, text: string, label = text) => <label><input type="checkbox" aria-label={label} data-setting={key} checked={Boolean(settings[key])} disabled={busy(key)} onChange={e => void change({ [key]: e.target.checked })} />{text}</label>;
  const radio = (key: keyof Settings, value: string | number | boolean, text: ReactNode, disabled = false) => <label><input type="radio" name={key} value={String(value)} data-setting={key} checked={settings[key] === value} disabled={busy(key) || disabled} onChange={() => void change({ [key]: value })} />{text}</label>;
  const number = (key: keyof Settings, label: string, min: number, max: number) => <SettingInput data-setting={key} label={label} type="number" min={min} max={max} value={String(settings[key])} disabled={busy(key)} save={async text => { const value = Number(text); if (!text || !Number.isInteger(value) || value < min || value > max) { setMessage(`${label}应为${min}—${max}的整数。`); return false; } return change({ [key]: value }); }} />;
  async function clear(kind: string) { try { await call('clear_cache', { kind }); setCache(await call<number>('cache_info')); setMessage('缓存已清理'); } catch (e) { setMessage(String(e)); } }
  /** 确认窗口固定本次内容主键；其他窗口新增或删除后由后端拒绝旧列表，避免误删未确认内容。 */
  function deleteLocal(kind: 'phrase' | 'common') {
    const ids = (kind === 'phrase' ? library.phrases : library.commonPhrases).map(item => item.id); const label = kind === 'phrase' ? '个人话术' : '常用短语';
    setDialog({ title: `删除全部${label}`, submit: '确认删除', fields: <div className="cache-delete-confirm"><p>将删除全部 {ids.length} 条{label}，请谨慎操作。</p>
      {kind === 'phrase' ? <><label><input type="checkbox" name="recycle" defaultChecked />删除的话术移入回收站（可以还原）</label><p className="settings-hint">取消勾选将永久删除，无法恢复。</p><p className="settings-hint">分类、常用短语、文件关联、图片素材和源文件会保留。</p></> : <><p className="validation-error">常用短语不进入回收站，删除后无法恢复。建议先导出完整备份。</p><p className="settings-hint">个人话术、分类、图片素材和文件关联会保留。</p></>}
    </div>, onSubmit: async data => { const count = await call<number>('delete_local_content', { kind, ids, recycle: kind === 'phrase' && data.has('recycle') }); setMessage(`已删除 ${count} 条${label}`); } });
  }
  const folder = (kind: string) => void call('open_directory', { kind }).catch(e => setMessage(String(e)));
  let content: ReactNode;
  if (tab === '常用设置') content = <div className="general-settings"><Row label="应用设置"><div className="startup-grid">{checkbox('startup', '开机启动')}{checkbox('tray', '最小化到托盘')}{checkbox('showFloatingOnMinimize', '最小化显示悬浮窗')}</div></Row>
    <Row label="系统缩放"><span className="system-scale-status">自动适配 Windows 缩放比例</span></Row>
    <Row label="发送按键模式" hint="发送按键应与目标软件中的设置一致，直接发送时生效。">{radio('enter', 'enter', 'Enter 键')}{radio('enter', 'ctrl-enter', 'Ctrl+Enter 键')}</Row>
    <Row label="搜索匹配方式">{radio('searchMode', 'fuzzy', '模糊匹配')}{radio('searchMode', 'exact', '精确匹配')}</Row>
    <Row label="搜索结果使用后">{radio('exitSearch', true, '退出搜索结果界面')}{radio('exitSearch', false, '保留搜索结果界面')}</Row>
    <Row label="话术双击" hint="保留目标现有草稿；直接发送作用于输入区的最终内容。">{radio('phraseDoubleClick', 'insert', '输入到编辑区')}{radio('phraseDoubleClick', 'send', '直接发送')}</Row>
    <Row label="悬浮时贴边自动隐藏">{checkbox('edgeAutoHide', '开启', '悬浮时贴边自动隐藏')}</Row>
    <Row label="贴边隐藏防误触" hint="独立主窗口贴屏幕左右边缘时隐藏，鼠标停留指定时长后展开。">{radio('edgeRevealDelay', 0, '不等待', !settings.edgeAutoHide)}{radio('edgeRevealDelay', 500, '等0.5秒', !settings.edgeAutoHide)}{radio('edgeRevealDelay', 1000, '等1秒', !settings.edgeAutoHide)}</Row>
    <Row label="分段发送间隔" hint="两段之间的间隔，默认500毫秒。"><div className="interval-presets">{radio('interval', 100, <span>间隔小<small>（100毫秒）</small></span>)}{radio('interval', 500, <span>间隔正常<small>（500毫秒）</small></span>)}{radio('interval', 1000, <span>间隔大<small>（1000毫秒）</small></span>)}</div><label>自定义{number('interval', '自定义分段间隔', 0, 10000)}毫秒</label></Row></div>;
  else if (tab === '界面设置') content = <div className="interface-settings">
    <section className="settings-section"><h2 className="settings-section-title">面板与显示</h2><div className="panel-settings"><div className="panel-options">{checkbox('showSearch', '显示主窗口搜索功能')}{checkbox('showCommon', '显示主窗口常用短语')}{checkbox('dockSearch', '显示底部吸附搜索栏')}{checkbox('dockCommon', '显示底部吸附常用短语')}{checkbox('freeResize', '支持窗口自由拉宽拉窄')}{checkbox('showNumbers', '显示话术序号')}{checkbox('rememberDock', '记住主窗口吸附状态')}{checkbox('pinned', '主窗口置顶')}</div></div></section>
    <section className="settings-section"><h2 className="settings-section-title">主题与文字</h2>
    <Row label="主题风格"><select aria-label="主题风格" data-setting="theme" value={settings.theme} disabled={busy('theme')} onChange={e => void change({ theme: e.target.value as Settings['theme'] })}>{([['default', '雾蓝灰（默认）'], ['green', '清新绿'], ['blue', '竹月蓝'], ['black', '鸦青黑'], ['pink', '少女粉'], ['lavender', '长春花蓝'], ['orange', '活力橙']] as const).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></Row>
    <Row label="话术字体大小"><select aria-label="话术字体大小" data-setting="fontSize" value={settings.fontSize} disabled={busy('fontSize')} onChange={e => void change({ fontSize: Number(e.target.value) })}>{[...new Set([10, 12, 14, 16, 18, settings.fontSize])].sort((a, b) => a - b).map(value => <option key={value} value={value}>{value}</option>)}</select></Row>
    <Row label="话术标题字体颜色" hint="默认使用暖橙色，单条话术自行设置的颜色优先。"><span data-setting="titleColor"><CategoryColorPicker label="话术标题" fontColor value={phraseTitleColor('', settings.titleColor) || ''} defaultColor={defaultTitleColor} disabled={busy('titleColor')} onChange={color => void change({ titleColor: color || '#ff5100' })} /></span></Row>
    </section><section className="settings-section"><h2 className="settings-section-title">分类与列表</h2>
    {/* 原生下拉负责键盘、取消和焦点；选择另一项才交换两个位置，点击本身不保存。 */}
    <Row label="类目显示位置排序" hint="点击选择，交换位置。">{([{ key: 'filesFirst', label: '类目', options: ['话术', '文件'] }, { key: 'teamsFirst', label: '话术库', options: ['个人话术', '共享话术'] }] as const).map(({ key, label, options }) => <div key={key} className="setting-tab-order" role="group" aria-label={`${label}显示位置排序`}>{(settings[key] ? [...options].reverse() : options).map((item, index) => <select key={index} aria-label={`${label}第${index + 1}位`} title="点击选择并交换位置" data-setting={key} value={item} disabled={busy(key)} onChange={e => { if (e.target.value !== item) void change({ [key]: !settings[key] }); }}>{options.map(option => <option key={option} value={option}>{option}</option>)}</select>)}</div>)}</Row>
    <Row label="分类默认背景色"><span data-setting="phraseCategoryColor"><CategoryColorPicker label="个人分类" value={settings.phraseCategoryColor} defaultColor={defaultCategoryColor} disabled={busy('phraseCategoryColor')} onChange={color => void change({ phraseCategoryColor: color })} /></span><span data-setting="fileCategoryColor"><CategoryColorPicker label="文件分类" value={settings.fileCategoryColor} defaultColor={defaultCategoryColor} disabled={busy('fileCategoryColor')} onChange={color => void change({ fileCategoryColor: color })} /></span></Row>
    <Row label="一级分类背景色" hint="仅修改所选一级分类标签；恢复默认后使用上方分类默认背景色。"><select aria-label="背景色所属分类" value={colorCategory} disabled={saving} onChange={e => setColorCategory(e.target.value)}>{library.categories.filter(c => !c.parentId).map(c => <option value={c.id} key={c.id}>{categoryLabel(c, library.categories)}{c.kind === 'file' ? '（文件）' : ''}</option>)}</select><CategoryColorPicker key={colorCategory} label={category?.name || '分类'} value={category?.background || ''} defaultColor={(category?.kind === 'file' ? settings.fileCategoryColor : settings.phraseCategoryColor) || defaultCategoryColor} disabled={saving || !category} onChange={color => void categoryColor(color)} /></Row>
    <Row label="话术显示模式">{([[true, true, '显示标题和内容'], [true, false, '只显示标题'], [false, true, '只显示内容']] as const).map(([showTitle, showPreview, text]) => <label key={text}><input type="radio" name="display-mode" checked={settings.showTitle === showTitle && settings.showPreview === showPreview} data-setting="showTitle" disabled={busy('showTitle', 'showPreview')} onChange={() => void change({ showTitle, showPreview })} />{text}</label>)}</Row>
    <Row label="二级分类下的话术">{radio('expand', true, '默认展开')}{radio('expand', false, '默认收起')}</Row>
    </section><section className="settings-section"><h2 className="settings-section-title">区域高度</h2>
    <Row label="话术分类区域高度" hint="最小31—200像素；自动换行，超出200可滚动。">{number('categoryHeight', '一级分类区域高度', 31, 200)}</Row>
    <Row label="文件分类区域高度">{number('fileCategoryHeight', '文件分类区域高度', 31, 200)}</Row>
    <Row label="侧边短语区域最大高度">{number('commonHeight', '侧边短语区域最大高度', 28, 300)}</Row>
    <Row label="底部吸附短语每行显示" hint="底部面板跟随目标窗口；超出指定行数时可滚动查看。">{radio('dockCommonRows', 1, '一行')}{radio('dockCommonRows', 2, '两行')}{radio('dockCommonRows', 3, '三行')}{radio('dockCommonRows', 0, '完全自适应')}</Row></section>
  </div>;
  else if (tab === '吸附设置') content = <><p>请选择使用聊天工具时，话术窗口的跟随位置：</p><div className="setting-block">{radio('dockSide', 'left', '聊天窗口左侧')}{radio('dockSide', 'right', '聊天窗口右侧')}</div><div className="setting-block"><p>请选择要吸附的软件：</p><div className="dock-software">{checkbox('dock', '企业微信')}</div></div><DockIllustration settings={settings} /></>;
  else if (tab === '导入数据' || tab === '导出数据') content = <DataTransfer key={`${tab}:${transferTeam}:${transferCategory}`} mode={tab === '导入数据' ? 'import' : 'export'} library={library} initialTeamId={transferTeam} initialCategoryId={transferCategory} />;
  else if (tab === '快捷键') content = <div className="hotkey-settings">
    {([['定位到搜索', 'searchHotkey', '上下键选择搜索结果，Enter 添加到输入区，Esc 退出'], ['显示／隐藏侧栏', 'windowHotkey', '左右键选择分类，上下键选择内容，Enter 添加到输入区'], ['定位到常用短语', 'commonHotkey', '左右键、上下键选择短语，Enter 直接发送']] as const).map(([label, key, hint]) => <div className="hotkey-row" key={key}><div className="hotkey-main"><span>{label}</span><strong>Alt键</strong><span>＋</span><SettingInput data-setting={key} type="text" maxLength={1} value={settings[key]} label={`${label}快捷键`} onFocus={e => e.target.select()} disabled={busy(key)} save={value => change({ [key]: value.toUpperCase() })} />{errors[key] && <small role="alert" className="validation-error">{errors[key]}</small>}</div><p>{hint}</p></div>)}
    <div className="sequence-settings"><div className="hotkey-section-title"><strong>序号话术快捷键</strong><div>{checkbox('sequenceHotkeys', '开启', '开启序号快捷键')}<span className="settings-hint">关闭设置窗口后生效。</span></div></div>
      <div className="sequence-send-option"><label><input type="checkbox" checked={!!settings.sequenceDirectSend} disabled={busy('sequenceDirectSend')} onChange={e => void change({ sequenceDirectSend: e.target.checked })} />直接发送</label><span className="settings-hint">未勾选时仅添加到输入区。</span></div>
      <div className="sequence-bindings">{(settings.sequenceBindings || Array.from({ length: 10 }, (_, i) => `Ctrl+${(i + 1) % 10}`)).map((binding, index) => <SequenceBinding key={index} index={index} value={binding} disabled={busy('sequenceBindings')} error={setMessage} save={value => { const bindings = [...settings.sequenceBindings]; bindings[index] = value; return change({ sequenceBindings: bindings }); }} />)}</div>
    </div>
    <div className="custom-hotkey-settings"><div className="hotkey-section-title"><strong>自定义话术快捷键</strong>{checkbox('customHotkeys', '开启', '开启自定义快捷键')}</div>
      <div className="hotkey-section-title"><strong>生效范围</strong><div>{checkbox('globalPhraseHotkeys', '全局生效')}<span className="settings-hint">适用于序号和自定义快捷键。</span></div></div>
      <p className="settings-hint">默认仅在闪语或目标窗口生效；勾选“全局生效”后可用于其他窗口。</p>
    </div>
  </div>;
  else if (tab === '缓存管理') content = <div className="cache-settings"><div className="setting-block"><button className="secondary-button wide-button" onClick={() => folder('cache')}>打开本地缓存文件夹</button></div>
    <div className="setting-block"><h2 className="settings-heading">一键删除（请谨慎操作）</h2><div className="setting-buttons"><button className="secondary-button" disabled={!library.phrases.length} onClick={() => deleteLocal('phrase')}>个人话术</button><button className="secondary-button" disabled={!library.commonPhrases.length} onClick={() => deleteLocal('common')}>常用短语</button></div></div>
    <div className="setting-block"><h2 className="settings-heading">清理本地缓存</h2><div className="setting-buttons"><button className="secondary-button" onClick={() => void clear('thumbnails')}>清理缩略图</button><button className="secondary-button" onClick={() => void clear('temp')}>清理临时文件</button></div><p className="settings-hint">可重新生成的缓存：{(cache / 1024 / 1024).toFixed(2)} MB</p><p className="settings-hint">清理缓存会保留话术、图片素材和关联的原文件。</p></div></div>;
  else content = <><div className="about-brand"><img src="/shanyu.png" width="72" height="72" alt="" draggable={false} /><div><h2 className="settings-heading">闪语 QuickPhrase</h2><p>Windows 本地话术工具{appVersion && ` · v${appVersion}`}</p></div></div><div className="setting-block"><p>快速找到话术，添加到输入区，按需直接发送。</p><p>支持本地文件、图片附件和分段话术。</p></div><UpdateSettings /><div className="setting-block"><p>本地数据目录</p><p className="data-path">{dataDirectory?.path || '正在读取…'}</p><div className="setting-buttons"><button className="secondary-button" onClick={() => folder('data')}>打开数据文件夹</button><button className="secondary-button" disabled={!dataDirectory?.path || choosingDirectory || saving || pending} onClick={() => void chooseDataDirectory()}>{choosingDirectory ? '选择中…' : '更换目录'}</button></div>{dataDirectory?.message && <p role="status">{dataDirectory.message}</p>}<p className="settings-hint">升级与卸载会保留本地数据。</p></div></>;
  if (tab === '账号') content = <TeamSettings />;
  if (tab === 'AI 连接') content = <McpSettings library={library} />;
  if (tab === '问题诊断') content = <Diagnostics />;
  return <FieldErrors.Provider value={errors}><section className="client-window settings-window"><Titlebar title="设置" minimize /><div className="settings-layout"><aside className="settings-sidebar"><nav>{tabs.map(t => <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>{t}</button>)}</nav><div className="settings-brand"><span>闪语</span><small>本地话术工具{appVersion && ` · ${appVersion}`}</small></div></aside><div className="settings-content">{content}{[...new Set(Object.entries(errors).filter(([key]) => ['searchHotkey', 'windowHotkey', 'commonHotkey', 'sequenceBindings', 'sequenceDirectSend', 'sequenceHotkeys', 'customHotkeys', 'globalPhraseHotkeys', 'dock', 'dockSide'].includes(key)).map(([, error]) => error))].map(error => <p key={error} role="alert" className="validation-error">{error}</p>)}</div></div>{dialog && <FormDialog spec={dialog} close={() => setDialog(null)} />}<Feedback message={message} clear={() => setMessage('')} /></section></FieldErrors.Provider>;
}
