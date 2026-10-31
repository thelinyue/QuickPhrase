import { useEffect, useState } from 'react';
import { useTeams } from './api';
import { CompanyFilesPanel } from './CompanyFiles';
import { CloudCommands, canUseCloudCommands } from './CloudCommands';
import { CloudPermissions } from './CloudPermissions';
import { Icon, Titlebar } from './ui';

/** 两个模块保留各自状态；隐藏文件页不会卸载监听或中断 Rust 传输队列。 */
export function CloudCenter() {
  const { status, version } = useTeams();
  const [module, setModule] = useState('files'), [permissionsEpoch, setPermissionsEpoch] = useState<number | null>(null);
  const epoch = status.sessionEpoch || 0, commands = canUseCloudCommands(status);
  const active = commands && module === `commands:${epoch}` ? 'commands' : 'files';
  useEffect(() => {
    const outside = (e: PointerEvent) => document.querySelectorAll<HTMLDetailsElement>('.cloud-module-more[open]').forEach(menu => { if (!menu.contains(e.target as Node)) menu.open = false; });
    const escape = (e: KeyboardEvent) => { if (e.key === 'Escape') document.querySelectorAll<HTMLDetailsElement>('.cloud-module-more[open]').forEach(menu => { menu.open = false; menu.querySelector('summary')?.focus(); e.preventDefault(); }); };
    document.addEventListener('pointerdown', outside); document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };
  }, []);
  return <section className="client-window company-window cloud-center"><Titlebar title="闪云中心" minimize maximize />
    <div className="cloud-center-navigation"><div className="cloud-segments" role="tablist" aria-label="闪云中心模块">
      <button role="tab" id="cloud-files-tab" aria-controls="cloud-files-panel" aria-selected={active === 'files'} onClick={() => setModule('files')}>云文件</button>
      {commands && <button role="tab" id="cloud-commands-tab" aria-controls="cloud-commands-panel" aria-selected={active === 'commands'} onClick={() => setModule(`commands:${epoch}`)}>云命令</button>}
    </div><span className="toolbar-spacer" /><span className="cloud-account">{status.user?.displayName || status.user?.username || status.username}</span>
    {status.user?.admin && <button className="company-button" onClick={() => setPermissionsEpoch(epoch)}><Icon name="shield" />权限管理</button>}</div>
    {status.user?.admin && !status.cloudCommands && <p className="company-message">当前闪云版本不支持云命令，升级服务端后可用；云文件不受影响。</p>}
    <div className="cloud-module" role="tabpanel" id="cloud-files-panel" aria-labelledby="cloud-files-tab" hidden={active !== 'files'}><CompanyFilesPanel active={active === 'files'} /></div>
    {commands && <div className="cloud-module" role="tabpanel" id="cloud-commands-panel" aria-labelledby="cloud-commands-tab" hidden={active !== 'commands'}><CloudCommands key={epoch} status={status} /></div>}
    {permissionsEpoch === epoch && status.user?.admin && <CloudPermissions key={epoch} status={status} version={version} close={() => setPermissionsEpoch(current => current === epoch ? null : current)} />}
  </section>;
}
