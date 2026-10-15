import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest';
import { ShortcutCandidatesWindow } from './ShortcutCandidates';
import { call } from './api';
import type { ShortcutCandidates } from './types';

const handler = vi.hoisted(() => ({ fn: (_: { payload: ShortcutCandidates | null }) => {} }));
vi.mock('./api', () => ({ call: vi.fn(async () => null) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async (_: string, callback: typeof handler.fn) => { handler.fn=callback; return () => {}; }) }));
beforeEach(() => { Element.prototype.scrollIntoView=vi.fn(); });
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const payload: ShortcutCandidates = { shortcut:'Q+W',selectedIndex:0,target:{hwnd:1,focus:2,pid:3},phrases:Array.from({length:12},(_,i)=>({id:String(i),rootId:'R',groupId:null,title:`话术${i+1}`,shortcut:'Q+W',position:i,color:'',background:'',segments:[]})) };
describe('原生候选快照绘制', () => {
  it('候选标题保留单条、全局、主题的颜色优先级', async () => {
    const phrases = payload.phrases.slice(0, 2).map((phrase, i) => ({ ...phrase, color: i ? '#ff5100' : '#123456' }));
    vi.mocked(call).mockImplementation(async name => name === 'get_shortcut_candidates' ? { ...payload, phrases } : null);
    const view = render(<ShortcutCandidatesWindow titleColor="#652f7b" />);
    await screen.findAllByRole('option');
    const labels = () => [...document.querySelectorAll<HTMLElement>('.candidate-label')].map(el => el.style.color);
    expect(labels()).toEqual(['rgb(18, 52, 86)', 'rgb(101, 47, 123)']);
    view.rerender(<ShortcutCandidatesWindow titleColor="#ff5100" />);
    expect(labels()).toEqual(['rgb(18, 52, 86)', '']);
  });
  it('显示正文摘要、合并空白并保留完整提示，空标题和重复正文不重复显示', async () => {
    const phrases = [
      { ...payload.phrases[0], title: '问候', segments: [{ blocks: [{ kind: 'text' as const, text: '您好\n  欢迎' }] }, { blocks: [{ kind: 'text' as const, text: '咨询' }] }] },
      { ...payload.phrases[1], title: '', segments: [{ blocks: [{ kind: 'text' as const, text: '完整正文'.repeat(30) }] }] },
      { ...payload.phrases[2], title: '相同 内容', segments: [{ blocks: [{ kind: 'text' as const, text: ' 相同\n内容 ' }] }] },
      { ...payload.phrases[3], title: '附件', segments: [{ blocks: [{ kind: 'file' as const, path: 'a.pdf', name: '说明.pdf' }, { kind: 'image' as const, path: 'a.png', name: '示例.png' }] }] },
    ];
    vi.mocked(call).mockImplementation(async name => name === 'get_shortcut_candidates' ? { ...payload, phrases } : null);
    render(<ShortcutCandidatesWindow />);
    const rows = await screen.findAllByRole('option');
    expect(rows[0].querySelector('.candidate-preview')?.textContent).toBe('您好 欢迎 咨询');
    expect(rows[0].getAttribute('title')).toBe('您好\n  欢迎\n咨询');
    expect(rows[1].querySelector('.candidate-label')).toBeNull();
    expect(rows[1].querySelector('.candidate-preview')?.textContent).toBe('完整正文'.repeat(30));
    expect(rows[2].querySelector('.candidate-preview')).toBeNull();
    expect(rows[3].querySelector('.candidate-preview')?.textContent).toBe('文件：说明.pdf · 图片：示例.png');
    await act(async () => handler.fn({ payload: { ...payload, phrases: [phrases[3]], selectedIndex: 0 } }));
    expect(screen.getAllByRole('option')).toHaveLength(1);
    expect(screen.getByRole('option').getAttribute('aria-selected')).toBe('true');
    expect(screen.queryByText('您好 欢迎 咨询')).toBeNull();
  });
  it('仅一项也绘制候选，查询后监听选择事件，候选多于十项仍可选', async () => {
    vi.mocked(call).mockImplementation(async name=>name==='get_shortcut_candidates'?{...payload,phrases:payload.phrases.slice(0,1)}:null);
    render(<ShortcutCandidatesWindow />); await screen.findByRole('option',{name:/话术1$/});
    await act(async()=>handler.fn({payload:{...payload,selectedIndex:11}}));
    expect(screen.getAllByRole('option')).toHaveLength(12); expect(screen.getByRole('option',{name:'话术12'}).getAttribute('aria-selected')).toBe('true');
    expect(document.querySelectorAll('.candidate-number')[9].textContent).toBe('0');
    fireEvent.click(screen.getByRole('option',{name:/话术3$/})); expect(call).toHaveBeenCalledWith('select_shortcut_candidate',{index:2});
    fireEvent.click(screen.getByRole('button',{name:'取消候选'})); expect(call).toHaveBeenCalledWith('cancel_shortcut_candidates');
    await act(async()=>handler.fn({payload:null})); await waitFor(()=>expect(screen.queryByRole('listbox')).toBeNull());
  });
});
