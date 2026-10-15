import { useState } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PhraseRow } from './PhraseRow';
import type { Phrase, Settings } from './types';
vi.mock('./api', () => ({ call: vi.fn(async () => ''), closeWindow: vi.fn() }));
afterEach(cleanup);
const settings = { showNumbers: true, showPreview: true, showTitle: true } as Settings;
const phrase = (id: string): Phrase => ({ id, rootId: 'common', groupId: null, title: id, color: '#ff5100', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: `${id}正文` }] }] });
function Harness({ action }: { action: (id: string, mode: string) => void }) {
  const [selected, select] = useState('A'); const [busy, setBusy] = useState(false);
  return <>{['A', 'B'].map((id, i) => <PhraseRow key={id} phrase={phrase(id)} index={i} selected={selected === id} busy={busy} settings={settings} onSelect={() => select(id)} onContext={() => {}} onDeliver={mode => { if (!busy) { action(id, mode); setBusy(true); } }} />)}</>;
}
describe('话术行发送规则', () => {
  it('快捷键浮层点击和双击沿用话术行操作，状态与来源位于覆盖区域外', async () => {
    const props = { index: 0, selected: false, busy: false, settings, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: vi.fn() };
    render(<PhraseRow {...props} phrase={{ ...phrase('B'), shortcut: 'Ctrl+1+2', ready: false }} sourceLabel="共享话术" />);
    const badge = screen.getByTitle('Ctrl+1+2');
    expect(badge.closest('.row-copy')).toBeTruthy();
    expect(screen.getByText('未缓存').closest('.row-copy')).toBeNull();
    expect(screen.getByText('共享话术').closest('.row-copy')).toBeNull();
    await userEvent.click(badge); expect(props.onSelect).toHaveBeenCalledTimes(1); expect(props.onDeliver).not.toHaveBeenCalled();
    await userEvent.dblClick(badge); expect(props.onDeliver.mock.calls).toEqual([['insert']]);
  });
  it('图片优先于文件，移除附件后依次恢复文件和文字图标', () => {
    const props = { index: 0, selected: false, busy: false, settings, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: vi.fn() };
    const item = phrase('媒体');
    item.segments.push({ blocks: [{ kind: 'file', path: 'a.txt', name: 'a.txt' }, { kind: 'image', path: 'a.png', name: 'a.png' }] });
    const view = render(<PhraseRow {...props} phrase={item} />);
    expect(screen.getByTitle('含图片话术')).toBeTruthy();
    item.segments[1].blocks.pop(); view.rerender(<PhraseRow {...props} phrase={{ ...item }} />);
    expect(screen.getByTitle('含文件话术')).toBeTruthy();
    item.segments.pop(); view.rerender(<PhraseRow {...props} phrase={{ ...item }} />);
    expect(screen.getByTitle('文字话术')).toBeTruthy();
  });
  it.each(['', '#ff5100', '#FF5100'])('默认标题色 %s 由主题接管，不修改话术记录', color => {
    const item = { ...phrase('B'), color };
    render(<PhraseRow phrase={item} index={0} selected={false} busy={false} settings={{ ...settings, theme: 'default', titleColor: '#ff5100' }} onSelect={() => {}} onContext={() => {}} onDeliver={() => {}} />);
    expect((screen.getByText('B', { selector: '.row-title' }) as HTMLElement).style.color).toBe('');
    expect(item.color).toBe(color);
  });
  it('单条自定义色优先于全局色，恢复默认后跟随全局且文件标题由样式接管', () => {
    const item = { ...phrase('B'), color: '#ca9663', background: '#fff2bc' };
    const props = { index: 0, selected: false, busy: false, settings: { ...settings, theme: 'default' as const, titleColor: '#0000ff' }, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: vi.fn() };
    const view = render(<PhraseRow {...props} phrase={item} query="正文" />);
    const title = () => screen.getByText('B', { selector: '.row-title' }) as HTMLElement;
    expect(title().style.color).toBe('rgb(202, 150, 99)');
    expect(title().style.background).toBe('rgb(255, 242, 188)');
    view.rerender(<PhraseRow {...props} phrase={{ ...item, color: '#ff5100' }} />);
    expect(title().style.color).toBe('rgb(0, 0, 255)');
    view.rerender(<PhraseRow {...props} file={{ id: 'F', categoryId: 'files', name: '附件.txt', path: 'C:/附件.txt', position: 0, missing: false }} />);
    expect((screen.getByText('附件.txt', { selector: '.row-title' }) as HTMLElement).style.color).toBe('');
    expect(item.color).toBe('#ca9663');
  });
  it('用户开启双击发送后列表和搜索提交send，文件双击与Enter仍只插入', async () => {
    const action = vi.fn(); const props = { index: 0, selected: false, busy: false, settings: { ...settings, phraseDoubleClick: 'send' as const }, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: action };
    const view = render(<PhraseRow {...props} phrase={phrase('B')} />);
    await userEvent.dblClick(screen.getByText('B正文')); expect(action.mock.calls).toEqual([['send']]); action.mockClear();
    view.rerender(<PhraseRow {...props} phrase={phrase('B')} query="B" />);
    fireEvent.doubleClick(screen.getByRole('option')); expect(action.mock.calls).toEqual([['send']]); action.mockClear();
    screen.getByRole('option').focus(); await userEvent.keyboard('{Enter}'); expect(action.mock.calls).toEqual([['insert',13]]); action.mockClear();
    view.rerender(<PhraseRow {...props} file={{ id: 'F', categoryId: 'files', name: '附件.txt', path: 'C:/附件.txt', position: 0, missing: false }} />);
    fireEvent.doubleClick(screen.getByRole('option')); expect(action.mock.calls).toEqual([['insert']]);
  });
  it('列表快捷键标签使用参考图的逗号格式，保留完整组合提示', () => {
    const props = { index: 0, selected: false, busy: false, settings, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: vi.fn() };
    const view = render(<PhraseRow {...props} phrase={{ ...phrase('B'), shortcut: 'Ctrl+1+2' }} />);
    expect(screen.getByText('Ctrl,1,2').getAttribute('title')).toBe('Ctrl+1+2');
    view.rerender(<PhraseRow {...props} phrase={{ ...phrase('B'), shortcut: 'Y+C' }} query="B" />);
    expect(screen.getByText('Y,C')).toBeTruthy();
  });
  it('选中A点击B的发送按钮时发送B且不改变选择', async () => { const action = vi.fn(); render(<Harness action={action} />); await userEvent.click(screen.getByRole('button', { name: '直接发送 B' })); expect(action.mock.calls).toEqual([['B', 'send']]); expect(screen.getAllByRole('option')[0].getAttribute('aria-selected')).toBe('true'); });
  it('正文双击只插入一次', async () => { const action = vi.fn(); render(<Harness action={action} />); await userEvent.dblClick(screen.getByText('B正文')); expect(action.mock.calls).toEqual([['B', 'insert']]); });
  it('快速双击发送按钮不会插入或重复发送', async () => { const action = vi.fn(); render(<Harness action={action} />); await userEvent.dblClick(screen.getByRole('button', { name: '直接发送 B' })); expect(action.mock.calls).toEqual([['B', 'send']]); });
  it('首个发送已完成后，双击的第二次点击仍不会重发', () => {
    const action = vi.fn(); const props = { phrase: phrase('B'), index: 0, selected: false, settings, onSelect: vi.fn(), onContext: vi.fn(), onDeliver: action };
    const view = render(<PhraseRow {...props} busy={false} />);
    fireEvent.click(screen.getByRole('button', { name: '直接发送 B' }), { detail: 1 });
    view.rerender(<PhraseRow {...props} busy={true} />);
    view.rerender(<PhraseRow {...props} busy={false} />);
    fireEvent.click(screen.getByRole('button', { name: '直接发送 B' }), { detail: 2 });
    expect(action.mock.calls).toEqual([['send']]);
  });
  it('无标题、无文字的图片话术仍有可识别的列表名称', () => {
    const media = { ...phrase('image'), title: '', segments: [{ blocks: [{ kind: 'image' as const, path: 'C:/images/sample.png', name: '截图.png' }] }] };
    render(<PhraseRow phrase={media} index={0} selected={false} busy={false} settings={{ ...settings, showPreview: false }} onSelect={() => {}} onContext={() => {}} onDeliver={() => {}} />);
    expect(screen.getByText('图片：截图.png')).toBeTruthy();
    expect(screen.getByRole('button', { name: '直接发送 图片：截图.png' })).toBeTruthy();
    expect(screen.getByTitle('含图片话术')).toBeTruthy();
  });
  it('按钮键盘Enter只发送，行内容Enter只插入', async () => { const action = vi.fn(); const first = render(<Harness action={action} />); screen.getByRole('button', { name: '直接发送 B' }).focus(); await userEvent.keyboard('{Enter}'); expect(action.mock.calls).toEqual([['B', 'send']]); first.unmount(); action.mockClear(); render(<Harness action={action} />); screen.getAllByRole('option')[1].focus(); await userEvent.keyboard('{Enter}'); expect(action.mock.calls).toEqual([['B', 'insert']]); });
  it('只显示内容时隐藏标题，仍保留该行的发送身份', () => {
    render(<PhraseRow phrase={phrase('B')} index={0} selected={false} busy={false} settings={{ ...settings, showTitle: false }} onSelect={() => {}} onContext={() => {}} onDeliver={() => {}} />);
    expect(screen.queryByText('B', { selector: '.row-title' })).toBeNull();
    expect(screen.getByText('B正文')).toBeTruthy();
    expect(screen.getByRole('button', { name: '直接发送 B' })).toBeTruthy();
  });
  it('只显示标题时，无标题文字话术使用正文摘要保持可识别', () => {
    render(<PhraseRow phrase={{ ...phrase('B'), title: '' }} index={0} selected={false} busy={false} settings={{ ...settings, showPreview: false }} onSelect={() => {}} onContext={() => {}} onDeliver={() => {}} />);
    expect(screen.getByText('B正文', { selector: '.row-title' })).toBeTruthy();
    expect(document.querySelector('.row-preview')).toBeNull();
    expect(screen.getByTitle('文字话术')).toBeTruthy();
  });
  it('文件多选的修饰键双击不会插入，普通双击仍只插入一次', () => {
    const action = vi.fn();
    render(<PhraseRow file={{ id: 'F', categoryId: 'files', name: '附件.txt', path: 'C:/附件.txt', position: 0, missing: false }} index={0} selected={false} busy={false} settings={settings} onSelect={() => {}} onContext={() => {}} onDeliver={action} />);
    const row = screen.getByRole('option');
    fireEvent.doubleClick(row, { ctrlKey: true });
    fireEvent.doubleClick(row, { shiftKey: true });
    fireEvent.doubleClick(row, { metaKey: true });
    expect(action).not.toHaveBeenCalled();
    fireEvent.doubleClick(row);
    expect(action).toHaveBeenCalledExactlyOnceWith('insert');
  });
});
