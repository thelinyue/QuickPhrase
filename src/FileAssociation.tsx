import { useEffect, useRef, useState, type FormEvent } from 'react';
import { confirm, open } from '@tauri-apps/plugin-dialog';
import { call, closeWindow, useTeams } from './api';
import { CloudFileTable } from './CompanyFiles';
import { Button, FileIcon, Titlebar } from './ui';
import { useCloseGuard, useEditorEscape } from './close';
import { categoryLabel, type CompanyFile, type Library } from './types';

/** 每次只关联一种来源；草稿独立于目录刷新，提交失败保留，真正写入由 Rust 事务完成。 */
export function FileEditor({ library, id, categoryId }: { library: Library; id: string; categoryId: string }) {
  const file = library.files.find(f => f.id === id), categories = library.categories.filter(c => c.kind === 'file');
  const { status } = useTeams(), epoch = status.sessionEpoch || 0;
  const [source, setSource] = useState<'local' | 'cloud'>(file?.cloud ? 'cloud' : 'local');
  const [category, setCategory] = useState(file?.categoryId || categoryId || categories[0]?.id || ''), [paths, setPaths] = useState<string[]>([]), [cloud, setCloud] = useState<CompanyFile[]>([]), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  const locked = useRef(false), current = useRef(epoch); current.current = epoch;
  const selection = JSON.stringify([source, category, paths, cloud.map(file => file.assetId)]), saved = useRef(selection), choosing = useRef(false);
  useCloseGuard({ busy: () => locked.current || choosing.current, dirty: () => selection !== saved.current });
  useEditorEscape(closeWindow);
  useEffect(() => { setCloud([]); }, [epoch]);
  async function switchSource(next: 'local' | 'cloud') {
    if (next === source || busy) return;
    if ((paths.length || cloud.length) && !await confirm('切换来源将清空本次选择，是否继续？', { title: '切换文件来源' })) return;
    setSource(next); setPaths([]); setCloud([]); setError('');
  }
  async function choose() {
    if (locked.current || choosing.current) return; choosing.current = true;
    try { const result = await open({ title: id ? '重新关联文件' : '批量关联文件', directory: false, multiple: !id }); if (result) setPaths(Array.isArray(result) ? result : [result]); setError(''); }
    catch (e) { setError(String(e)); } finally { choosing.current = false; }
  }
  async function save(e: FormEvent) {
    e.preventDefault(); if (locked.current) return;
    if (!(source === 'local' ? paths.length : cloud.length)) { setError('请先选择文件。'); return; }
    locked.current = true; setBusy(true); setError('');
    try {
      if (id && !library.files.some(f => f.id === id)) throw new Error('原关联已失效或账号已变化，请关闭后重新打开。');
      if (!categories.some(c => c.id === category)) throw new Error('文件分类已变化，请重新选择。');
      if (source === 'cloud') await call('add_cloud_files', { sessionEpoch: epoch, categoryId: category, assetIds: cloud.map(f => f.assetId), replaceId: id || null });
      else if (id) await call('relink_file', { id, path: paths[0], categoryId: category });
      else await call('add_files', { paths, categoryId: category });
      if (current.current === epoch) { saved.current = selection; locked.current = false; await closeWindow(); }
    } catch (e) { if (current.current === epoch) setError(String(e)); }
    finally { locked.current = false; setBusy(false); }
  }
  return <section className="client-window file-association-window"><Titlebar minimize maximize title={id ? '重新关联文件' : '关联文件'} /><form onSubmit={save} className="association-form">
    <div className="association-category"><label>文件分类 <select aria-label="文件分类" value={category} disabled={busy} onChange={e => setCategory(e.target.value)} required>{!categories.some(c => c.id === category) && <option value={category}>原文件分类已不存在，请重新选择</option>}{categories.map(c => <option key={c.id} value={c.id}>{categoryLabel(c, categories)}</option>)}</select></label>{file && <span title={file.name}>原关联：{file.name}</span>}</div>
    <div className="association-body" inert={busy}><nav className="association-sources" aria-label="关联来源"><Button variant="ghost" type="button" className={source === 'local' ? 'active' : ''} disabled={busy} onClick={() => void switchSource('local')}>关联本地文件</Button><Button variant="ghost" type="button" className={source === 'cloud' ? 'active' : ''} disabled={busy} onClick={() => void switchSource('cloud')}>关联云文件</Button></nav>
      {source === 'cloud' ? <CloudFileTable status={status} selected={cloud} onChange={files => { if (!locked.current) setCloud(files); }} limit={id ? 1 : 50} /> : <div className="local-selection"><div className="company-toolbar"><Button variant="primary" type="button" disabled={busy} onClick={() => void choose()}>＋ 选择本地文件</Button></div><div className="local-selection-list">{paths.length ? paths.map(path => <div className="cloud-file-name" key={path}><FileIcon path={path} /><span title={path}>{path.split(/[\\/]/).pop()}<small>{path}</small></span></div>) : <p className="company-empty">选择本机文件，可按住 Ctrl 多选</p>}</div></div>}
    </div>{error && <p role="alert" className="validation-error">{error}</p>}<footer className="association-footer"><span>已选择 {source === 'local' ? paths.length : cloud.length} 个文件 · 仅管理关联</span><span className="toolbar-spacer" /><Button variant="secondary" type="button" disabled={busy} onClick={() => void closeWindow()}>取消</Button><Button variant="primary" type="submit" busy={busy} disabled={busy || (source === 'cloud' && (!status.user || status.offline))}>{busy ? '保存中…' : '确定关联'}</Button></footer>
  </form></section>;
}
