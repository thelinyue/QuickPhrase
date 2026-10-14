import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
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
const invoke = (cmd,args={},window=page) => window.evaluate(({cmd,args})=>window.__TAURI_INTERNALS__.invoke(cmd,args),{cmd,args});
const key = async (code, modifiers=[], extra=[]) => {const result=await native(['-Action','key','-Key',String(code),...modifiers,...extra]);assert.equal(result.desktop,result.inputDesktop,'测试按键应进入隔离输入桌面');return result;};
const chord = () => native(['-Action','key','-Keys','81,87']);
const focusHost = async () => { for(let i=0;i<3;i++){await native(['-Action','focus','-Handle',String(hostInfo.hwnd)]);await control('focus');await delay(300);if((await native(['-Handle',String(hostInfo.hwnd)])).foreground===hostInfo.hwnd)return;}throw Error('隔离宿主未取得稳定输入焦点。'); };
// 点击活动页签取得前台权限，避免固定坐标误打开共享库菜单或命中拖动区域。
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
  assert.equal((await native(['-ClipboardExpected',expectedClipboard])).clipboardMatches,true,'保留最后投递内容；没有投递时不改剪贴板');
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
  host=spawn(hostExe,[events,controls],{windowsHide:true,stdio:['ignore','ignore','pipe']});host.stderr.on('data',b=>console.error(b.toString()));hostInfo=await receipt();
  // 连续切换隔离输入桌面时，旧WebView进程可能尚未释放公共配置目录；每次独立配置和调试端口。
  const debuggerSocket=net.createServer();await new Promise(resolve=>debuggerSocket.listen(0,'127.0.0.1',resolve));
  const debuggerPort=debuggerSocket.address().port;await new Promise(resolve=>debuggerSocket.close(resolve));
  app=spawn(path.join(repo,'src-tauri','target','debug','QuickPhrase.exe'),[],{cwd:repo,windowsHide:true,stdio:'ignore',env:{...process.env,SHANYU_TEST_DATA_DIR:path.join(root,'data'),WEBVIEW2_USER_DATA_FOLDER:path.join(root,'webview'),WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${debuggerPort}`}});
  for(let i=0;i<80;i++){try{browser=await chromium.connectOverCDP(`http://127.0.0.1:${debuggerPort}`);break;}catch{await delay(200);}}
  if(!browser)throw Error('无法连接隔离客户端。');context=browser.contexts()[0];context.setDefaultTimeout(6000);page=context.pages().find(p=>!new URL(p.url()).hash);
  await page.locator('.main-window').waitFor();assert.equal((await invoke('get_library')).dataDir,path.join(root,'data'));
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

  await test('双键完整匹配只显示候选，按分类排序、无激活且不占任务栏',async()=>{
    await prepare();const payload=await opened();assert.deepEqual(payload.phrases.map(p=>p.id),ids.map(id=>references.get(id)));assert.deepEqual(payload.target,targetContext);
    await poll(async()=>(await native(['-Handle',String(candidateHandle)])).visible,'候选窗口显示');
    const actual=await native(['-Handle',String(candidateHandle)]);assert.equal(actual.foreground,hostInfo.hwnd);assert.equal(actual.visible,true);assert.equal(actual.toolWindow,true);assert.equal(actual.appWindow,false);assert.equal(actual.noActivate,true);
    assert.ok(actual.rect.left>=actual.workArea.left&&actual.rect.top>=actual.workArea.top&&actual.rect.right<=actual.workArea.right&&actual.rect.bottom<=actual.workArea.bottom,'候选完整限制在屏幕工作区内');
    assert.equal((await receipt()).draft,'已有草稿：');await candidates.screenshot({path:path.join(evidence,'candidates-native.png')});await captureScales(candidates,'candidates',370,354);await key(27);await noCandidate();await record('Esc取消保留草稿');
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
  await test('双键超时在原输入区回放，焦点和外部点击取消候选',async()=>{
    await prepare('超时：');await key(81);await submitted('超时：q');await noCandidate();await record('双键超时回放');
    await prepare();await opened();await control('blur');await noCandidate();await record('输入控件变化取消');
    assert.equal((await receipt()).draft,'已有草稿：');
    await prepare();await opened();await native(['-Action','focus','-Handle',String(hostInfo.hwnd)]);await noCandidate();await record('点击候选外部取消');
    await prepare();await opened();await showMain();await noCandidate();assert.equal((await receipt()).draft,'已有草稿：');await record('窗口切换取消');
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
    await prepare('搜索输入：');await showMain();await page.getByRole('textbox',{name:'搜索话术、本地文件',exact:true}).fill('单候选');await page.locator('#search-results [data-row]').waitFor();
    const searchBox=await page.getByRole('textbox',{name:'搜索话术、本地文件',exact:true}).boundingBox();await native(['-Action','focus','-Handle',String(main),'-X',String(Math.round(searchBox.x+12)),'-Y',String(Math.round(searchBox.y+12))]);
    assert.equal(await page.getByRole('textbox',{name:'搜索话术、本地文件',exact:true}).evaluate(e=>e===document.activeElement),true);
    await key(49,['-Ctrl']);await submitted('搜索输入：正文单候选');await record('搜索框保持焦点时序号插入原目标');
    const attachment=path.join(root,'序号搜索文件.txt');await fs.writeFile(attachment,'隔离验收');await invoke('add_files',{categoryId:fileRoot,paths:[attachment]});
    // 文件搜索属于本地文件来源，先切页签再验证文件槽位不会触发话术。
    await showMain();await page.getByRole('button',{name:'本地文件',exact:true}).click();await page.getByRole('textbox',{name:'搜索话术、本地文件',exact:true}).fill('序号搜索文件');await page.locator('#search-results [data-row]').waitFor();
    await control('reset',{draft:'文件占位：'});await focusHost();await key(49,['-Ctrl']);await delay(200);await submitted('文件占位：');await record('搜索文件槽位不发送话术');
    await invoke('sync_sequence_map',{ids:[ids[0]],loading:true});await key(49,['-Ctrl']);await delay(100);await submitted('文件占位：');
    await showMain();await page.getByRole('textbox',{name:'搜索话术、本地文件',exact:true}).fill('');await page.getByRole('button',{name:'话术',exact:true}).click();await settings({sequenceDirectSend:true});await focusHost();await key(49,['-Ctrl']);await submitted('',1);await record('序号直接发送');
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
    await prepare();await key(56,['-Alt']);await poll(async()=>page.getByRole('textbox',{name:'搜索话术、本地文件'}).evaluate(e=>e===document.activeElement),'Alt搜索聚焦');
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
  await test('共同全局范围开关限制其他窗口，不向新目标回放未完成序列',async()=>{
    const otherEvents=path.join(root,'other-events.json'),otherControls=path.join(root,'other-controls.json');
    otherHost=spawn('powershell.exe',['-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',path.join(repo,'tests','native-host.ps1'),'-EventsPath',otherEvents,'-ControlPath',otherControls],{windowsHide:true,stdio:'ignore'});const other=await read(otherEvents);
    await prepare();await settings({globalPhraseHotkeys:false,sequenceHotkeys:true,sequenceDirectSend:false});await native(['-Action','focus','-Handle',String(other.hwnd)]);await control('reset',{draft:'范围：'},otherControls);await control('focus',{},otherControls);await chord();await key(49,['-Ctrl']);await delay(180);assert.equal((await read(otherEvents)).draft,'范围：qw');await noCandidate();
    await settings({globalPhraseHotkeys:true});await opened();await key(27);await noCandidate();
    await key(49,['-Ctrl']);await poll(async()=>(await read(otherEvents)).draft==='范围：qw正文1','全局序号在其他窗口生效');receipts.push({name:'全局序号在其他窗口插入',...await read(otherEvents)});
    await focusHost();await control('reset',{draft:'未完成：'});await key(81);await native(['-Action','focus','-Handle',String(other.hwnd)]);await control('focus',{},otherControls);await delay(1200);assert.equal((await receipt()).draft,'未完成：');assert.equal((await read(otherEvents)).draft,'范围：qw正文1');await record('未完成序列切窗取消');
  });
  await fs.writeFile(path.join(evidence,'native-report.json'),JSON.stringify({root,results,receipts,scope:'Windows 11 独立输入桌面、标准 WinForms 及 Debug 吸附宿主；真实聊天软件未验收',scales:'WebView2 deviceScaleFactor 1/1.5/2；系统物理 DPI 与真实输入法未验收'},null,2));
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
