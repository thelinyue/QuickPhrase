'use strict';

// 独立界面评审稿：样例只保存在内存，发送动作不会触达外部程序。
// 主列表与搜索共用行组件，便于核对悬停发送、双击插入及正确的行身份。
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const icon = (name) => '<svg aria-hidden="true"><use href="#i-' + name + '"></use></svg>';
const colors = [
  ['#ff5100', '#fff', '默认颜色'], ['#ff8842', '#ff8842', '橙色'], ['#1685ff', '#1685ff', '蓝色'],
  ['#ff76ed', '#ff76ed', '粉色'], ['#ae61ff', '#ae61ff', '紫色'], ['#35bb25', '#35bb25', '绿色'],
  ['#f47b93', '#f47b93', '浅红色'], ['#00afb0', '#00afb0', '青色'], ['#ca9663', '#ca9663', '棕色'], ['#5c6770', '#5c6770', '灰色'],
];
const settingsTabs = ['常用设置', '界面设置', '吸附设置', '导入数据', '导出数据', '快捷键', '缓存管理', '关于'];
const captions = {
  main: '主窗口 · 330 × 720 DIP', hover: '话术行悬停 · 左侧直接发送',
  search: '搜索浮层 · 固定在底部搜索框上方', context: '话术右键菜单',
  editor: '新增／编辑话术 · 四按钮编辑器', files: '独立本地文件库',
  'file-editor': '新增文件 · 分类与批量选择', settings: '设置窗口 · 独立窗口',
  empty: '空分类 · 保留新增入口', error: '文件失效 · 保留关联记录',
};
let state, draft, deliveryTimer, toastTimer;
let selectedFiles = [], activeSegment = 0, activeBlock = 0, deliveryBusy = false, serial = 100;
const events = [];

function sampleState() {
  const rows = [
    ['PRO-远程ssh', '您好，这边判断您的问题需要远程协助，请保持设备联网。', 'g-pro', 'Alt+1'],
    ['PRO-设备日志', '辛苦您提供下设备日志，我们会根据日志进一步判断。', 'g-pro', ''],
    ['PRO-WIN-播放器日志', '您好，麻烦您提供下播放器的日志文件。', 'g-pro', ''],
    ['PRO-移动端-设备日志', '辛苦您提供下客户端中的设备日志。', 'g-pro', ''],
    ['PRO-WIN-日志', '您电脑访问【%appdata%】后，请进入日志目录。', 'g-pro', ''],
    ['PRO-MAC-日志', 'mac端日志访问方法：按住 option 后打开资源库。', 'g-pro', ''],
    ['PRO-重置-网络', '可以通过按机器背面的 reset 按钮重置网络设置。', 'g-pro', ''],
    ['解散群聊', '您好，这边群聊问题已经解决，后续有需要请联系我们。', 'g-pro', 'Alt+2'],
    ['ugos-局域网-ssh', '这边有电脑可以远程协助，请确认电脑与设备在同一网络。', 'g-ugos', 'Ctrl+Alt+1'],
    ['UGOS-重置-系统', '您好，建议您尝试恢复系统设置，操作前请确认相关设置。', 'g-ugos', ''],
    ['UGOS-设备日志', '您好，您出现的情况需要进一步分析，辛苦提供设备日志。', 'g-ugos', ''],
    ['UGOS-WIN日志', '您电脑访问【%appdata%】后，进入客户端目录。', 'g-ugos', ''],
    ['解散群聊', '您好，这边群聊问题已经解决，后续有需要请联系我们。', 'g-ugos', ''],
    ['远程前', '这边需要创建一个临时账号排查，请您确认授权范围。', 'g-general', ''],
    ['远程后', '临时账号记得删除，远程访问和 ssh 服务可以关闭。', 'g-general', ''],
    ['联系方式', '方便留一个联系方式吗？这边为您继续跟进。', 'g-general', ''],
    ['工单号', '这个是工单号，后续也可以凭借工单号查询进度。', 'g-general', ''],
    ['日志生成', '您好，生成的日志文件不完整，请重新导出设备日志。', 'g-general', ''],
    ['售后SSH', '感谢您使用我们的服务！请注意保管自己的设备账号。', 'g-general', ''],
    ['docker-技术支持', '您好，感谢您的反馈！我们会根据您提供的信息进一步确认。', 'g-general', ''],
    ['docker容器使用', '您好！我们提供的是 Docker 容器管理能力。', 'g-general', ''],
    ['OS支持UPS型号', '您好，私有云产品在停电时可通过 UPS 保持供电。', 'g-ups', ''],
    ['pro-ups型号', '您好！关于 NAS 产品在断电情况下的 UPS 使用，建议查看支持列表。', 'g-ups', ''],
    ['OS如何连接UPS', '您好，将您绿联 NAS 私有云与 UPS 通过 USB 连接即可。', 'g-ups', ''],
    ['OS管理员权限', '您好，管理员可以修改 UPS 设置并查看当前连接状态。', 'g-ups', ''],
    ['OS恢复出厂设置', '您好，可以在系统设置中恢复配置，UPS 相关设置也会重置。', 'g-ups', ''],
    ['UGOS-重置系统', '您好，尝试重新连接 UPS 后再确认当前系统状态。', 'g-ups', ''],
  ];
  return {
    library: 'phrases', rootId: 'c-common', fileCategoryId: 'f-11', selectedId: 'p-9', searchIndex: 0,
    query: '', scene: 'main', composing: false,
    roots: [{ id: 'c-common', name: '常用' }, { id: 'c-pro', name: 'PRO' }],
    groups: [{ id: 'g-pro', rootId: 'c-common', name: 'PRO' }, { id: 'g-ugos', rootId: 'c-common', name: 'UGOS' }, { id: 'g-general', rootId: 'c-common', name: '通用' }, { id: 'g-ups', rootId: 'c-pro', name: 'UPS' }],
    collapsed: new Set(), fileCategories: [{ id: 'f-11', name: '11' }],
    phrases: rows.map((r, i) => ({ id: 'p-' + (i + 1), title: r[0], groupId: r[2], rootId: i < 21 ? 'c-common' : 'c-pro', color: '#ff5100', background: '', shortcut: r[3], segments: [{ blocks: [{ kind: 'text', text: r[1] }] }] })),
    files: [{ id: 'file-1', name: 'download.svg', categoryId: 'f-11', missing: false }],
    settings: { startup: false, tray: true, enter: 'enter', exitSearch: true, autoHide: true, hideDelay: '500', interval: '500', fontSize: 12, showNumbers: true, showPreview: true, expand: true, dock: true, dockSide: 'right', searchMode: 'fuzzy', searchHotkey: 'Q', windowHotkey: 'W', commonHotkey: 'E', customHotkeys: true, globalPhraseHotkeys: false },
    settingsTab: '常用设置',
  };
}
function textOf(p) { return p.segments.map((s) => s.blocks.filter((b) => b.kind === 'text').map((b) => b.text).join('')).join('\n'); }
function labelOf(p) { return p.title || textOf(p).trim().slice(0, 35) || '附件话术'; }
function record(action, message, itemId = null) {
  events.push({ action, message, itemId, time: new Date().toISOString() });
  const li = document.createElement('li'), time = document.createElement('time');
  time.textContent = new Date().toLocaleTimeString('zh-CN', { hour12: false });
  li.append(time, document.createTextNode(message)); $('#action-log').prepend(li);
  while ($('#action-log').children.length > 12) $('#action-log').lastElementChild.remove();
}
function toast(message) {
  clearTimeout(toastTimer); $('#review-toast').textContent = message; $('#review-toast').hidden = false;
  toastTimer = setTimeout(() => { $('#review-toast').hidden = true; }, 2200);
}
function highlight(text) {
  const terms = state.query.trim().split(/\s+/).filter(Boolean), ranges = [], lowered = text.toLocaleLowerCase('zh-CN');
  for (const term of terms) {
    let start = lowered.indexOf(term.toLocaleLowerCase('zh-CN'));
    while (start >= 0) { ranges.push([start, start + term.length]); start = lowered.indexOf(term.toLocaleLowerCase('zh-CN'), start + term.length); }
  }
  ranges.sort((a, b) => a[0] - b[0]);
  let cursor = 0, html = '';
  for (const [start, end] of ranges) {
    if (start < cursor) continue;
    html += esc(text.slice(cursor, start)) + '<mark>' + esc(text.slice(start, end)) + '</mark>'; cursor = end;
  }
  return html + esc(text.slice(cursor));
}
function rowHtml(item, index, search = false, kind = 'phrase') {
  const file = kind === 'file', title = file ? item.name : item.title, preview = file ? '' : textOf(item).replace(/\s+/g, ' ');
  const selected = search ? index === state.searchIndex : item.id === state.selectedId;
  const shortcut = !file && item.shortcut ? '<span class="row-shortcut">' + esc(item.shortcut.replace('Ctrl+Alt+', 'Ctrl,')) + '</span>' : '';
  return '<div class="content-row' + (file ? ' file-row' : '') + (selected ? ' selected' : '') + '" data-row="' + esc(item.id) + '" data-kind="' + kind + '" data-search="' + search + '" data-index="' + index + '" role="option" aria-selected="' + selected + '" tabindex="0">' +
    '<button class="row-send" type="button" aria-label="直接发送 ' + esc(file ? item.name : labelOf(item)) + '" title="直接发送" ' + (deliveryBusy ? 'disabled' : '') + '><svg class="send-arrow" viewBox="0 0 16 16" aria-hidden="true"><path d="M14 1 2 8l12 7v-4L7 8l7-3z"></path></svg></button>' +
    '<svg class="row-icon" aria-hidden="true"><use href="#i-' + (file ? 'file' : 'document') + '"></use></svg>' +
    (state.settings.showNumbers ? '<span class="row-index">' + (index + 1) + '.</span>' : '') +
    (title ? '<span class="row-title" style="color:' + (file ? '#181818' : item.color) + ';' + (item.background ? 'background:' + item.background + ';' : '') + '">' + (search ? highlight(title) : esc(title)) + '</span>' : '') +
    (!file && state.settings.showPreview ? '<span class="row-preview">' + (search ? highlight(preview) : esc(preview)) + '</span>' : '') +
    (file && item.missing ? '<span class="row-status">文件失效</span>' : '') + shortcut + '</div>';
}
function renderMain() {
  $$('.content-tabs button').forEach((b) => b.classList.toggle('active', b.dataset.library === state.library));
  $('#library-scope').hidden = state.library === 'files';
  const cats = state.library === 'phrases' ? state.roots : state.fileCategories;
  const chosen = state.library === 'phrases' ? state.rootId : state.fileCategoryId;
  $('#category-tabs').className = 'category-tabs' + (state.library === 'files' ? ' files-tabs' : '');
  $('#category-tabs').innerHTML = cats.map((c) => '<button type="button" data-category="' + c.id + '" class="' + (c.id === chosen ? 'active' : '') + '">' + esc(c.name) + '</button>').join('') + '<button class="add-category" type="button" aria-label="新增一级分类" title="新增分类">＋</button>';
  $('#content-list').setAttribute('aria-label', state.library === 'phrases' ? '话术列表' : '本地文件列表');
  $('#quick-add').title = state.library === 'phrases' ? '新增话术' : '新增关联文件';
  $('#quick-add').setAttribute('aria-label', $('#quick-add').title);
  let html = '';
  if (state.library === 'phrases') {
    let seq = 0;
    const groups = state.groups.filter((g) => g.rootId === state.rootId);
    const ungrouped = state.phrases.filter((p) => p.rootId === state.rootId && !p.groupId);
    html += ungrouped.map((p) => rowHtml(p, seq++)).join('');
    for (const group of groups) {
      const collapsed = state.collapsed.has(group.id);
      html += '<div class="group-heading' + (collapsed ? ' collapsed' : '') + '"><button class="group-toggle" type="button" data-group="' + group.id + '" aria-expanded="' + !collapsed + '"><span class="group-arrow"></span>' + esc(group.name) + '</button><button class="group-edit" type="button" data-edit-group="' + group.id + '" aria-label="编辑分类 ' + esc(group.name) + '">' + icon('pencil') + '</button></div>';
      const phrases = state.phrases.filter((p) => p.groupId === group.id);
      if (!collapsed) html += phrases.map((p) => rowHtml(p, seq++)).join('');
      else seq += phrases.length;
    }
    if (!groups.length && !ungrouped.length) html = '<div class="empty-state">' + icon('document') + '<span>这个分类还没有话术</span><button type="button" data-empty-add>新增话术</button></div>';
  } else {
    const files = state.files.filter((f) => f.categoryId === state.fileCategoryId);
    html = files.map((f, i) => rowHtml(f, i, false, 'file')).join('');
    if (!files.length) html = '<div class="empty-state">' + icon('folder') + '<span>还没有关联文件</span><button type="button" data-empty-add>新增关联文件</button></div>';
  }
  $('#content-list').innerHTML = html;
  $('#pin-window').setAttribute('aria-pressed', String(Boolean(state.pinned)));
  $('#auto-hide').setAttribute('aria-pressed', String(state.settings.autoHide));
  document.documentElement.style.setProperty('--phrase-size', state.settings.fontSize + 'px');
  $('#search-input').value = state.query; renderSearch();
}
function matchingItems() {
  const query = state.query.trim().toLocaleLowerCase('zh-CN'); if (!query) return [];
  const terms = state.settings.searchMode === 'exact' ? [query] : query.split(/\s+/);
  const matches = (text) => terms.every((t) => text.toLocaleLowerCase('zh-CN').includes(t));
  return [...state.phrases.filter((p) => matches(p.title + ' ' + textOf(p))).map((item) => ({ item, kind: 'phrase' })), ...state.files.filter((f) => matches(f.name)).map((item) => ({ item, kind: 'file' }))];
}
function renderSearch() {
  const active = Boolean(state.query.trim());
  $('#search-backdrop').hidden = !active; $('#search-panel').hidden = !active; $('#clear-search').hidden = !state.query;
  if (!active) return;
  const matches = matchingItems(); state.searchIndex = Math.max(0, Math.min(state.searchIndex, matches.length - 1));
  let lastKind = '';
  $('#search-results').innerHTML = matches.length ? matches.map(({ item, kind }, i) => {
    const heading = lastKind === kind ? '' : '<div class="search-kind">' + (kind === 'phrase' ? '话术' : '本地文件') + '</div>';
    lastKind = kind; return heading + rowHtml(item, i, true, kind);
  }).join('') : '<div class="no-results">没有找到匹配的内容</div>';
}
function selectRow(row) {
  state.selectedId = row.dataset.row;
  if (row.dataset.search === 'true') state.searchIndex = Number(row.dataset.index);
  $$('.content-row').forEach((el) => {
    const selected = el.dataset.search === 'true' ? Number(el.dataset.index) === state.searchIndex : el.dataset.row === state.selectedId;
    el.classList.toggle('selected', selected); el.setAttribute('aria-selected', String(selected));
  });
}

// 显式使用触发行 ID，同步加锁后再提交动作，防止冒泡和连续点击重复发送。
function deliver(itemId, kind, mode) {
  if (deliveryBusy) return;
  const item = (kind === 'file' ? state.files : state.phrases).find((e) => e.id === itemId); if (!item) return;
  if (kind === 'file' && item.missing) { record('error', '文件失效：' + item.name + '；关联记录保留。', itemId); toast('文件已失效，请重新关联'); return; }
  deliveryBusy = true; $$('.row-send').forEach((b) => { b.disabled = true; });
  const label = kind === 'file' ? item.name : labelOf(item), segments = kind === 'file' ? 1 : item.segments.length;
  record(mode, (mode === 'send' ? '直接发送：' : '添加到输入区：') + label + (mode === 'send' && segments > 1 ? '（' + segments + '段，间隔' + state.settings.interval + 'ms）' : '') + ' [' + itemId + ']', itemId);
  toast((mode === 'send' ? '发送动作已记录：' : '插入动作已记录：') + label);
  if (state.settings.exitSearch && state.query) clearSearch();
  clearTimeout(deliveryTimer);
  deliveryTimer = setTimeout(() => { deliveryBusy = false; $$('.row-send').forEach((b) => { b.disabled = false; }); }, 700);
}
function clearSearch() { state.query = ''; $('#search-input').value = ''; state.searchIndex = 0; renderSearch(); }
function closeContext() { $('#context-menu').hidden = true; }
function showMenu(items, x, y) {
  const menu = $('#context-menu');
  menu.innerHTML = items.map((item, i) => item ? '<button type="button" role="menuitem" data-menu-index="' + i + '">' + esc(item.label) + '</button>' : '<div class="menu-separator" role="separator"></div>').join('');
  menu.hidden = false; menu.style.left = '0px'; menu.style.top = '0px';
  const bounds = menu.getBoundingClientRect();
  menu.style.left = Math.max(4, Math.min(x, innerWidth - bounds.width - 4)) + 'px';
  menu.style.top = Math.max(4, Math.min(y, innerHeight - bounds.height - 4)) + 'px';
  menu.onclick = (event) => { const b = event.target.closest('[data-menu-index]'); if (b) { closeContext(); items[Number(b.dataset.menuIndex)].action(); } };
}
function dialog(title, fields, onSubmit, submitLabel = '确定') {
  const el = $('#small-dialog');
  el.innerHTML = '<form method="dialog"><h2>' + esc(title) + '</h2>' + fields + '<div class="dialog-actions"><button class="secondary-button" value="cancel" type="submit" formnovalidate>取消</button><button class="primary-button" value="confirm" type="submit">' + esc(submitLabel) + '</button></div></form>';
  el.onclose = () => { if (el.returnValue === 'confirm') onSubmit(el); };
  el.returnValue = ''; el.showModal();
}
function addCategory(level = 'root') {
  const file = state.library === 'files';
  dialog(file ? '新增文件分类' : level === 'group' ? '新增二级分类' : '新增一级分类', '<label>分类名称<input type="text" name="name" maxlength="40" required autofocus></label>', (el) => {
    const name = $('[name="name"]', el).value.trim(); if (!name) return; const id = 'category-' + ++serial;
    if (file) { state.fileCategories.push({ id, name }); state.fileCategoryId = id; }
    else if (level === 'group') state.groups.push({ id, name, rootId: state.rootId });
    else { state.roots.push({ id, name }); state.rootId = id; }
    record('category.add', '新增分类：' + name); renderMain();
  });
}
function manageCategory() {
  const category = (state.library === 'files' ? state.fileCategories : state.roots).find((c) => c.id === (state.library === 'files' ? state.fileCategoryId : state.rootId));
  dialog('分类设置', '<label>分类名称<input type="text" name="name" value="' + esc(category.name) + '" maxlength="40" required></label>', (el) => { category.name = $('[name="name"]', el).value.trim() || category.name; renderMain(); record('category.rename', '修改分类：' + category.name); });
}
function editGroup(id) {
  const group = state.groups.find((g) => g.id === id);
  dialog('编辑二级分类', '<label>分类名称<input type="text" name="name" value="' + esc(group.name) + '" maxlength="40" required></label>', (el) => { group.name = $('[name="name"]', el).value.trim() || group.name; renderMain(); record('category.rename', '修改二级分类：' + group.name); });
}
function rootOptions(selected = state.rootId) { return state.roots.map((r) => '<option value="' + r.id + '"' + (r.id === selected ? ' selected' : '') + '>' + esc(r.name) + '</option>').join(''); }
function groupOptions(rootId, selected = '') { return '<option value="">不选二级分类</option>' + state.groups.filter((g) => g.rootId === rootId).map((g) => '<option value="' + g.id + '"' + (g.id === selected ? ' selected' : '') + '>' + esc(g.name) + '</option>').join(''); }
function copyOrMove(phrase, copy) {
  dialog(copy ? '复制到其他分类' : '移动到其他分类', '<label>一级分类<select name="root">' + rootOptions() + '</select></label><label>二级分类<select name="group">' + groupOptions(state.rootId) + '</select></label>', (el) => {
    const changed = copy ? structuredClone(phrase) : phrase;
    changed.rootId = $('[name="root"]', el).value; changed.groupId = $('[name="group"]', el).value;
    if (copy) { changed.id = 'p-' + ++serial; changed.shortcut = ''; state.phrases.push(changed); }
    renderMain(); record(copy ? 'phrase.copy' : 'phrase.move', (copy ? '复制：' : '移动：') + labelOf(phrase));
  });
  $('[name="root"]', $('#small-dialog')).onchange = (event) => { $('[name="group"]', $('#small-dialog')).innerHTML = groupOptions(event.target.value); };
}
function sortPhrase(phrase) {
  const siblings = state.phrases.filter((p) => p.rootId === phrase.rootId && p.groupId === phrase.groupId);
  dialog('话术排序', '<label>分类中的位置<input type="number" name="position" min="1" max="' + siblings.length + '" value="' + (siblings.indexOf(phrase) + 1) + '"></label>', (el) => {
    const position = Math.max(0, Math.min(siblings.length - 1, Number($('[name="position"]', el).value) - 1));
    const others = siblings.filter((p) => p !== phrase); others.splice(position, 0, phrase); let cursor = 0;
    state.phrases = state.phrases.map((p) => siblings.includes(p) ? others[cursor++] : p);
    renderMain(); record('phrase.sort', '排序：' + labelOf(phrase) + ' → 第' + (position + 1) + '项');
  });
}
function setShortcut(item, editingDraft = false) {
  dialog('自定义快捷键', '<label>快捷键<input type="text" name="shortcut" value="' + esc(item.shortcut || '') + '" placeholder="例如 Ctrl+Alt+1" autocomplete="off"></label><p>默认仅在客户端或目标窗口生效。可清空快捷键。</p>', (el) => {
    item.shortcut = $('[name="shortcut"]', el).value.trim();
    if (editingDraft) $('#editor-shortcut').innerHTML = icon('keyboard') + esc(item.shortcut || '自定义快捷键'); else renderMain();
    record('shortcut.set', '话术快捷键：' + (item.shortcut || '已清空'));
  });
}
function phraseMenu(p, x, y) {
  showMenu([
    { label: '发送到输入区（双击）', action: () => deliver(p.id, 'phrase', 'insert') },
    { label: '直接发送', action: () => deliver(p.id, 'phrase', 'send') },
    { label: '复制内容到剪贴板', action: () => record('copy', '复制内容：' + labelOf(p), p.id) }, null,
    { label: '新增话术', action: () => openEditor() }, { label: '插入一条话术', action: () => openEditor(null, p.id) },
    { label: '编辑', action: () => openEditor(p) },
    { label: '标题背景色', action: () => dialog('标题背景色', '<label>选择颜色<input type="color" name="color" value="' + (p.background || '#fff2bc') + '"></label><label><span><input name="reset" type="checkbox"> 恢复默认背景</span></label>', (el) => { p.background = $('[name="reset"]', el).checked ? '' : $('[name="color"]', el).value; renderMain(); record('color.set', '修改标题背景：' + labelOf(p)); }) },
    { label: '自定义快捷键', action: () => setShortcut(p) }, { label: '排序', action: () => sortPhrase(p) },
    { label: '移动到其他分类', action: () => copyOrMove(p, false) }, { label: '复制到其他分类', action: () => copyOrMove(p, true) },
    { label: '删除', action: () => dialog('删除话术', '<p>确认删除「' + esc(labelOf(p)) + '」？</p>', () => { state.phrases = state.phrases.filter((item) => item.id !== p.id); renderMain(); record('phrase.delete', '删除话术：' + labelOf(p)); }, '删除') },
  ], x, y);
}
function fileMenu(file, x, y) {
  showMenu([
    { label: '发送到输入区（双击）', action: () => deliver(file.id, 'file', 'insert') },
    { label: '直接发送', action: () => deliver(file.id, 'file', 'send') },
    { label: '重新关联文件', action: () => openFileEditor(file) },
    { label: '刷新文件关联状态', action: () => record('file.refresh', '刷新关联状态：' + file.name, file.id) },
    { label: '删除关联', action: () => dialog('删除文件关联', '<p>仅删除「' + esc(file.name) + '」的关联记录，原文件保留。</p>', () => { state.files = state.files.filter((item) => item.id !== file.id); renderMain(); record('file.delete', '删除关联，原文件保留：' + file.name); }, '删除关联') },
  ], x, y);
}
function mainMenu(x, y) {
  const file = state.library === 'files';
  showMenu([
    { label: file ? '新增关联文件' : '新增话术', action: () => file ? openFileEditor() : openEditor() }, { label: '分类设置', action: manageCategory },
    ...(file ? [] : [{ label: '新增二级分类', action: () => addCategory('group') }]), null,
    { label: '界面设置', action: () => openSettings('界面设置') }, { label: '快捷键', action: () => openSettings('快捷键') },
    { label: '导入', action: () => openSettings('导入数据') }, { label: '导出', action: () => openSettings('导出数据') },
    { label: '缓存管理', action: () => openSettings('缓存管理') }, { label: '关于 闪语', action: () => openSettings('关于') },
  ], x, y);
}
function showWindow(id) {
  closeContext(); for (const name of ['main-window', 'editor-window', 'file-window', 'settings-window']) $('#' + name).hidden = name !== id;
}
function setSceneCaption(scene) {
  state.scene = scene; $('#scene-caption').textContent = captions[scene] || captions.main;
  $$('.review-navigation > button').forEach((b) => b.classList.toggle('active', b.dataset.scene === scene));
  $('#scene-help').textContent = ['editor', 'file-editor', 'settings'].includes(scene) ? '所有更改仅作用于当前评审页。关闭窗口后回到主列表。' : '选中一行后，将鼠标移到另一行发送按钮，可核对发送目标。';
}
function closeWindow() { showWindow('main-window'); setSceneCaption(state.library === 'files' ? 'files' : 'main'); renderMain(); }
function openEditor(phrase = null, afterId = null) {
  draft = phrase ? structuredClone(phrase) : { id: null, afterId, title: '', rootId: state.rootId, groupId: afterId ? state.phrases.find((p) => p.id === afterId)?.groupId || '' : state.groups.find((g) => g.rootId === state.rootId)?.id || '', color: '#ff5100', background: '', shortcut: '', segments: [{ blocks: [{ kind: 'text', text: '' }] }] };
  activeSegment = 0; activeBlock = 0; $('#editor-heading').textContent = phrase ? '编辑话术' : '新增话术';
  $('#editor-root').innerHTML = rootOptions(draft.rootId); $('#editor-group').innerHTML = groupOptions(draft.rootId, draft.groupId);
  $('#phrase-title').value = draft.title; $('#editor-shortcut').innerHTML = icon('keyboard') + esc(draft.shortcut || '自定义快捷键');
  $('#editor-error').hidden = true; renderPalette(); renderEditor(); showWindow('editor-window'); setSceneCaption('editor');
}
function renderPalette() { $('#title-colors').innerHTML = colors.map(([value, display, name]) => '<button type="button" data-color="' + value + '" title="' + name + '" aria-label="' + name + '" aria-pressed="' + (draft.color === value) + '" style="background:' + display + '"></button>').join(''); }
function renderEditor() {
  const multiple = draft.segments.length > 1;
  $('#segment-editor').innerHTML = draft.segments.map((segment, si) => '<div class="segment' + (multiple ? ' multiple' : '') + '" data-segment="' + si + '">' +
    (multiple ? '<div class="segment-label"><span>第' + (si + 1) + '段</span><button type="button" data-remove-segment="' + si + '" aria-label="删除第' + (si + 1) + '段">×</button></div>' : '') +
    segment.blocks.map((b, bi) => b.kind === 'text' ? '<textarea data-segment-index="' + si + '" data-block-index="' + bi + '" aria-label="第' + (si + 1) + '段文字" placeholder="请输入话术，不超过5000个字">' + esc(b.text) + '</textarea>' : '<div class="media-block" data-block-index="' + bi + '">' + (b.kind === 'image' ? '<img src="' + esc(b.url) + '" alt="' + esc(b.name) + '">' : icon('attachment')) + '<span>' + esc(b.name) + '</span><button type="button" data-remove-media="' + si + ',' + bi + '" aria-label="删除附件 ' + esc(b.name) + '">×</button></div>').join('') + '</div>').join('');
}
function editorCount(kind) { return draft.segments.reduce((n, s) => n + s.blocks.filter((b) => b.kind === kind).length, 0); }
function editorError(message) { $('#editor-error').textContent = message; $('#editor-error').hidden = false; }
function insertMedia(files, kind) {
  if (!files.length) return;
  if (editorCount(kind) + files.length > 9) { editorError('每条话术最多添加9' + (kind === 'image' ? '张图片。' : '个文件。')); return; }
  const segment = draft.segments[activeSegment], block = segment.blocks[activeBlock];
  const textarea = $('#segment-editor textarea[data-segment-index="' + activeSegment + '"][data-block-index="' + activeBlock + '"]');
  const offset = textarea ? textarea.selectionStart : block?.text?.length || 0;
  const media = files.map((f) => ({ kind, name: f.name, url: kind === 'image' ? URL.createObjectURL(f) : '' }));
  if (block?.kind === 'text') { segment.blocks.splice(activeBlock, 1, { kind: 'text', text: block.text.slice(0, offset) }, ...media, { kind: 'text', text: block.text.slice(offset) }); activeBlock += media.length + 1; }
  else { segment.blocks.push(...media, { kind: 'text', text: '' }); activeBlock = segment.blocks.length - 1; }
  renderEditor(); $('#segment-editor textarea[data-segment-index="' + activeSegment + '"][data-block-index="' + activeBlock + '"]').focus(); $('#editor-error').hidden = true;
}
function insertDate() {
  const input = $('#segment-editor textarea[data-segment-index="' + activeSegment + '"][data-block-index="' + activeBlock + '"]') || $('#segment-editor textarea'); if (!input) return;
  const date = new Intl.DateTimeFormat('sv-SE', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
  input.setRangeText(date, input.selectionStart, input.selectionEnd, 'end');
  draft.segments[Number(input.dataset.segmentIndex)].blocks[Number(input.dataset.blockIndex)].text = input.value; input.focus();
}
function savePhrase(event) {
  event.preventDefault(); draft.title = $('#phrase-title').value.trim(); draft.rootId = $('#editor-root').value; draft.groupId = $('#editor-group').value;
  if ([...draft.title].length > 200) { editorError('标题不能超过200个字。'); return; }
  if ([...textOf(draft)].length > 5000) { editorError('话术内容不能超过5000个字。'); return; }
  if (!textOf(draft).trim() && !editorCount('image') && !editorCount('file')) { editorError('请填写话术内容或添加图片、文件。'); return; }
  if (draft.shortcut && state.phrases.some((p) => p.id !== draft.id && p.shortcut.toLowerCase() === draft.shortcut.toLowerCase())) { editorError('此快捷键已被其他话术使用，请修改。'); return; }
  const saved = structuredClone(draft);
  if (saved.id) state.phrases[state.phrases.findIndex((p) => p.id === saved.id)] = saved;
  else { saved.id = 'p-' + ++serial; const after = state.phrases.findIndex((p) => p.id === saved.afterId); state.phrases.splice(after >= 0 ? after + 1 : state.phrases.length, 0, saved); }
  state.rootId = saved.rootId; state.library = 'phrases'; state.selectedId = saved.id;
  record('phrase.save', '保存话术：' + labelOf(saved) + '（' + saved.segments.length + '段）', saved.id); closeWindow(); draft = null;
}
function openFileEditor(file = null) {
  state.relinkFileId = file?.id || null; selectedFiles = [];
  $('#local-file-picker').value = '';
  $('#file-category').innerHTML = state.fileCategories.map((c) => '<option value="' + c.id + '"' + (c.id === state.fileCategoryId ? ' selected' : '') + '>' + esc(c.name) + '</option>').join('');
  $('#chosen-files').innerHTML = ''; showWindow('file-window'); setSceneCaption('file-editor');
}
function openSettings(tab = '常用设置') { state.settingsTab = tab; renderSettings(); showWindow('settings-window'); setSceneCaption('settings'); }
function option(name, value, label, radio = false) {
  const checked = radio ? state.settings[name] === value : Boolean(state.settings[name]);
  return '<label><input data-setting="' + name + '" type="' + (radio ? 'radio' : 'checkbox') + '" name="' + name + '" value="' + esc(value) + '"' + (checked ? ' checked' : '') + '>' + label + '</label>';
}
function settingsRow(label, options, hint = '') { return '<div class="settings-row"><span class="settings-label">' + label + '</span><div class="settings-options">' + options + '</div>' + (hint ? '<p class="settings-hint">' + hint + '</p>' : '') + '</div>'; }
function renderSettings() {
  $('#settings-nav').innerHTML = settingsTabs.map((t) => '<button type="button" data-settings-tab="' + t + '" class="' + (t === state.settingsTab ? 'active' : '') + '">' + t + '</button>').join('');
  let html = '';
  if (state.settingsTab === '常用设置') {
    html = '<div class="startup-grid">' + option('startup', '', '开机启动') + option('tray', '', '最小化到托盘') + '</div>' +
      settingsRow('发送按键模式', option('enter', 'enter', 'Enter 键', true) + option('enter', 'ctrl-enter', 'Ctrl+Enter 键', true), '发送按键应与聊天软件中的设置一致，点击直接发送时生效') +
      settingsRow('搜索匹配方式', option('searchMode', 'fuzzy', '模糊匹配', true) + option('searchMode', 'exact', '精确匹配', true)) +
      settingsRow('搜索结果使用后', option('exitSearch', '', '退出搜索结果界面')) +
      settingsRow('话术双击', '<span>添加到输入区</span>') +
      settingsRow('悬浮时自动隐藏', option('autoHide', '', '开启')) +
      settingsRow('贴边隐藏防误触', option('hideDelay', '0', '不等待', true) + option('hideDelay', '500', '等0.5秒', true) + option('hideDelay', '1000', '等1秒', true), '停留指定时长后显示主窗口') +
      settingsRow('逐条／分段发送间隔', option('interval', '100', '100 毫秒', true) + option('interval', '500', '500 毫秒', true) + option('interval', '1000', '1000 毫秒', true), '多段话术逐段发送时，两段之间的间隔');
  } else if (state.settingsTab === '界面设置') {
    html = '<h2 class="settings-heading">面板设置</h2>' + settingsRow('列表显示', option('showNumbers', '', '显示话术序号') + option('showPreview', '', '显示正文摘要')) +
      settingsRow('主题风格', '<span class="import-path">默认蓝色</span>') + settingsRow('话术字体大小', '<input type="number" data-setting="fontSize" min="10" max="18" value="' + state.settings.fontSize + '">') +
      settingsRow('二级分类', option('expand', '', '默认展开')) + settingsRow('分类背景色', '<span>浅蓝色分组栏</span>') + settingsRow('窗口行为', '<span>可调整尺寸 · 自动适应系统缩放</span>');
  } else if (state.settingsTab === '吸附设置') {
    html = '<p>请选择使用聊天工具时，话术窗口的跟随位置：</p><div class="setting-block">' + option('dockSide', 'left', '聊天窗口左侧', true) + option('dockSide', 'right', '聊天窗口右侧', true) + '</div><div class="setting-block"><p>请选择要吸附的软件：</p><div class="dock-software">' + option('dock', '', '企业微信') + '</div></div>';
  } else if (state.settingsTab === '导入数据') {
    html = '<h2 class="settings-heading">导入话术</h2><div class="setting-block"><label><input type="checkbox" checked disabled> 导入到我的私人话术</label></div><div class="setting-block"><label><input name="importType" type="radio" value="excel" checked> 从 Excel 导入</label><label><input name="importType" type="radio" value="backup"> 从 闪语 完整备份导入</label><div class="setting-buttons"><button class="secondary-button" id="choose-import" type="button">选择导入文件</button><span id="import-filename" class="import-path">未选择文件</span></div><input id="import-picker" type="file" accept=".xlsx,.zip" hidden></div><div class="setting-block"><label><input type="checkbox" id="merge-categories"> 导入的一级分类与现有分类重复时合并</label><p class="settings-hint">先预览内容，再执行导入。</p></div><div class="setting-actions"><button class="primary-button" id="import-preview" type="button">查看导入预览</button></div>';
  } else if (state.settingsTab === '导出数据') {
    html = '<h2 class="settings-heading">导出话术</h2><div class="setting-block"><label><input type="checkbox" checked disabled> 导出我的私人话术</label><select id="export-category"><option value="">全部分类</option>' + rootOptions() + '</select></div><div class="setting-block"><p>导出类型：</p><label><input type="radio" name="exportType" value="backup" checked> 完整备份（含图片与文件关联）</label><label><input type="radio" name="exportType" value="excel"> Excel 表格（文字内容）</label><p class="settings-hint">关联的外部文件需要单独备份。</p></div><div class="setting-actions"><button class="primary-button" id="export-data" type="button">导出话术</button></div>';
  } else if (state.settingsTab === '快捷键') {
    html = [['定位到搜索', 'searchHotkey', '上下键选择搜索结果，按Enter添加到输入区，按Esc退出'], ['显示／隐藏主窗口', 'windowHotkey', '快速唤起话术窗口'], ['定位到常用话术', 'commonHotkey', '按Enter添加到输入区']].map(([label, setting, hint]) => '<div class="hotkey-row"><div class="hotkey-main"><span>' + label + '</span><strong>Alt键</strong><span>＋</span><input type="text" maxlength="1" data-setting="' + setting + '" value="' + state.settings[setting] + '" aria-label="' + label + '快捷键"></div><p>' + hint + '</p></div>').join('') +
      settingsRow('自定义话术快捷键', option('customHotkeys', '', '开启')) + settingsRow('生效范围', option('globalPhraseHotkeys', '', '全局生效'), '默认仅在客户端或目标输入窗口中有效');
  } else if (state.settingsTab === '缓存管理') {
    html = '<button class="secondary-button wide-button" type="button" data-cache-action="打开缓存文件夹">打开本地缓存文件夹</button><div class="setting-block"><p>清理可重新生成的缓存</p><div class="setting-buttons"><button class="secondary-button" type="button" data-cache-action="清理缩略图">清理缩略图</button><button class="secondary-button" type="button" data-cache-action="清理临时导入文件">清理临时导入文件</button></div><p class="settings-hint">话术、图片素材和关联的原文件保留。</p></div>';
  } else html = '<h2 class="settings-heading">闪语</h2><p>Windows 本地话术工具</p><div class="setting-block"><p>快速找到话术，添加到输入区，按需直接发送。</p><p>支持本地文件、图片附件和分段话术。</p></div>';
  $('#settings-content').innerHTML = html;
}
function openScene(scene) {
  closeContext(); clearSearch(); $$('.forced-hover').forEach((r) => r.classList.remove('forced-hover'));
  if (scene === 'editor') { state.library = 'phrases'; openEditor(); return; }
  if (scene === 'file-editor') { state.library = 'files'; openFileEditor(); return; }
  if (scene === 'settings') { openSettings(); return; }
  state.library = ['files', 'error'].includes(scene) ? 'files' : 'phrases';
  if (scene === 'empty') { if (!state.roots.some((r) => r.id === 'c-empty')) state.roots.push({ id: 'c-empty', name: '新分类' }); state.rootId = 'c-empty'; }
  else state.rootId = 'c-common';
  if (scene === 'error' && state.files[0]) state.files[0].missing = true;
  else if (scene === 'files' && state.files[0]) state.files[0].missing = false;
  showWindow('main-window'); setSceneCaption(scene); renderMain();
  if (scene === 'hover') { state.selectedId = 'p-10'; renderMain(); $('#content-list [data-row="p-9"]')?.classList.add('forced-hover'); }
  if (scene === 'search') { state.query = 'ups'; renderMain(); }
  if (scene === 'context') { const row = $('#content-list [data-row="p-2"]'); if (row) { selectRow(row); const b = row.getBoundingClientRect(); phraseMenu(state.phrases.find((p) => p.id === 'p-2'), b.left + 106, b.top + 5); } }
}
function reset() {
  clearTimeout(deliveryTimer); deliveryBusy = false; state = sampleState(); draft = null; selectedFiles = []; events.length = 0; serial = 100;
  $('#action-log').innerHTML = ''; closeContext(); if ($('#small-dialog').open) $('#small-dialog').close();
  showWindow('main-window'); setSceneCaption('main'); renderMain(); record('review.ready', '评审稿已就绪，样例数据可自由编辑。');
}

// 委托处理保持行节点稳定，避免第一次单击重绘后丢失浏览器的双击事件。
$('#list-area').addEventListener('click', (event) => {
  const row = event.target.closest('[data-row]');
  if (event.target.closest('.row-send')) { event.stopPropagation(); if (row) deliver(row.dataset.row, row.dataset.kind, 'send'); return; }
  if (row) { selectRow(row); return; }
  const group = event.target.closest('[data-group]');
  if (group) { if (state.collapsed.has(group.dataset.group)) state.collapsed.delete(group.dataset.group); else state.collapsed.add(group.dataset.group); renderMain(); return; }
  const edit = event.target.closest('[data-edit-group]'); if (edit) { editGroup(edit.dataset.editGroup); return; }
  if (event.target.closest('[data-empty-add]')) { if (state.library === 'files') openFileEditor(); else openEditor(); }
});
$('#list-area').addEventListener('dblclick', (event) => {
  if (event.target.closest('.row-send')) { event.stopPropagation(); return; }
  const row = event.target.closest('[data-row]'); if (row) deliver(row.dataset.row, row.dataset.kind, 'insert');
});
$('#list-area').addEventListener('contextmenu', (event) => {
  event.preventDefault(); const row = event.target.closest('[data-row]');
  if (row) { selectRow(row); const item = (row.dataset.kind === 'file' ? state.files : state.phrases).find((e) => e.id === row.dataset.row); if (row.dataset.kind === 'file') fileMenu(item, event.clientX, event.clientY); else phraseMenu(item, event.clientX, event.clientY); }
  else showMenu(state.library === 'files' ? [
    { label: '新增关联文件', action: () => openFileEditor() }, { label: '新增分类', action: () => addCategory() }, { label: '刷新文件关联状态', action: () => record('file.refresh', '刷新全部文件关联状态') },
  ] : [{ label: '新增话术', action: () => openEditor() }, { label: '新增一级分类', action: () => addCategory() }, { label: '新增二级分类', action: () => addCategory('group') }], event.clientX, event.clientY);
});
$('#list-area').addEventListener('keydown', (event) => {
  if (event.target.closest('.row-send')) return;
  const row = event.target.closest('[data-row]'); if (row && event.key === 'Enter') { event.preventDefault(); deliver(row.dataset.row, row.dataset.kind, 'insert'); }
});
$$('.review-navigation > button').forEach((b) => { b.onclick = () => openScene(b.dataset.scene); });
$$('.content-tabs button').forEach((b) => { b.onclick = () => { state.library = b.dataset.library; clearSearch(); setSceneCaption(state.library === 'files' ? 'files' : 'main'); renderMain(); }; });
$('#category-tabs').onclick = (event) => {
  if (event.target.closest('.add-category')) { addCategory(); return; }
  const b = event.target.closest('[data-category]'); if (!b) return;
  if (state.library === 'files') state.fileCategoryId = b.dataset.category; else state.rootId = b.dataset.category; renderMain();
};
$('#category-tabs').oncontextmenu = (event) => { event.preventDefault(); const b = event.target.closest('[data-category]'); if (b) { if (state.library === 'files') state.fileCategoryId = b.dataset.category; else state.rootId = b.dataset.category; } showMenu([{ label: '编辑分类', action: manageCategory }, { label: '新增分类', action: () => addCategory() }], event.clientX, event.clientY); };
$('#search-input').addEventListener('compositionstart', () => { state.composing = true; });
$('#search-input').addEventListener('compositionend', () => { state.composing = false; state.query = $('#search-input').value; state.searchIndex = 0; renderSearch(); });
$('#search-input').oninput = (event) => { if (state.composing || event.isComposing) return; state.query = event.target.value; state.searchIndex = 0; renderSearch(); };
$('#search-input').onkeydown = (event) => {
  if (state.composing || event.isComposing) return; const results = matchingItems();
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); state.searchIndex = Math.max(0, Math.min(results.length - 1, state.searchIndex + (event.key === 'ArrowDown' ? 1 : -1))); renderSearch(); $('#search-results .selected')?.scrollIntoView({ block: 'nearest' }); }
  if (event.key === 'Enter' && results[state.searchIndex]) { event.preventDefault(); const m = results[state.searchIndex]; deliver(m.item.id, m.kind, 'insert'); }
  if (event.key === 'Escape') { event.preventDefault(); clearSearch(); }
};
$('#clear-search').onclick = () => { clearSearch(); $('#search-input').focus(); };
$('#close-search').onclick = clearSearch; $('#search-backdrop').onclick = clearSearch;
$('#quick-add').onclick = () => { if (state.library === 'files') openFileEditor(); else openEditor(); };
$('#pin-window').onclick = () => { state.pinned = !state.pinned; $('#pin-window').setAttribute('aria-pressed', String(state.pinned)); record('window.pin', state.pinned ? '开启置顶' : '关闭置顶'); };
$('#minimize-window').onclick = () => { record('window.minimize', '最小化到托盘（界面动作）'); toast('已记录最小化到托盘'); };
$('#auto-hide').onclick = () => { state.settings.autoHide = !state.settings.autoHide; $('#auto-hide').setAttribute('aria-pressed', String(state.settings.autoHide)); record('window.autohide', '自动隐藏：' + (state.settings.autoHide ? '开启' : '关闭')); };
$('#show-window').onclick = () => record('window.show', '显示主窗口'); $('#open-settings').onclick = () => openSettings();
for (const name of ['top-menu', 'bottom-menu']) $('#' + name).onclick = (event) => { const b = event.currentTarget.getBoundingClientRect(); mainMenu(b.left, b.bottom); };
$('#display-menu').onclick = () => openSettings('界面设置');
$$('[data-close-window]').forEach((b) => { b.onclick = closeWindow; });
$('#reset-review').onclick = reset; $('#phrase-form').onsubmit = savePhrase;
$('#editor-root').onchange = (event) => { draft.rootId = event.target.value; draft.groupId = ''; $('#editor-group').innerHTML = groupOptions(draft.rootId); };
$('#editor-group').onchange = (event) => { draft.groupId = event.target.value; };
$('#title-colors').onclick = (event) => { const b = event.target.closest('[data-color]'); if (b) { draft.color = b.dataset.color; renderPalette(); } };
$('#editor-shortcut').onclick = () => setShortcut(draft, true);
$('#segment-editor').addEventListener('focusin', (event) => { if (event.target.matches('textarea')) { activeSegment = Number(event.target.dataset.segmentIndex); activeBlock = Number(event.target.dataset.blockIndex); } });
$('#segment-editor').addEventListener('input', (event) => { if (event.target.matches('textarea')) { draft.segments[Number(event.target.dataset.segmentIndex)].blocks[Number(event.target.dataset.blockIndex)].text = event.target.value; $('#editor-error').hidden = true; } });
$('#segment-editor').onclick = (event) => {
  const media = event.target.closest('[data-remove-media]');
  if (media) { const [si, bi] = media.dataset.removeMedia.split(',').map(Number); draft.segments[si].blocks.splice(bi, 1); activeSegment = 0; activeBlock = 0; renderEditor(); return; }
  const segment = event.target.closest('[data-remove-segment]');
  if (segment && draft.segments.length > 1) { draft.segments.splice(Number(segment.dataset.removeSegment), 1); activeSegment = 0; activeBlock = 0; renderEditor(); }
};
$('#insert-date').onclick = insertDate; $('#insert-image').onclick = () => $('#image-picker').click(); $('#insert-file').onclick = () => $('#attachment-picker').click();
$('#image-picker').onchange = (event) => { const files = Array.from(event.target.files); if (files.some((f) => !f.type.startsWith('image/'))) { editorError('请选择图片文件。'); return; } insertMedia(files, 'image'); event.target.value = ''; };
$('#attachment-picker').onchange = (event) => { insertMedia(Array.from(event.target.files), 'file'); event.target.value = ''; };
$('#insert-segment').onclick = () => { draft.segments.splice(activeSegment + 1, 0, { blocks: [{ kind: 'text', text: '' }] }); activeSegment += 1; activeBlock = 0; renderEditor(); $('#segment-editor textarea[data-segment-index="' + activeSegment + '"]').focus(); };
$('#choose-local-files').onclick = () => $('#local-file-picker').click();
$('#local-file-picker').onchange = (event) => { selectedFiles = Array.from(event.target.files); $('#chosen-files').innerHTML = selectedFiles.map((f) => '<span>' + esc(f.name) + '</span>').join(''); };
$('#file-form').onsubmit = (event) => {
  event.preventDefault(); if (!selectedFiles.length) { toast('请先选择文件'); return; }
  if (state.relinkFileId) { const f = state.files.find((item) => item.id === state.relinkFileId); if (f) { f.name = selectedFiles[0].name; f.missing = false; } record('file.relink', '重新关联文件：' + selectedFiles[0].name); }
  else { for (const f of selectedFiles) state.files.push({ id: 'file-' + ++serial, name: f.name, categoryId: $('#file-category').value, missing: false }); record('file.add', '新增' + selectedFiles.length + '个文件关联'); }
  state.fileCategoryId = $('#file-category').value; state.library = 'files'; closeWindow();
};
$('#settings-nav').onclick = (event) => { const b = event.target.closest('[data-settings-tab]'); if (b) { state.settingsTab = b.dataset.settingsTab; renderSettings(); } };
$('#settings-content').onchange = (event) => {
  const setting = event.target.dataset.setting;
  if (setting) {
    state.settings[setting] = event.target.type === 'checkbox' ? event.target.checked : event.target.type === 'number' ? Math.max(10, Math.min(18, Number(event.target.value) || 12)) : event.target.value;
    if (setting === 'expand') { if (event.target.checked) state.collapsed.clear(); else state.groups.forEach((g) => state.collapsed.add(g.id)); }
    record('settings.change', '设置已更新：' + setting); renderMain();
  }
  if (event.target.id === 'import-picker' && event.target.files[0]) $('#import-filename').textContent = event.target.files[0].name;
};
$('#settings-content').onclick = (event) => {
  if (event.target.id === 'choose-import') $('#import-picker').click();
  if (event.target.id === 'import-preview') dialog('导入内容预览', '<p>评审样例：实际文件解析由后续 Rust 数据层完成。</p><table class="template-table"><thead><tr><th>一级分类</th><th>二级分类</th><th>标题</th><th>内容</th></tr></thead><tbody><tr><td>常用</td><td>通用</td><td>欢迎语</td><td>您好，很高兴为您服务。</td></tr></tbody></table>', () => { record('import.preview', '确认导入界面流程（未解析文件）'); toast('导入界面流程已记录'); }, '确认导入');
  if (event.target.id === 'export-data') { record('export.preview', '导出界面：' + ($('[name="exportType"]:checked').value === 'backup' ? '完整备份' : 'Excel文字')); toast('导出界面流程已记录'); }
  if (event.target.dataset.cacheAction) { record('cache.preview', event.target.dataset.cacheAction + '（界面动作，未操作磁盘）'); toast('缓存操作已记录，话术和原文件保留'); }
};
document.addEventListener('pointerdown', (event) => { if (!event.target.closest('#context-menu')) closeContext(); });
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') { closeContext(); if (!$('#small-dialog').open) clearSearch(); }
  if (!event.altKey || event.ctrlKey || event.metaKey || event.isComposing || event.target.closest('#editor-window, #file-window, #small-dialog')) return;
  const key = event.key.toUpperCase();
  if (key === state.settings.searchHotkey.toUpperCase()) { event.preventDefault(); showWindow('main-window'); $('#search-input').focus(); }
  if (key === state.settings.commonHotkey.toUpperCase()) { event.preventDefault(); state.rootId = 'c-common'; state.library = 'phrases'; closeWindow(); $('#content-list [data-row]')?.focus(); }
  if (key === state.settings.windowHotkey.toUpperCase()) { event.preventDefault(); closeWindow(); record('window.toggle', '显示／隐藏主窗口快捷键（界面动作）'); }
});
window.__review = { get state() { return state; }, get events() { return events; }, get draft() { return draft; }, openScene, reset, openEditor };
reset();
