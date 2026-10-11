import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { call } from './api';
import { Icon } from './ui';

const modifiers = ['', 'Ctrl', 'Shift', 'Alt'];
/** 编辑器与列表共用快捷键录入。确认只提交当前草稿，取消（包括删除后的取消）不改变原绑定。 */
export function ShortcutDialog({ id, value, onConfirm, close }: { id: string; value: string; onConfirm: (shortcut: string) => Promise<void>; close: () => void }) {
  const parts = value.split('+'); const keys = parts.filter(p => /^[A-Z0-9]$|^F(?:[1-9]|1[0-9]|2[0-4])$/i.test(p));
  const [modifier, setModifier] = useState(parts.filter(p => p && !keys.includes(p)).join('+'));
  const [first, setFirst] = useState(keys[0] || ''); const [second, setSecond] = useState(keys[1] || '');
  const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const busyRef = useRef(false);
  const [ready, setReady] = useState(false); const [tutorial, setTutorial] = useState(false);
  const dialog = useRef<HTMLDialogElement>(null); const inputs = useRef<(HTMLInputElement | null)[]>([]);
  useEffect(() => {
    dialog.current?.showModal(); let disposed = false;
    // 原生监听暂停后才允许录入，避免已保存的快捷键抢走正在设置的按键。
    void call('set_shortcut_input', { active: true }).then(() => { if (!disposed) { setReady(true); requestAnimationFrame(() => inputs.current[0]?.focus()); } }).catch(e => { if (!disposed) setError(String(e)); });
    return () => { disposed = true; void call('set_shortcut_input', { active: false }).catch(() => {}); };
  }, []);
  function record(event: KeyboardEvent<HTMLInputElement>, index: number) {
    if (event.key === 'Tab' || event.key === 'Escape') return;
    event.preventDefault(); event.stopPropagation();
    if (event.repeat || event.nativeEvent.isComposing || ['Control', 'Shift', 'Alt', 'Meta'].includes(event.key)) return;
    if (event.key === 'Backspace' || event.key === 'Delete') { (index ? setSecond : setFirst)(''); setError(''); return; }
    const key = /^(Key[A-Z]|Digit[0-9])$/.test(event.code) ? event.code.replace(/^(Key|Digit)/, '') : event.key.toUpperCase();
    if (!/^[A-Z0-9]$|^F(?:[1-9]|1[0-9]|2[0-4])$/.test(key)) { setError('请直接按字母、数字或F1—F24。'); return; }
    const held = [[event.ctrlKey, 'Ctrl'], [event.altKey, 'Alt'], [event.shiftKey, 'Shift'], [event.metaKey, 'Win']].filter(([active]) => active).map(([, name]) => name).join('+');
    if (held) setModifier(held);
    (index ? setSecond : setFirst)(key); setError(''); if (!index) inputs.current[1]?.focus();
  }
  async function submit(event: FormEvent) {
    event.preventDefault(); if (busyRef.current || !ready) return;
    if ((!first && second) || (first && !second && !modifier)) { setError('请选择第一个按键；无功能键时需要两个按键。'); return; }
    const shortcut = first ? [modifier, first, second].filter(Boolean).join('+') : '';
    busyRef.current = true; setBusy(true);
    try { const canonical = await call<string>('validate_phrase_shortcut', { id, shortcut }); await onConfirm(canonical); close(); }
    catch (e) { setError(String(e)); } finally { busyRef.current = false; setBusy(false); }
  }
  return <dialog ref={dialog} className="shortcut-dialog" aria-labelledby="shortcut-title" onCancel={e => { e.preventDefault(); if (!busyRef.current) close(); }}><form onSubmit={submit}>
    <header className="standard-titlebar"><img className="app-symbol" src="/shanyu.png" alt="" draggable={false} /><span id="shortcut-title">设置自定义快捷键</span><span className="titlebar-spacer" /><button type="button" aria-label="关闭快捷键设置" disabled={busy} onClick={close}><Icon name="close" /></button></header>
    <div className="shortcut-body"><div className="shortcut-fields"><select aria-label="功能键" value={modifier} disabled={!ready || busy} onChange={e => { setModifier(e.target.value); setError(''); }}>{modifiers.map(m => <option key={m} value={m}>{m || '功能键（无）'}</option>)}{modifier && !modifiers.includes(modifier) && <option value={modifier}>{modifier}</option>}</select>
      {[first, second].map((key, index) => <input key={index} type="text" ref={el => { inputs.current[index] = el; }} aria-label={index ? '第二个按键' : '第一个按键'} value={key} readOnly disabled={!ready || busy} onKeyDown={event => record(event, index)} />)}
      <button type="button" className="delete-shortcut" title="删除话术快捷键" aria-label="删除话术快捷键" disabled={!ready || busy} onClick={() => { setModifier(''); setFirst(''); setSecond(''); setError(''); }}><Icon name="trash" /></button></div>
      <p className="shortcut-hint">点击按键框，直接按键录入</p>{error && <p className="validation-error" role="alert">{error}</p>}
      <div className="shortcut-footer"><button type="button" className="shortcut-tutorial" aria-label="自定义快捷键使用教程" aria-expanded={tutorial} onClick={() => setTutorial(v => !v)}><span aria-hidden="true">▶</span>使用说明</button><button type="button" className="secondary-button" disabled={busy} onClick={close}>取消</button><button type="submit" className="primary-button" disabled={!ready || busy}>{busy ? '保存中' : '确定'}</button></div>
      {tutorial && <div className="shortcut-help"><p>单组合键只填第一键；顺序双键填两键。</p>
        {/* 按住修饰键与依次按下普通键分别展示，避免把顺序双键误读为同时按下。 */}
        <div className="shortcut-examples"><figure><figcaption>Ctrl+Y+C</figcaption><div className="shortcut-sequence"><div><kbd>Ctrl</kbd><small>按住</small></div><span aria-hidden="true">＋</span><div><kbd>Y</kbd><small>按下 · 松开</small></div><span aria-hidden="true">→</span><div><kbd>C</kbd><small>按下 · 松开</small></div></div></figure>
          <figure><figcaption>1+1（无功能键）</figcaption><div className="shortcut-sequence"><div><kbd>1</kbd><small>按下 · 松开</small></div><span aria-hidden="true">→</span><div><kbd>1</kbd><small>再按一次</small></div></div></figure></div>
        <p className="shortcut-timing">两键间隔 ≤ 1 秒；匹配后的候选选择不限时。</p><p>同键可绑定多条话术，单条也显示候选。上下键选择，数字 1—9、0 或空格插入，Enter 直接发送，Esc 取消。</p><p>默认在闪语或目标窗口生效。搜索允许序号组合键；编辑、录入和模态输入暂停业务快捷键。</p><p>未完成时还原首键；切换窗口、输入区或点击候选外部即取消。</p><p>设置或删除后点“确定”；编辑器内还需保存话术。</p><button type="button" className="shortcut-tutorial" onClick={() => void call('open_shortcut_tutorial').catch(e => setError(String(e)))}>打开视频教程</button>
      </div>}
    </div></form></dialog>;
}
