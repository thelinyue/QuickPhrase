import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { SettingsWindow } from './Settings';
import type { Library } from './types';
const callbacks=vi.hoisted(()=>new Map<string,(e:{payload:unknown})=>void>());
vi.mock('./api',()=>({call:vi.fn(async()=>({})),closeWindow:vi.fn()}));
vi.mock('./TeamSettings',()=>({TeamSettings:()=> <div>账号页入口</div>}));
vi.mock('@tauri-apps/api/event',()=>({listen:vi.fn(async(name,callback)=>{callbacks.set(name,callback);return()=>callbacks.delete(name);})}));
vi.mock('@tauri-apps/api/app',()=>({getVersion:vi.fn(async()=> '0.2.3')}));
const library:Library={categories:[],phrases:[],files:[],commonPhrases:[],dataDir:'',settings:{} as Library['settings']};
afterEach(()=>{cleanup();callbacks.clear();});
it.each(['账号与同步','共享话术','账号与共享'])('初始设置入口 %s 兼容跳转账号与共享',async tab=>{
  await act(async()=>{render(<SettingsWindow library={library} initialTab={tab}/>);});
  expect(screen.getByText('账号页入口')).toBeTruthy();
  expect(screen.getByRole('button',{name:'账号与共享'}).classList.contains('active')).toBe(true);
});
it('复用设置窗口时两个旧导航名称也能进入账号页',async()=>{
  await act(async()=>{render(<SettingsWindow library={library} initialTab="常用设置"/>);});
  for(const tab of ['账号与同步','共享话术']) {
    await act(async()=>callbacks.get('settings-navigation')?.({payload:{tab,categoryId:''}}));
    expect(screen.getByText('账号页入口')).toBeTruthy();
  }
});
