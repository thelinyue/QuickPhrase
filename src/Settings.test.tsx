import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SettingsWindow } from './Settings';
import { call } from './api';
import { getVersion } from '@tauri-apps/api/app';
import { open } from '@tauri-apps/plugin-dialog';
import type { Library } from './types';

vi.mock('./api', () => ({ call: vi.fn(async () => 0), closeWindow: vi.fn(async () => {}) }));
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn(async () => () => {}) }));
vi.mock('@tauri-apps/api/app', () => ({ getVersion: vi.fn(async () => '0.1.1') }));
vi.mock('@tauri-apps/plugin-dialog', () => ({ open: vi.fn(async () => null) }));
beforeEach(() => { vi.mocked(call).mockResolvedValue(0); vi.mocked(getVersion).mockResolvedValue('0.1.1'); HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.open = true; }); });
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const library: Library = { categories: [], phrases: ['P1', 'P2'].map(id => ({ id, rootId: 'root', groupId: null, title: '话术', color: '', background: '', shortcut: '', position: 0, segments: [{ blocks: [{ kind: 'text', text: '正文' }] }] })), commonPhrases: [{ id: 'C1', text: '您好', position: 0 }], files: [], dataDir: '', settings: {} as Library['settings'] };
const deletions = () => vi.mocked(call).mock.calls.filter(([command]) => command === 'delete_local_content');
const submit = () => fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);

describe('青羽默认主题', () => {
  const settings = { ...library.settings, theme: 'default' as const, fontSize: 14, titleColor: '#ff5100', phraseCategoryColor: '', fileCategoryColor: '' };
  const title = () => screen.getByRole('button', { name: '设置 话术标题 字体颜色' }).querySelector('span') as HTMLElement;
  it('默认色板展示中性标题，切换主题不改写颜色设置', async () => {
    render(<SettingsWindow library={{ ...library, settings }} initialTab="界面设置" />);
    const theme = screen.getByRole('combobox', { name: '主题风格' }) as HTMLSelectElement;
    expect(theme.options.length).toBe(7);
    expect(theme.selectedOptions[0].textContent).toBe('青羽（默认）');
    expect(title().style.color).toBe('rgb(38, 49, 59)');
    fireEvent.change(theme, { target: { value: 'green' } });
    await waitFor(() => expect(call).toHaveBeenCalled());
    expect(title().style.color).toBe('rgb(38, 49, 59)');
    expect(call).toHaveBeenCalledWith('save_settings', { settings: { ...settings, theme: 'green' }, libraryId: undefined });
    expect(settings.titleColor).toBe('#ff5100');
  });
  it('自定义标题色在切换主题后保留，恢复默认继续保存历史标记', async () => {
    const custom = { ...settings, titleColor: '#0000ff', phraseCategoryColor: '#ff9800' };
    render(<SettingsWindow library={{ ...library, settings: custom }} initialTab="界面设置" />);
    expect(title().style.color).toBe('rgb(0, 0, 255)');
    fireEvent.change(screen.getByRole('combobox', { name: '主题风格' }), { target: { value: 'blue' } });
    await waitFor(() => expect(call).toHaveBeenCalledWith('save_settings', { settings: { ...custom, theme: 'blue' }, libraryId: undefined }));
    expect(title().style.color).toBe('rgb(0, 0, 255)');
    fireEvent.click(screen.getByRole('button', { name: '设置 话术标题 字体颜色' }));
    fireEvent.click(screen.getByRole('button', { name: '恢复默认颜色' }));
    await waitFor(() => expect(title().style.color).toBe('rgb(38, 49, 59)'));
    expect(call).toHaveBeenCalledWith('save_settings', { settings: { ...custom, theme: 'blue', titleColor: '#ff5100' }, libraryId: undefined });
  });
});

describe('更换本地数据目录', () => {
  beforeEach(() => {
    vi.mocked(open).mockResolvedValue(null);
    vi.mocked(call).mockImplementation(async command => command === 'get_data_directory' ? { path: 'D:\\闪语数据', message: '' } : 0);
  });
  async function about() {
    render(<SettingsWindow library={{ ...library, dataDir: 'D:\\闪语数据\\private\\account' }} initialTab="关于" />);
    await screen.findByText('D:\\闪语数据');
  }
  it('显示数据根目录并打开根文件夹，选择取消不提交迁移', async () => {
    await about();
    expect(screen.queryByText('D:\\闪语数据\\private\\account')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '打开数据文件夹' }));
    expect(call).toHaveBeenCalledWith('open_directory', { kind: 'data' });
    fireEvent.click(screen.getByRole('button', { name: '更换目录' }));
    await waitFor(() => expect(open).toHaveBeenCalledWith({ directory: true, multiple: false, title: '选择空数据文件夹', defaultPath: 'D:\\闪语数据' }));
    await waitFor(() => expect((screen.getByRole('button', { name: '更换目录' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(call).not.toHaveBeenCalledWith('change_data_directory', expect.anything());
  });
  it('确认前展示新旧目录和清理说明，取消保留原目录', async () => {
    vi.mocked(open).mockResolvedValue('E:\\新目录');
    await about();
    fireEvent.click(screen.getByRole('button', { name: '更换目录' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('D:\\闪语数据');
    expect(dialog.textContent).toContain('E:\\新目录');
    expect(dialog.textContent).toContain('清理原数据');
    expect(dialog.textContent).toContain('保留外部源文件');
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    expect(call).not.toHaveBeenCalledWith('change_data_directory', expect.anything());
    expect(screen.getByText('D:\\闪语数据')).toBeTruthy();
  });
  it('选择和迁移提交防止重复点击，成功后提示重启', async () => {
    let selected!: (value: string) => void; let complete!: (value: number) => void;
    vi.mocked(open).mockImplementation(async () => new Promise<string>(resolve => { selected = resolve; }));
    vi.mocked(call).mockImplementation(async command => command === 'get_data_directory' ? { path: 'D:\\闪语数据', message: '' } : command === 'change_data_directory' ? new Promise<number>(resolve => { complete = resolve; }) : 0);
    await about();
    const choose = screen.getByRole('button', { name: '更换目录' });
    fireEvent.click(choose); fireEvent.click(choose);
    expect(open).toHaveBeenCalledTimes(1);
    selected('E:\\新目录'); await screen.findByRole('dialog');
    submit(); submit();
    expect(vi.mocked(call).mock.calls.filter(([command]) => command === 'change_data_directory')).toEqual([['change_data_directory', { path: 'E:\\新目录' }]]);
    complete(0); await screen.findByText('正在重启并迁移…');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('后端拒绝时确认框保留错误，不显示成功或新路径', async () => {
    vi.mocked(open).mockResolvedValue('E:\\新目录');
    vi.mocked(call).mockImplementation(async command => {
      if (command === 'change_data_directory') throw new Error('请选择空文件夹');
      return command === 'get_data_directory' ? { path: 'D:\\闪语数据', message: '' } : 0;
    });
    await about();
    fireEvent.click(screen.getByRole('button', { name: '更换目录' }));
    await screen.findByRole('dialog'); submit();
    expect((await screen.findByRole('alert')).textContent).toContain('请选择空文件夹');
    expect(screen.queryByText('正在重启并迁移…')).toBeNull();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });
  it('显示启动阶段的迁移或清理结果，目录查询失败时禁止更换', async () => {
    vi.mocked(call).mockResolvedValue({ path: 'E:\\新目录', message: '原目录仍有应用数据未清理' });
    const view = render(<SettingsWindow library={library} initialTab="关于" />);
    expect((await screen.findByRole('status')).textContent).toContain('未清理');
    expect(screen.getByText('E:\\新目录')).toBeTruthy();
    view.unmount();
    vi.mocked(call).mockImplementation(async command => { if (command === 'get_data_directory') throw new Error('定位配置无法读取'); return 0; });
    render(<SettingsWindow library={library} initialTab="关于" />);
    await screen.findByText('Error: 定位配置无法读取');
    expect((screen.getByRole('button', { name: '更换目录' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

it('类目点击不保存，选择另一项才交换，两组排序独立', async () => {
  render(<SettingsWindow library={{ ...library, settings: { ...library.settings, filesFirst: false, fontSize: 12 } }} initialTab="界面设置" />);
  const first = screen.getByRole('combobox', { name: '类目第1位' }) as HTMLSelectElement;
  const second = screen.getByRole('combobox', { name: '类目第2位' }) as HTMLSelectElement;
  const privateFirst = screen.getByRole('combobox', { name: '话术库第1位' }) as HTMLSelectElement;
  fireEvent.click(first); fireEvent.change(first, { target: { value: '话术' } });
  expect(call).not.toHaveBeenCalledWith('save_settings', expect.anything());
  expect(Array.from(first.options).map(o => o.textContent)).toEqual(['话术', '本地文件']);
  fireEvent.change(first, { target: { value: '本地文件' } });
  await waitFor(() => expect([first.value, second.value]).toEqual(['本地文件', '话术']));
  expect(call).toHaveBeenCalledWith('save_settings', expect.objectContaining({ settings: expect.objectContaining({ filesFirst: true }) }));
  fireEvent.change(privateFirst, { target: { value: '共享话术' } });
  await waitFor(() => expect(privateFirst.value).toBe('共享话术'));
  expect([first.value, second.value]).toEqual(['本地文件', '话术']);
  expect(call).toHaveBeenCalledWith('save_settings', expect.objectContaining({ settings: expect.objectContaining({ filesFirst: true, teamsFirst: true }) }));
  fireEvent.change(second, { target: { value: '本地文件' } });
  await waitFor(() => expect([first.value, second.value]).toEqual(['话术', '本地文件']));
  expect(privateFirst.value).toBe('共享话术');
});

it('排序保存失败保留原顺序和焦点', async () => {
  render(<SettingsWindow library={{ ...library, settings: { ...library.settings, filesFirst: false, fontSize: 12 } }} initialTab="界面设置" />);
  const first = screen.getByRole('combobox', { name: '类目第1位' }) as HTMLSelectElement;
  first.focus();
  vi.mocked(call).mockRejectedValueOnce(new Error('保存失败'));
  fireEvent.change(first, { target: { value: '本地文件' } });
  await screen.findByText(/保存失败/);
  expect(first.value).toBe('话术');
  expect(document.activeElement).toBe(first);
});

describe('应用版本', () => {
  it('关于页面与侧栏显示实际 RC 版本', async () => {
    vi.mocked(getVersion).mockResolvedValue('0.1.1-rc.2');
    render(<SettingsWindow library={library} initialTab="关于" />);
    expect(screen.getByRole('heading', { name: '闪语 QuickPhrase' })).toBeTruthy();
    expect(await screen.findByText('Windows 本地话术工具 · v0.1.1-rc.2')).toBeTruthy();
    expect(screen.getByText('本地话术工具 · 0.1.1-rc.2')).toBeTruthy();
  });
  it('读取失败不显示硬编码版本', async () => {
    vi.mocked(getVersion).mockRejectedValue(new Error('版本服务不可用'));
    render(<SettingsWindow library={library} initialTab="关于" />);
    expect((await screen.findByRole('status')).textContent).toContain('无法读取应用版本');
    expect(screen.queryByText(/v0\.1\.1| · 0\.1\.1/)).toBeNull();
  });
});

describe('十项序号与快捷键设置', () => {
  const shortcuts = () => ({ ...library, settings: { ...library.settings, searchHotkey: 'Q', windowHotkey: 'W', commonHotkey: 'E', sequenceHotkeys: false, sequenceDirectSend: false, sequenceBindings: Array.from({ length: 10 }, (_, i) => `Ctrl+${(i+1)%10}`), customHotkeys: true, globalPhraseHotkeys: false } });
  it('默认关闭，十项单组合键录入、单项关闭和重新录入恢复', async () => {
    render(<SettingsWindow library={shortcuts()} initialTab="快捷键" />);
    expect((screen.getByRole('checkbox', { name: '开启序号快捷键' }) as HTMLInputElement).checked).toBe(false);
    for (let i=0;i<10;i++) expect((screen.getByLabelText(`发送序号${i+1}`) as HTMLInputElement).value).toBe(`Ctrl+${(i+1)%10}`);
    fireEvent.click(screen.getByRole('button',{name:'关闭序号10快捷键'}));
    await waitFor(()=>expect((screen.getByLabelText('发送序号10') as HTMLInputElement).value).toBe(''));
    fireEvent.keyDown(screen.getByLabelText('发送序号10'),{key:'F10',code:'F10',ctrlKey:true,shiftKey:true});
    await waitFor(()=>expect((screen.getByLabelText('发送序号10') as HTMLInputElement).value).toBe('Ctrl+Shift+F10'));
    fireEvent.click(screen.getByRole('checkbox',{name:'直接发送'}));
    await waitFor(()=>expect((screen.getByRole('checkbox',{name:'直接发送'}) as HTMLInputElement).checked).toBe(true));
  });
  it('冲突保留旧绑定与开关，Alt导航保存失败恢复输入值', async () => {
    vi.mocked(call).mockRejectedValue(new Error('与已有快捷键冲突，请更换。'));
    render(<SettingsWindow library={shortcuts()} initialTab="快捷键" />);
    fireEvent.keyDown(screen.getByLabelText('发送序号1'),{key:'2',code:'Digit2',ctrlKey:true});
    await screen.findByRole('status'); await waitFor(()=>expect((screen.getByLabelText('发送序号1') as HTMLInputElement).value).toBe('Ctrl+1'));
    fireEvent.click(screen.getByRole('checkbox',{name:'开启序号快捷键'})); await waitFor(()=>expect((screen.getByRole('checkbox',{name:'开启序号快捷键'}) as HTMLInputElement).disabled).toBe(false));
    expect((screen.getByRole('checkbox',{name:'开启序号快捷键'}) as HTMLInputElement).checked).toBe(false);
    const alt=screen.getByLabelText('定位到搜索快捷键'); fireEvent.change(alt,{target:{value:'W'}});fireEvent.blur(alt);
    await waitFor(()=>expect((alt as HTMLInputElement).value).toBe('Q'));
  });
  it('拒绝裸键，生效范围保留全局开关且不提供视频入口', async () => {
    render(<SettingsWindow library={shortcuts()} initialTab="快捷键" />);
    fireEvent.keyDown(screen.getByLabelText('发送序号1'),{key:'a',code:'KeyA'});
    expect(screen.getByRole('status').textContent).toContain('单个组合键');
    expect(vi.mocked(call).mock.calls.filter(([name])=>name==='save_settings')).toHaveLength(0);
    expect(screen.queryByRole('button',{name:'使用教程'})).toBeNull();
    expect(screen.getByText('生效范围')).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox',{name:'全局生效'}));
    await waitFor(()=>expect((screen.getByRole('checkbox',{name:'全局生效'}) as HTMLInputElement).checked).toBe(true));
    expect(call).toHaveBeenCalledWith('save_settings', { settings: expect.objectContaining({ globalPhraseHotkeys: true }) });
  });
});

describe('本地缓存管理', () => {
  it('缓存操作只影响个人库；个人话术默认回收，取消不删除', async () => {
    render(<SettingsWindow library={library} initialTab="缓存管理" />);
    expect(screen.queryByText(/云端|公司话术/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '个人话术' }));
    expect(screen.getByText(/全部 2 条个人话术/)).toBeTruthy();
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    expect(deletions()).toHaveLength(0); expect(screen.queryByRole('dialog')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '个人话术' })); submit();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(deletions()).toEqual([['delete_local_content', { kind: 'phrase', ids: ['P1', 'P2'], recycle: true }]]);
  });
  it('明确取消回收选项才能永久删除话术', async () => {
    render(<SettingsWindow library={library} initialTab="缓存管理" />);
    fireEvent.click(screen.getByRole('button', { name: '个人话术' }));
    fireEvent.click(screen.getByRole('checkbox')); submit();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(deletions()).toEqual([['delete_local_content', { kind: 'phrase', ids: ['P1', 'P2'], recycle: false }]]);
  });
  it('常用短语告知不可恢复，重复提交与执行中的Escape只提交一次', async () => {
    let complete!: (value: number) => void;
    vi.mocked(call).mockImplementation(async command => command === 'delete_local_content' ? await new Promise<number>(resolve => { complete = resolve; }) : 0);
    render(<SettingsWindow library={library} initialTab="缓存管理" />);
    fireEvent.click(screen.getByRole('button', { name: '常用短语' }));
    expect(screen.getByText(/不进入回收站，删除后无法恢复/)).toBeTruthy();
    submit(); submit();
    const dialog = screen.getByRole('dialog'); const cancel = new Event('cancel', { cancelable: true }); fireEvent(dialog, cancel);
    expect(cancel.defaultPrevented).toBe(true); expect(deletions()).toHaveLength(1);
    expect((screen.getByRole('button', { name: '取消' }) as HTMLButtonElement).disabled).toBe(true);
    complete(1); await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(deletions()[0][1]).toEqual({ kind: 'common', ids: ['C1'], recycle: false });
  });
  it('其他窗口改变列表时保留确认范围，错误留在窗口内，不报告成功', async () => {
    vi.mocked(call).mockImplementation(async command => { if (command === 'delete_local_content') throw new Error('内容列表已变化，请关闭确认窗口后重新操作。'); return 0; });
    const { rerender } = render(<SettingsWindow library={library} initialTab="缓存管理" />);
    fireEvent.click(screen.getByRole('button', { name: '个人话术' }));
    rerender(<SettingsWindow library={{ ...library, phrases: [...library.phrases, { ...library.phrases[0], id: 'P3' }] }} initialTab="缓存管理" />); submit();
    await screen.findByRole('alert'); expect(screen.queryByRole('status')).toBeNull();
    expect(deletions()[0][1]?.ids).toEqual(['P1', 'P2']);
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    fireEvent.click(screen.getByRole('button', { name: '个人话术' }));
    expect(screen.getByText(/全部 3 条个人话术/)).toBeTruthy();
  });
  it('空库禁用内容删除，缓存清理只调用对应缓存类型并更新统计', async () => {
    vi.mocked(call).mockImplementation(async command => command === 'cache_info' ? 1048576 : 0);
    render(<SettingsWindow library={{ ...library, phrases: [], commonPhrases: [] }} initialTab="缓存管理" />);
    for (const name of ['个人话术', '常用短语']) expect((screen.getByRole('button', { name }) as HTMLButtonElement).disabled).toBe(true);
    await screen.findByText('可重新生成的缓存：1.00 MB');
    for (const [name, kind] of [['清理缩略图', 'thumbnails'], ['清理临时文件', 'temp']]) {
      fireEvent.click(screen.getByRole('button', { name }));
      await waitFor(() => expect(call).toHaveBeenCalledWith('clear_cache', { kind }));
    }
    expect(deletions()).toHaveLength(0);
  });
});
