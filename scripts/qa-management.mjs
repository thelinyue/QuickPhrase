import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

mkdirSync("qa-artifacts", { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1200, height: 760 }, deviceScaleFactor: 1 });
const managementUrl = process.env.MANAGEMENT_QA_URL || "http://127.0.0.1:4173/management.html";
const consoleErrors = [];
page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
page.on("pageerror", (error) => consoleErrors.push(error.message));

await page.addInitScript(() => {
  let categories = [
    { id: "cat-device", name: "设备问题", parentId: null, sortOrder: 0, version: 1 },
    { id: "cat-support", name: "售后服务", parentId: null, sortOrder: 1, version: 1 },
  ];
  let phrases = Array.from({ length: 12 }, (_, index) => ({
    id: `phrase-${index}`,
    title: index === 0 ? "恢复出厂设置" : `标准回复 ${index + 1}`,
    content: index === 0 ? "恢复出厂设置前请先备份重要数据。" : `这是第 ${index + 1} 条本地标准回复。`,
    categoryId: index % 2 ? "cat-support" : "cat-device",
    tags: [{ id: `tag-${index}`, name: index % 2 ? "售后" : "设备" }],
    favorite: index === 0,
    shortcutMode: index === 0 ? "Quick" : "None",
    shortcut: index === 0 ? { display: "Alt + 1", normalized: "alt+1" } : null,
    usageCount: 12 - index,
    lastUsedAtUtc: new Date(Date.now() - index * 3600000).toISOString(),
    version: 1,
  }));
  let settings = { version: 1, launchOnStartup: true, startMinimized: false, stayInTrayOnClose: true, launcherShortcutDisplay: "Alt + Space", autoSend: false, clipboardCompatibilityMode: true, launcherEnabledAdapters: { WXWork: true } };
  let phraseInsertCount = 0;
  const listeners = new Set();
  const respond = (request, ok, data, error) => queueMicrotask(() => listeners.forEach((listener) => listener({ data: JSON.stringify({ protocolVersion: 1, requestId: request.requestId, ok, data, error }) })));
  const handle = (request) => {
    let data = null;
    if (request.type === "system.ping" || request.type === "system.ready") data = { ready: true, protocolVersion: 1 };
    else if (request.type === "phrase.list") data = { items: phrases, total: phrases.length };
    else if (request.type === "phrase.search") data = { items: phrases.filter((phrase) => `${phrase.title} ${phrase.content}`.toLowerCase().includes(String(request.payload.query).toLowerCase())).map((phrase) => ({ phrase })) };
    else if (request.type === "category.list") data = categories;
    else if (request.type === "settings.get") data = settings;
    else if (request.type === "hotkey.status") data = { launcher: { available: true, conflict: false } };
    else if (request.type === "adapter.status") data = { adapterId: "WXWork", productVersion: "5.0.9.6065", profileVersion: "1", insertText: "Verified", verifyInsert: "Unverified", sendText: "Unsupported" };
    else if (request.type === "adapter.catalog") data = [{ adapterId: "WXWork", displayName: "企业微信", processName: "WXWork", supportedProductVersion: "5.0.9.6065", profileVersion: "phase5-wecom-3", insertText: "Verified", verifyInsert: "Unverified", sendText: "Unsupported", verifySend: "Unsupported", fallbackMode: "CopyOnly" }];
    else if (request.type === "window.sceneChanged") data = { accepted: true, scene: request.payload.scene };
    else if (request.type === "phrase.update") { const next = { ...phrases.find((phrase) => phrase.id === request.payload.id), ...request.payload, version: 2 }; phrases = phrases.map((phrase) => phrase.id === next.id ? next : phrase); data = next; }
    else if (request.type === "phrase.create") { const next = { ...request.payload, version: 1, usageCount: 0, tags: (request.payload.tags || []).map((name, index) => ({ id: `new-tag-${index}`, name })) }; phrases = [next, ...phrases]; data = next; }
    else if (request.type === "phrase.delete") { phrases = phrases.filter((phrase) => phrase.id !== request.payload.id); data = { deleted: true }; }
    else if (request.type === "phrase.insert") { phraseInsertCount += 1; data = { accepted: true }; }
    else if (request.type === "category.create") { const next = { ...request.payload, version: 1 }; categories.push(next); data = next; }
    else if (request.type === "category.move") { const current = categories.find((category) => category.id === request.payload.id); const next = { ...current, ...request.payload, version: (current?.version || 1) + 1 }; categories.splice(categories.indexOf(current), 1, next); data = next; }
    else if (request.type === "category.rename") { const current = categories.find((category) => category.id === request.payload.id); const next = { ...current, ...request.payload, version: (current?.version || 1) + 1 }; categories.splice(categories.indexOf(current), 1, next); data = next; }
    else if (request.type === "category.delete") { categories = categories.filter((category) => category.id !== request.payload.id); data = { deleted: true }; }
    else if (request.type === "launcher.open") data = { accepted: true, mode: request.payload.mode };
    else if (request.type === "settings.update") { settings = { ...settings, ...request.payload, version: 2 }; data = settings; }
    else { respond(request, false, null, { code: "UNKNOWN", message: "未知测试请求" }); return; }
    respond(request, true, data, null);
  };
  window.__emitHostEvent = (event) => listeners.forEach((listener) => listener({ data: JSON.stringify(event) }));
  window.__phraseInsertCount = () => phraseInsertCount;
  window.chrome = { webview: { addEventListener: (_type, listener) => listeners.add(listener), removeEventListener: (_type, listener) => listeners.delete(listener), postMessage: (raw) => handle(JSON.parse(raw)) } };
});

await page.goto(managementUrl, { waitUntil: "networkidle" });
await page.getByRole("button", { name: "展开话术库" }).click();
await page.getByRole("heading", { name: "全部话术" }).waitFor();
if (!(await page.getByRole("button", { name: "话术库", exact: true }).getAttribute("aria-current")) || !(await page.getByRole("button", { name: "设置", exact: true }).isVisible())) throw new Error("统一管理导航缺失");
await page.getByRole("button", { name: "设置", exact: true }).click();
await page.getByRole("heading", { name: "设置" }).waitFor();
if (!(await page.getByRole("button", { name: "设置", exact: true }).getAttribute("aria-current"))) throw new Error("设置导航未保持激活状态");
await page.getByRole("button", { name: "话术库", exact: true }).click();
await page.getByRole("heading", { name: "全部话术" }).waitFor();
const initialOverflow = await page.evaluate(() => { const rect = (selector) => { const box = document.querySelector(selector)?.getBoundingClientRect(); return box ? { top: box.top, left: box.left, width: box.width, height: box.height, bottom: box.bottom } : null; }; return { width: document.documentElement.scrollWidth, viewport: window.innerWidth, height: document.documentElement.scrollHeight, viewportHeight: window.innerHeight, root: rect(".management-shell"), window: rect(".library-window"), layout: rect(".library-layout"), list: rect(".library-list") }; });
if (initialOverflow.width > initialOverflow.viewport + 1 || initialOverflow.height > initialOverflow.viewportHeight + 1) throw new Error(`管理库外层发生滚动: ${JSON.stringify(initialOverflow)}`);
const listScroll = await page.locator(".library-list").evaluate((node) => ({ clientHeight: node.clientHeight, scrollHeight: node.scrollHeight }));
if (listScroll.scrollHeight <= listScroll.clientHeight) throw new Error(`话术列表没有形成内部滚动: ${JSON.stringify(listScroll)}`);
await page.screenshot({ path: "qa-artifacts/management-library-1200.png" });
await page.locator(".phrase-row").first().dblclick();
await page.locator(".host-toast").getByText("已请求安全插入", { exact: false }).waitFor();
if (await page.evaluate(() => window.__phraseInsertCount()) !== 1) throw new Error("双击未执行一次安全插入");

await page.locator(".sidebar-item").filter({ hasText: "收藏" }).click();
if (await page.locator(".phrase-row").count() !== 1) throw new Error("收藏筛选未生效");
await page.getByLabel("新建一级分类名称").fill("网络问题");
await page.keyboard.press("Enter");
if (!(await page.getByRole("heading", { name: "网络问题" }).isVisible())) throw new Error("新建分类后未切换到分类");

await page.getByRole("button", { name: /新建话术/ }).click();
await page.getByLabel("话术标题").fill("新的标准回复");
await page.getByLabel("话术正文").fill("这是新的标准回复正文。");
await page.getByLabel("标签").fill("客服");
await page.keyboard.press("Enter");
const editorSaveRect = await page.getByRole("button", { name: "保存话术" }).boundingBox();
if (!editorSaveRect || editorSaveRect.y + editorSaveRect.height > 760 || editorSaveRect.y < 0) throw new Error(`编辑器固定操作栏未在首屏可见: ${JSON.stringify(editorSaveRect)}`);
await page.screenshot({ path: "qa-artifacts/management-editor-1200.png" });
await page.getByRole("button", { name: "保存话术" }).click();
if (!(await page.locator(".host-toast").getByText("话术已保存", { exact: true }).isVisible())) throw new Error("编辑器保存反馈缺失");
if (!(await page.getByRole("heading", { name: "全部话术" }).isVisible()) || !(await page.locator(".phrase-row").filter({ hasText: "新的标准回复" }).isVisible())) throw new Error("保存后未回到可见的话术库上下文");

await page.getByRole("button", { name: /新建话术/ }).click();
await page.getByLabel("话术标题").fill("未保存测试");
await page.getByRole("button", { name: "设置", exact: true }).click();
await page.getByRole("dialog", { name: "还有未保存的改动" }).waitFor();
await page.getByRole("button", { name: "继续编辑" }).click();
await page.getByRole("button", { name: "设置", exact: true }).click();
await page.getByRole("dialog", { name: "还有未保存的改动" }).waitFor();
await page.getByRole("button", { name: "放弃改动" }).click();
await page.getByRole("heading", { name: "设置" }).waitFor();
await page.getByRole("button", { name: "应用适配" }).click();
const adapterToggle = page.locator(".adapter-switch input");
const adapterBefore = await adapterToggle.isChecked();
await adapterToggle.click();
await page.getByRole("button", { name: "话术库", exact: true }).click();
await page.getByRole("dialog", { name: "还有未保存的改动" }).waitFor();
await page.getByRole("button", { name: "放弃改动" }).click();
await page.getByRole("button", { name: "设置", exact: true }).click();
await page.getByRole("button", { name: "应用适配" }).click();
if ((await page.locator(".adapter-switch input").isChecked()) !== adapterBefore) throw new Error("设置放弃后未恢复基线");

await page.evaluate(() => window.__emitHostEvent({ event: "navigation.requested", data: { scene: "settings" } }));
await page.getByRole("heading", { name: "设置" }).waitFor();
await page.setViewportSize({ width: 760, height: 660 });
await page.screenshot({ path: "qa-artifacts/management-settings-760.png" });
await page.getByRole("button", { name: "发送行为" }).click();
if (!(await page.getByText("自动发送", { exact: true }).isVisible())) throw new Error("设置分组导航未滚动到发送行为");
await page.getByRole("button", { name: "应用适配" }).click();
if (!(await page.getByText("企业微信", { exact: true }).isVisible())) throw new Error("应用适配真实状态缺失");
await page.getByRole("button", { name: "完成" }).click();
if (!(await page.locator(".library-page").isVisible())) throw new Error("设置完成未返回话术库");

await page.setViewportSize({ width: 680, height: 700 });
await page.evaluate(() => window.__emitHostEvent({ event: "navigation.requested", data: { scene: "settings" } }));
await page.getByRole("heading", { name: "设置" }).waitFor();
const narrowOverflow = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: window.innerWidth }));
if (narrowOverflow.width > narrowOverflow.viewport + 1) throw new Error(`窄屏发生横向溢出: ${JSON.stringify(narrowOverflow)}`);
await page.screenshot({ path: "qa-artifacts/management-settings-680.png" });

if (consoleErrors.length) throw new Error(`浏览器控制台错误: ${consoleErrors.join(" | ")}`);
await browser.close();
console.log("MANAGEMENT_QA_PASS", JSON.stringify({ screenshots: [
  "management-library-1200.png",
  "management-editor-1200.png",
  "management-settings-760.png",
  "management-settings-680.png",
] }));
