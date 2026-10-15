import { useEffect, useState } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { call, closeWindow, openWindow } from './api';
import { Feedback, Titlebar } from './ui';
import type { Settings } from './types';

const topics = ['快速开始', '搜索与快捷键', '窗口吸附', '本地文件与云文件', '账号与共享话术', '数据管理'] as const;
type Topic = typeof topics[number];

/** 本地帮助只解释实际操作，不创建示例内容或试发消息；保持打开时可以切回目标应用练习。 */
export function UsageGuideWindow({ settings }: { settings?: Settings }) {
  const [topic, setTopic] = useState<Topic>('快速开始'), [message, setMessage] = useState('');
  const hotkey = (key: 'searchHotkey' | 'windowHotkey' | 'commonHotkey', fallback: string) => `Alt+${settings?.[key] || fallback}`;
  useEffect(() => {
    let disposed = false;
    // 实际可见并完成首帧后才记为已展示，窗口创建失败不会吞掉下次入门提示。
    const frame = requestAnimationFrame(() => { void getCurrentWindow().isVisible().then(visible => {
      if (visible && !disposed) return call('mark_usage_guide_shown');
    }).catch(e => { if (!disposed) setMessage(String(e)); }); });
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.isComposing) { e.preventDefault(); void closeWindow(); } };
    document.addEventListener('keydown', escape);
    return () => { disposed = true; cancelAnimationFrame(frame); document.removeEventListener('keydown', escape); };
  }, []);
  async function begin() { try { await call('window_action', { action: 'start-using' }); } catch (e) { setMessage(String(e)); } }
  const content = topic === '快速开始' ? <>
    <h2>从第一条话术开始</h2><p className="guide-intro">先准备常用内容，再把它添加到目标输入区。</p>
    <ol className="guide-steps"><li><h3>创建分类</h3><p>在“个人话术”点击“新增一级分类”。已有分类时，从右上角一级分类菜单选择“新增同级分类”。</p></li><li><h3>添加话术</h3><p>在列表空白处右键，选择“新增话术”，填写标题和正文后保存。工具栏也可添加图片、文件和分段。</p></li><li><h3>聚焦目标，再插入</h3><p>先点击聊天或其他软件的输入区，再返回闪语。选中话术后按 Enter 添加到输入区，检查内容后自行发送。</p></li></ol>
    <div className="guide-note"><strong>插入与发送</strong><p>“添加到输入区”只粘贴内容；“直接发送”还会提交发送按键。当前话术双击设置为{settings?.phraseDoubleClick === 'send' ? '直接发送' : '添加到输入区'}，可在“常用设置”调整。</p><p>没有可用目标或操作被中断时会停止，不自动重复发送。系统操作完成不代表目标应用最终发送成功。</p></div>
  </> : topic === '搜索与快捷键' ? <>
    <h2>更快找到常用内容</h2><p>按 <kbd>{hotkey('searchHotkey', 'Q')}</kbd> 打开并聚焦搜索，输入标题或正文关键词；上下键选择结果，Enter 添加到输入区，Esc 退出搜索。</p>
    <p><kbd>{hotkey('windowHotkey', 'W')}</kbd> 显示／隐藏侧栏，<kbd>{hotkey('commonHotkey', 'E')}</kbd> 定位到常用短语。快捷键可在“快捷键”设置页修改。</p>
    <h3>常用短语</h3><p>点击“＋”添加不超过16字的单行短语，右键修改、删除或排序。单击添加到输入区，双击不重复插入；Enter 直接发送。</p>
    <h3>话术快捷键</h3><p>可在话术右键菜单设置专用快捷键，也可在设置中开启序号快捷键。同键候选用上下键选择，空格插入，Enter 直接发送，Esc 取消。目标变化时取消候选。</p>
    <button className="secondary-button" onClick={() => void openWindow('settings', '', '', '快捷键').catch(e => setMessage(String(e)))}>打开快捷键设置</button>
  </> : topic === '窗口吸附' ? <>
    <h2>显示、隐藏与跟随</h2><p>从桌面或开始菜单手动打开闪语会显示窗口；重复打开会唤起已有窗口。开机自启驻留后台，不主动抢焦点。</p>
    <p>右上角磁铁开关控制企业微信吸附，图钉控制置顶。吸附方向在“吸附设置”中调整；目标最小化或切换到其他程序时，跟随面板会隐藏，返回目标后恢复。</p>
    <p>没有企业微信目标时可独立使用。手动隐藏后，通过托盘、显示快捷键或重新打开闪语恢复。最小化到托盘的行为可在设置中调整。</p>
    <p>底部眼睛按钮显示／隐藏常用短语和搜索框；底部吸附短语与搜索可以在“界面设置”中分别设置。</p>
  </> : topic === '本地文件与云文件' ? <>
    <h2>管理和使用文件</h2><h3>本地文件</h3><p>切到“本地文件”，先创建分类，再从主菜单新增关联文件。Ctrl 单击多选，Shift 选择连续项，Ctrl+A 选择当前分类。</p><p>移除关联、移动分类和改显示名都保留磁盘源文件；失效文件可右键重新关联。</p>
    <h3>云文件</h3><p>登录并完成初始改密后，从主菜单打开“云文件”。有上传权限时，可多选本机文件，或将文件拖到云文件列表、上传管理正文区，直接加入上传任务。</p><p>单文件需为1字节至128MB，文件夹不上传。无效文件单独提示，其他文件继续；在上传管理查看进度、停止任务或核对中断结果。关闭管理窗口后任务继续，退出客户端会停止。</p><p>下载完成后不会自动投递。服务管理员分别授予上传、删除、恢复权限；云文件删除移入回收站。</p>
  </> : topic === '账号与共享话术' ? <>
    <h2>个人内容与团队共享</h2><p>在主菜单“账号与共享”填写 Hub 地址并登录。首次登录需修改初始密码；本地功能无需登录即可使用。</p><p>“个人话术”保存个人内容，私人同步需要主动开启。“共享话术 ▾”切换已授权的共享话术；查看者阅读和使用，编辑者维护内容，库管理员管理成员。</p><p>个人话术和共享话术通过复制分别维护；切换账号不会把上一个账号的任务或内容交给新账号。</p>
    <button className="secondary-button" onClick={() => void openWindow('settings', '', '', '账号与共享').catch(e => setMessage(String(e)))}>打开账号与共享</button>
  </> : <>
    <h2>导入、导出与回收</h2><p>从主菜单打开“导入数据”或“导出数据”，选择个人库、共享话术或本机文件与设置。导入先预览，确认后保存；完整图文和附件请使用 ZIP 备份，Excel 只保存文字。</p><p>删除话术和分类默认移入回收站，可从主菜单查看并还原。永久删除无法撤回；本地文件关联和常用短语不进入回收站。</p><p>安装、升级和卸载保留用户数据。同步不能代替独立备份，重要内容可定期导出。</p>
  </>;
  return <section className="client-window usage-guide-window"><Titlebar title="使用指引" minimize /><div className="settings-layout"><aside className="settings-sidebar guide-sidebar"><nav aria-label="指引主题">{topics.map(value => <button key={value} aria-current={topic === value ? 'page' : undefined} className={topic === value ? 'active' : ''} onClick={() => setTopic(value)}>{value}</button>)}</nav><div className="settings-brand"><span>闪语</span><small>随时查看，边看边练</small></div></aside><main className="guide-content" key={topic}>{content}</main></div><footer className="guide-footer"><span>以后可从主菜单打开“使用指引”</span><button className="primary-button" onClick={() => void begin()}>开始使用</button></footer><Feedback message={message} clear={() => setMessage('')} /></section>;
}
