import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { call } from './api';
import { IconButton, Button, DialogHeader, DialogWindowClose, FormDialog } from './ui';
import { useModalClose } from './close';

/** 密码只存在表单内；生成使用系统安全随机数，关闭弹窗即销毁输入。 */
export function PasswordField({ label, name, current = false, autoFocus = false, generate = false }: { label: string; name: string; current?: boolean; autoFocus?: boolean; generate?: boolean }) {
  const [shown, setShown] = useState(false); const id = useId(); const input = useRef<HTMLInputElement>(null);
  function generatePassword() {
    // 64个字符对应随机字节的低6位，不使用Math.random，也不把密码写入状态、日志或剪贴板。
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
    if (input.current) input.current.value = Array.from(crypto.getRandomValues(new Uint8Array(8)), byte => alphabet[byte & 63]).join('');
    setShown(true); input.current?.focus();
  }
  return <div className="password-field"><div className="password-label"><label htmlFor={id}>{label}</label>{generate && <Button variant="text" type="button" onClick={generatePassword}>生成密码</Button>}</div><span className="password-control"><input ref={input} id={id} name={name} type={shown ? 'text' : 'password'} required minLength={current ? undefined : 8} autoComplete={current ? 'current-password' : 'new-password'} autoFocus={autoFocus} /><Button variant="ghost" type="button" aria-label={`${shown ? '隐藏' : '显示'}${label}`} aria-pressed={shown} onClick={() => setShown(value => !value)}>{shown ? '隐藏' : '显示'}</Button></span></div>;
}

export function PasswordDialog({ first = false, close, changed, windowClose = false }: { first?: boolean; close: () => void; changed: () => void; windowClose?: boolean }) {
  return <FormDialog close={close} windowClose={windowClose} spec={{ title: first ? '修改初始密码' : '修改密码', submit: '修改', className: 'account-dialog', fields: <><PasswordField label="当前密码" name="current" current autoFocus /><PasswordField label="新密码" name="password" /><PasswordField label="确认新密码" name="confirmation" /><p className="account-hint">新密码至少8位。</p></>, onSubmit: async form => {
    if (form.get('password') !== form.get('confirmation')) throw new Error('两次新密码不一致。');
    await call('team_change_password', { request: { currentPassword: String(form.get('current')), password: String(form.get('password')) } }); changed();
  } }} />;
}

/** 同步预览、冲突及设置共享滚动正文和固定操作区，不挤占账号页。 */
export function AccountModal({ title, children, footer, busy, dirty = false, close }: { title: string; children: ReactNode; footer: (requestClose: () => void) => ReactNode; busy: boolean; dirty?: boolean; close: () => void }) {
  const modal = useModalClose({ close, busy: () => busy, dirty: () => dirty }); const id = useId();
  // 预览到达时仍在读取状态，控件可能暂时禁用；就绪后将焦点交给首个可操作字段或按钮。
  useEffect(() => { if (!busy && document.activeElement === modal.ref.current) modal.ref.current?.querySelector<HTMLElement>('.account-panel-body input:not(:disabled),.account-panel-body select:not(:disabled),.dialog-actions button:not(:disabled)')?.focus(); }, [busy]);
  return <dialog ref={modal.ref} className="small-dialog account-dialog account-panel" aria-labelledby={id} onCancel={modal.onCancel} onKeyDown={modal.onKeyDown}>{modal.windowClose && <DialogWindowClose busy={!!busy} />}<DialogHeader title={title} id={id} busy={busy} close={() => void modal.requestClose()} /><div className="account-panel-body"><fieldset className="dialog-fields" disabled={busy}>{children}{modal.error && <p className="validation-error" role="alert">{modal.error}</p>}</fieldset></div><div className="dialog-actions">{footer(() => void modal.requestClose())}</div></dialog>;
}

/** 成功提示短暂显示；失败保留供核对，不能把成功文案渲染成红色错误。 */
export function AccountNotice({ message, error, clear, count = 1 }: { message: string; error: boolean; clear: () => void; count?: number }) {
  const clearRef = useRef(clear); clearRef.current = clear;
  const [hovered, setHovered] = useState(false); const [focused, setFocused] = useState(false);
  useEffect(() => { if (!message || error || hovered || focused) return; const timer = window.setTimeout(() => clearRef.current(), 3500); return () => window.clearTimeout(timer); }, [message, error, hovered, focused]);
  return message ? <div className={`account-notice${error ? ' error' : ''}`} role={error ? 'alert' : 'status'} onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)} onFocusCapture={() => setFocused(true)} onBlurCapture={e => { if (!e.currentTarget.contains(e.relatedTarget)) setFocused(false); }}><span>{message}{count > 1 && <small>　还有 {count - 1} 条提示</small>}</span><IconButton size="compact" aria-label="关闭提示" title="关闭提示" onClick={clear}>×</IconButton></div> : null;
}
