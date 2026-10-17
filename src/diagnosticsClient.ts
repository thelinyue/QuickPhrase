import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';

const operations: Record<string, string> = {
  get_library: 'get_library', search_content: 'search_content', get_member_view: 'get_member_view',
  get_team_view: 'get_team_view', patch_settings: 'save_settings', save_settings: 'save_settings', save_phrase: 'save_phrase',
};
let expires = 0;
/** 只在临时性能记录期间上报白名单操作，不发送参数、异常文本或路由。 */
export function reportDiagnosticOperation(command: string, ok: boolean, started: number) {
  const operation = operations[command];
  if (!operation || performance.now() >= expires) return;
  void invoke('diagnostics_frontend', { operation, ok, elapsedMs: Math.round(performance.now() - started) }).catch(() => {});
}
interface Session { running: boolean; categories: string[]; remainingSeconds: number }
export async function startDiagnosticObserver() {
  let revision = 0, timer: number | undefined;
  const apply = (session: Session) => {
    window.clearInterval(timer);
    expires = session?.running && session.categories.includes('performance') ? performance.now() + session.remainingSeconds * 1000 : 0;
    if (!expires) return;
    let previous = performance.now();
    timer = window.setInterval(() => {
      if (performance.now() >= expires) { window.clearInterval(timer); return; }
      reportDiagnosticHeartbeat(previous); previous = performance.now();
    }, 2000);
  };
  // 先订阅再读取，防止请求期间发生的停止/开启被旧快照覆盖。
  await listen<Session>('diagnostics-changed', event => { ++revision; apply(event.payload); });
  const current = revision;
  const session = await invoke<Session>('diagnostics_status');
  if (current === revision) apply(session);
}
function reportDiagnosticHeartbeat(started: number) {
  void invoke('diagnostics_frontend', { operation: 'heartbeat', ok: true, elapsedMs: Math.round(performance.now() - started) }).catch(() => {});
}
