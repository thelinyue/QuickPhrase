import { useEffect, useRef, useState, type ReactNode } from 'react';
import { open, save } from '@tauri-apps/plugin-dialog';
import { listen } from '@tauri-apps/api/event';
import { getVersion } from '@tauri-apps/api/app';
import { call } from './api';
import { CategoryColorPicker, Feedback, FormDialog, Titlebar, type DialogSpec } from './ui';
import { categoryLabel, localDate, type ImportPreview, type Library, type Settings } from './types';

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

const tabs = ['常用设置', '界面设置', '吸附设置', '导入数据', '导出数据', '快捷键', '缓存管理', '关于'];
function Row({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) { return <div className="settings-row" title={hint}><span className="settings-label">{label}</span><div className="settings-options">{children}</div>{hint && <p className="settings-hint">{hint}</p>}</div>; }

/** 位置示意只读取现有设置，用布局表达侧栏与底部面板的关系，不操作真实聊天窗口。 */
function DockIllustration({ settings }: { settings: Settings }) {
  const side = settings.dockSide === 'left' ? '左侧' : '右侧';
  const bottom = [settings.dockSearch && '搜索', settings.dockCommon && '常用短语'].filter(Boolean).join('与');
  return <figure className="dock-illustration"><figcaption>位置示意</figcaption>
    <div className="dock-preview" data-side={settings.dockSide} role="img" aria-label={`闪语位于聊天窗口${side}${bottom ? `，底部显示${bottom}` : '，底部面板已关闭'}`}>
      <div className="dock-preview-sidebar" aria-hidden="true"><strong>闪语</strong><i /><i /><i /><i /></div>
      <div className="dock-preview-chat" aria-hidden="true"><strong>聊天窗口</strong><div className="dock-preview-messages"><i /><i /></div><span>输入区</span></div>
      {bottom && <div className="dock-preview-bottom" aria-hidden="true">{settings.dockSearch && <div className="dock-preview-search">搜索</div>}{settings.dockCommon && <div className="dock-preview-common">常用短语</div>}</div>}
    </div><p className="settings-hint">底部搜索与常用短语可在“界面设置”中开关。</p>
  </figure>;
}

/** 设置只展示本地可用功能；保存完成后通知各窗口，原生窗口行为也使用同一份设置。
 * 点击行为的新字段使用旧规则作为默认值，升级或恢复旧备份不会自动启用直接发送。 */
export function SettingsWindow({ library, initialTab, initialCategoryId = '' }: { library: Library; initialTab: string; initialCategoryId?: string }) {
  const [tab, setTab] = useState(tabs.includes(initialTab) ? initialTab : tabs[0]); const [settings, setSettings] = useState(library.settings); const [message, setMessage] = useState(''); const [saving, setSaving] = useState(false);
  const [appVersion, setAppVersion] = useState('');
  // 使用 Tauri 实际版本，候选构建不会被两个设置入口误标为稳定版。
  useEffect(() => {
    let disposed = false;
    void getVersion().then(version => { if (!disposed) setAppVersion(version); }).catch(error => { if (!disposed) setMessage(`无法读取应用版本：${String(error)}`); });
    return () => { disposed = true; };
  }, []);
  const savingRef = useRef(false);
  const [dialog, setDialog] = useState<DialogSpec | null>(null); const [preview, setPreview] = useState<ImportPreview | null>(null); const [merge, setMerge] = useState(true); const [restore, setRestore] = useState(false);
  const [exportType, setExportType] = useState('backup'); const [exportCategory, setExportCategory] = useState(''); const [cache, setCache] = useState(0);
  const [colorCategory, setColorCategory] = useState(initialCategoryId || library.categories.find(c => !c.parentId)?.id || '');
  const category = library.categories.find(c => c.id === colorCategory && !c.parentId);
  // 菜单入口既支持首次打开，也支持已有设置窗口切换页面和分类，不重新创建窗口。
  useEffect(() => {
    let disposed = false; let unlisten: (() => void) | undefined;
    void listen<{ tab: string; categoryId: string }>('settings-navigation', e => {
      if (tabs.includes(e.payload.tab)) setTab(e.payload.tab);
      if (e.payload.categoryId) setColorCategory(e.payload.categoryId);
    }).then(fn => disposed ? fn() : unlisten = fn).catch(e => setMessage(String(e)));
    return () => { disposed = true; unlisten?.(); };
  }, []);
  useEffect(() => { if (!library.categories.some(c => c.id === colorCategory && !c.parentId)) setColorCategory(library.categories.find(c => !c.parentId)?.id || ''); }, [library.categories, colorCategory]);
  useEffect(() => setSettings(library.settings), [library.settings]);
  useEffect(() => { if (tab === '缓存管理') void call<number>('cache_info').then(setCache).catch(e => setMessage(String(e))); }, [tab]);
  async function change(patch: Partial<Settings>) { if (savingRef.current) return false; const next = { ...settings, ...patch }; savingRef.current = true; setSaving(true); try { await call('save_settings', { settings: next }); setSettings(next); return true; } catch (e) { setMessage(String(e)); return false; } finally { savingRef.current = false; setSaving(false); } }
  async function categoryColor(background: string) { if (savingRef.current || !category) return; savingRef.current = true; setSaving(true); try { await call('save_category', { category: { ...category, background } }); } catch (e) { setMessage(String(e)); } finally { savingRef.current = false; setSaving(false); } }
  const checkbox = (key: keyof Settings, text: string, label = text) => <label><input type="checkbox" aria-label={label} checked={Boolean(settings[key])} disabled={saving} onChange={e => void change({ [key]: e.target.checked })} />{text}</label>;
  const radio = (key: keyof Settings, value: string | number | boolean, text: string) => <label><input type="radio" name={key} value={String(value)} checked={settings[key] === value} disabled={saving} onChange={() => void change({ [key]: value })} />{text}</label>;
  const number = (key: keyof Settings, label: string, min: number, max: number) => <input key={String(settings[key])} aria-label={label} type="number" min={min} max={max} defaultValue={String(settings[key])} disabled={saving} onBlur={e => { const value = Number(e.target.value); if (!e.target.value || !Number.isInteger(value) || value < min || value > max) { setMessage(`${label}应为${min}—${max}的整数。`); e.target.value = String(settings[key]); } else if (value !== settings[key]) void change({ [key]: value }); }} />;
  async function chooseImport() { try { const path = await open({ title: '选择导入文件', directory: false, multiple: false, filters: [{ name: '闪语备份或Excel文字', extensions: ['zip', 'xlsx'] }] }); if (typeof path === 'string') { setPreview(await call<ImportPreview>('preview_import', { path })); setRestore(false); } } catch (e) { setMessage(String(e)); } }
  async function template() { try { const path = await save({ title: '保存文字模板', defaultPath: '闪语文字模板.xlsx', filters: [{ name: 'Excel', extensions: ['xlsx'] }] }); if (path) { await call('export_template', { path }); setMessage('文字模板已保存'); } } catch (e) { setMessage(String(e)); } }
  function showPreview() { if (!preview) { setMessage('请先选择导入文件'); return; }
    setDialog({ title: '导入预览', submit: '确认导入', fields: <div className="import-preview"><p>{preview.filename}</p><p>{preview.categories} 个分类 · {preview.phrases} 条话术 · {preview.commonPhrases} 条常用短语 · {preview.images} 张图片 · {preview.files} 个文件关联 · {preview.recycled} 条回收记录</p><div className="preview-table"><table className="template-table"><thead><tr><th>一级分类</th><th>标题</th><th>正文摘要</th></tr></thead><tbody>{preview.rows.map((row, i) => <tr key={i}><td>{row.category}</td><td>{row.title || '无标题'}</td><td>{row.text}</td></tr>)}</tbody></table></div>{preview.notes.map(note => <p key={note} className="settings-hint">{note}</p>)}<p className="settings-hint">{merge ? '合并到同级列表中第一个同名分类' : '保留原名称，每个分类独立导入'}{restore ? '；还原备份中的设置' : '；保留当前客户端设置'}。</p></div>, onSubmit: async () => { await call('confirm_import', { previewId: preview.id, merge, restoreSettings: restore }); setMessage('导入完成'); setPreview(null); } });
  }
  async function exportData() { try { const extension = exportType === 'backup' ? 'zip' : 'xlsx'; const path = await save({ title: '导出话术', defaultPath: `闪语${exportType === 'backup' ? '备份' : '文字'}-${localDate()}.${extension}`, filters: [{ name: exportType === 'backup' ? '闪语完整备份' : 'Excel文字', extensions: [extension] }] }); if (path) { await call('export_data', { path, kind: exportType, categoryId: exportCategory || null }); setMessage('导出完成'); } } catch (e) { setMessage(String(e)); } }
  async function clear(kind: string) { try { await call('clear_cache', { kind }); setCache(await call<number>('cache_info')); setMessage('缓存已清理'); } catch (e) { setMessage(String(e)); } }
  /** 确认窗口固定本次内容主键；其他窗口新增或删除后由后端拒绝旧列表，避免误删未确认内容。 */
  function deleteLocal(kind: 'phrase' | 'common') {
    const ids = (kind === 'phrase' ? library.phrases : library.commonPhrases).map(item => item.id); const label = kind === 'phrase' ? '私人话术' : '常用短语';
    setDialog({ title: `删除全部${label}`, submit: '确认删除', fields: <div className="cache-delete-confirm"><p>将删除全部 {ids.length} 条{label}，请谨慎操作。</p>
      {kind === 'phrase' ? <><label><input type="checkbox" name="recycle" defaultChecked />删除的话术移入回收站（可以还原）</label><p className="settings-hint">取消勾选将永久删除，无法恢复。</p><p className="settings-hint">分类、常用短语、本地文件关联、图片素材和源文件会保留。</p></> : <><p className="validation-error">常用短语不进入回收站，删除后无法恢复。建议先导出完整备份。</p><p className="settings-hint">私人话术、分类、图片素材和本地文件会保留。</p></>}
    </div>, onSubmit: async data => { const count = await call<number>('delete_local_content', { kind, ids, recycle: kind === 'phrase' && data.has('recycle') }); setMessage(`已删除 ${count} 条${label}`); } });
  }
  const folder = (kind: string) => void call('open_directory', { kind }).catch(e => setMessage(String(e)));
  let content: ReactNode;
  if (tab === '常用设置') content = <div className="general-settings"><Row label="应用设置"><div className="startup-grid">{checkbox('startup', '开机启动')}{checkbox('tray', '最小化到托盘')}<span className="settings-hint">自动适应系统缩放</span></div></Row>
    <Row label="发送按键模式" hint="发送按键应与目标软件中的设置一致，直接发送时生效。">{radio('enter', 'enter', 'Enter 键')}{radio('enter', 'ctrl-enter', 'Ctrl+Enter 键')}</Row>
    <Row label="搜索匹配方式">{radio('searchMode', 'fuzzy', '模糊匹配')}{radio('searchMode', 'exact', '精确匹配')}</Row>
    <Row label="搜索结果使用后">{radio('exitSearch', true, '退出搜索结果界面')}{radio('exitSearch', false, '保留搜索结果界面')}</Row>
    <Row label="话术双击" hint="保留目标现有草稿；直接发送作用于输入区的最终内容。">{radio('phraseDoubleClick', 'insert', '输入到编辑区')}{radio('phraseDoubleClick', 'send', '直接发送')}</Row>
    <Row label="常用短语单击" hint="默认单击选中、双击插入。单击改为插入或发送后，双击不重复提交。">{radio('commonSingleClick', 'select', '仅选中')}{radio('commonSingleClick', 'insert', '输入到编辑区')}{radio('commonSingleClick', 'send', '直接发送')}</Row>
    <Row label="分段发送间隔" hint="两段之间的间隔，默认500毫秒。">{radio('interval', 100, '间隔小（100毫秒）')}{radio('interval', 500, '间隔正常（500毫秒）')}{radio('interval', 1000, '间隔大（1000毫秒）')}<label>自定义{number('interval', '自定义分段间隔', 0, 10000)}毫秒</label></Row></div>;
  else if (tab === '界面设置') content = <div className="interface-settings">
    <div className="panel-settings"><strong>面板设置</strong><div className="panel-options">{checkbox('showSearch', '显示主窗口搜索功能')}{checkbox('showCommon', '显示主窗口常用短语')}{checkbox('dockSearch', '显示底部吸附搜索栏')}{checkbox('dockCommon', '显示底部吸附常用短语')}{checkbox('freeResize', '支持窗口自由拉宽拉窄')}{checkbox('showNumbers', '显示话术序号')}{checkbox('rememberDock', '记住主窗口吸附状态')}{checkbox('pinned', '主窗口置顶')}</div></div>
    <Row label="类目显示位置排序"><div className="setting-tab-order" role="group" aria-label="类目显示位置排序">{(settings.filesFirst ? ['本地文件', '话术'] : ['话术', '本地文件']).map(label => <span key={label}>{label}</span>)}<button aria-label="交换话术与本地文件位置" disabled={saving} onClick={() => void change({ filesFirst: !settings.filesFirst })}>⇄</button></div></Row>
    <Row label="主题风格"><select aria-label="主题风格" value={settings.theme} disabled={saving} onChange={e => void change({ theme: e.target.value as Settings['theme'] })}>{([['default', '默认'], ['green', '清新绿'], ['blue', '竹月蓝'], ['black', '鸦青黑'], ['pink', '少女粉'], ['lavender', '长春花蓝'], ['orange', '活力橙']] as const).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></Row>
    <Row label="话术字体大小"><select aria-label="话术字体大小" value={settings.fontSize} disabled={saving} onChange={e => void change({ fontSize: Number(e.target.value) })}>{[...new Set([10, 12, 14, 16, 18, settings.fontSize])].sort((a, b) => a - b).map(value => <option key={value} value={value}>{value}</option>)}</select></Row>
    <Row label="话术标题字体颜色" hint="默认色随设置变化，单条话术自行设置的颜色优先。"><CategoryColorPicker label="话术标题" fontColor value={settings.titleColor} defaultColor="#ff5100" disabled={saving} onChange={color => void change({ titleColor: color || '#ff5100' })} /></Row>
    <Row label="分类默认背景色"><CategoryColorPicker label="私人分类" value={settings.phraseCategoryColor} disabled={saving} onChange={color => void change({ phraseCategoryColor: color })} /><CategoryColorPicker label="文件分类" value={settings.fileCategoryColor} defaultColor="#4d5761" disabled={saving} onChange={color => void change({ fileCategoryColor: color })} /></Row>
    <Row label="一级分类背景色" hint="仅修改所选一级分类标签；恢复默认后使用上方分类默认背景色。"><select aria-label="背景色所属分类" value={colorCategory} disabled={saving} onChange={e => setColorCategory(e.target.value)}>{library.categories.filter(c => !c.parentId).map(c => <option value={c.id} key={c.id}>{categoryLabel(c, library.categories)}{c.kind === 'file' ? '（文件）' : ''}</option>)}</select><CategoryColorPicker key={colorCategory} label={category?.name || '分类'} value={category?.background || ''} defaultColor={(category?.kind === 'file' ? settings.fileCategoryColor || '#4d5761' : settings.phraseCategoryColor || '#ffac00')} disabled={saving || !category} onChange={color => void categoryColor(color)} /></Row>
    <Row label="话术显示模式">{([[true, true, '显示标题和内容'], [true, false, '只显示标题'], [false, true, '只显示内容']] as const).map(([showTitle, showPreview, text]) => <label key={text}><input type="radio" name="display-mode" checked={settings.showTitle === showTitle && settings.showPreview === showPreview} disabled={saving} onChange={() => void change({ showTitle, showPreview })} />{text}</label>)}</Row>
    <Row label="二级分类下的话术">{radio('expand', true, '默认展开')}{radio('expand', false, '默认收起')}</Row>
    <Row label="话术分类区域高度" hint="31—200像素；标签自动换行，超出区域可滚动。">{number('categoryHeight', '一级分类区域高度', 31, 200)}</Row>
    <Row label="文件分类区域高度">{number('fileCategoryHeight', '文件分类区域高度', 31, 200)}</Row>
    <Row label="侧边短语区域最大高度">{number('commonHeight', '侧边短语区域最大高度', 28, 300)}</Row>
    <Row label="底部吸附短语每行显示" hint="底部面板跟随目标窗口；超出指定行数时可滚动查看。">{radio('dockCommonRows', 1, '一行')}{radio('dockCommonRows', 2, '两行')}{radio('dockCommonRows', 3, '三行')}{radio('dockCommonRows', 0, '完全自适应')}</Row>
  </div>;
  else if (tab === '吸附设置') content = <><p>请选择使用聊天工具时，话术窗口的跟随位置：</p><div className="setting-block">{radio('dockSide', 'left', '聊天窗口左侧')}{radio('dockSide', 'right', '聊天窗口右侧')}</div><div className="setting-block"><p>请选择要吸附的软件：</p><div className="dock-software">{checkbox('dock', '企业微信')}</div></div><DockIllustration settings={settings} /></>;
  else if (tab === '导入数据') content = <><h2 className="settings-heading">导入话术</h2><div className="setting-block"><label><input type="checkbox" checked disabled />导入到我的私人话术</label></div><div className="setting-block"><p>支持 Excel 文字模板与闪语完整备份。</p><div className="setting-buttons"><button className="secondary-button" onClick={() => void chooseImport()}>选择导入文件</button><button className="secondary-button" onClick={() => void template()}>下载文字模板</button></div><span className="import-path">{preview?.filename || '未选择文件'}</span></div><div className="setting-block"><label><input type="checkbox" checked={merge} onChange={e => setMerge(e.target.checked)} />导入的分类与现有分类同名时合并</label><label><input type="checkbox" checked={restore} disabled={preview?.kind !== 'backup'} onChange={e => setRestore(e.target.checked)} />还原完整备份中的设置</label><p className="settings-hint">先查看预览，确认后导入。失败时回滚整个批次。</p></div><div className="setting-actions"><button className="primary-button" disabled={!preview} onClick={showPreview}>查看导入预览</button></div></>;
  else if (tab === '导出数据') content = <><h2 className="settings-heading">导出话术</h2><div className="setting-block"><label><input type="checkbox" checked disabled />导出我的私人库</label><select value={exportCategory} onChange={e => setExportCategory(e.target.value)}><option value="">全部分类</option>{library.categories.filter(c => !c.parentId).map(c => <option key={c.id} value={c.id}>{categoryLabel(c, library.categories)}{c.kind === 'file' ? '（文件）' : ''}</option>)}</select></div><div className="setting-block"><p>导出类型：</p><label><input type="radio" name="exportType" checked={exportType === 'backup'} onChange={() => setExportType('backup')} />完整备份（分类、话术、常用短语、图片、文件关联与设置）</label><label><input type="radio" name="exportType" checked={exportType === 'excel'} onChange={() => setExportType('excel')} />Excel 表格（文字内容）</label><p className="settings-hint">关联的外部文件需要单独备份；Excel 仅导出含文字的话术，不包含图片、文件和分段结构。</p></div><div className="setting-actions"><button className="primary-button" onClick={() => void exportData()}>导出话术</button></div></>;
  else if (tab === '快捷键') content = <div className="hotkey-settings">
    {([['定位到搜索', 'searchHotkey', '上下键选择搜索结果，Enter 添加到输入区，Esc 退出'], ['显示／隐藏侧栏', 'windowHotkey', '左右键选择分类，上下键选择内容，Enter 添加到输入区'], ['定位到常用短语', 'commonHotkey', '左右键、上下键选择短语，Enter 直接发送']] as const).map(([label, key, hint]) => <div className="hotkey-row" key={key}><div className="hotkey-main"><span>{label}</span><strong>Alt键</strong><span>＋</span><input key={settings[key]} type="text" maxLength={1} defaultValue={settings[key]} aria-label={`${label}快捷键`} onFocus={e => e.target.select()} disabled={saving} onBlur={e => { const input = e.currentTarget; if (input.value.toUpperCase() !== settings[key].toUpperCase()) void change({ [key]: input.value.toUpperCase() }).then(ok => { if (!ok) input.value = settings[key]; }); }} /></div><p>{hint}</p></div>)}
    <div className="sequence-settings"><div className="hotkey-section-title"><strong>序号话术快捷键</strong><div>{checkbox('sequenceHotkeys', '开启', '开启序号快捷键')}<span className="settings-hint">开启后，关闭设置窗口，快捷键生效</span></div></div>
      <div className="sequence-send-option"><label><input type="checkbox" checked={!!settings.sequenceDirectSend} disabled={saving} onChange={e => void change({ sequenceDirectSend: e.target.checked })} />直接发送</label><span className="settings-hint">不勾选则添加到输入区</span></div>
      <div className="sequence-bindings">{(settings.sequenceBindings || Array.from({ length: 10 }, (_, i) => `Ctrl+${(i + 1) % 10}`)).map((binding, index) => <SequenceBinding key={index} index={index} value={binding} disabled={saving} error={setMessage} save={value => { const bindings = [...settings.sequenceBindings]; bindings[index] = value; return change({ sequenceBindings: bindings }); }} />)}</div>
    </div>
    <div className="custom-hotkey-settings"><div className="hotkey-section-title"><strong>自定义话术快捷键</strong>{checkbox('customHotkeys', '开启', '开启自定义快捷键')}</div>
      <div className="hotkey-section-title"><button className="shortcut-tutorial" onClick={() => void call('open_shortcut_tutorial').catch(e => setMessage(String(e)))}><span aria-hidden="true">▶</span>使用教程</button><div>{checkbox('globalPhraseHotkeys', '全局生效')}<span className="settings-hint">序号与自定义话术共用此范围</span></div></div>
      <p className="settings-hint">默认在闪语或记录的目标窗口生效。同键话术显示候选；数字、空格插入，Enter 直接发送。</p>
    </div>
  </div>;
  else if (tab === '缓存管理') content = <div className="cache-settings"><div className="setting-block"><button className="secondary-button wide-button" onClick={() => folder('cache')}>打开本地缓存文件夹</button></div>
    <div className="setting-block"><h2 className="settings-heading">一键删除（请谨慎操作）</h2><div className="setting-buttons"><button className="secondary-button" disabled={!library.phrases.length} onClick={() => deleteLocal('phrase')}>私人话术</button><button className="secondary-button" disabled={!library.commonPhrases.length} onClick={() => deleteLocal('common')}>常用短语</button></div></div>
    <div className="setting-block"><h2 className="settings-heading">清理本地缓存</h2><div className="setting-buttons"><button className="secondary-button" onClick={() => void clear('thumbnails')}>清理缩略图</button><button className="secondary-button" onClick={() => void clear('temp')}>清理临时文件</button></div><p className="settings-hint">可重新生成的缓存：{(cache / 1024 / 1024).toFixed(2)} MB</p><p className="settings-hint">清理缓存会保留话术、图片素材和关联的原文件。</p></div></div>;
  else content = <><div className="about-brand"><img src="/shanyu.png" width="64" height="64" alt="" draggable={false} /><div><h2 className="settings-heading">闪语</h2><p>Windows 本地话术工具{appVersion && ` · v${appVersion}`}</p></div></div><div className="setting-block"><p>快速找到话术，添加到输入区，按需直接发送。</p><p>支持本地文件、图片附件和分段话术。</p></div><div className="setting-block"><p>本地数据目录</p><p className="data-path">{library.dataDir}</p><button className="secondary-button" onClick={() => folder('data')}>打开数据文件夹</button><p className="settings-hint">升级与卸载会保留本地数据。</p></div></>;
  return <section className="client-window settings-window"><Titlebar title="设置" minimize /><div className="settings-layout"><aside className="settings-sidebar"><nav>{tabs.map(t => <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>{t}</button>)}</nav><div className="settings-brand"><span>闪语</span><small>本地话术工具{appVersion && ` · ${appVersion}`}</small></div></aside><div className="settings-content">{content}</div></div>{dialog && <FormDialog spec={dialog} close={() => setDialog(null)} />}<Feedback message={message} clear={() => setMessage('')} /></section>;
}
