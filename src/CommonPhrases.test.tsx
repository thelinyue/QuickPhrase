import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CommonDockWindow, CommonPhraseBar, commonNavigationIndex } from './CommonPhrases';
import { call, useTeams } from './api';
import type { Library, Settings } from './types';

vi.mock('./api', () => ({ call: vi.fn(), closeWindow: vi.fn(), openWindow: vi.fn(), useTeams: vi.fn() }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn().mockResolvedValue(() => {}) }));
beforeEach(() => { const status = { server: '', username: '', user: null, teams: [], offline: false }; vi.mocked(useTeams).mockReturnValue({ status, version: 0, loaded: true }); });
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); });
function bar(mode: Settings['commonSingleClick'] = 'select', compact = false, busy = false) {
  const onDeliver = vi.fn();
  const library = { commonPhrases: [{ id: 'B', text: '谢谢', position: 0 }], settings: { commonSingleClick: mode, commonHeight: 144, dockCommonRows: 0 } } as Library;
  render(<CommonPhraseBar library={library} compact={compact} busy={busy} onDeliver={onDeliver} onError={vi.fn()} />);
  return onDeliver;
}
describe('常用短语统一单击插入', () => {
  it.each([false, true])('左右邻项和上下最近项导航，Enter发送防长按，横条=%s', compact => {
    const onDeliver=vi.fn(); const library={commonPhrases:[{id:'A',text:'第一项',position:0},{id:'B',text:'第二项',position:1},{id:'C',text:'下一行',position:2}],settings:{commonSingleClick:'select',commonHeight:144,dockCommonRows:0}} as Library;
    render(<CommonPhraseBar library={library} compact={compact} busy={false} onDeliver={onDeliver} onError={vi.fn()} />);
    const buttons=screen.getAllByRole('option'); const rects=[{left:0,top:0,width:50},{left:60,top:0,width:100},{left:50,top:30,width:70}];
    buttons.forEach((button,i)=>vi.spyOn(button,'getBoundingClientRect').mockReturnValue(rects[i] as DOMRect));
    buttons[0].focus(); fireEvent.keyDown(buttons[0],{key:'ArrowRight'}); expect(document.activeElement).toBe(buttons[1]);
    fireEvent.keyDown(buttons[1],{key:'ArrowDown'}); expect(document.activeElement).toBe(buttons[2]);
    fireEvent.keyDown(buttons[2],{key:'ArrowUp'}); expect(document.activeElement).toBe(buttons[1]);
    fireEvent.keyDown(buttons[1],{key:'Enter'}); fireEvent.keyDown(buttons[1],{key:'Enter',repeat:true}); expect(onDeliver.mock.calls).toEqual([['B','send',13]]);
    expect(commonNavigationIndex(rects,0,'ArrowUp')).toBe(0);
  });
  it('焦点切换后的旧repeat标记允许首个Enter，松键后可再次提交', () => {
    const action=bar(); const button=screen.getByRole('option');
    fireEvent.keyDown(button,{key:'Enter',repeat:true});fireEvent.keyDown(button,{key:'Enter',repeat:true});expect(action).toHaveBeenCalledTimes(1);
    fireEvent.keyUp(button,{key:'Enter'});fireEvent.keyDown(button,{key:'Enter'});expect(action).toHaveBeenCalledTimes(2);
    fireEvent.blur(window);fireEvent.keyDown(button,{key:'Enter',repeat:true});expect(action).toHaveBeenCalledTimes(3);
  });
  it.each([false, true])('点击栏外或焦点移出后取消短语选中，横条模式=%s', compact => {
    bar('select', compact); const phrase = screen.getByRole('option', { name: '谢谢' });
    fireEvent.click(phrase); expect(phrase.getAttribute('aria-selected')).toBe('true');
    fireEvent.click(document.body); expect(phrase.getAttribute('aria-selected')).toBe('false');
    fireEvent.click(phrase); fireEvent.click(screen.getByRole('region', { name: '常用短语' }));
    expect(phrase.getAttribute('aria-selected')).toBe('false');
    fireEvent.click(phrase); fireEvent.blur(phrase, { relatedTarget: document.body });
    expect(phrase.getAttribute('aria-selected')).toBe('false');
  });
  it('默认单击插入，双击不追加重复投递', async () => {
    const action = bar(); const phrase = screen.getByRole('option', { name: '谢谢' });
    await userEvent.click(phrase); expect(action.mock.calls).toEqual([['B', 'insert']]); action.mockClear();
    expect(phrase.getAttribute('aria-selected')).toBe('true');
    await userEvent.dblClick(phrase); expect(action.mock.calls).toEqual([['B', 'insert']]);
  });
  it.each(['select', 'insert', 'send'] as const)('两处面板旧配置为%s时，双击仍只插入一次', async mode => {
    for (const compact of [false, true]) {
      const action = bar(mode, compact); await userEvent.dblClick(screen.getByRole('option', { name: '谢谢' }));
      expect(action.mock.calls).toEqual([['B', 'insert']]); cleanup();
    }
  });
  it('键盘Enter直接发送，发送期间不接受重复双击', async () => {
    const action = bar('send'); screen.getByRole('option', { name: '谢谢' }).focus();
    await userEvent.keyboard('{Enter}'); expect(action.mock.calls).toEqual([['B', 'send',13]]); cleanup();
    const locked = bar('send', false, true); fireEvent.doubleClick(screen.getByRole('option', { name: '谢谢' }));
    expect(locked).not.toHaveBeenCalled();
  });
});

describe('底部短语整栏隐藏', () => {
  it.each([false, true])('隐藏整条短语栏并保存开关，底部搜索开启=%s时保持独立', async dockSearch => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    vi.mocked(call).mockResolvedValue(undefined);
    const library = { commonPhrases: [{ id: 'B', text: '谢谢', position: 0 }], settings: { commonSingleClick: 'select', dockCommon: true, dockSearch, showCommon: true, commonHeight: 144, dockCommonRows: 0 } } as Library;
    const view = render(<CommonDockWindow library={library} />);
    await userEvent.click(screen.getByRole('button', { name: '隐藏短语' }));
    expect(call).toHaveBeenCalledWith('patch_settings', { patch: { dockCommon: false } });
    view.rerender(<CommonDockWindow library={{ ...library, settings: { ...library.settings, dockCommon: false } }} />);
    expect(view.container.querySelector('.common-phrases')).toBeNull();
    expect(screen.queryByRole('button', { name: '常用短语菜单' })).toBeNull();
    expect(screen.queryByRole('button', { name: '新增常用短语' })).toBeNull();
    expect(Boolean(screen.queryByRole('textbox', { name: '底部搜索话术、文件' }))).toBe(dockSearch);
    view.rerender(<CommonDockWindow library={library} />);
    expect(screen.getByRole('option', { name: '谢谢' })).toBeTruthy();
  });
  it('保存失败时保留短语栏并允许重试，快速连点不重复保存', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    let rejectSave: (reason: Error) => void = () => {};
    vi.mocked(call).mockImplementation(() => new Promise((_, reject) => { rejectSave = reject; }));
    const library = { commonPhrases: [{ id: 'B', text: '谢谢', position: 0 }], settings: { commonSingleClick: 'select', dockCommon: true, dockSearch: false, commonHeight: 144, dockCommonRows: 0 } } as Library;
    render(<CommonDockWindow library={library} />);
    const hide = screen.getByRole('button', { name: '隐藏短语' });
    fireEvent.click(hide); fireEvent.click(hide);
    expect(vi.mocked(call).mock.calls.filter(([name]) => name === 'patch_settings')).toHaveLength(1);
    rejectSave(new Error('设置保存失败'));
    expect((await screen.findByRole('alert')).textContent).toContain('设置保存失败');
    expect(screen.getByRole('option', { name: '谢谢' })).toBeTruthy();
    expect((hide as HTMLButtonElement).disabled).toBe(false);
  });
});

it('底栏投递失败时显示占用程序，并保留完整悬停提示', async () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  const message = 'example.exe（PID 1234）在检测时可能占用剪贴板，请检查后重试。';
  vi.mocked(call).mockImplementation(async name => name === 'get_target' ? { hwnd: 1, focus: 2, pid: 3 } : name === 'deliver_content' ? { status: 'failed', completedSegments: 0, completedBlocks: 0, error: message } : undefined);
  const library = { commonPhrases: [{ id: 'B', text: '谢谢', position: 0 }], settings: { commonSingleClick: 'select', dockCommon: true, dockSearch: false, commonHeight: 144, dockCommonRows: 0 } } as Library;
  render(<CommonDockWindow library={library} />);
  await userEvent.click(screen.getByRole('option', { name: '谢谢' }));
  const status = await screen.findByRole('alert');
  expect(status.textContent).toContain(message);
  expect(status.getAttribute('title')).toBe(message);
  expect(screen.getByRole('option', { name: '谢谢' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '查看' }));
  expect(document.querySelector('.dock-notice-detail')?.textContent).toBe(message);
  expect(document.querySelector('.dock-notice-summary')?.textContent).toBe('提示详情');
});

it('底栏部分执行时，摘要优先提醒检查目标', async () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  vi.mocked(call).mockImplementation(async name => name === 'get_target' ? { hwnd: 1, focus: 2, pid: 3 } : name === 'deliver_content' ? { status: 'interrupted', completedSegments: 1, completedBlocks: 1, error: '已执行部分内容，请先检查目标。' } : undefined);
  const library = { commonPhrases: [{ id: 'B', text: '谢谢', position: 0 }], settings: { commonSingleClick: 'select', dockCommon: true, dockSearch: false, commonHeight: 144, dockCommonRows: 0 } } as Library;
  render(<CommonDockWindow library={library} />);
  await userEvent.click(screen.getByRole('option', { name: '谢谢' }));
  expect((await screen.findByRole('alert')).querySelector('.dock-notice-summary')?.textContent).toBe('请先检查目标，操作已中断');
  fireEvent.click(screen.getByRole('button', { name: '查看' }));
  expect(document.querySelector('.dock-notice-detail')?.textContent).toContain('已执行部分内容');
});
