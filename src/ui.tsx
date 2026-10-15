import { useEffect, useId, useRef, useState, type ReactNode, type FormEvent } from 'react';
import { convertFileSrc } from '@tauri-apps/api/core';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { call, closeWindow } from './api';
import { categoryLabel, type Category } from './types';

/** 发送会移走焦点，WebView 可能残留 repeat 标记；用实际松键和失焦控制一次按住只提交一次。 */
export function useEnterSubmit() {
  const held = useRef(false);
  useEffect(() => {
    const release = (event: KeyboardEvent) => { if (event.key === 'Enter') held.current = false; };
    const blur = () => { held.current = false; };
    window.addEventListener('keyup', release); window.addEventListener('blur', blur);
    return () => { window.removeEventListener('keyup', release); window.removeEventListener('blur', blur); };
  }, []);
  return () => { if (held.current) return false; held.current = true; return true; };
}

const paths: Record<string, string> = {
  check: 'm4 10 4 4 8-8',
  // 话术类型使用独立轮廓，避免调整列表图标时影响编辑器和预览的通用图标。
  'phrase-text': 'M5 2h10a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1ZM7 6h6M7 10h6M7 14h4',
  'phrase-image': 'M3 3h14a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1ZM2 14l5-5 4 4 3-3 4 4M14.5 6.5a1 1 0 1 1-2 0 1 1 0 0 1 2 0',
  'phrase-file': 'M11 2H5a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V7l-5-5ZM11 2v5h5M11 10v4a2 2 0 0 1-4 0v-3a1 1 0 0 1 2 0v3',
  document: 'M5 2.5h9l2 2v13H5zM8 6h5M8 9h5M8 12h4', file: 'M5 2.5h7l4 4v11H5zM12 2.5v4h4M8 10h5M8 13h4',
  search: 'M14.5 8.5a6 6 0 1 1-12 0 6 6 0 0 1 12 0M13 13l4 4', pencil: 'm4 12 9-9 3 3-9 9-4 1zM11 5l3 3',
  dock: 'M3 4h8a6 6 0 0 1 0 12H3v-3h8a3 3 0 0 0 0-6H3V4Z',
  // 图钉缩小顶帽、延长针尖，空心与实心共用轮廓，切换时不偏移。
  pin: 'm12.5 2.5 5 5-2.5 1-3 3v3L5.5 8h3l3-3 1-2.5ZM8.5 11.5 3 17', minus: 'M4 10h12', close: 'm5 5 10 10M15 5 5 15',
  menu: 'M3 5h14M3 10h14M3 15h14', settings: 'M8 2h4l.5 2 1 .5 2-.5 2 3.5-1.5 1.5v1l1.5 1.5-2 3.5-2-.5-1 .5-.5 2H8l-.5-2-1-.5-2 .5-2-3.5L4 10.5v-1L2.5 8 4.5 4.5l2 .5 1-.5zM12.5 10a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0',
  eye: 'M1.5 10S5 4 10 4s8.5 6 8.5 6-3.5 6-8.5 6-8.5-6-8.5-6zM12.5 10a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0',
  'eye-off': 'M1.5 10S5 4 10 4s8.5 6 8.5 6-3.5 6-8.5 6-8.5-6-8.5-6zM12.5 10a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0M3 17 17 3',
  folder: 'M2 5h6l2 2h8v10H2zM2 5V3h6l2 2', calendar: 'M3 4h14v13H3zM3 8h14M6 2v4M14 2v4M6 11h2M11 11h2M6 14h2',
  image: 'M4 3h12l2 2v10l-2 2H4l-2-2V5zM8 7a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0M3 15l5-5 3 3 3-5 3 7',
  attachment: 'm7 13 7-7a2 2 0 0 0-3-3L3 11a4 4 0 0 0 6 6l8-8a3 3 0 0 0-4-4l-7 7a1 1 0 0 0 2 2l6-6',
  segment: 'M2 3h16v4H2zM2 13h16v4H2zM4 10h3M10 10h2M15 10h2',
  keyboard: 'M3 4h14l2 2v8l-2 2H3l-2-2V6zM4 7h1M8 7h1M12 7h1M16 7h1M4 10h1M8 10h1M12 10h1M16 10h1M5 13h10',
  trash: 'M3 5h14M7 5V2h6v3M5 5v12h10V5M8 8v6M12 8v6',
  refresh: 'M16 7a6 6 0 1 0 0 6M16 3v4h-4',
  upload: 'M10 14V3M6 7l4-4 4 4M3 12v5h14v-5',
  download: 'M10 3v11M6 10l4 4 4-4M3 13v4h14v-4',
  back: 'M7 4l-5 5 5 5M2 9h10a5 5 0 0 1 0 10',
  shield: 'M10 2l7 3v5c0 4-4 7-7 8-3-1-7-4-7-8V5zM7 10l2 2 4-4',
  recycle: 'm7 3 3-2 4 6M14 3v4h-4M16 8l3 5-3 5h-5M17 17l-4 1 1-4M8 18H3l-2-5 3-5M2 10l2-3 3 3',
  more: 'M4 10h1M9.5 10h1M15 10h1',
  save: 'M3 2h11l3 3v13H3zM6 2v5h7V2M6 18v-7h8v7',
  restore: 'M5 7H1V3M2 7a8 8 0 1 1 0 6M10 6v5l3 2',
  stop: 'M4 4h12v12H4z',
  play: 'M6 3l11 7-11 7z',
};
/** 关闭斜线与磁铁交叉处留出透明间隙，避免小尺寸下堆成黑块；遮罩随按钮背景自然变化。 */
function DockOffIcon() {
  const maskId = useId();
  const slash = 'M2.5 17.5l15-15';
  return <svg viewBox="0 0 20 20" aria-hidden="true"><defs><mask id={maskId}><rect width="20" height="20" fill="white" stroke="none" /><path d={slash} stroke="black" strokeWidth="4" /></mask></defs><path d={paths.dock} mask={`url(#${maskId})`} /><path d={slash} /></svg>;
}
export function Icon({ name }: { name: string }) { return name === 'dock-off' ? <DockOffIcon /> : <svg viewBox="0 0 20 20" aria-hidden="true"><path d={paths[name] || paths.document} /></svg>; }
/** 空值和历史橙色均表示默认标题色，由当前主题的 CSS 接管。
 * 单条话术自定义色优先于全局自定义色；只解析显示值，不改写库中的旧记录。 */
export function phraseTitleColor(color?: string, defaultColor?: string) {
  return [color, defaultColor].find(value => value && value.toLowerCase() !== '#ff5100');
}
/** 自定义标签背景为浅色时使用深色文字，避免白底白字；默认标签由当前主题接管。 */
export function categoryTextColor(background: string) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(background.slice(i, i + 2), 16));
  return (r * 299 + g * 587 + b * 114) / 1000 > 160 ? '#171717' : '#ffffff';
}
const categoryColors = ['#84c394', '#88b3fa', '#ff9800', '#d8bda6', '#8bc5e8', '#f3829e', '#aea0e6', '#83d0c9', '#dca4c9', '#929eaa', '#f4a1b5', '#ffaa95'];
/** 分类色板使用固定色块与原位弹层，避免系统颜色对话框遮住当前分类。
 * 恢复默认提交空字符串，由标签的默认配色接管，不改其他分类；选中色用勾号表示，不增加外框。 */
export function CategoryColorPicker({ label, value, defaultColor = '#ffac00', disabled = false, fontColor = false, onChange }: { label: string; value: string; defaultColor?: string; disabled?: boolean; fontColor?: boolean; onChange: (color: string) => void }) {
  const [open, setOpen] = useState(false); const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: PointerEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setOpen(false); ref.current?.querySelector<HTMLButtonElement>('.category-color-trigger')?.focus(); } };
    document.addEventListener('pointerdown', close); ref.current?.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', close); ref.current?.removeEventListener('keydown', escape); };
  }, [open]);
  return <div className="category-color-picker" ref={ref}><button type="button" className="category-color-trigger" aria-label={`设置 ${label} ${fontColor ? '字体颜色' : '背景色'}`} aria-expanded={open} disabled={disabled} onClick={() => setOpen(v => !v)}><span style={fontColor ? { color: value || defaultColor } : undefined}>{fontColor ? '颜色效果' : label}</span>{!fontColor && <i style={{ background: value || defaultColor }} />}</button>
    {open && <div className="category-color-popover"><div className="category-color-grid" role="group" aria-label={fontColor ? '标题字体色色板' : '分类背景色色板'}>{(fontColor ? ['#ff6600', '#0000ff', '#ff00ff', '#32ad21', '#ff0000', '#ca9663', '#00caca', '#000000'] : categoryColors).map(color => <button key={color} type="button" style={{ background: color, color: categoryTextColor(color) }} aria-label={`颜色 ${color}`} aria-pressed={value === color} disabled={disabled} onClick={() => { onChange(color); setOpen(false); ref.current?.querySelector<HTMLButtonElement>('.category-color-trigger')?.focus(); }}>{value === color && <Icon name="check" />}</button>)}</div><button type="button" className="restore-category-color" disabled={disabled} onClick={() => { onChange(''); setOpen(false); ref.current?.querySelector<HTMLButtonElement>('.category-color-trigger')?.focus(); }}>恢复默认颜色</button></div>}
  </div>;
}
export function Titlebar({ title, minimize = false, maximize = false }: { title: string; minimize?: boolean; maximize?: boolean }) { return <header className="standard-titlebar"><img className="app-symbol" src="/shanyu.png" alt="" draggable={false} /><span data-tauri-drag-region>{title}</span><span className="titlebar-spacer" data-tauri-drag-region />{minimize && <button aria-label="最小化窗口" onClick={() => void getCurrentWindow().minimize()}><Icon name="minus" /></button>}{maximize && <button aria-label="最大化或还原窗口" onClick={() => void getCurrentWindow().toggleMaximize()}><svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5 5h10v10H5z" /></svg></button>}<button aria-label="关闭窗口" onClick={() => void closeWindow()}><Icon name="close" /></button></header>; }
export function Feedback({ message, clear }: { message: string; clear: () => void }) {
  useEffect(() => { if (message) { const id = window.setTimeout(clear, 6000); return () => clearTimeout(id); } }, [message]);
  return message ? <div className="review-toast" role="status" onClick={clear}>{message}</div> : null;
}
export function Highlight({ text, query = '' }: { text: string; query?: string }) {
  const terms = query.trim().split(/\s+/).filter(Boolean).map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  if (!terms.length) return <>{text}</>;
  const pattern = new RegExp(`(${terms.join('|')})`, 'ig');
  return <>{text.split(pattern).map((part, i) => i % 2 ? <mark key={i}>{part}</mark> : part)}</>;
}
export function FileIcon({ path }: { path: string }) {
  const [url, setUrl] = useState('');
  useEffect(() => { let disposed = false; void call<string>('get_file_icon', { path }).then(path => { if (!disposed) setUrl(convertFileSrc(path)); }).catch(() => {}); return () => { disposed = true; }; }, [path]);
  return url ? <img className="row-system-icon" src={url} alt="" /> : <Icon name="file" />;
}
export interface DialogSpec { title: string; fields: ReactNode; submit?: string; className?: string; onSubmit: (data: FormData) => Promise<void> }
export function FormDialog({ spec, close, windowClose = false }: { spec: DialogSpec; close: () => void; windowClose?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null); const busyRef = useRef(false); const [error, setError] = useState(''); const [busy, setBusy] = useState(false);
  const titleId = useId();
  const trigger = useRef(document.activeElement as HTMLElement | null);
  useEffect(() => { ref.current?.showModal(); return () => { if (trigger.current?.isConnected) trigger.current.focus(); }; }, []);
  // 同步锁覆盖同一事件批次内的重复提交；执行期间 Escape 也不能提前关闭确认窗口。
  async function submit(e: FormEvent<HTMLFormElement>) { e.preventDefault(); if (busyRef.current) return; busyRef.current = true; setBusy(true); try { await spec.onSubmit(new FormData(e.currentTarget)); close(); } catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); } }
  // 模态dialog会让外面的自绘标题栏失去交互；管理窗关闭按钮在顶层原位置接收点击，仍经过草稿确认。
  return <dialog ref={ref} className={`small-dialog ${spec.className || ''}`} aria-labelledby={titleId} onCancel={e => { if (busyRef.current) e.preventDefault(); else close(); }}>{windowClose && <button type="button" className="dialog-window-close" aria-label="关闭窗口" disabled={busy} onClick={() => void closeWindow()}><Icon name="close" /></button>}<form onSubmit={submit}><h2 id={titleId}>{spec.title}</h2><fieldset className="dialog-fields" disabled={busy}>{spec.fields}{error && <p className="validation-error" role="alert">{error}</p>}</fieldset><div className="dialog-actions"><button type="button" className="secondary-button" onClick={close} disabled={busy}>取消</button><button type="submit" className="primary-button" disabled={busy}>{busy ? '提交中…' : spec.submit || '确定'}</button></div></form></dialog>;
}
export function CategoryPicker({ categories, rootId, groupId = null }: { categories: Category[]; rootId: string; groupId?: string | null }) {
  const [root, setRoot] = useState(rootId);
  return <><label>一级分类<select name="root" value={root} onChange={e => setRoot(e.target.value)}>{categories.filter(c => c.kind === 'phrase' && !c.parentId).map(c => <option key={c.id} value={c.id}>{categoryLabel(c, categories)}</option>)}</select></label><label>二级分类<select name="group" key={root} defaultValue={root === rootId ? groupId || '' : ''}><option value="">不选二级分类</option>{categories.filter(c => c.parentId === root).map(c => <option key={c.id} value={c.id}>{categoryLabel(c, categories)}</option>)}</select></label></>;
}
