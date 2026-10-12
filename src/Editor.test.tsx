import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { open } from '@tauri-apps/plugin-dialog';
import { Editor } from './Editor';
import { call } from './api';
import type { Block, Library, Phrase } from './types';

vi.mock('./api', () => ({ call: vi.fn(async () => {}), closeWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ convertFileSrc: (path: string) => path }));
const originalEdit = document.execCommand;
afterEach(() => { cleanup(); vi.clearAllMocks(); document.execCommand = originalEdit; });
const image: Block = { kind: 'image', path: 'C:/images/示例.png', name: '示例.png' };
const phrase = (blocks: Block[]): Phrase => ({ id: 'P', rootId: 'root', groupId: null, title: '图文', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks }] });
function editor(blocks: Block[], creating = false) {
  const library = { categories: [{ id: 'root', name: '分类', kind: 'phrase', parentId: null, position: 0, background: '' }], phrases: [phrase(blocks)] } as Library;
  render(<Editor library={library} id={creating ? '' : 'P'} afterId="" categoryId="root" groupId="" />);
  return screen.getByRole('textbox', { name: '第1段文字' });
}
function select(input: HTMLElement, start: number, end = start) {
  fireEvent.focus(input); const range = document.createRange(); range.setStart(input.firstChild!, start); range.setEnd(input.firstChild!, end);
  window.getSelection()!.removeAllRanges(); window.getSelection()!.addRange(range); fireEvent.pointerUp(input);
}
const saved = () => vi.mocked(call).mock.calls.find(([cmd]) => cmd === 'save_phrase')?.[1]?.phrase as Phrase;
const clipboardImage = () => Object.assign(new File([new Uint8Array([1, 2, 3])], '截图.png', { type: 'image/png' }), { arrayBuffer: vi.fn(async () => new Uint8Array([1, 2, 3]).buffer) });

describe('行内图片编辑', () => {
  it.each(['新增', '编辑'])('%s话术粘贴设备日志话术保留电脑、手机表情及换行，保存重开不丢失', async mode => {
    const text = `您好，辛苦您帮忙导出一下设备日志，我们好反馈给技术同事排查问题～

根据您当前登录的客户端，任选一种方式操作即可：

💻 电脑/网页端
打开绿联云 → 帮助中心 → 联系我们 → 辅助工具 → 点击“生成系统诊断信息”，把生成的压缩包发给我们。

📱 手机APP端
打开绿联云APP → 首页→ 帮助中心 → 辅助工具 → 点击“一键生成”生成诊断信息后，点击下载日志。然后去手机自带的【文件】应用里找到压缩包发给我们：

  安卓手机路径：文件APP → Download → ugreenPro

  苹果手机路径：文件APP → 绿联云 → Download

两种方式任选一种就行，弄好后直接发我，感谢您的配合～`;
    const input = editor([{ kind: 'text', text: '' }], mode === '新增');
    fireEvent.paste(input, { clipboardData: { getData: (format: string) => format === 'text/plain' ? text : '' } });
    expect(input.textContent).toBe(text);
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks).toEqual([{ kind: 'text', text }]));
    const blocks = saved().segments[0].blocks; cleanup();
    expect(editor(blocks).textContent).toBe(text);
  });
  it('粘贴组合表情保留肤色、连接符及文字／表情变体选择符', async () => {
    const input = editor([{ kind: 'text', text: '前后' }]); select(input, 1);
    const text = '👩🏽‍💻 🏳️‍🌈 ☎︎ ☎️';
    fireEvent.paste(input, { clipboardData: { getData: () => text } });
    expect(input.textContent).toBe(`前${text}后`);
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks).toEqual([{ kind: 'text', text: `前${text}后` }]));
  });
  it.each(['新增', '编辑'])('%s话术粘贴图片替换原选区，移动光标不改变插入位置，保存保留图文顺序', async mode => {
    const input = editor([{ kind: 'text', text: '前文替换后文' }], mode === '新增');
    input.textContent = '前文替换后文'; fireEvent.input(input); select(input, 2, 4);
    const file = clipboardImage(); vi.mocked(call).mockResolvedValue([image]);
    fireEvent.paste(input, { clipboardData: { files: [file], getData: () => '图片的文字描述' } });
    select(input, 6); await screen.findByAltText('示例.png');
    expect(input.textContent).toBe('前文×后文');
    expect(call).toHaveBeenCalledWith('import_pasted_images', { images: [[1, 2, 3]] });
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks).toEqual([{ kind: 'text', text: '前文' }, image, { kind: 'text', text: '后文' }]));
  });
  it('粘贴图片达到9张上限时不读取文件或导入，原内容保留', async () => {
    const input = editor(Array.from({ length: 9 }, () => ({ ...image }))); const file = clipboardImage();
    fireEvent.paste(input, { clipboardData: { files: [file], getData: () => '' } });
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Error: 每条话术最多9张图片。');
    expect(file.arrayBuffer).not.toHaveBeenCalled(); expect(call).not.toHaveBeenCalled(); expect(screen.getAllByAltText('示例.png')).toHaveLength(9);
  });
  it('图片导入失败保留文字与选区，连续粘贴不会重入，失败后可以重试', async () => {
    const input = editor([{ kind: 'text', text: '前后' }]); select(input, 1);
    vi.mocked(call).mockRejectedValueOnce(new Error('图片无法读取或尺寸过大。'));
    const clipboardData = { files: [clipboardImage()], getData: () => '' };
    fireEvent.paste(input, { clipboardData }); fireEvent.paste(input, { clipboardData });
    await screen.findByRole('alert'); expect(input.textContent).toBe('前后'); expect(call).toHaveBeenCalledTimes(1);
    vi.mocked(call).mockResolvedValueOnce([image]); fireEvent.paste(input, { clipboardData });
    await screen.findByAltText('示例.png'); expect(input.textContent).toBe('前×后');
  });
  it('浏览器已经插入但返回false时不再次插入附件', async () => {
    const input = editor([{ kind: 'text', text: '前后' }]); select(input, 1);
    document.execCommand = vi.fn((_command, _ui, html) => {
      const range = window.getSelection()!.getRangeAt(0); const fragment = range.createContextualFragment(html!); const last = fragment.lastChild!;
      range.deleteContents(); range.insertNode(fragment); range.setStartAfter(last); range.collapse(true); return false;
    });
    vi.mocked(open).mockResolvedValue(['C:/附件.txt']);
    fireEvent.click(screen.getByRole('button', { name: '文件' }));
    await screen.findByRole('button', { name: '删除附件 附件.txt' });
    expect(input.querySelectorAll('[data-block-kind="file"]')).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks.map(b => b.kind)).toEqual(['text', 'file', 'text']));
  });
  it('在选区插入图片，保存前文、图片、后文的原顺序，删除图片后两侧文字保留', async () => {
    const input = editor([{ kind: 'text', text: '前文替换后文' }]); select(input, 2, 4);
    vi.mocked(open).mockResolvedValue(['C:/选取.png']); vi.mocked(call).mockResolvedValue([image]);
    fireEvent.click(screen.getByRole('button', { name: '图片' }));
    await screen.findByAltText('示例.png');
    expect(input.textContent).toBe('前文×后文');
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks).toEqual([{ kind: 'text', text: '前文' }, image, { kind: 'text', text: '后文' }]));
    fireEvent.click(screen.getByRole('button', { name: '删除附件 示例.png' }));
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(vi.mocked(call).mock.calls.filter(([cmd]) => cmd === 'save_phrase')).toHaveLength(2));
    const last = vi.mocked(call).mock.calls.filter(([cmd]) => cmd === 'save_phrase').at(-1)![1]!.phrase as Phrase;
    expect(last.segments[0].blocks).toEqual([{ kind: 'text', text: '前文后文' }]);
  });
  it('已含9张图片时拒绝新增，不导入图片或改变原内容', async () => {
    editor(Array.from({ length: 9 }, () => ({ ...image })));
    vi.mocked(open).mockResolvedValue(['C:/第十张.png']);
    fireEvent.click(screen.getByRole('button', { name: '图片' }));
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Error: 每条话术最多9张图片。');
    expect(call).not.toHaveBeenCalled(); expect(screen.getAllByAltText('示例.png')).toHaveLength(9);
  });
  it('粘贴只插入纯文字；中文、表情和空行输入不重建正在编辑的DOM', async () => {
    const input = editor([{ kind: 'text', text: '前后' }]); select(input, 1);
    fireEvent.paste(input, { clipboardData: { getData: (format: string) => format === 'text/plain' ? '<测试>\n中文' : '<img src="恶意路径">' } });
    expect(input.textContent).toBe('前<测试>\n中文后'); expect(input.querySelector('img')).toBeNull();
    input.innerHTML = '欢迎<div>中文📎</div><div><br></div><div>下一行</div>'; const composingNode = input.childNodes[1];
    fireEvent.compositionStart(input); fireEvent.input(input, { inputType: 'insertCompositionText', isComposing: true });
    expect(input.childNodes[1]).toBe(composingNode); fireEvent.compositionEnd(input);
    fireEvent.click(screen.getByRole('button', { name: '确定' }));
    await waitFor(() => expect(saved()?.segments[0].blocks).toEqual([{ kind: 'text', text: '欢迎\n中文📎\n\n下一行' }]));
  });
});
