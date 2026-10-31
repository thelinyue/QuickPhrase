export type Block = { kind: 'text'; text: string } | { kind: 'image' | 'file'; path: string; name: string; companyAssetId?: string; companySessionEpoch?: number };
export interface Segment { blocks: Block[] }
export type ContentRef = { source: 'team'; teamId: string; id: string; revision: number; lifecycleVersion: number } | { source: 'private'; libraryId: string; id: string; revision: number; teamId?: never; lifecycleVersion?: never } | { source: 'member'; grantId: string; grantVersion: number; subscriptionVersion: number; generation: string; id: string; revision: number; teamId?: never; lifecycleVersion?: never };
export const contentKey = (p: Phrase) => p.contentRef?.source === 'private' ? `private:${p.contentRef.libraryId}:phrase:${p.contentRef.id}:${p.contentRef.revision}` : p.id;
export interface Phrase { id: string; rootId: string; groupId: string | null; title: string; color: string; background: string; shortcut: string; position: number; segments: Segment[]; contentRef?: ContentRef; ready?: boolean }
export interface Category { id: string; name: string; kind: 'phrase' | 'file'; parentId: string | null; position: number; background: string; readOnly?: boolean; sourceLabel?: string }
/** 选择器按同级列表顺序区分重名分类；显示序号不写入名称，操作仍提交独立 ID。 */
export function categoryLabel(category: Category, categories: Category[]) {
  const matches = categories.filter(c => c.kind === category.kind && c.parentId === category.parentId && c.name.toLowerCase() === category.name.toLowerCase());
  return matches.length > 1 ? `${category.name}（同名 ${matches.findIndex(c => c.id === category.id) + 1}）` : category.name;
}
export interface LinkedFile { id: string; categoryId: string; path: string; name: string; position: number; missing: boolean; cloud?: { server: string; userId: string; assetId: string } | null; customName?: boolean; canRename?: boolean; sourceName?: string; status?: string }
export interface CommonPhrase { id: string; text: string; position: number }
export interface RecycleItem { id: string; kind: 'phrase' | 'primary-category' | 'secondary-category'; label: string; location: string; deletedAt: number; phraseCount: number }
export interface RecycleRecord extends Omit<RecycleItem, 'phraseCount'> { categories: Category[]; phrases: Phrase[] }
export interface Settings { startup: boolean; tray: boolean; showFloatingOnMinimize: boolean; enter: 'enter' | 'ctrl-enter'; interval: number; exitSearch: boolean; autoHide: boolean; hideDelay: number; edgeAutoHide: boolean; edgeRevealDelay: 0 | 500 | 1000; showNumbers: boolean; showPreview: boolean; showTitle: boolean; showSearch: boolean; showCommon: boolean; dockCommon: boolean; fontSize: number; expand: boolean; dock: boolean; dockSide: 'left' | 'right'; searchMode: 'fuzzy' | 'exact'; searchHotkey: string; windowHotkey: string; commonHotkey: string; customHotkeys: boolean; sequenceHotkeys: boolean; sequenceDirectSend: boolean; sequenceBindings: string[]; globalPhraseHotkeys: boolean; pinned: boolean; categoryHeight: number; phraseDoubleClick: 'insert' | 'send'; commonSingleClick: 'select' | 'insert' | 'send'; dockSearch: boolean; freeResize: boolean; rememberDock: boolean; theme: 'default' | 'green' | 'blue' | 'black' | 'pink' | 'lavender' | 'orange'; titleColor: string; phraseCategoryColor: string; fileCategoryColor: string; filesFirst: boolean; teamsFirst: boolean; fileCategoryHeight: number; commonHeight: number; dockCommonRows: number }
export interface Library { settingsRevision?: number; categories: Category[]; phrases: Phrase[]; files: LinkedFile[]; commonPhrases: CommonPhrase[]; settings: Settings; dataDir: string; libraryId?: string; privateRevisions?: Record<string, number>; privateReady?: Record<string, boolean>; ownerLabel?: string }
export interface SearchHit { id: string; kind: 'phrase' | 'file'; phrase: Phrase | null; file: LinkedFile | null; sourceLabel?: string; teamId?: string; privateKey?: string }
export type TeamRole = 'member' | 'editor' | 'manager';
export const teamRoleLabel = (role: TeamRole) => role === 'manager' ? '库管理员' : role === 'editor' ? '编辑者' : '查看者';
export interface CompanyFilePermissions { canUpload: boolean; canRecycle: boolean; canRestore: boolean; version: number; uploadVersion: number }
export interface CloudCommandPermissions { canUse: boolean; canManage: boolean; version: number }
export interface CloudCommandSummary { id: string; name: string; category: string; description: string; revision: number; updatedAt: number; maintainedBy: string; maintainerName: string; deletedAt: number | null }
export interface CloudCommand extends CloudCommandSummary { body: string }
export interface CloudCommandsView { commands: CloudCommandSummary[]; total: number; categories: string[] }
export interface CloudCommandDraft { name: string; category: string; description: string; body: string; expectedRevision: number | null }
export interface TeamUser { id: string; username: string; admin: boolean; disabled: boolean; mustChangePassword: boolean; displayName: string; contact: string; canCreateTeams: boolean; companyFilePermissions?: CompanyFilePermissions; cloudCommandPermissions?: CloudCommandPermissions; memberships?: { teamId: string; name: string; role: TeamRole }[] }
export interface TeamInfo { teamId: string; name: string; version: number; editable: boolean; role: TeamRole; membershipVersion: number; lifecycleVersion: number; memberCount?: number }
export interface TeamStatus { sessionEpoch?: number; server: string; username: string; user: TeamUser | null; teams: TeamInfo[]; teamSyncTimes?: Record<string, number>; offline: boolean; privateSync?: boolean; memberPhraseSync?: boolean; companyFiles?: boolean; companyFilePermissions?: boolean; cloudCommands?: boolean }
export interface MemberSubscription { grantId: string | null; version: number }
export interface MemberGrant { id: string; owner: TeamMemberIdentity; enabled: boolean; mode: 'all' | 'selected'; rootIds: string[]; roots: Category[]; version: number }
export interface MemberOverview { sharing: MemberGrant | null; users: TeamMemberIdentity[]; offline?: boolean; available: MemberGrant[]; subscription: MemberSubscription; categories: Category[]; sourceReady: boolean }
export interface MemberView { categories: Category[]; phrases: Phrase[]; subscription: MemberSubscription; ownerLabel: string; state: string; message: string; lastSuccessfulSyncAt: number | null }
export interface CompanyFile { assetId: string; name: string; size: number; uploadedBy: string; uploaderName: string; uploaderUsername?: string; createdAt: number; deletedAt: number | null; deletedBy?: string | null; deleterUsername?: string; cached: boolean }
export interface CompanyUpload { id: string; name: string; size: number }
export interface CompanyView { version: number; files: CompanyFile[]; uploads: CompanyUpload[]; offline?: boolean }
export interface CompanyTransfer { sessionEpoch: number; assetId: string; name: string; direction: 'upload' | 'download'; done: number; total: number; status: string; revision: number; message?: string }
export interface TeamView { team: TeamInfo; categories: Category[]; phrases: Phrase[]; offline: boolean }
export interface TeamMembership { userId: string; teamId: string; role: TeamRole }
export interface TeamMemberIdentity { userId: string; username: string; displayName: string; disabled: boolean }
export interface TeamMember extends TeamMembership, TeamMemberIdentity {}
export interface TeamMembers { membershipVersion: number; lifecycleVersion: number; memberCount: number; phraseCount: number; members: TeamMember[] }
export interface DeletedTeam { teamId: string; name: string; lifecycleVersion: number; deletedAt: number; deletedBy: string; memberCount: number; phraseCount: number }
export interface TeamAssetCleanup { teamId: string; name: string; pendingAssets: number }
export interface TeamRecycle { id: string; teamId: string; label: string; deletedAt: number; categories: Category[]; phrases: { title: string; segments: { blocks: ({ kind: 'text'; text: string } | { kind: 'image' | 'file'; assetId: string; name: string })[] }[] }[] }
export interface TargetContext { hwnd: number; focus: number; pid: number }
export interface ShortcutCandidates { sessionId: string; layoutKey: string; maxWidth: number; maxHeight: number; shortcut: string; phrases: Phrase[]; selectedIndex: number; target: TargetContext }
export interface DeliveryReport { status: 'submitted' | 'interrupted' | 'failed'; completedSegments: number; completedBlocks: number; error: string | null; noticeStored?: boolean }
export interface ImportPreview { id: string; kind: 'excel' | 'backup'; filename: string; categories: number; phrases: number; commonPhrases: number; files: number; images: number; recycled: number; rows: { category: string; title: string; text: string }[]; notes: string[] }
export const textOf = (phrase: Phrase) => phrase.segments.map(s => s.blocks.filter((b): b is Extract<Block, { kind: 'text' }> => b.kind === 'text').map(b => b.text).join('')).join('\n');
export const labelOf = (phrase: Phrase) => phrase.title || textOf(phrase).trim().slice(0, 35) || phrase.segments.flatMap(s => s.blocks).filter(b => b.kind !== 'text').map(b => `${b.kind === 'image' ? '图片' : '文件'}：${b.name}`).join(' · ') || '附件话术';
export const localDate = () => new Intl.DateTimeFormat('sv-SE', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());

/** 使用系统时区显示完整时间；旧库没有成功记录时不推断时间。 */
export function syncTime(value: number | null | undefined) {
  if (value == null || !Number.isFinite(value)) return '暂无成功同步记录';
  const date = new Date(value), pad = (n: number) => String(n).padStart(2, '0');
  if (Number.isNaN(date.getTime())) return '暂无成功同步记录';
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** 搜索身份不包含修订号，刷新后保留选中；执行仍使用带修订的内容引用。 */
export function searchHitKey(hit: SearchHit) {
  const ref = hit.phrase?.contentRef;
  return JSON.stringify([hit.kind, ref?.source || (hit.teamId ? 'team' : hit.kind === 'file' ? 'file' : 'private'), ref?.source === 'private' ? ref.libraryId : ref?.source === 'team' ? ref.teamId : ref?.source === 'member' ? ref.grantId : hit.teamId || '', ref?.id || hit.id]);
}
export const searchDeliveryKey = (hit: SearchHit) => hit.privateKey || (hit.phrase ? contentKey(hit.phrase) : hit.id);
