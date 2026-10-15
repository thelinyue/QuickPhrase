import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import type { MemberView, TeamStatus } from './types';

/** 成员缓存只在主窗口展示时组合，账号与请求序号共同拦截切换前的旧视图。 */
export function useMembers(status: TeamStatus) {
  const account = `${status.server}:${status.user?.id || ''}:${status.sessionEpoch ?? ''}`;
  const [loaded, setLoaded] = useState<{ account: string; view: MemberView } | null>(null);
  const sequence = useRef(0);
  const allowed = !!status.user && !status.user.mustChangePassword && status.memberPhraseSync === true;
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined;
    const refresh = async () => {
      const request = ++sequence.current;
      if (!allowed) { setLoaded(null); return; }
      try {
        const view = await call<MemberView>('get_member_view');
        if (!disposed && request === sequence.current && view) setLoaded({ account, view });
      } catch { /* 连接状态与设置页提供错误反馈，主列表保留同账号的完整离线缓存。 */ }
    };
    void listen('member-content-changed', () => void refresh()).then(fn => { if (disposed) fn(); else { stop = fn; void refresh(); } });
    return () => { disposed = true; sequence.current++; stop?.(); };
  }, [account, allowed, status.offline]);
  return allowed && loaded?.account === account ? loaded.view : null;
}
