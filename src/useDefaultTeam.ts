import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { call } from './api';
import type { TeamStatus } from './types';
/** 偏好请求和回执绑定账号；退出登录立即隐藏旧账号选择。 */
export function useDefaultTeam(status: TeamStatus) {
  const account = JSON.stringify([status.server, status.user?.id, status.sessionEpoch]);
  const sequence = useRef(0);
  const current = useRef(account); current.current = account;
  const [snapshot, setSnapshot] = useState({ account: '', id: '' });
  const [error, setError] = useState('');
  const [pending, setPending] = useState<{ account: string; id: string } | null>(null);
  const pendingRef = useRef<typeof pending>(null);
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined; setError('');
    const read = async () => { const request = ++sequence.current; try { const id = await call<string>('get_default_team'); if (!disposed && request === sequence.current) setSnapshot({ account, id }); } catch (e) { if (!disposed) setError(String(e)); } };
    void listen('default-team-changed', () => void read()).then(fn => { if (disposed) fn(); else { stop = fn; void read(); } });
    return () => { disposed = true; stop?.(); };
  }, [account]);
  async function save(id: string) {
    if (pendingRef.current?.account === account) return;
    const operation = { account, id }; pendingRef.current = operation; setPending(operation); setError('');
    ++sequence.current;
    try {
      await call('set_default_team', { server: status.server, userId: status.user?.id || '', teamId: id });
      if (current.current === account) setSnapshot({ account, id });
    } catch (e) { if (current.current === account) setError(String(e)); }
    finally { if (pendingRef.current === operation) { pendingRef.current = null; setPending(null); } }
  }
  return { id: pending?.account === account ? pending.id : snapshot.account === account ? snapshot.id : '', save, error, saving: pending?.account === account };
}
