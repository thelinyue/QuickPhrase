import { useEffect, useRef, useState } from 'react';
import { call, type SettingsSnapshot } from './api';
import type { Settings } from './types';

/** 每个字段仅有一个未完成修改；串行提交不同字段，不锁住整页，也不拿旧快照覆盖其他窗口。 */
export function useSettingsSave(value: Settings, libraryId?: string, settingsRevision = 0) {
  const generation = useRef(0); const revision = useRef(settingsRevision);
  const latest = useRef(value); const incoming = useRef(value); const owner = useRef(libraryId); const alive = useRef(true);
  const pending = useRef<Partial<Settings>>({}); const queue = useRef(Promise.resolve());
  const failed = useRef(new Set<keyof Settings>());
  const [draft, setDraft] = useState<Partial<Settings>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  if (owner.current !== libraryId) { owner.current = libraryId; ++generation.current; pending.current = {}; failed.current.clear(); latest.current = value; revision.current = settingsRevision; }
  if (incoming.current !== value) { incoming.current = value; if (settingsRevision >= revision.current) { latest.current = value; revision.current = settingsRevision; } }
  useEffect(() => { setDraft({}); setErrors({}); }, [libraryId]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  function change(patch: Partial<Settings>): Promise<boolean> {
    const keys = Object.keys(patch) as (keyof Settings)[];
    if (keys.some(key => key in pending.current)) return Promise.resolve(false);
    const account = libraryId, version = generation.current;
    pending.current = { ...pending.current, ...patch }; setDraft({ ...pending.current });
    keys.forEach(key => failed.current.delete(key));
    setErrors(current => Object.fromEntries(Object.entries(current).filter(([key]) => !keys.includes(key as keyof Settings))));
    let resolve!: (ok: boolean) => void; const result = new Promise<boolean>(done => { resolve = done; });
    queue.current = queue.current.then(async () => {
      if (!alive.current || (owner.current !== account || generation.current !== version)) { resolve(false); return; }
      let ok = false;
      try {
        const snapshot = await call<SettingsSnapshot>('patch_settings', { patch, libraryId: account });
        if ((owner.current === account && generation.current === version)) { if (snapshot.revision >= revision.current) { latest.current = snapshot.settings; revision.current = snapshot.revision; } ok = true; }
      } catch (error) {
        if (alive.current && (owner.current === account && generation.current === version)) { keys.forEach(key => failed.current.add(key)); setErrors(current => ({ ...current, ...Object.fromEntries(keys.map(key => [key, String(error)])) })); }
      } finally {
        if (alive.current && (owner.current === account && generation.current === version)) {
          keys.forEach(key => { delete pending.current[key]; }); setDraft({ ...pending.current });
        }
        resolve(ok);
      }
    });
    return result;
  }
  /** 关闭窗口前等完同一账号的队列；等待期间新增的修改也要保存，失败不自动重试。 */
  async function flush() {
    const version = generation.current;
    let current: Promise<void>;
    do { current = queue.current; await current; if (!alive.current || version !== generation.current) return false; } while (current !== queue.current);
    return failed.current.size === 0;
  }
  const isSaving = () => Object.keys(pending.current).length > 0;
  return { settings: { ...latest.current, ...draft }, change, errors, busy: (...keys: (keyof Settings)[]) => keys.some(key => key in draft), pending: Object.keys(draft).length > 0, isSaving, flush };
}
