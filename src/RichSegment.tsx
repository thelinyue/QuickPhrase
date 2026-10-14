import { useImperativeHandle, useLayoutEffect, useRef, type Ref } from 'react';
import { convertFileSrc } from '@tauri-apps/api/core';
import type { Block } from './types';

export interface SegmentHandle { capture: () => Range | null; insert: (blocks: Block[], at?: Range | null) => void; focus: () => void }

/** 只编辑有序的文字、图片和附件，不保存任意 HTML。浏览器负责光标、换行、撤销和中文输入法；
 * 输入后转换回 Block，自己的状态更新不重建 DOM，避免中文组词和选区被打断。
 * 工具栏与图片粘贴先保存 Range，导入完成后仍在该位置插入，媒体是可整体删除的行内节点。 */
export function RichSegment({ blocks, label, disabled, onChange, onFocus, onPasteImages, ref }: { blocks: Block[]; label: string; disabled: boolean; onChange: (blocks: Block[]) => void; onFocus: () => void; onPasteImages: (files: File[], at: Range | null) => void; ref: Ref<SegmentHandle> }) {
  const root = useRef<HTMLDivElement>(null); const selection = useRef<Range | null>(null); const current = useRef('');
  const capture = () => {
    const range = window.getSelection()?.rangeCount ? window.getSelection()!.getRangeAt(0) : null;
    if (range && root.current?.contains(range.startContainer) && root.current.contains(range.endContainer)) selection.current = range.cloneRange();
    return selection.current?.cloneRange() || null;
  };
  const publish = () => { if (!root.current) return; const value = readBlocks(root.current); current.current = JSON.stringify(value); onChange(value); capture(); };
  function insert(value: Block[], at = capture()) {
    const element = root.current; if (!element || disabled) return;
    element.focus(); const range = at && element.contains(at.startContainer) && element.contains(at.endContainer) ? at : document.createRange();
    if (range !== at) { range.selectNodeContents(element); range.collapse(false); }
    const selected = window.getSelection(); selected?.removeAllRanges(); selected?.addRange(range);
    const fragment = renderBlocks(value); const box = document.createElement('div'); box.append(fragment.cloneNode(true));
    // WebView2 有时已经插入附件却返回 false，不能仅按返回值再次插入。
    // 编辑命令保留系统撤销栈；只有命令失败且 DOM 未变化时才使用 Range。
    const before = element.innerHTML;
    const inserted = document.execCommand?.('insertHTML', false, box.innerHTML);
    if (!inserted && element.innerHTML === before) {
      range.deleteContents(); const last = fragment.lastChild; range.insertNode(fragment);
      if (last) range.setStartAfter(last); range.collapse(true); selected?.removeAllRanges(); selected?.addRange(range);
    }
    publish();
  }
  useImperativeHandle(ref, () => ({ capture, insert, focus: () => { root.current?.focus(); } }));
  useLayoutEffect(() => {
    const value = JSON.stringify(blocks); if (value === current.current || !root.current) return;
    root.current.replaceChildren(renderBlocks(blocks)); current.current = value; selection.current = null;
  }, [blocks]);
  return <div ref={root} className="rich-segment" contentEditable={!disabled} suppressContentEditableWarning role="textbox" aria-label={label} aria-multiline="true" aria-disabled={disabled} data-placeholder="请输入话术，不超过5000个字"
    onFocus={onFocus} onBlur={capture} onPointerUp={capture} onKeyUp={capture} onInput={publish}
    onPaste={e => {
      e.preventDefault(); if (disabled) return;
      const files = Array.from(e.clipboardData.files || []).filter(file => file.type.startsWith('image/'));
      if (!files.length) for (const item of Array.from(e.clipboardData.items || [])) {
        if (item.type.startsWith('image/')) { const file = item.getAsFile(); if (file) files.push(file); }
      }
      if (files.length) { onPasteImages(files, capture()); return; }
      const text = e.clipboardData.getData('text/plain'); if (text) insert([{ kind: 'text', text }]);
    }}
    onClick={e => {
      const remove = (e.target as Element).closest('[data-remove-media]'); const media = remove?.closest<HTMLElement>('[data-block-kind]');
      if (!media || disabled) return; e.preventDefault(); const range = document.createRange(); range.selectNode(media); root.current?.focus();
      const selected = window.getSelection(); selected?.removeAllRanges(); selected?.addRange(range);
      if (!document.execCommand?.('delete')) media.remove(); publish();
    }} />;
}

function renderBlocks(blocks: Block[]) {
  const fragment = document.createDocumentFragment();
  for (const block of blocks) {
    if (block.kind === 'text') { fragment.append(document.createTextNode(block.text)); continue; }
    const media = document.createElement('span'); media.className = `media-block inline-${block.kind}`; media.contentEditable = 'false';
    media.dataset.blockKind = block.kind; media.dataset.path = block.path; media.dataset.name = block.name; media.title = block.name;
    if (block.companyAssetId) { media.dataset.companyAssetId = block.companyAssetId; media.dataset.companySessionEpoch = String(block.companySessionEpoch); }
    if (block.kind === 'image') { const img = document.createElement('img'); img.src = convertFileSrc(block.path); img.alt = block.name; img.draggable = false; media.append(img); }
    else { const name = document.createElement('span'); name.textContent = `📎 ${block.name}`; media.append(name); }
    const remove = document.createElement('button'); remove.type = 'button'; remove.dataset.removeMedia = ''; remove.setAttribute('aria-label', `删除附件 ${block.name}`); remove.textContent = '×'; media.append(remove);
    fragment.append(media);
  }
  return fragment;
}

/** div/p/br 只转成换行；图片及文件只读取我们生成的节点，粘贴 HTML 不进入数据模型。 */
function readBlocks(root: HTMLElement): Block[] {
  const blocks: Block[] = [];
  const text = (value: string) => { if (!value) return; const last = blocks.at(-1); if (last?.kind === 'text') last.text += value; else blocks.push({ kind: 'text', text: value }); };
  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE) { text(node.textContent || ''); return; }
    if (!(node instanceof HTMLElement)) return;
    const kind = node.dataset.blockKind;
    if (kind === 'image' || kind === 'file') { blocks.push({ kind, path: node.dataset.path || '', name: node.dataset.name || '', ...(node.dataset.companyAssetId ? { companyAssetId: node.dataset.companyAssetId, companySessionEpoch: Number(node.dataset.companySessionEpoch) } : {}) }); return; }
    if (node.tagName === 'BR') {
      // Chromium 在空行或最后一个换行之后添加占位 br，它本身不代表额外的一行。
      if (!node.nextSibling && (!node.previousSibling || (node.previousSibling as Element).tagName === 'BR')) return;
      text('\n'); return;
    }
    const line = node.tagName === 'DIV' || node.tagName === 'P';
    if (line && node.previousSibling) text('\n');
    node.childNodes.forEach(walk);
    if (line && node.nextSibling && !['DIV', 'P'].includes((node.nextSibling as Element).tagName)) text('\n');
  };
  root.childNodes.forEach(walk);
  return blocks.length ? blocks : [{ kind: 'text', text: '' }];
}
