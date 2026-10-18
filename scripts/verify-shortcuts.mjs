import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

// 完整原生链路只在隔离 Debug 数据目录和标准 WinForms 宿主验收；不操作真实聊天软件。
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = path.join(repo, '.build', `shortcuts-${Date.now()}`);
const evidence = root;
const run = promisify(execFile), delay = ms => new Promise(done => setTimeout(done, ms));
const powershell = args => run('powershell.exe', ['-NoProfile','-STA','-ExecutionPolicy','Bypass',...args], { windowsHide:true,encoding:'utf8' });
const native = async args => JSON.parse((await powershell(['-File',path.join(repo,'tests','native-driver.ps1'),...args])).stdout.trim());
const events = path.join(root,'events.json'), controls = path.join(root,'control.json');
const results = [], receipts = [];
const filter = process.argv.find(arg=>arg.startsWith('--filter='))?.slice(9);
const keyboard = process.argv.includes('--keyboard');
let host, otherHost, app, browser, page, candidates, settingsPage, dockPage, context, hostInfo, main, candidateHandle;
let expectedClipboard = '快捷键验收剪贴板';
const read = async filename => { for(let i=0;i<150;i++) {try{return JSON.parse(await fs.readFile(filename,'utf8'));}catch{await delay(30);}} throw Error(`无法读取宿主记录：${filename}`); };
const receipt = () => read(events);
const control = async (command, fields={}, filename=controls) => {
  const temporary=`${filename}.tmp`;await fs.writeFile(temporary,JSON.stringify({command,...fields,nonce:Date.now()}));
  for(let i=0;;i++){try{await fs.rename(temporary,filename);break;}catch(error){if(i>50||!['EBUSY','EPERM','EACCES'].includes(error.code))throw error;await delay(10);}}
  await delay(120);
};
const poll = async (check,label) => {for(let i=0;i<100;i++){if(await check())return;await delay(35);}throw Error(`等待超时：${label}`);};
// 原生锁等待时 evaluate 不会自动超时；保留明确命令名，不能等到整套桌面验收被强制终止。
const invoke = async (cmd,args={},window=page) => {
  let timer;
  try {return await Promise.race([window.evaluate(({cmd,args})=>window.__TAURI_INTERNALS__.invoke(cmd,args),{cmd,args}),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error(`原生请求超时：${cmd}`)),10000);})]);}
  finally {clearTimeout(timer);}
};
const key = async (code, modifiers=[], extra=[]) => {const result=await native(['-Action','key','-Key',String(code),...modifiers,...extra]);assert.equal(result.desktop,result.inputDesktop,'测试按键应进入隔离输入桌面');return result;};
const chord = () => native(['-Action','key','-Keys','81,87']);
const focusHost = async () => { for(let i=0;i<3;i++){await native(['-Action','focus','-Handle',String(hostInfo.hwnd)]);await control('focus');await delay(300);if((await native(['-Handle',String(hostInfo.hwnd)])).foreground===hostInfo.hwnd)return;}throw Error('隔离宿主未取得稳定输入焦点。'); };
// 点击活动页签取得前台权限，避免固定坐标误打开共享话术菜单或命中拖动区域。
const focusMain = async () => {
  const tab=await page.locator('.content-tabs button.active').boundingBox();
  await native(['-Action','focus','-Handle',String(main),'-X',String(Math.round(tab.x+tab.width/2)),'-Y',String(Math.round(tab.y+tab.height/2))]);
};
const prepare = async (draft='已有草稿：') => {
  expectedClipboard = '快捷键验收剪贴板';
  await control('reset',{draft});await control('copy',{text:expectedClipboard});await focusHost();
  await invoke('window_action',{action:'show'});await focusMain();await delay(100);
  await poll(async()=>(await invoke('get_target'))?.focus===hostInfo.focus,'冻结原宿主目标');await invoke('window_action',{action:'hide'});await delay(120);await focusHost();
};
const opened = async (trigger=chord) => {await trigger();await poll(async()=>!!await invoke('get_shortcut_candidates'),'原生候选');return invoke('get_shortcut_candidates');};
const noCandidate = () => poll(async()=>!await invoke('get_shortcut_candidates'),'候选关闭');
const submitted = async (draft, sends=0) => {
  await poll(async()=>{const r=await receipt();return r.draft===draft&&r.events.filter(e=>e.kind==='send').length===sends;},'宿主内容');
  await poll(async()=>await page.locator('.stop-delivery').count()===0,'发送完成回执');
  const paste=(await receipt()).events.filter(e=>e.kind==='paste-text').at(-1);
  if(paste)expectedClipboard=paste.value;
  const clipboard=await native(['-ClipboardExpected',expectedClipboard]);
  if(!clipboard.clipboardMatches)receipts.push({name:'剪贴板检查失败',expectedLength:expectedClipboard.length,clipboard});
  assert.equal(clipboard.clipboardMatches,true,'保留最后投递内容；没有投递时不改剪贴板');
};
const record = async name => {receipts.push({name,...await receipt()});};
const test = async (name,fn) => {if(filter&&!name.includes(filter))return;try{await fn();results.push({name,passed:true});console.log(`通过：${name}`);}catch(error){results.push({name,passed:false,error:String(error)});throw error;}};
const settings = async patch => {const library=await invoke('get_library');await invoke('save_settings',{settings:{...library.settings,...patch}});await delay(160);};
const showMain = async () => {await invoke('window_action',{action:'show'});await delay(120);if(main)await focusMain();};
const captureScales = async (window, name, width, height) => {
  const session=await context.newCDPSession(window);
  try {for(const scale of [1,1.5,2]) {
    await session.send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:scale,mobile:false});await delay(80);
    assert.equal(await window.evaluate(()=>devicePixelRatio),scale);
    const overflow=await window.evaluate(()=>document.documentElement.scrollWidth>innerWidth);assert.equal(overflow,false,`${name}不应横向溢出`);
    // 截图区域使用 CSS 像素；deviceScaleFactor 已提供像素密度，clip 不重复放大。
    const capture=await session.send('Page.captureScreenshot',{format:'png',clip:{x:0,y:0,width,height,scale:1}});
    const png=Buffer.from(capture.data,'base64');assert.equal(png.readUInt32BE(16),Math.round(width*scale));assert.equal(png.readUInt32BE(20),Math.round(height*scale));
    await fs.writeFile(path.join(evidence,`${name}-${scale*100}.png`),png);
  }} finally {await session.send('Emulation.clearDeviceMetricsOverride');await session.detach();}
};
try {
  await fs.mkdir(root,{recursive:true});await fs.mkdir(evidence,{recursive:true});
  const hostExe=path.join(root,'ShanyuDockHost.exe');await powershell(['-File',path.join(repo,'tests','build-dock-host.ps1'),'-OutputPath',hostExe]);
  host=spawn(hostExe,[events,controls,'WeWorkWindow',keyboard?'keyboard':''],{windowsHide:true,stdio:['ignore','ignore','pipe']});host.stderr.on('data',b=>console.error(b.toString()));hostInfo=await receipt();
  // 连续切换隔离输入桌面时，旧WebView进程可能尚未释放公共配置目录；每次独立配置和调试端口。
  const debuggerSocket=net.createServer();await new Promise(resolve=>debuggerSocket.listen(0,'127.0.0.1',resolve));
  const debuggerPort=debuggerSocket.address().port;await new Promise(resolve=>debuggerSocket.close(resolve));
  app=spawn(path.join(repo,'src-tauri','target','debug','QuickPhrase.exe'),['--autostart'],{cwd:repo,windowsHide:true,stdio:'ignore',env:{...process.env,SHANYU_TEST_DATA_DIR:path.join(root,'data'),WEBVIEW2_USER_DATA_FOLDER:path.join(root,'webview'),WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${debuggerPort}`}});
  for(let i=0;i<80;i++){try{browser=await chromium.connectOverCDP(`http://127.0.0.1:${debuggerPort}`);break;}catch{await delay(200);}}
  if(!browser)throw Error('无法连接隔离客户端。');context=browser.contexts()[0];context.setDefaultTimeout(6000);page=context.pages().find(p=>!new URL(p.url()).hash);
  await page.locator('.main-window').waitFor();assert.equal((await invoke('get_library')).dataDir,path.join(root,'data'));
  const clientVersion=await invoke('plugin:app|version');assert.equal(clientVersion,JSON.parse(await fs.readFile(path.join(repo,'package.json'),'utf8')).version);
  await poll(async()=>{try{await settings({dock:false,searchHotkey:'8',windowHotkey:'9',commonHotkey:'0',sequenceHotkeys:false,globalPhraseHotkeys:true,showCommon:true});return true;}catch(error){if(String(error).includes('服务尚未启动'))return false;throw error;}},'Windows监听服务');await showMain();
  main=(await native(['-ProcessId',String(app.pid),'-Title','闪语'])).handle;
  candidates=context.pages().find(p=>p.url().includes('#shortcut-candidates'));await candidates.locator('.shortcut-candidates-window').waitFor();
  dockPage=context.pages().find(p=>p.url().includes('#common-dock'));
  for(const window of [page,dockPage])await window.evaluate(()=>{window.__shortcutQaKeys=[];for(const name of ['keydown','keyup'])document.addEventListener(name,e=>{window.__shortcutQaKeys.push({type:name,key:e.key,code:e.code,repeat:e.repeat,composing:e.isComposing,prevented:e.defaultPrevented,target:e.target.dataset.common||e.target.className});if(window.__shortcutQaKeys.length>50)window.__shortcutQaKeys.shift();});});
  candidateHandle=(await native(['-ProcessId',String(app.pid),'-Title','快捷键候选 · 闪语'])).handle;
  // 隔离验收显式准备分类，不依赖产品自动初始化。
  const phraseRoot = await invoke('save_category', { category: { id: '', name: '常用', kind: 'phrase', parentId: null, position: 0, background: '' } });
  const fileRoot = await invoke('save_category', { category: { id: '', name: '默认', kind: 'file', parentId: null, position: 0, background: '' } });
  const group=await invoke('save_category',{category:{id:'',name:'二级分组',kind:'phrase',parentId:phraseRoot,position:0,background:''}});
  const otherRoot=await invoke('save_category',{category:{id:'',name:'另一个分类',kind:'phrase',parentId:null,position:0,background:''}});
  const savePhrase=async (title,shortcut='Q+W',rootId=phraseRoot,groupId=null) => invoke('save_phrase',{phrase:{id:'',title,rootId,groupId,shortcut,color:'',background:'',position:0,segments:[{blocks:[{kind:'text',text:`正文${title}`}]}]},afterId:null});
  const ids=[];
  for(let i=0;i<12;i++)ids.push(await savePhrase(String(i+1),'Q+W',i<4?phraseRoot:otherRoot,i===2||i===3?group:null));
  const solo=await savePhrase('单候选','Ctrl+Shift+F11');await delay(250);
  const library=await invoke('get_library');
  const references=new Map([...ids,solo].map(id=>[id,`private:${library.libraryId}:phrase:${id}:${library.privateRevisions[`phrase:${id}`]}`]));
  const targetContext={hwnd:hostInfo.hwnd,focus:hostInfo.focus,pid:hostInfo.pid};

  if(keyboard) await test('企业微信键盘撤回保留草稿且按实际次数清理，中断不重试',async()=>{
    await settings({customHotkeys:true,sequenceHotkeys:false,globalPhraseHotkeys:true});
    await prepare('原草稿：后文');await control('select',{start:4});await opened();await key(32);await submitted('原草稿：正文1后文');
    assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-backspace').length,2);
    assert.equal((await receipt()).events.filter(e=>e.kind==='paste-text').length,1);
    const repeated=await savePhrase('数字长按','1+1');
    try {
      await prepare();await opened(()=>key(0,[],['-Keys','49,49','-Repeats','5']));await key(49);await submitted('已有草稿：正文数字长按');
      assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-backspace').length,10);
      await prepare();await opened();await key(27);await noCandidate();await submitted('已有草稿：qw');
      assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-backspace').length,0);
      await prepare();await opened();await control('select',{start:0});
      await invoke('submit_shortcut_candidate',{index:0,mode:'insert'}).then(r=>assert.equal(r.status,'failed'),e=>assert.match(String(e),/候选已关闭/));
      assert.equal((await receipt()).draft,'已有草稿：qw');assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-backspace').length,0);
      await prepare();await opened();await control('copy-on-backspace',{text:'退格期间新复制'});
      const report=await invoke('submit_shortcut_candidate',{index:0,mode:'insert'});assert.equal(report.status,'interrupted');assert.equal(report.completedBlocks,0);assert.match(report.error,/退格已提交.*不会自动重试或回填/);
      await delay(250);const result=await receipt();assert.equal(result.draft,'已有草稿：');assert.equal(result.events.filter(e=>e.kind==='trigger-backspace').length,2);assert.equal(result.events.filter(e=>e.kind==='paste-text').length,0);
      assert.equal((await native(['-ClipboardExpected','退格期间新复制'])).clipboardMatches,true);
      await record('自绘输入区键盘替换、实际重复次数、取消、光标失效与退格后新复制');
    } finally {await invoke('delete_item',{id:repeated,kind:'phrase',recycle:false});}
  });

  await test('自动吸附目标同步后非全局快捷键无需点击闪语或重开吸附',async()=>{
    const otherEvents=path.join(root,'target-other-events.json'),otherControls=path.join(root,'target-other-controls.json');
    const neutral=spawn('powershell.exe',['-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',path.join(repo,'tests','native-host.ps1'),'-EventsPath',otherEvents,'-ControlPath',otherControls],{windowsHide:true,stdio:'ignore'});
    const chats=[];
    const focus=async(info,controls)=>{for(let i=0;i<3;i++){await native(['-Action','focus','-Handle',String(info.hwnd),'-X','550','-Y','400']);await control('focus',{},controls);if((await native(['-Handle',String(info.hwnd)])).foreground===info.hwnd)return;}throw Error('宿主未取得前台输入焦点。');};
    const visibleCandidate=async(info)=>{
      const payload=await opened();assert.deepEqual(payload.target,{hwnd:info.hwnd,focus:info.focus,pid:info.pid});
      await poll(async()=>(await native(['-Handle',String(candidateHandle)])).visible,'非全局候选实际可见');
      assert.equal((await native(['-Handle',String(candidateHandle)])).foreground,info.hwnd,'候选不抢输入焦点');
      return payload;
    };
    try {
      const other=await read(otherEvents);
      for(const dock of [true,false]) {
        await settings({dock,customHotkeys:true,sequenceHotkeys:false,globalPhraseHotkeys:false});
        await focus(other,otherControls);await showMain();
        await poll(async()=>(await invoke('get_target'))?.hwnd===other.hwnd,'准备残留的其他窗口目标');
        // 从这里开始不激活闪语、不调用 show、不改吸附，直接进入已识别的聊天窗口。
        await focusHost();await control('reset',{draft:'自动目标：'});
        if(dock)await poll(async()=>{const panel=await native(['-Handle',String(main)]),target=await native(['-Handle',String(hostInfo.hwnd)]);return panel.visible&&Math.abs(panel.rect.top-target.rect.top)<16&&Math.abs(panel.rect.bottom-target.rect.bottom)<16;},'侧栏已经自动吸附');
        await visibleCandidate(hostInfo);await key(27);await noCandidate();
        assert.equal((await receipt()).draft,'自动目标：qw');
        receipts.push({name:`自动目标同步 dock=${dock}`,target:await invoke('get_target')});
      }
      // 同时存活的主窗／独立聊天窗互切，随后关闭并重新创建独立窗口。
      for(const [index,windowClass] of ['WwStandaloneConversationWnd','CRMCustomerStandaloneWindow','WwStandaloneConversationWnd'].entries()) {
        const output=path.join(root,`target-chat-${index}.json`),controls=path.join(root,`target-chat-${index}-control.json`);
        const process=spawn(hostExe,[output,controls,windowClass],{windowsHide:true,stdio:'ignore'});chats.push({process,controls});
        const info=await read(output);await focus(info,controls);await visibleCandidate(info);
        await focusHost();await noCandidate();await control('reset',{draft:'自动目标：'});await visibleCandidate(hostInfo);
        await focus(info,controls);await noCandidate();await visibleCandidate(info);
        // 确认输入控件变化也取消旧会话，重新按键才打开新会话。
        await control('blur',{},controls);await noCandidate();await focus(info,controls);await visibleCandidate(info);await key(27);await noCandidate();
        await control('reset',{draft:'未完成：'},controls);await key(81);await focusHost();await delay(1200);await noCandidate();
        assert.equal((await read(output)).draft,'未完成：q');assert.equal((await receipt()).draft,'自动目标：qw');
        await focus(info,controls);await visibleCandidate(info);await control('close',{},controls);
        await poll(async()=>process.exitCode!==null,'独立窗口关闭');await noCandidate();await focusHost();await visibleCandidate(hostInfo);await key(27);await noCandidate();
        receipts.push({name:`独立窗口切换及关闭重开 ${index}`,windowClass,hwnd:info.hwnd});
      }
      await focus(other,otherControls);await control('reset',{draft:'范围：'},otherControls);await chord();await delay(200);await noCandidate();
      assert.equal((await read(otherEvents)).draft,'范围：qw','非全局模式不放行无关窗口');
      await settings({globalPhraseHotkeys:true});await visibleCandidate(other);await key(27);await noCandidate();
    } finally {
      for(const chat of chats){if(chat.process.exitCode===null){await control('close',{},chat.controls).catch(()=>{});chat.process.kill();}}
      await control('close',{},otherControls).catch(()=>{});if(neutral.exitCode===null)neutral.kill();
      await settings({dock:false,globalPhraseHotkeys:true});
    }
  });

  await test('设置页四个开关实际点击保存并控制原生触发和作用范围',async()=>{
    const labels={sequenceHotkeys:'开启序号快捷键',sequenceDirectSend:'直接发送',customHotkeys:'开启自定义快捷键',globalPhraseHotkeys:'全局生效'};
    const edit=async patch=>{
      const previous=(await invoke('get_library')).settings;
      await showMain();await page.getByRole('button',{name:'设置',exact:true}).click();
      await poll(async()=>context.pages().some(p=>p.url().includes('#settings')),'打开设置');
      settingsPage=context.pages().find(p=>p.url().includes('#settings'));await settingsPage.getByRole('button',{name:'快捷键',exact:true}).click();
      for(const [field,name] of Object.entries(labels))assert.equal(await settingsPage.getByRole('checkbox',{name,exact:true}).isChecked(),previous[field],`${name}重开后与已保存值一致`);
      for(const [field,value] of Object.entries(patch)) {
        const box=settingsPage.getByRole('checkbox',{name:labels[field],exact:true});
        await box.setChecked(value);
        await poll(async()=>!await box.isDisabled()&&(await invoke('get_library')).settings[field]===value,`${labels[field]}保存`);
      }
      // 设置窗口打开期间，两个业务快捷键都应暂停。
      await focusHost();await control('reset',{draft:'设置中：'});await chord();await key(49,['-Ctrl']);await delay(180);
      assert.equal((await receipt()).draft,'设置中：qw');await noCandidate();
      await settingsPage.getByRole('button',{name:'关闭窗口',exact:true}).click();
      await poll(async()=>!context.pages().includes(settingsPage),'设置关闭');
    };
    await edit({sequenceHotkeys:false,sequenceDirectSend:false,customHotkeys:false,globalPhraseHotkeys:false});
    await prepare();await chord();await key(49,['-Ctrl']);await delay(180);await noCandidate();await submitted('已有草稿：qw');await record('两个开启开关均关闭时不触发');
    await edit({sequenceHotkeys:true});await prepare();await key(49,['-Ctrl']);await submitted('已有草稿：正文1');await record('界面开启序号后仅插入');
    await edit({sequenceDirectSend:true});await prepare();await key(49,['-Ctrl']);await submitted('',1);assert.equal((await receipt()).events.find(e=>e.kind==='send').value.text,'已有草稿：正文1');await record('界面开启直接发送后提交一次');
    await edit({sequenceDirectSend:false,sequenceHotkeys:false,customHotkeys:true});await prepare();await key(49,['-Ctrl']);await submitted('已有草稿：');await opened();await key(27);await noCandidate();await record('自定义开启独立于序号开关');
    await edit({sequenceHotkeys:true});await prepare();await opened();await key(27);await noCandidate();await key(49,['-Ctrl']);await submitted('已有草稿：qw正文1');
    const otherEvents=path.join(root,'four-switch-events.json'),otherControls=path.join(root,'four-switch-controls.json');
    otherHost=spawn('powershell.exe',['-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',path.join(repo,'tests','native-host.ps1'),'-EventsPath',otherEvents,'-ControlPath',otherControls],{windowsHide:true,stdio:'ignore'});
    const other=await read(otherEvents);
    const focusOther=async()=>{await native(['-Action','focus','-Handle',String(other.hwnd)]);await control('focus',{},otherControls);await delay(180);};
    await focusOther();await control('reset',{draft:'范围关闭：'},otherControls);await chord();await key(49,['-Ctrl']);await delay(180);await noCandidate();assert.equal((await read(otherEvents)).draft,'范围关闭：qw');
    await edit({globalPhraseHotkeys:true});await focusOther();await control('reset',{draft:'全局：'},otherControls);await opened();await key(27);await noCandidate();await key(49,['-Ctrl']);
    await poll(async()=>(await read(otherEvents)).draft==='全局：qw正文1','全局序号插入其他窗口');receipts.push({name:'界面全局开启后两种快捷键均可用',...await read(otherEvents)});
    await edit({globalPhraseHotkeys:false});await prepare();await focusOther();await control('reset',{draft:'再次关闭：'},otherControls);await chord();await key(49,['-Ctrl']);await delay(180);await noCandidate();assert.equal((await read(otherEvents)).draft,'再次关闭：qw');
    // 重开读取持久化结果，不能只检查当前复选框的乐观状态。
    await edit({sequenceHotkeys:false,sequenceDirectSend:false,customHotkeys:false,globalPhraseHotkeys:false});
    const saved=(await invoke('get_library')).settings;for(const field of Object.keys(labels))assert.equal(saved[field],false);
    await control('close',{},otherControls);otherHost=null;
    await settings({customHotkeys:true,sequenceHotkeys:false,globalPhraseHotkeys:true});
  });

  await test('快捷键开关快速修改后立即关闭不会丢保存',async()=>{
    const fields=['sequenceHotkeys','sequenceDirectSend','customHotkeys','globalPhraseHotkeys'];
    await settings(Object.fromEntries(fields.map(field=>[field,false])));
    await showMain();await page.getByRole('button',{name:'设置',exact:true}).click();
    await poll(async()=>context.pages().some(p=>p.url().includes('#settings')),'设置窗口');
    settingsPage=context.pages().find(p=>p.url().includes('#settings'));await settingsPage.getByRole('button',{name:'快捷键',exact:true}).click();
    // 仅延迟第一项回执，真实SQLite保存仍由Rust执行，用于稳定复现关闭时排队项被丢弃。
    await settingsPage.evaluate(()=>{const original=window.fetch.bind(window);let first=true;window.fetch=async(input,init)=>{const url=new URL(typeof input==='string'?input:input.url);if(url.hostname==='ipc.localhost'&&url.pathname==='/patch_settings'&&first){first=false;window.__delayedSettings=true;await new Promise(resolve=>setTimeout(resolve,700));}return original(input,init);};});
    for(const name of ['开启序号快捷键','直接发送','开启自定义快捷键','全局生效'])await settingsPage.getByRole('checkbox',{name,exact:true}).check();
    await settingsPage.waitForFunction(()=>window.__delayedSettings);
    await settingsPage.getByRole('button',{name:'关闭窗口',exact:true}).click();
    await poll(async()=>!context.pages().includes(settingsPage),'保存后关闭设置');await delay(900);
    const saved=(await invoke('get_library')).settings;for(const field of fields)assert.equal(saved[field],true,`${field}不能因关闭窗口而丢失`);
    await settings({customHotkeys:true,sequenceHotkeys:false,sequenceDirectSend:false,globalPhraseHotkeys:true});
  });

  await test('双键完整匹配只显示候选，按分类排序、无激活且不占任务栏',async()=>{
    await prepare();const payload=await opened();assert.deepEqual(payload.phrases.map(p=>p.id),ids.map(id=>references.get(id)));assert.deepEqual(payload.target,targetContext);
    await poll(async()=>(await native(['-Handle',String(candidateHandle)])).visible,'候选窗口显示');
    const actual=await native(['-Handle',String(candidateHandle)]);assert.equal(actual.foreground,hostInfo.hwnd);assert.equal(actual.visible,true);assert.equal(actual.toolWindow,true);assert.equal(actual.appWindow,false);assert.equal(actual.noActivate,true);
    assert.ok(actual.rect.left>=actual.workArea.left&&actual.rect.top>=actual.workArea.top&&actual.rect.right<=actual.workArea.right&&actual.rect.bottom<=actual.workArea.bottom,'候选完整限制在屏幕工作区内');
    // 读取实际原生矩形与WebView视口，防止前端行高更新后原生窗口仍沿用旧高度。
    for (const count of [12, 1]) {
      if(count===1)await opened(()=>key(122,['-Ctrl','-Shift']));
      const geometry=await native(['-Handle',String(candidateHandle)]), scale=await invoke('plugin:window|scale_factor',{},candidates);
      const viewport=await candidates.evaluate(()=>({width:innerWidth,height:innerHeight,row:document.querySelector('[data-candidate]').getBoundingClientRect().height,scroll:document.querySelector('.shortcut-candidates-list').scrollWidth,client:document.querySelector('.shortcut-candidates-list').clientWidth}));
      assert.ok(viewport.width>=240&&viewport.width<=360);assert.ok(viewport.height<=420&&viewport.row>=32);assert.equal(viewport.scroll,viewport.client);
      assert.ok(geometry.rect.right-geometry.rect.left<=Math.round(360*scale));assert.ok(geometry.rect.bottom-geometry.rect.top<=Math.round(420*scale));
      if(count===1) {
        const headerFits=await candidates.evaluate(()=>{
          const header=document.querySelector('header'),label=header.querySelector('strong'),close=header.querySelector('button'),text=label.textContent;
          try {label.textContent='Ctrl+Alt+Shift+Win+Y+C';const h=header.getBoundingClientRect(),b=close.getBoundingClientRect();return b.right<=h.right&&label.scrollWidth>label.clientWidth;}
          finally {label.textContent=text;}
        });
        assert.equal(headerFits,true,'长快捷键省略且取消按钮保持可见');
        assert.equal(await candidates.evaluate(()=>[...document.querySelectorAll('footer > span')].every(hint=>hint.getBoundingClientRect().height<=21)&&document.querySelectorAll('footer > span').length===4),true,'快捷键提示按完整操作分组换行');
      }

      receipts.push({name:'实际候选窗口尺寸',count,scale,rect:geometry.rect,...viewport});
      const active=await invoke('get_shortcut_candidates');
      await invoke('resize_shortcut_candidates',{sessionId:'expired',layoutKey:active.layoutKey,width:1,height:1});
      await invoke('resize_shortcut_candidates',{sessionId:active.sessionId,layoutKey:'expired',width:1,height:1});
      assert.deepEqual((await native(['-Handle',String(candidateHandle)])).rect,geometry.rect,'迟到测量不能改变当前候选尺寸');

      assert.equal((await receipt()).draft,'已有草稿：qw');await candidates.screenshot({path:path.join(evidence,count===12?'candidates-native.png':'candidate-single-native.png')});
      if(count===12)await captureScales(candidates,'candidates',viewport.width,viewport.height);
      await key(27);await noCandidate();await record('Esc取消保留草稿');
    }
  });
  await test('数字1与0、空格插入；Enter直接发送；超过十项可选择且选择不限时',async()=>{
    for(const [selection,expected] of [[49,'正文1'],[48,'正文10']]){await prepare();await opened();await key(selection);await submitted(`已有草稿：${expected}`);await record(`数字${selection-48}插入`);}
    await prepare();await opened();await key(40);await key(32);await submitted('已有草稿：正文2');await record('空格插入当前项');
    await prepare();await opened();await key(13);await submitted('',1);assert.equal((await receipt()).events.find(e=>e.kind==='send').value.text,'已有草稿：正文1');await record('Enter直接发送');
    await prepare();await opened();for(let i=0;i<12;i++)await key(40);assert.equal((await invoke('get_shortcut_candidates')).selectedIndex,11);
    await delay(1300);assert.equal((await invoke('get_shortcut_candidates')).selectedIndex,11);await key(32);await submitted('已有草稿：正文12');await record('第十二候选不限时插入');
  });
  await test('单条也弹候选；长按触发与重复提交只执行一次',async()=>{
    await prepare();const payload=await opened(()=>key(122,['-Ctrl','-Shift'],['-Repeats','5']));assert.equal(payload.phrases.length,1);assert.equal(payload.phrases[0].id,references.get(solo));
    await key(13,[],['-Repeats','8']);await submitted('',1);await delay(250);assert.equal((await receipt()).events.filter(e=>e.kind==='paste-text').length,1);await record('长按Enter只发送一次');
  });
  await test('同键双击、不同键重叠与Ctrl+Y+C连续触发',async()=>{
    const repeated=await savePhrase('同键双击','1+1');
    const modified=await savePhrase('带修饰序列','Ctrl+Y+C');
    try {
      for(const [keys,mods,extra,title] of [['49,49',[],[],'同键双击'],['81,87',[],['-Overlap'],'1'],['89,67',['-Ctrl'],['-Overlap'],'带修饰序列']]) {
        for(let attempt=0;attempt<2;attempt++) {
          await prepare();
          const payload=await opened(()=>key(0,mods,['-Keys',keys,...extra]));
          assert.equal(payload.phrases[0].title,title);
          await key(27);await noCandidate();await submitted(`已有草稿：${mods.length?'':keys==='49,49'?'11':'qw'}`);
        }
      }
    } finally { for(const id of [repeated,modified])await invoke('delete_item',{id,kind:'phrase',recycle:false}); }
  });
  await test('普通输入即时透传，继续打字关闭候选，数字长按只替换本次触发文字',async()=>{
    await prepare();await key(0,[],['-Keys','81,87,69']);await noCandidate();await submitted('已有草稿：qwe');await record('继续输入正常透传');
    const repeated=await savePhrase('数字长按','1+1');
    const overlap=await savePhrase('数字重叠','1+2');
    try {
      for(const [keys,extra,draft] of [['49,49',[],'1111111111'],['49,50',['-Overlap'],'1111122222']]) {
        await prepare();await opened(()=>key(0,[],['-Keys',keys,'-Repeats','5',...extra]));
        await submitted(`已有草稿：${draft}`);assert.equal((await receipt()).events.filter(e=>e.kind==='paste-text'||e.kind==='send').length,0);
        await key(49,[],['-Repeats','5']);await submitted(`已有草稿：正文${keys==='49,49'?'数字长按':'数字重叠'}`);
        assert.equal((await receipt()).events.filter(e=>e.kind==='paste-text').length,1);await record('数字触发松键后才能选择且长按不重复投递');
      }
    } finally {for(const id of [repeated,overlap])await invoke('delete_item',{id,kind:'phrase',recycle:false});}
  });
  await test('范围替换保留前后草稿，失效素材和光标变化不清理，清理后失败不重试',async()=>{
    await prepare('原草稿：后文');await control('select',{start:'原草稿：'.length});
    const payload=await opened();assert.equal('replacement' in payload,false,'原生输入上下文不得通过 IPC 暴露');
    await key(49);await submitted('原草稿：正文1后文');
    assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-clear').length,1);await record('范围替换保留原草稿及光标后文字');

    const shifted=await savePhrase('大写触发','Shift+Q+W');
    const missing=await invoke('save_phrase',{phrase:{id:'',title:'首块失效文件',rootId:phraseRoot,groupId:null,shortcut:'P+P',color:'',background:'',position:0,segments:[{blocks:[{kind:'file',path:path.join(root,'不存在的触发素材.txt'),name:'触发素材.txt'}]}]}});
    try {
      await prepare();await opened(()=>key(0,['-Shift'],['-Keys','81,87']));await key(32);await submitted('已有草稿：正文大写触发');await record('Shift大写触发范围替换');
      await prepare();await opened(()=>key(0,[],['-Keys','80,80']));
      await assert.rejects(invoke('submit_shortcut_candidate',{index:0,mode:'insert'}),/素材未缓存/);
      await submitted('已有草稿：pp');assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-clear').length,0);await record('首块素材失败不清理');
    } finally {for(const id of [shifted,missing])await invoke('delete_item',{id,kind:'phrase',recycle:false});}

    await prepare();await opened();await control('select',{start:0});
    await invoke('submit_shortcut_candidate',{index:0,mode:'insert'}).then(r=>assert.equal(r.status,'failed'),e=>assert.match(String(e),/候选已关闭/));
    assert.equal((await receipt()).draft,'已有草稿：qw');
    assert.equal((await receipt()).events.filter(e=>e.kind==='trigger-clear'||e.kind==='paste-text').length,0);await record('程序移动光标后拒绝清理');

    await prepare();await opened();await control('ignore-clear');
    const unconfirmed=await invoke('submit_shortcut_candidate',{index:0,mode:'insert'});
    assert.equal(unconfirmed.status,'interrupted');assert.match(unconfirmed.error,/无法确认快捷键清理结果/);assert.ok(!unconfirmed.error.includes('本次快捷键已清理'));
    assert.equal((await receipt()).draft,'已有草稿：qw');assert.equal((await receipt()).events.filter(e=>e.kind==='paste-text'||e.kind==='send').length,0);await record('清理命令未生效时不宣称已清理且不粘贴');

    await prepare();await opened();await control('copy-on-clear',{text:'清理后新复制'});
    const interrupted=await invoke('submit_shortcut_candidate',{index:0,mode:'insert'});
    assert.equal(interrupted.status,'interrupted');assert.equal(interrupted.completedBlocks,0);assert.match(interrupted.error,/本次快捷键已清理.*不会自动重试或回填/);
    await delay(250);const after=await receipt();assert.equal(after.draft,'已有草稿：');
    assert.equal(after.events.filter(e=>e.kind==='trigger-clear').length,1);assert.equal(after.events.filter(e=>e.kind==='paste-text'||e.kind==='send').length,0);
    assert.equal((await native(['-ClipboardExpected','清理后新复制'])).clipboardMatches,true);await record('清理后新复制中断并保留剪贴板');
  });
  await test('普通首键即时输入且超时不回放，焦点和外部点击取消候选',async()=>{
    await prepare('超时：');const timing=await key(81);await submitted('超时：q');const latency=Date.parse((await receipt()).lastTextChangedAt)-Date.parse(timing.keyStartedAt);assert.ok(latency>=0&&latency<800,`首键应立即输入，实际延迟 ${latency} ms`);receipts.push({name:'首键即时输入',latency});await delay(1200);await submitted('超时：q');await noCandidate();await record('超时不重复回放');
    await prepare();await opened();await control('blur');await noCandidate();await record('输入控件变化取消');
    assert.equal((await receipt()).draft,'已有草稿：qw');
    await prepare();await opened();await native(['-Action','focus','-Handle',String(hostInfo.hwnd)]);await noCandidate();await record('点击候选外部取消');
    await prepare();await opened();await showMain();await noCandidate();assert.equal((await receipt()).draft,'已有草稿：qw');await record('窗口切换取消');
  });
  await test('提交前核对库和修订，正文更新、改绑或删除后拒绝旧候选并保留草稿',async()=>{
    await prepare();await opened(()=>key(122,['-Ctrl','-Shift']));
    const original=(await invoke('get_library')).phrases.find(p=>p.id===solo);
    await invoke('save_phrase',{phrase:{...original,segments:[{blocks:[{kind:'text',text:'更新后的另一版正文'}]}]},afterId:null});
    await key(13);await noCandidate();await submitted('已有草稿：');await record('正文更新后旧候选拒绝发送');
    await invoke('save_phrase',{phrase:original,afterId:null});
    await prepare();await opened(()=>key(122,['-Ctrl','-Shift']));
    const phrase=(await invoke('get_library')).phrases.find(p=>p.id===solo);
    await invoke('save_phrase',{phrase:{...phrase,shortcut:'Ctrl+Shift+F12'},afterId:null});await key(13);await noCandidate();await submitted('已有草稿：');await record('候选改绑后拒绝发送');
    await invoke('save_phrase',{phrase,afterId:null});
    const removed=await savePhrase('待删除候选','Ctrl+Shift+F10',otherRoot);
    await prepare();await opened(()=>key(121,['-Ctrl','-Shift']));await invoke('delete_item',{id:removed,kind:'phrase'});await key(13);await noCandidate();await submitted('已有草稿：');await record('候选删除后拒绝发送');
  });
  await test('序号按实际绘制列表、折叠连续编号、搜索文件占位、隐藏后沿用最后列表',async()=>{
    await settings({sequenceHotkeys:true,sequenceDirectSend:false,exitSearch:false});await prepare();await key(49,['-Ctrl']);await submitted('已有草稿：正文1');await record('Ctrl+1插入当前第一项');
    await showMain();await page.getByRole('button',{name:'二级分组',exact:true}).click();await poll(async()=>await page.locator('.content-list [data-row]').count()===3,'折叠后的列表');
    assert.deepEqual(await page.locator('.content-list .row-index').allTextContents(),['1.','2.','3.']);
    await prepare();await key(51,['-Ctrl']);await submitted('已有草稿：正文单候选');await record('折叠后第三项');
    await invoke('window_action',{action:'hide'});await control('reset',{draft:'隐藏：'});await focusHost();await key(50,['-Ctrl']);await submitted('隐藏：正文2');await record('隐藏沿用最后列表');
    await prepare('搜索输入：');await showMain();await page.getByRole('textbox',{name:'搜索话术、文件',exact:true}).fill('单候选');await page.locator('#search-results [data-row]').waitFor();
    const searchBox=await page.getByRole('textbox',{name:'搜索话术、文件',exact:true}).boundingBox();await native(['-Action','focus','-Handle',String(main),'-X',String(Math.round(searchBox.x+12)),'-Y',String(Math.round(searchBox.y+12))]);
    assert.equal(await page.getByRole('textbox',{name:'搜索话术、文件',exact:true}).evaluate(e=>e===document.activeElement),true);
    await key(49,['-Ctrl']);await submitted('搜索输入：正文单候选');await record('搜索框保持焦点时序号插入原目标');
    const attachment=path.join(root,'序号搜索文件.txt');await fs.writeFile(attachment,'隔离验收');await invoke('add_files',{categoryId:fileRoot,paths:[attachment]});
    // 文件搜索属于本地文件来源，先切页签再验证文件槽位不会触发话术。
    await showMain();await page.getByRole('button',{name:'文件',exact:true}).click();await page.getByRole('textbox',{name:'搜索话术、文件',exact:true}).fill('序号搜索文件');await page.locator('#search-results [data-row]').waitFor();
    await control('reset',{draft:'文件占位：'});await focusHost();await key(49,['-Ctrl']);await delay(200);await submitted('文件占位：');await record('搜索文件槽位不发送话术');
    await invoke('sync_sequence_map',{ids:[ids[0]],loading:true});await key(49,['-Ctrl']);await delay(100);await submitted('文件占位：');
    await showMain();await page.getByRole('textbox',{name:'搜索话术、文件',exact:true}).fill('');await page.getByRole('button',{name:'话术',exact:true}).click();await settings({sequenceDirectSend:true});await focusHost();await key(49,['-Ctrl']);await submitted('',1);await record('序号直接发送');
  });
  await test('序号与自定义开关独立，冲突失败保留配置，设置关闭才恢复业务快捷键',async()=>{
    await settings({sequenceHotkeys:false});await prepare();await opened();await key(27);await noCandidate();
    await settings({sequenceHotkeys:true,customHotkeys:false,sequenceDirectSend:false});await prepare();await key(49,['-Ctrl']);await submitted('已有草稿：正文1');
    const old=(await invoke('get_library')).settings;const invalid=[...old.sequenceBindings];invalid[0]='Ctrl+Shift+F11';
    await assert.rejects(invoke('save_settings',{settings:{...old,sequenceBindings:invalid}}),/冲突/);assert.deepEqual((await invoke('get_library')).settings,old);
    await showMain();await page.getByRole('button',{name:'设置',exact:true}).click();await poll(async()=>context.pages().some(p=>p.url().includes('#settings')),'设置窗口');settingsPage=context.pages().find(p=>p.url().includes('#settings'));await settingsPage.locator('.settings-window').waitFor();
    await control('reset',{draft:'设置开启：'});await focusHost();await key(49,['-Ctrl']);await delay(180);await submitted('设置开启：');
    await settingsPage.getByRole('button',{name:'快捷键',exact:true}).click();await settingsPage.screenshot({path:path.join(evidence,'settings-native.png')});await captureScales(settingsPage,'settings',594,620);
    await settingsPage.getByRole('button',{name:'关闭窗口',exact:true}).click();await poll(async()=>!context.pages().includes(settingsPage),'设置关闭');await focusHost();await key(49,['-Ctrl']);await submitted('设置开启：正文1');await record('设置关闭后序号生效');
    await settings({customHotkeys:true,sequenceHotkeys:false});
  });
  await test('Alt搜索／侧栏与常用短语导航经过真实监听，短语Enter直接发送',async()=>{
    await prepare();await key(56,['-Alt']);await poll(async()=>page.getByRole('textbox',{name:'搜索话术、文件'}).evaluate(e=>e===document.activeElement),'Alt搜索聚焦');
    await key(27);await poll(async()=>page.locator('.content-list').evaluate(e=>e===document.activeElement),'Esc退出搜索');
    await key(57,['-Alt']);await poll(async()=>!(await native(['-Handle',String(main)])).visible,'Alt隐藏侧栏');
    await key(57,['-Alt']);await poll(async()=>page.locator('.content-list').evaluate(e=>e===document.activeElement),'Alt显示侧栏聚焦列表');
    await key(39);assert.equal(await page.locator('.category-tabs .active').textContent(),'另一个分类');await key(37);assert.equal(await page.locator('.category-tabs .active').textContent(),'常用');
    await control('reset',{draft:'列表：'});await key(40);await key(40);await key(13);await submitted('列表：正文2');await record('列表上下选择Enter插入');
    const commonId=await invoke('save_common_phrase',{phrase:{id:'',text:'谢谢',position:0}});
    const nextCommon=await invoke('save_common_phrase',{phrase:{id:'',text:'收到',position:0}});await delay(150);
    await focusHost();await key(48,['-Alt']);await poll(async()=>dockPage.locator(`[data-common="${commonId}"]`).evaluate(e=>e===document.activeElement),'Alt底部短语聚焦');
    await key(39);assert.equal(await dockPage.locator(`[data-common="${nextCommon}"]`).evaluate(e=>e===document.activeElement),true);await key(37);
    await control('reset',{draft:'底部短语：'});await key(13,[],['-Repeats','5']);await submitted('',1);assert.equal((await receipt()).events.find(e=>e.kind==='send').value.text,'底部短语：谢谢');await record('底部短语Enter直接发送');
    await control('reset',{draft:'主栏短语：'});await showMain();await key(48,['-Alt']);await poll(async()=>page.locator(`[data-common="${commonId}"]`).evaluate(e=>e===document.activeElement),'Alt主窗口短语聚焦');
    await key(39);assert.equal(await page.locator(`[data-common="${nextCommon}"]`).evaluate(e=>e===document.activeElement),true);await key(37);
    await key(13,[],['-Repeats','5']);await submitted('',1);assert.equal((await receipt()).events.find(e=>e.kind==='send').value.text,'主栏短语：谢谢');await record('主窗口短语Enter直接发送');
  });
  await test('点击常用短语后立即回车只发送一次且不误报中断',async()=>{
    await settings({dock:false,dockCommon:true,dockSearch:false});
    const commonId=await invoke('save_common_phrase',{phrase:{id:'',text:'好的',position:0}});
    for(const [label,window] of [['底栏',dockPage],['主窗口',page]]) {
      await prepare(`${label}草稿：`);
      if(window===page)await showMain();
      else {await key(48,['-Alt']);await poll(async()=>window.locator('[data-common]').first().evaluate(e=>e===document.activeElement),'底栏短语聚焦');}
      await control('enter-on-paste');
      await window.locator(`[data-common="${commonId}"]`).click();
      await submitted('',1);await delay(200);
      const r=await receipt();
      assert.equal(r.events.filter(e=>e.kind==='paste-text').length,1,'短语只插入一次');
      assert.equal(r.events.find(e=>e.kind==='send').value.text,`${label}草稿：好的`);
      assert.equal(await window.getByRole('status').filter({hasText:'操作已停止'}).count(),0,'插入完成后的回车不应误报中断');
      await record(`${label}单击后立即回车`);
    }
  });
  await test('共同全局范围开关限制其他窗口，不向新目标回放未完成序列',async()=>{
    const otherEvents=path.join(root,'other-events.json'),otherControls=path.join(root,'other-controls.json');
    otherHost=spawn('powershell.exe',['-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',path.join(repo,'tests','native-host.ps1'),'-EventsPath',otherEvents,'-ControlPath',otherControls],{windowsHide:true,stdio:'ignore'});const other=await read(otherEvents);
    await prepare();await settings({globalPhraseHotkeys:false,sequenceHotkeys:true,sequenceDirectSend:false});await native(['-Action','focus','-Handle',String(other.hwnd)]);await control('reset',{draft:'范围：'},otherControls);await control('focus',{},otherControls);await chord();await key(49,['-Ctrl']);await delay(180);assert.equal((await read(otherEvents)).draft,'范围：qw');await noCandidate();
    await settings({globalPhraseHotkeys:true});await opened();await key(27);await noCandidate();
    await key(49,['-Ctrl']);await poll(async()=>(await read(otherEvents)).draft==='范围：qwqw正文1','全局序号在其他窗口生效');receipts.push({name:'全局序号在其他窗口插入',...await read(otherEvents)});
    await focusHost();await control('reset',{draft:'未完成：'});await key(81);await native(['-Action','focus','-Handle',String(other.hwnd)]);await control('focus',{},otherControls);await delay(1200);assert.equal((await receipt()).draft,'未完成：q');assert.equal((await read(otherEvents)).draft,'范围：qwqw正文1');await record('未完成序列切窗取消');
  });
  await test('候选跨屏重测期间视口缩小仍完整显示数量关闭按钮和底部提示',async()=>{
    const id=await savePhrase('跨屏长标题'.repeat(15),'Ctrl+Shift+F10');
    const session=await context.newCDPSession(candidates);
    try {
      await prepare();await opened(()=>key(121,['-Ctrl','-Shift']));
      await poll(async()=>await candidates.evaluate(()=>innerWidth>=350),'跨屏前宽候选');
      // 模拟 DPI 通知与原生重测间的窄视口，不把浏览器模拟结果当成系统双屏验收。
      await session.send('Emulation.setDeviceMetricsOverride',{width:240,height:420,deviceScaleFactor:1.5,mobile:false});await delay(180);
      const actual=await candidates.evaluate(()=>{
        const root=document.querySelector('section'),close=root.querySelector('header button'),count=root.querySelector('header span');
        const fits=element=>{const r=element.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;};
        const fixed={root:fits(root),close:fits(close),count:fits(count),hints:[...root.querySelectorAll('footer span')].every(fits),overflow:document.documentElement.scrollWidth>innerWidth};
        // 同一真实 WebView 中去掉本次约束应重现旧布局的右侧裁切。
        root.style.maxWidth='none';const previousClipped=!fits(close);root.style.removeProperty('max-width');
        return {...fixed,previousClipped};
      });
      assert.deepEqual(actual,{root:true,close:true,count:true,hints:true,overflow:false,previousClipped:true});
      receipts.push({name:'候选跨屏过渡视口模拟',...actual});
      // WebView2 的虚拟视口截图可能重复拼接原生表面；恢复真实视口后才保留视觉证据。
      await session.send('Emulation.clearDeviceMetricsOverride');await delay(180);
      await native(['-Handle',String(candidateHandle),'-Screenshot',path.join(evidence,'candidate-dpi-restored.png')]);
    } finally {await session.send('Emulation.clearDeviceMetricsOverride');await session.detach();await key(27);await noCandidate();await invoke('delete_item',{id,kind:'phrase',recycle:false});}
  });
  const dpiName='候选跨屏实际系统DPI往返后内容与客户区一致';
  if(!filter||dpiName.includes(filter)) {
    const monitors=(await native([])).monitors;
    if(monitors.length<2)results.push({name:dpiName,skipped:true,reason:'只有一个显示器，未执行真实跨屏 DPI 验收'});
    else await test(dpiName,async()=>{
      await prepare();await opened();const original=await native(['-Handle',String(hostInfo.hwnd)]),observed=new Set();
      try {
        for(let round=0;round<2;round++)for(const [index,monitor] of monitors.entries()) {
          await native(['-Action','move','-Handle',String(hostInfo.hwnd),'-X',String(monitor.work.left+80),'-Y',String(monitor.work.top+120),'-Width','600','-Height','450']);
          await poll(async()=>{
            const actual=await native(['-Handle',String(candidateHandle)]),payload=await invoke('get_shortcut_candidates');
            const view=await candidates.evaluate(()=>({width:innerWidth,height:innerHeight,root:document.querySelector('section').getBoundingClientRect().width,close:document.querySelector('header button').getBoundingClientRect().right,footer:document.querySelector('footer').getBoundingClientRect().bottom,dpr:devicePixelRatio}));
            if(!actual.visible||actual.workArea.left!==monitor.work.left||Math.abs(view.dpr-actual.dpi/96)>.01||Math.abs(actual.clientRect.right-view.width*actual.dpi/96)>1||view.root>view.width+.5||view.close>view.width+.5||view.footer>view.height+.5)return false;
            observed.add(actual.dpi);receipts.push({name:`系统跨屏 ${round}/${index}`,actual,view,layoutKey:payload.layoutKey});return true;
          },'跨屏候选尺寸与自身DPI一致');
          await candidates.screenshot({path:path.join(evidence,`candidate-monitor-${round}-${index}.png`)});
        }
        receipts.push({name:'跨屏实际覆盖DPI',values:[...observed]});
      } finally {await key(27);await native(['-Action','move','-Handle',String(hostInfo.hwnd),'-X',String(original.rect.left),'-Y',String(original.rect.top),'-Width',String(original.rect.right-original.rect.left),'-Height',String(original.rect.bottom-original.rect.top)]);}
    });
  }
  await test('候选紧凑定位跟随光标和窗口移动，无光标按控件及窗口回退',async()=>{
    await settings({sequenceHotkeys:false,customHotkeys:true,globalPhraseHotkeys:true});
    await prepare();
    const initial=await native(['-Handle',String(hostInfo.hwnd)]), w=initial.rect.right-initial.rect.left, h=initial.rect.bottom-initial.rect.top;
    const move=async(x,y)=>native(['-Action','move','-Handle',String(hostInfo.hwnd),'-X',String(x),'-Y',String(y),'-Width',String(w),'-Height',String(h)]);
    try {
      await move(initial.workArea.left+80,initial.workArea.top+240);
      await opened(()=>key(122,['-Ctrl','-Shift']));
      await poll(async()=>(await native(['-Handle',String(candidateHandle)])).visible,'紧凑候选显示');
      const before=await native(['-Handle',String(candidateHandle)]), target=await native(['-Handle',String(hostInfo.hwnd)]), scale=before.dpi/96, gap=Math.round(6*scale);
      assert.ok(target.caretRect,'宿主提供实际系统光标');
      assert.equal(before.rect.left,target.caretRect.left);assert.equal(before.rect.bottom,target.caretRect.top-gap);
      const dims=[before.rect.right-before.rect.left,before.rect.bottom-before.rect.top];
      const snapshot=await invoke('get_shortcut_candidates');
      // 观察真实 IPC，确认移动没有请求前端重新测量。
      await candidates.evaluate(()=>{window.__candidateResizeCalls=0;window.__candidateOriginalInvoke=window.__TAURI_INTERNALS__.invoke;window.__TAURI_INTERNALS__.invoke=(command,args,options)=>{if(command==='resize_shortcut_candidates')window.__candidateResizeCalls++;return window.__candidateOriginalInvoke(command,args,options);};});
      await move(target.rect.left+60,target.rect.top+40);
      await poll(async()=>{const a=await native(['-Handle',String(candidateHandle)]);return a.rect.left===before.rect.left+60&&a.rect.top===before.rect.top+40;},'候选跟随同屏移动');
      let actual=await native(['-Handle',String(candidateHandle)]);
      assert.deepEqual([actual.rect.right-actual.rect.left,actual.rect.bottom-actual.rect.top],dims);
      assert.equal(await candidates.evaluate(()=>window.__candidateResizeCalls),0);assert.equal((await invoke('get_shortcut_candidates')).sessionId,snapshot.sessionId);
      await control('no-caret');
      await poll(async()=>!(await native(['-Handle',String(hostInfo.hwnd)])).caretRect,'宿主移除系统光标');
      const focus=(await native(['-Handle',String(hostInfo.hwnd)])).focusRect;
      await poll(async()=>{const a=await native(['-Handle',String(candidateHandle)]);return a.rect.left===focus.left&&a.rect.bottom===focus.top-gap;},'候选回退当前输入控件');
      await control('full-input');await delay(200);
      const full=await native(['-Handle',String(hostInfo.hwnd)]);
      await poll(async()=>{const a=await native(['-Handle',String(candidateHandle)]);return a.rect.left===full.rect.left+Math.round(12*scale)&&a.rect.bottom===full.rect.bottom-Math.round(12*scale);},'整窗焦点容器回退可见窗口内缩位置');
      await control('restore-input');await key(27);await noCandidate();
      await move(initial.workArea.left+80,initial.workArea.top);
      await opened(()=>key(122,['-Ctrl','-Shift']));
      const top=await native(['-Handle',String(hostInfo.hwnd)]);
      await poll(async()=>(await native(['-Handle',String(candidateHandle)])).rect.top===top.caretRect.bottom+gap,'上方不足时放到光标下方');
      await move(initial.workArea.right-200,initial.workArea.top+240);
      await poll(async()=>{const a=await native(['-Handle',String(candidateHandle)]);return a.rect.right===a.workArea.right-gap;},'右边缘避让');
      actual=await native(['-Handle',String(candidateHandle)]);assert.equal(actual.foreground,hostInfo.hwnd);assert.equal(actual.noActivate,true);
      await candidates.screenshot({path:path.join(evidence,'candidate-compact-position.png')});
      receipts.push({name:'候选定位及移动',before,after:actual,dims});
    } finally {
      await candidates.evaluate(()=>{if(window.__candidateOriginalInvoke)window.__TAURI_INTERNALS__.invoke=window.__candidateOriginalInvoke;});
      await key(27);await control('restore-input');await move(initial.rect.left,initial.rect.top);
    }
  });
  await test('大字号长标题正文及纯附件候选限制宽高，选择不重调尺寸',async()=>{
    await settings({fontSize:18,customHotkeys:true});
    const ids=[], images=await invoke('import_images',{paths:[path.join(repo,'public','shanyu.png')]});
    for(let i=0;i<12;i++)ids.push(await invoke('save_phrase',{phrase:{id:'',title:i===1?'':'长标题'.repeat(25),rootId:phraseRoot,groupId:null,shortcut:'Ctrl+Shift+F9',color:'',background:'',position:0,segments:[{blocks:i===1?images:[{kind:'text',text:'很长的单行正文摘要'.repeat(30)}]}]},afterId:null}));
    await prepare();await opened(()=>key(120,['-Ctrl','-Shift']));
    await poll(async()=>await candidates.evaluate(()=>innerWidth>=350),'长内容扩展宽度');
    const geometry=await native(['-Handle',String(candidateHandle)]);
    const measured=await candidates.evaluate(()=>({width:innerWidth,height:innerHeight,row:document.querySelector('[data-candidate]').getBoundingClientRect().height,overflow:document.documentElement.scrollWidth>innerWidth,title:document.querySelector('[data-candidate]').title}));
    assert.ok(measured.width<=360&&measured.height<=420&&measured.row>=32);assert.equal(measured.overflow,false);assert.ok(measured.title.length>200);
    for(let i=0;i<11;i++)await key(40);
    assert.deepEqual((await native(['-Handle',String(candidateHandle)])).rect,geometry.rect);
    assert.equal(await candidates.evaluate(()=>{const row=document.querySelector('[aria-selected="true"]'),list=document.querySelector('.shortcut-candidates-list'),r=row.getBoundingClientRect(),l=list.getBoundingClientRect();return row===list.lastElementChild&&r.top>=l.top&&r.bottom<=l.bottom+1;}),true,'超过十项时末项自动滚动可见');
    await candidates.screenshot({path:path.join(evidence,'candidates-long-large-font.png')});
    await key(27);await noCandidate();
    for(const id of ids)await invoke('delete_item',{id,kind:'phrase',recycle:false});
    await settings({fontSize:14});
  });
  await test('统一诊断按类启用、透传与导出隐私、重启保留', async()=>{
    await settings({customHotkeys:true,globalPhraseHotkeys:true,sequenceHotkeys:false});
    await prepare('诊断草稿：');
    assert.equal((await invoke('diagnostics_status')).running,false);
    await showMain();await page.getByRole('button',{name:'设置',exact:true}).click();
    await poll(async()=>context.pages().some(p=>p.url().includes('#settings')),'诊断设置窗口');
    settingsPage=context.pages().find(p=>p.url().includes('#settings'));
    await settingsPage.getByRole('button',{name:'问题诊断',exact:true}).click();
    await settingsPage.getByRole('button',{name:'开始记录'}).waitFor();
    for(const name of ['账号与同步','窗口与显示','卡顿与响应'])await settingsPage.getByRole('checkbox',{name:new RegExp(name)}).check();
    await settingsPage.screenshot({path:path.join(evidence,'diagnostics-settings.png')});
    await captureScales(settingsPage,'diagnostics-small',480,480);
    await settingsPage.getByRole('button',{name:'开始记录'}).click();
    await poll(async()=>!context.pages().includes(settingsPage),'开始记录后正常关闭设置');
    assert.equal((await invoke('diagnostics_status')).running,true);
    await focusHost();await opened();
    assert.equal((await receipt()).draft,'诊断草稿：qw','开启诊断仍透传触发字母');
    assert.equal((await native(['-Handle',String(candidateHandle)])).foreground,hostInfo.hwnd);
    await key(49);await submitted('诊断草稿：正文1');
    // 本机临时HTTP宿主返回带隐私哨兵的错误，证明导出不会复制原始错误或账号字段。
    const server=http.createServer((_req,res)=>{res.writeHead(401,{'content-type':'application/json'});res.end(JSON.stringify({message:'DIAG_SECRET_SERVER_ERROR'}));});
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    try {await assert.rejects(invoke('team_login',{server:`http://127.0.0.1:${server.address().port}`,username:'DIAG_SECRET_USER',password:'DIAG_SECRET_PASSWORD'}));}
    finally {await new Promise(resolve=>server.close(resolve));}
    await delay(2200);await invoke('diagnostics_stop');
    const zipPath=path.join(evidence,'diagnostics.zip');await invoke('diagnostics_export',{path:zipPath});
    const unzip=async filename=>JSON.parse((await run('python',['-c','import json,sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); print(json.dumps({n:z.read(n).decode("utf-8") for n in z.namelist()},ensure_ascii=True))',filename],{encoding:'utf8'})).stdout);
    const entries=await unzip(zipPath),raw=JSON.stringify(entries);
    assert.deepEqual(Object.keys(entries).sort(),['events.jsonl','manifest.json','说明.txt'].sort());
    for(const secret of ['DIAG_SECRET_','正文1','诊断草稿：',hostInfo.hwnd.toString(),'Q+W'])assert.equal(raw.includes(secret),false,`诊断不得包含${secret}`);
    const records=entries['events.jsonl'].trim().split('\n').map(JSON.parse),events=records.map(r=>r.event);
    const trigger=events.filter(e=>e.event==='key'&&[1,2].includes(e.role));
    assert.ok(trigger.length>=4);assert.ok(trigger.every(e=>!e.local_blocked&&!e.chain_blocked));
    assert.ok(trigger.some(e=>e.down)&&trigger.some(e=>!e.down));
    for(const name of ['startup','bindings','hook','context','candidate','delivery','layout','network','ipc','heartbeat'])assert.ok(events.some(e=>e.event===name),`缺少${name}记录`);
    assert.ok(events.some(e=>e.event==='network'&&e.status===401));
    const manifest=JSON.parse(entries['manifest.json']);assert.equal(manifest.imeComposition,'unknown');assert.ok(manifest.windowsBuild>0);
    await focusHost();await key(81);await key(87);await key(27);
    await invoke('diagnostics_export',{path:zipPath});
    const stopped=await unzip(zipPath);assert.equal(stopped['events.jsonl'].trim().split('\n').map(JSON.parse).filter(r=>r.event.event==='key').length,events.filter(e=>e.event==='key').length);
    // 只重启本套验收进程，不操作安装版；持久记录可导出且详细采集不会自启。
    await invoke('window_action',{action:'exit'});await poll(async()=>app.exitCode!==null,'诊断隔离进程退出');await browser.close().catch(()=>{});
    app=spawn(path.join(repo,'src-tauri','target','debug','QuickPhrase.exe'),['--autostart'],{cwd:repo,windowsHide:true,stdio:'ignore',env:{...process.env,SHANYU_TEST_DATA_DIR:path.join(root,'data'),WEBVIEW2_USER_DATA_FOLDER:path.join(root,'webview'),WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${debuggerPort}`}});
    browser=null;for(let i=0;i<80;i++){try{browser=await chromium.connectOverCDP(`http://127.0.0.1:${debuggerPort}`);break;}catch{await delay(200);}}
    assert.ok(browser);context=browser.contexts()[0];page=context.pages().find(p=>!new URL(p.url()).hash);await page.locator('.main-window').waitFor();
    assert.equal((await invoke('diagnostics_status')).running,false);
    await invoke('diagnostics_export',{path:path.join(evidence,'diagnostics-restarted.zip')});
    const restarted=await unzip(path.join(evidence,'diagnostics-restarted.zip'));
    const restored=restarted['events.jsonl'].trim().split('\n').map(JSON.parse);
    assert.ok(new Set(restored.map(r=>r.process)).size>=2);
    assert.equal(restored.filter(r=>r.event.event==='key').length,events.filter(e=>e.event==='key').length);
    await invoke('diagnostics_clear');assert.equal((await invoke('diagnostics_status')).records,0);
    receipts.push({name:'统一诊断包',path:zipPath,records:records.length,manifest});
  });
  await fs.writeFile(path.join(evidence,'native-report.json'),JSON.stringify({root,results,receipts,keyboard,clientVersion,scope:'Windows 11 独立输入桌面、固定测试输入宿主及 Debug 客户端；真实聊天软件单独验收',scales:'WebView2 deviceScaleFactor 1/1.5/2；系统物理 DPI 与真实输入法未验收'},null,2));
} catch(error) {
  const diagnosis={windows:{main,candidates:candidateHandle},foreground:hostInfo?await native(['-Handle',String(hostInfo.hwnd)]).catch(()=>null):null,log:await fs.readFile(path.join(root,'data','shanyu.log'),'utf8').catch(()=>''),receipt:hostInfo?await receipt().catch(()=>null):null};
  diagnosis.uiKeys={main:page?await page.evaluate(()=>window.__shortcutQaKeys).catch(()=>null):null,dock:dockPage?await dockPage.evaluate(()=>({keys:window.__shortcutQaKeys,active:document.activeElement?.outerHTML})).catch(()=>null):null};
  await fs.writeFile(path.join(evidence,'native-report.json'),JSON.stringify({root,results,receipts,error:String(error),diagnosis},null,2));
  if(page)await page.screenshot({path:path.join(evidence,'failure.png')}).catch(()=>{});console.error(error);process.exitCode=1;
} finally {
  if(otherHost)await control('close',{},path.join(root,'other-controls.json')).catch(()=>{});
  if(host) {
    const state=await native([]).catch(()=>null);
    if(app&&state?.clipboardOwnerPid===app.pid)await control('adopt',{sequence:state.clipboardSequence,ownerPid:app.pid}).catch(()=>{});
    await control('close').catch(()=>{});
  }
  if(page&&!page.isClosed())await invoke('window_action',{action:'exit'}).catch(()=>{});if(browser)await browser.close().catch(()=>{});
  if(app&&!app.exitCode)app.kill();if(host&&!host.exitCode)host.kill();if(otherHost&&!otherHost.exitCode)otherHost.kill();
}
