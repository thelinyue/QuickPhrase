export type Block = { kind: 'text'; text: string } | { kind: 'image' | 'file'; path: string; name: string };
export interface Segment { blocks: Block[] }
export interface Phrase { id: string; rootId: string; groupId: string | null; title: string; color: string; background: string; shortcut: string; position: number; segments: Segment[] }
export interface Category { id: string; name: string; kind: 'phrase' | 'file'; parentId: string | null; position: number; background: string }
/** 选择器按同级列表顺序区分重名分类；显示序号不写入名称，操作仍提交独立 ID。 */
export function categoryLabel(category: Category, categories: Category[]) {
  const matches = categories.filter(c => c.kind === category.kind && c.parentId === category.parentId && c.name.toLowerCase() === category.name.toLowerCase());
  return matches.length > 1 ? `${category.name}（同名 ${matches.findIndex(c => c.id === category.id) + 1}）` : category.name;
}
export interface LinkedFile { id: string; categoryId: string; path: string; name: string; position: number; missing: boolean }
export interface CommonPhrase { id: string; text: string; position: number }
export interface RecycleItem { id: string; kind: 'phrase' | 'primary-category' | 'secondary-category'; label: string; location: string; deletedAt: number; phraseCount: number }
export interface RecycleRecord extends Omit<RecycleItem, 'phraseCount'> { categories: Category[]; phrases: Phrase[] }
export interface Settings { startup: boolean; tray: boolean; enter: 'enter' | 'ctrl-enter'; interval: number; exitSearch: boolean; autoHide: boolean; hideDelay: number; showNumbers: boolean; showPreview: boolean; showTitle: boolean; showSearch: boolean; showCommon: boolean; dockCommon: boolean; fontSize: number; expand: boolean; dock: boolean; dockSide: 'left' | 'right'; searchMode: 'fuzzy' | 'exact'; searchHotkey: string; windowHotkey: string; commonHotkey: string; customHotkeys: boolean; sequenceHotkeys: boolean; sequenceDirectSend: boolean; sequenceBindings: string[]; globalPhraseHotkeys: boolean; pinned: boolean; categoryHeight: number; phraseDoubleClick: 'insert' | 'send'; commonSingleClick: 'select' | 'insert' | 'send'; dockSearch: boolean; freeResize: boolean; rememberDock: boolean; theme: 'default' | 'green' | 'blue' | 'black' | 'pink' | 'lavender' | 'orange'; titleColor: string; phraseCategoryColor: string; fileCategoryColor: string; filesFirst: boolean; fileCategoryHeight: number; commonHeight: number; dockCommonRows: number }
export interface Library { categories: Category[]; phrases: Phrase[]; files: LinkedFile[]; commonPhrases: CommonPhrase[]; settings: Settings; dataDir: string }
export interface SearchHit { id: string; kind: 'phrase' | 'file'; phrase: Phrase | null; file: LinkedFile | null }
export interface TargetContext { hwnd: number; focus: number; pid: number }
export interface ShortcutCandidates { shortcut: string; phrases: Phrase[]; selectedIndex: number; target: TargetContext }
export interface DeliveryReport { status: 'submitted' | 'interrupted' | 'failed'; completedSegments: number; completedBlocks: number; error: string | null }
export interface ImportPreview { id: string; kind: 'excel' | 'backup'; filename: string; categories: number; phrases: number; commonPhrases: number; files: number; images: number; recycled: number; rows: { category: string; title: string; text: string }[]; notes: string[] }
export const textOf = (phrase: Phrase) => phrase.segments.map(s => s.blocks.filter((b): b is Extract<Block, { kind: 'text' }> => b.kind === 'text').map(b => b.text).join('')).join('\n');
export const labelOf = (phrase: Phrase) => phrase.title || textOf(phrase).trim().slice(0, 35) || phrase.segments.flatMap(s => s.blocks).filter(b => b.kind !== 'text').map(b => `${b.kind === 'image' ? '图片' : '文件'}：${b.name}`).join(' · ') || '附件话术';
export const localDate = () => new Intl.DateTimeFormat('sv-SE', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
