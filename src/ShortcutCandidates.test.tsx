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
