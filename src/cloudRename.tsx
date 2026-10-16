import { call } from './api';
import type { CompanyFile } from './types';
import type { DialogSpec } from './ui';

export function cloudNameError(name: string): string {
  const stem = name.split('.')[0].trimEnd();
  return !name.trim() || name.length > 255 || name !== name.trim() || /[<>:"/\\|?*\u0000-\u001f\u007f]/.test(name) || name.endsWith('.') || /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(stem) ? '文件名无效，请勿使用特殊字符、保留名称或末尾空格和句点。' : '';
}
/** 公共名称与本机关联显示名分离；扩展名固定，冲突交给 FormDialog 保留输入草稿。 */
export function cloudRenameDialog(file: CompanyFile, epoch: number, saved: () => Promise<unknown>): DialogSpec {
  const dot = file.name.lastIndexOf('.'), extension = dot > 0 ? file.name.slice(dot) : '', stem = extension ? file.name.slice(0, dot) : file.name;
  return { title: '重命名云文件', submit: '保存名称', fields: <><label>文件名<input name="stem" aria-label="文件名主体" defaultValue={stem} maxLength={255 - extension.length} required autoFocus onFocus={e => e.currentTarget.select()} /></label><p>扩展名：{extension || '无'}。修改后其他用户将看到新名称。</p></>, onSubmit: async form => { const name = String(form.get('stem')) + extension, error = cloudNameError(name); if (error) throw new Error(error); await call('rename_company_file', { sessionEpoch: epoch, id: file.assetId, expectedName: file.name, name }); await saved(); } };
}
