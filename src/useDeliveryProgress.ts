import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';

export interface DeliveryProgress { operation: number; sequence: number; recipient: 'main' | 'common-dock'; waiting: boolean }

/** 只接受新操作或该操作更新的序号；迟到的等待事件不能让旧提示重新出现。 */
export function useDeliveryProgress(window: 'main' | 'common-dock') {
  const latest = useRef({ operation: 0, sequence: 0 });
  const [waiting, setWaiting] = useState<DeliveryProgress | null>(null);
  useEffect(() => {
    let disposed = false; let stop: (() => void) | undefined;
    void listen<DeliveryProgress>('delivery-progress', event => {
      const next = event.payload, previous = latest.current;
      if (next.operation < previous.operation || (next.operation === previous.operation && next.sequence <= previous.sequence)) return;
      latest.current = { operation: next.operation, sequence: next.sequence };
      setWaiting(next.waiting && next.recipient === window ? next : null);
    }).then(fn => { if (disposed) fn(); else stop = fn; }).catch(() => {});
    return () => { disposed = true; stop?.(); };
  }, [window]);
  return { waiting, dismissWaiting: () => setWaiting(null) };
}
