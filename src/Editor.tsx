import { useRef, useState, type FormEvent } from 'react';
import { open } from '@tauri-apps/plugin-dialog';
import { call, closeWindow } from './api';
import { Icon, Titlebar } from './ui';
import { ShortcutDialog } from './ShortcutDialog';
import { categoryLabel, localDate, type Block, type Library, type Phrase } from './types';
import { RichSegment, type SegmentHandle } from './RichSegment';

const colors = [['', '默认颜色'], ['#ff8842', '橙色'], ['#1685ff', '蓝色'], ['#ff76ed', '粉色'], ['#ae61ff', '紫色'], ['#35bb25', '绿色'], ['#f47b93', '浅红色'], ['#00afb0', '青色'], ['#ca9663', '棕色'], ['#5c6770', '灰色']];
const basename = (path: string) => path.split(/[\\/]/).pop() || path;

/** 编辑器的唯一内容源是有序分段。媒体插入会在当前选区拆分文字，不将图片或文件移动到段尾。 */
export function Editor({ library, id, afterId, categoryId, groupId }: { library: Library; id: string; afterId: string; categoryId: string; groupId: string }) {
  const original = library.phrases.find(p => p.id === id); const after = library.phrases.find(p => p.id === afterId);
  const roots = library.categories.filter(c => c.kind === 'phrase' && !c.parentId);
  const [draft, setDraft] = useState<Phrase>(() => original ? structuredClone(original) : { id, rootId: after?.rootId || categoryId || roots[0]?.id || '', groupId: after?.groupId || groupId || null, title: '', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '' }] }] });
  const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const [shortcutOpen, setShortcutOpen] = useState(false); const active = useRef(0); const inputs = useRef(new Map<number, SegmentHandle>());
  const [addingMedia, setAddingMedia] = useState(false); const mediaBusy = useRef(false);
  const update = (fn: (draft: Phrase) => void) => { setDraft(previous => { const next = structuredClone(previous); fn(next); return next; }); setError(''); };
  const count = (kind: 'image' | 'file') => draft.segments.reduce((n, s) => n + s.blocks.filter(b => b.kind === kind).length, 0);
  async function media(kind: 'image' | 'file') {
    if (mediaBusy.current || busyRef.current) return; mediaBusy.current = true; setAddingMedia(true);
    const input = inputs.current.get(active.current); const at = input?.capture(); try {
      const chosen = await open({ multiple: true, directory: false, title: kind === 'image' ? '添加图片' : '添加话术附件', filters: kind === 'image' ? [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'] }] : undefined });
      const paths = !chosen ? [] : typeof chosen === 'string' ? [chosen] : chosen;
      if (!paths.length) return;
      if (count(kind) + paths.length > 9) throw new Error(`每条话术最多9${kind === 'image' ? '张图片' : '个文件'}。`);
      const blocks = kind === 'image' ? await call<Block[]>('import_images', { paths }) : paths.map(path => ({ kind: 'file' as const, path, name: basename(path) }));
      input?.insert(blocks, at);
    } catch (e) { setError(String(e)); } finally { mediaBusy.current = false; setAddingMedia(false); }
  }
  /** 粘贴只读取本次事件提供的图片，复用图片导入限制；异步导入始终回到原分段和选区。 */
  async function pasteImages(files: File[], segment: number, at: Range | null) {
    if (mediaBusy.current || busyRef.current) return; mediaBusy.current = true; setAddingMedia(true);
    try {
      if (count('image') + files.length > 9) throw new Error('每条话术最多9张图片。');
      if (files.some(file => file.size > 128 * 1024 * 1024)) throw new Error('图片超过128MB。');
      const images = await Promise.all(files.map(async file => Array.from(new Uint8Array(await file.arrayBuffer()))));
      const blocks = await call<Block[]>('import_pasted_images', { images });
      inputs.current.get(segment)?.insert(blocks, at);
    } catch (e) { setError(String(e)); } finally { mediaBusy.current = false; setAddingMedia(false); }
  }
  function date() { inputs.current.get(active.current)?.insert([{ kind: 'text', text: localDate() }]); }
  function addSegment() { const index = active.current + 1; update(draft => draft.segments.splice(index, 0, { blocks: [{ kind: 'text', text: '' }] })); active.current = index; requestAnimationFrame(() => inputs.current.get(index)?.focus()); }
  async function save(e: FormEvent) { e.preventDefault(); if (busyRef.current || mediaBusy.current) return; busyRef.current = true; setBusy(true);
    try { await call('save_phrase', { phrase: { ...draft, title: draft.title.trim() }, afterId: afterId || null }); await closeWindow(); }
    catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  return <section className="client-window editor-window"><Titlebar title={id ? '编辑话术' : '新增话术'} /><form className="editor-form" onSubmit={save}>
    <div className="phrase-kind">话术类型：<label><input type="checkbox" checked disabled />私人话术</label></div><p className="field-hint">管理自己的常用话术，按分类快速查找。</p>
    <div className="editor-categories"><label>请选择一级分类<select required value={draft.rootId} onChange={e => update(p => { p.rootId = e.target.value; p.groupId = null; })}>{roots.map(c => <option key={c.id} value={c.id}>{categoryLabel(c, library.categories)}</option>)}</select></label><label>请选择二级分类（可不选）<select value={draft.groupId || ''} onChange={e => update(p => { p.groupId = e.target.value || null; })}><option value="">不选二级分类</option>{library.categories.filter(c => c.parentId === draft.rootId).map(c => <option key={c.id} value={c.id}>{categoryLabel(c, library.categories)}</option>)}</select></label></div>
    <label className="title-field" htmlFor="phrase-title">话术标题（可不填）</label><div className="title-and-colors"><input id="phrase-title" placeholder="请输入话术标题，不超过200个字" value={draft.title} onChange={e => update(p => { p.title = e.target.value; })} /><div className="color-palette" aria-label="标题颜色">{colors.map(([color, name], i) => <button type="button" key={color} aria-label={name} title={name} aria-pressed={draft.color === color} style={{ background: i ? color : '#fff', color: color === '#5c6770' ? '#fff' : '#111' }} onClick={() => update(p => { p.color = color; })}>{draft.color === color && <Icon name="check" />}</button>)}</div></div>
    <div className="editor-toolbar"><span>话术内容</span><span className="toolbar-spacer" /><button type="button" disabled={busy || addingMedia} onClick={date}><Icon name="calendar" />日期</button><button type="button" disabled={busy || addingMedia} onClick={() => void media('image')}><Icon name="image" />图片</button><button type="button" disabled={busy || addingMedia} onClick={() => void media('file')}><Icon name="attachment" />文件</button><button type="button" disabled={busy || addingMedia} onClick={addSegment}><Icon name="segment" />添加分段</button></div>
    <div className="segment-editor">{draft.segments.map((segment, si) => <div className={`segment${draft.segments.length > 1 ? ' multiple' : ''}`} key={si}>
      {draft.segments.length > 1 && <div className="segment-label"><span>第{si + 1}段</span><button type="button" disabled={busy || addingMedia} aria-label={`删除第${si + 1}段`} onClick={() => { update(p => p.segments.splice(si, 1)); active.current = 0; }}>×</button></div>}
      <RichSegment blocks={segment.blocks} label={`第${si + 1}段文字`} disabled={busy} ref={input => { if (input) inputs.current.set(si, input); else inputs.current.delete(si); }} onFocus={() => { active.current = si; }} onChange={blocks => update(p => { p.segments[si].blocks = blocks; })} onPasteImages={(files, at) => void pasteImages(files, si, at)} />
    </div>)}</div>
    {error && <div className="validation-error" role="alert">{error}</div>}
    <footer className="editor-footer"><button className="text-button orange" type="button" disabled={busy || addingMedia} onClick={() => setShortcutOpen(true)}><Icon name="keyboard" />{draft.shortcut || '自定义快捷键'}</button><span className="attachment-limit">图片 {count('image')}/9 · 文件 {count('file')}/9</span><button className="secondary-button" type="button" disabled={busy || addingMedia} onClick={() => void closeWindow()}>取消</button><button className="primary-button" type="submit" disabled={busy || addingMedia}>{busy ? '保存中' : '确定'}</button></footer>
  </form>{shortcutOpen && <ShortcutDialog id={draft.id} value={draft.shortcut} onConfirm={async shortcut => update(p => { p.shortcut = shortcut; })} close={() => setShortcutOpen(false)} />}</section>;
}

export function FileEditor({ library, id, categoryId }: { library: Library; id: string; categoryId: string }) {
  const file = library.files.find(f => f.id === id); const categories = library.categories.filter(c => c.kind === 'file');
  const [category, setCategory] = useState(file?.categoryId || categoryId || categories[0]?.id || ''); const [paths, setPaths] = useState<string[]>([]); const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  async function choose() { try { const chosen = await open({ title: id ? '重新关联文件' : '批量关联文件', directory: false, multiple: !id }); if (chosen) setPaths(typeof chosen === 'string' ? [chosen] : chosen); setError(''); } catch (e) { setError(String(e)); } }
  async function save(e: FormEvent) { e.preventDefault(); if (busyRef.current) return; if (!paths.length) { setError('请先选择文件。'); return; } busyRef.current = true; setBusy(true);
    try { if (id) await call('relink_file', { id, path: paths[0], categoryId: category }); else await call('add_files', { paths, categoryId: category }); await closeWindow(); }
    catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  return <section className="client-window file-window"><Titlebar title={id ? '重新关联文件' : '新增文件'} /><form className="file-form" onSubmit={save}>{file && <p className="field-hint">原关联：{file.name}</p>}<label>请选择分类<select value={category} onChange={e => setCategory(e.target.value)} required>{categories.map(c => <option key={c.id} value={c.id}>{categoryLabel(c, library.categories)}</option>)}</select></label><button className="primary-button add-files" type="button" onClick={() => void choose()}>＋ 添加文件</button><div className="chosen-files">{paths.map(path => <span key={path} title={path}>{basename(path)}</span>)}</div>{error && <p className="validation-error" role="alert">{error}</p>}<div className="file-form-footer"><span>{id ? '关联变更不会删除原文件' : '温馨提示：按住Ctrl键多选，可以批量添加文件'}</span><span className="toolbar-spacer" /><button className="secondary-button" type="button" onClick={() => void closeWindow()}>取消</button><button className="primary-button" type="submit" disabled={busy}>{busy ? '保存中' : '确定'}</button></div></form></section>;
}
