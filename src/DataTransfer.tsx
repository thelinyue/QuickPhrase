import { useEffect, useRef, useState } from 'react';
import { open, save } from '@tauri-apps/plugin-dialog';
import { call, useTeams, useTeamView } from './api';
import { FormDialog, type DialogSpec } from './ui';
import { categoryLabel, localDate, type Library } from './types';

export type TransferTarget = { source: 'private'; libraryId: string; sessionEpoch: number; devicePart?: boolean } | { source: 'team'; teamId: string; version: number; lifecycleVersion: number; sessionEpoch: number };
interface Preview { id: string; kind: string; filename: string; categories: number; phrases: number; commonPhrases: number; files: number; images: number; attachments: number; recycled: number; rows: { category: string; title: string; text: string }[]; notes: string[]; target: TransferTarget; targetLabel: string; merge: boolean; restoreSettings: boolean; state: string }
interface ImportResult { state: string; message: string }

/** 界面只选择库和选项；预览目标、批次ID和实际素材由Rust固定，旧响应不能切回旧账号。 */
export function DataTransfer({ mode, library, initialTeamId = '', initialCategoryId = '' }: { mode: 'import' | 'export'; library: Library; initialTeamId?: string; initialCategoryId?: string }) {
  const { status, version } = useTeams();
  const [source, setSource] = useState<'private' | 'team'>(initialTeamId ? 'team' : 'private');
  const [teamId, setTeamId] = useState(initialTeamId); const { view, error } = useTeamView(teamId, status, version);
  const [category, setCategory] = useState(initialCategoryId); const [kind, setKind] = useState('backup');
  const [devicePart, setDevicePart] = useState(false); const [hasLegacy, setHasLegacy] = useState(false);
  const [path, setPath] = useState(''); const [merge, setMerge] = useState(false); const [restore, setRestore] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null); const [dialog, setDialog] = useState<DialogSpec | null>(null);
  const [message, setMessage] = useState(''); const [busy, setBusy] = useState(false); const operation = useRef(''); const sequence = useRef(0); const failure = useRef('');
  const target: TransferTarget | null = source === 'private' ? library.libraryId ? { source, libraryId: library.libraryId, sessionEpoch: status.sessionEpoch || 0, ...(devicePart ? { devicePart: true } : {}) } : null : view ? { source, teamId, version: view.team.version, lifecycleVersion: view.team.lifecycleVersion, sessionEpoch: status.sessionEpoch || 0 } : null;
  const stamp = source === 'private' ? `private:${library.libraryId}:${status.sessionEpoch || 0}:${devicePart}` : `team:${status.server}:${status.user?.id}:${teamId}:${view?.team.lifecycleVersion}:${status.sessionEpoch || 0}`;
  const currentStamp = useRef(stamp); currentStamp.current = stamp;
  const editable = source === 'private' || !!(view?.team.editable && !status.offline && !status.user?.mustChangePassword);
  const categories = source === 'private' ? library.categories.filter(c => !library.ownerLabel || c.kind === 'phrase') : view?.categories || [];
  const privateLabel = library.ownerLabel ? `个人话术 · ${library.ownerLabel}` : '个人话术';
  const label = source === 'private' ? privateLabel : `共享话术 · ${view?.team.name || ''}`;
  const packageLabel = source === 'private' && !category ? '完整备份' : '话术包';
  function invalidate() { sequence.current++; setPreview(null); setDialog(null); setMessage(''); }
  useEffect(() => {
    invalidate(); if (operation.current) void call('cancel_data_transfer', { operationId: operation.current }).catch(() => {});
    setCategory(initialCategoryId); setPath('');
    return () => { sequence.current++; if (operation.current) void call('cancel_data_transfer', { operationId: operation.current }).catch(() => {}); };
  }, [stamp]);
  useEffect(() => { if (category && !categories.some(c => c.id === category && !c.parentId)) setCategory(''); }, [categories, category]);
  useEffect(() => {
    if (mode !== 'import' || !target) return;
    const request = ++sequence.current;
    void call<Preview | null>('get_import_pending', { target }).then(saved => {
      if (request !== sequence.current || !saved) return;
      setPreview(saved); setMerge(saved.merge); setRestore(saved.restoreSettings);
      setMessage(saved.state === 'pending' ? '上次导入结果待确认' : '已恢复上次预览');
    }).catch(e => { if (request === sequence.current) setMessage(String(e)); });
  }, [mode, stamp, !!target]);
  useEffect(() => {
    let disposed = false; setHasLegacy(false);
    if (mode === 'import' && source === 'private' && !devicePart && library.libraryId) {
      void call<boolean>('has_legacy_private_content', { libraryId: library.libraryId }).then(value => { if (!disposed) setHasLegacy(value === true); }).catch(() => {});
    }
    return () => { disposed = true; };
  }, [mode, stamp]);
  async function recoverLegacy() {
    if (!target || devicePart) return;
    const next = await run(id => call<Preview>('preview_legacy_private_content', { target, operationId: id }));
    if (next) { setPath(''); setMerge(false); setRestore(false); setPreview(next); show(next); }
  }
  async function run<T>(work: (id: string) => Promise<T>): Promise<T | undefined> {
    if (operation.current) return;
    const id = crypto.randomUUID(), scope = currentStamp.current; operation.current = id; setBusy(true); setMessage(''); failure.current = '';
    try { const result = await work(id); return scope === currentStamp.current ? result : undefined; }
    catch (e) { if (scope === currentStamp.current) { failure.current = String(e); setMessage(failure.current); } return undefined; }
    finally { if (operation.current === id) { operation.current = ''; setBusy(false); } }
  }
  async function choose() {
    const scope = currentStamp.current;
    await run(async () => { const selected = await open({ title: '选择导入文件', directory: false, multiple: false, filters: [{ name: '话术包或Excel', extensions: ['zip', 'xlsx'] }] }); if (typeof selected === 'string' && scope === currentStamp.current) { invalidate(); setPath(selected); setRestore(false); } });
  }
  async function prepare() {
    if (!target || !path) return;
    const next = await run(id => call<Preview>('preview_import', { path, target, merge, restoreSettings: restore, operationId: id }));
    if (next) { setPreview(next); show(next); }
  }
  function show(next: Preview) {
    if (next.state === 'pending') return;
    setDialog({ title: '导入预览', submit: '确认导入', fields: <div className="import-preview"><p><strong>{next.targetLabel}</strong> · {next.filename}</p><p>{next.categories} 个分类 · {next.phrases} 条话术 · {next.images} 张图片 · {next.attachments} 个附件</p>{(next.commonPhrases > 0 || next.recycled > 0 || next.files > 0) && <p>{next.commonPhrases} 条常用短语 · {next.recycled} 条回收记录 · {next.files} 个文件关联</p>}<div className="preview-table"><table className="template-table"><thead><tr><th>分类</th><th>标题</th><th>正文摘要</th></tr></thead><tbody>{next.rows.map((row, i) => <tr key={i}><td>{row.category}</td><td>{row.title || '无标题'}</td><td>{row.text}</td></tr>)}</tbody></table></div>{next.notes.map((note, i) => <p className="settings-hint" key={i}>{note}</p>)}<p className="settings-hint">{next.merge ? '合并同级唯一同名分类' : '分类独立导入'}；{next.restoreSettings ? '恢复设置，发送与快捷键另行确认' : '保留当前设置'}。</p></div>, onSubmit: async () => {
      const result = await run(id => call<ImportResult>('confirm_import', { previewId: next.id, target: next.target, operationId: id }));
      if (!result) throw new Error(failure.current || '导入未完成，请保留预览后重试。');
      setMessage(result.message);
      if (result.state === 'completed') setPreview(null); else setPreview({ ...next, state: result.state });
    } });
  }
  async function inspect() {
    if (!preview) return;
    const result = await run(id => call<ImportResult>('check_import_result', { previewId: preview.id, target: preview.target, operationId: id }));
    if (result) { setMessage(result.message); if (result.state === 'completed') setPreview(null); else setPreview({ ...preview, state: result.state }); }
  }
  async function refreshPreview() {
    if (!preview) return;
    const next = await run(id => call<Preview | null>('refresh_import_preview', { previewId: preview.id, target: preview.target, operationId: id }));
    if (next) { setPreview(next); show(next); } else if (next === null) { setPreview(null); setMessage('导入完成'); }
  }
  async function exportData() {
    if (!target) return;
    await run(async id => {
      const extension = kind === 'backup' ? 'zip' : 'xlsx'; const name = label.replace(/[<>:"/\\|?*]/g, '_');
      const selected = await save({ title: '导出话术', defaultPath: `闪语-${name}-${localDate()}.${extension}`, filters: [{ name: kind === 'backup' ? packageLabel : 'Excel文字', extensions: [extension] }] });
      if (!selected || currentStamp.current !== stamp) return;
      await call('export_data', { path: selected, kind, categoryId: category || null, target, operationId: id });
      if (currentStamp.current === stamp) setMessage('导出完成');
    });
  }
  async function template() { const scope = currentStamp.current; await run(async () => { const selected = await save({ title: '保存文字模板', defaultPath: '闪语文字模板.xlsx', filters: [{ name: 'Excel', extensions: ['xlsx'] }] }); if (selected && scope === currentStamp.current) { await call('export_template', { path: selected }); if (scope === currentStamp.current) setMessage('文字模板已保存'); } }); }
  const pending = preview?.state === 'pending';
  return <div className="data-transfer"><h2 className="settings-heading">{mode === 'import' ? '导入话术' : '导出话术'}</h2>
    <div className="setting-block"><label className="transfer-row"><span>{mode === 'import' ? '目标' : '来源'}</span><select aria-label={mode === 'import' ? '目标库' : '来源库'} value={source} disabled={busy} onChange={e => { invalidate(); setCategory(''); setSource(e.target.value as 'private' | 'team'); setDevicePart(false); if (!teamId) setTeamId(status.teams[0]?.teamId || ''); }}><option value="private">{privateLabel}</option><option value="team" disabled={!status.teams.length}>共享话术</option></select></label>
      {mode === 'import' && source === 'private' && <label className="transfer-row"><span>导入部分</span><select aria-label="导入部分" value={devicePart ? 'device' : 'personal'} disabled={busy || pending} onChange={e => { invalidate(); setDevicePart(e.target.value === 'device'); setRestore(false); }}><option value="personal">个人内容</option><option value="device">本机文件与设备设置</option></select></label>}
      {source === 'team' && <label className="transfer-row"><span>共享话术</span><select aria-label="选择共享话术" value={teamId} disabled={busy} onChange={e => { invalidate(); setCategory(''); setTeamId(e.target.value); }}>{status.teams.map(t => <option key={t.teamId} value={t.teamId}>{t.name}{mode === 'import' && !t.editable ? '（只读）' : ''}</option>)}</select></label>}
      {mode === 'export' && <label className="transfer-row"><span>分类</span><select aria-label="导出分类" value={category} disabled={busy} onChange={e => setCategory(e.target.value)}><option value="">全部分类</option>{categories.filter(c => !c.parentId).map(c => <option key={c.id} value={c.id}>{categoryLabel(c, categories)}{c.kind === 'file' ? '（文件关联）' : ''}</option>)}</select></label>}
      {status.offline && source === 'team' && <p className="settings-hint transfer-status">离线 · 使用已缓存内容</p>}
    </div>
    {mode === 'import' ? <><div className="setting-block"><p>支持闪语备份、话术包及 Excel。</p><div className="setting-buttons"><button className="secondary-button" disabled={busy || !target || !editable || pending} onClick={() => void choose()}>选择文件</button><button className="secondary-button" disabled={busy} onClick={() => void template()}>下载文字模板</button></div>{hasLegacy && <button className="secondary-button" disabled={busy || !target || pending} onClick={() => void recoverLegacy()}>找回旧本机内容</button>}<p className="import-path">{path || preview?.filename || '未选择文件'}</p></div>
      <div className="setting-block"><label><input type="checkbox" checked={merge} disabled={busy || pending} onChange={e => { invalidate(); setMerge(e.target.checked); }} />合并同级唯一同名分类</label>{source === 'private' && <label><input type="checkbox" checked={restore} disabled={busy || pending || path.toLowerCase().endsWith('.xlsx')} onChange={e => { invalidate(); setRestore(e.target.checked); }} />{devicePart ? '恢复设备设置' : '恢复个人设置'}</label>}<p className="settings-hint">先预览，再导入独立副本。失败整批回滚。个人内容与设备部分分别预览、分别提交。</p>{!editable && <p className="settings-hint">导入需要联网及共享话术编辑权限。</p>}</div>
      <div className="setting-actions">{preview && <button className="secondary-button" disabled={busy} onClick={() => void inspect()}>核对结果</button>}{pending ? <span role="status">结果待确认</span> : <>{preview && <button className="secondary-button" disabled={busy || !editable} onClick={() => void refreshPreview()}>重新预览</button>}<button className="primary-button" disabled={busy || !target || !editable || (!path && !preview) || preview?.state === 'invalid'} onClick={() => preview ? show(preview) : void prepare()}>预览导入</button></>}</div></>
      : <><div className="setting-block"><p>格式</p><label><input type="radio" name="exportType" checked={kind === 'backup'} disabled={busy} onChange={() => setKind('backup')} />{packageLabel}（含图片、附件）</label><label><input type="radio" name="exportType" checked={kind === 'excel'} disabled={busy} onChange={() => setKind('excel')} />Excel（仅文字）</label><p className="settings-hint">{source === 'private' ? '导出当前本机版本，包含待同步修改。' : '仅导出在用话术，不含回收站、成员及权限。'}素材不足时请联网补齐。</p>{source === 'private' && !library.ownerLabel && <p className="settings-hint">独立文件库只保存路径关联，源文件需单独备份。</p>}</div><div className="setting-actions"><button className="primary-button" disabled={busy || !target} onClick={() => void exportData()}>导出</button></div></>}
    {busy && <div className="setting-buttons"><span role="status">处理中…</span><button className="secondary-button" onClick={() => void call('cancel_data_transfer', { operationId: operation.current }).catch(e => setMessage(String(e)))}>取消</button></div>}
    {(message || error) && <p className="validation-error" role="status">{message || error}</p>}
    {dialog && <FormDialog spec={dialog} close={() => setDialog(null)} />}
  </div>;
}
