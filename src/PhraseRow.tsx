import type { MouseEvent } from 'react';
import { FileIcon, Highlight, Icon, phraseTitleColor, useEnterSubmit } from './ui';
import { usePhraseHover } from './PhrasePreview';
import { labelOf, textOf, type Phrase, type LinkedFile, type Settings } from './types';

interface Props { phrase?: Phrase; file?: LinkedFile; index: number; selected: boolean; busy: boolean; settings: Settings; query?: string; onSelect: (e: MouseEvent) => void; onDeliver: (mode: 'insert' | 'send', triggerKey?: number) => void; onContext: (e: MouseEvent) => void }
/** 行内容和发送按钮分开处理。发送始终使用该行身份，固定按钮列保证显隐时文字不移动。 */
export function PhraseRow({ phrase, file, index, selected, busy, settings, query, onSelect, onDeliver, onContext }: Props) {
  const hover = usePhraseHover(phrase, busy);
  const enterSubmit = useEnterSubmit();
  const label = file?.name || (phrase ? labelOf(phrase) : '');
  const hasImage = phrase?.segments.some(segment => segment.blocks.some(block => block.kind === 'image'));
  const hasFile = phrase?.segments.some(segment => segment.blocks.some(block => block.kind === 'file'));
  // 混合话术优先提示图片，其次附件；删除媒体后由内容自然恢复文字图标。
  const phraseIcon = hasImage ? 'phrase-image' : hasFile ? 'phrase-file' : 'phrase-text';
  const title = file?.name || phrase?.title || (phrase && (!settings.showPreview || !textOf(phrase).trim()) ? label : '');
  const terms = query?.trim().toLowerCase().split(/\s+/).filter(Boolean) || [];
  const matchedFiles = phrase?.segments.flatMap(s => s.blocks).filter(b => b.kind === 'file' && terms.some(t => b.name.toLowerCase().includes(t))).map(b => b.kind === 'file' ? b.name : '').join(' · ') || '';
  // 旧版以橙色写入默认色；保留原记录，渲染时让旧默认色和新空值共同跟随全局设置。
  return <div className={`content-row${file ? ' file-row' : ''}${selected ? ' selected' : ''}`} role="option" aria-selected={selected} tabIndex={0} data-row={file?.id || phrase?.id} {...hover}
    onClick={onSelect} onContextMenu={onContext}
    onDoubleClick={e => { if (!(e.target as HTMLElement).closest('.row-send') && !busy && !(file && (e.ctrlKey || e.shiftKey || e.metaKey))) onDeliver(file ? 'insert' : settings.phraseDoubleClick || 'insert'); }}
    onKeyDown={e => { if (e.key === 'Enter' && !e.nativeEvent.isComposing && !e.altKey && !e.ctrlKey && !e.shiftKey && !e.metaKey && !(e.target as HTMLElement).closest('.row-send')) { e.preventDefault(); if (!busy && enterSubmit()) onDeliver('insert', 13); } }}>
    <button type="button" className="row-send" title="直接发送" aria-label={`直接发送 ${label}`} disabled={busy}
      onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); if (!busy && !e.nativeEvent.isComposing && enterSubmit()) onDeliver('send', 13); } }}
      onClick={e => { e.stopPropagation(); if (!busy && e.detail < 2) onDeliver('send'); }} onDoubleClick={e => e.stopPropagation()}>
      <svg className="send-arrow" viewBox="0 0 16 16" aria-hidden="true"><path d="M14 1 2 8l12 7v-4L7 8l7-3z" /></svg>
    </button>
    <span className="row-icon" title={file ? undefined : hasImage ? '含图片话术' : hasFile ? '含文件话术' : '文字话术'}>{file ? <FileIcon path={file.path || file.sourceName || file.name} /> : <Icon name={phraseIcon} />}</span>
    {settings.showNumbers && <span className="row-index">{index + 1}.</span>}
    <span className="row-copy">
    {title && (file || settings.showTitle !== false || !phrase || !textOf(phrase).trim() || terms.some(t => title.toLowerCase().includes(t))) && <span className="row-title" title={file ? title : undefined} style={{ color: file ? undefined : phraseTitleColor(phrase?.color, settings.titleColor), background: phrase?.background || undefined }}><Highlight text={title} query={query} /></span>}
    {phrase && (settings.showPreview || matchedFiles) && <span className="row-preview">{settings.showPreview && <Highlight text={textOf(phrase).replace(/\s+/g, ' ')} query={query} />}{matchedFiles && <>{settings.showPreview && ' · '}<Highlight text={matchedFiles} query={query} /></>}</span>}
    {phrase?.shortcut && <span className="row-shortcut" title={phrase.shortcut}>{phrase.shortcut.replaceAll('+', ',')}</span>}
    </span>
    {file && !query && <span className="row-source">{file.cloud ? '云端' : '本地'}</span>}
    {file && (file.status || file.missing) && <span className="row-status">{file.status || '文件失效'}</span>}
    {phrase?.ready === false && <span className="row-status">未缓存</span>}
  </div>;
}
