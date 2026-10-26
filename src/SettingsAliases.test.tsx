import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { SettingsWindow } from './Settings';

vi.mock('@tauri-apps/api/window', () => ({ getCurrentWindow: () => ({ onCloseRequested: vi.fn(async () => () => {}) }) }));
import type { Library } from './types';
const callbacks=vi.hoisted(()=>new Map<string,(e:{payload:unknown})=>void>());
vi.mock('./api',()=>({call:vi.fn(async()=>({})),closeWindow:vi.fn()}));
vi.mock('./TeamSettings',()=>({TeamSettings:()=> <div>账号页入口</div>}));
vi.mock('@tauri-apps/api/event',()=>({listen:vi.fn(async(name,callback)=>{callbacks.set(name,callback);return()=>callbacks.delete(name);})}));
vi.mock('@tauri-apps/api/app',()=>({getVersion:vi.fn(async()=> '0.2.3')}));
const library:Library={categories:[],phrases:[],files:[],commonPhrases:[],dataDir:'',settings:{} as Library['settings']};
afterEach(()=>{cleanup();callbacks.clear();});
it.each(['账号','账号与同步','共享话术','账号与共享'])('初始设置入口 %s 进入账号页',async tab=>{
  await act(async()=>{render(<SettingsWindow library={library} initialTab={tab}/>);});
  expect(screen.getByText('账号页入口')).toBeTruthy();
  expect(screen.getByRole('button',{name:'账号'}).classList.contains('active')).toBe(true);
  expect(screen.queryByRole('button',{name:'账号与共享'})).toBeNull();
});
it('复用设置窗口时新旧导航名称都能进入账号页',async()=>{
  await act(async()=>{render(<SettingsWindow library={library} initialTab="常用设置"/>);});
  for(const tab of ['账号与同步','共享话术','账号与共享','账号']) {
    await act(async()=>callbacks.get('settings-navigation')?.({payload:{tab,categoryId:''}}));
    expect(screen.getByText('账号页入口')).toBeTruthy();
    expect(screen.getByRole('button',{name:'账号'}).classList.contains('active')).toBe(true);
  }
});
