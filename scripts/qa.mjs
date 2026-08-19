import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

mkdirSync("qa-artifacts", { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1024 }, deviceScaleFactor: 1 });
const consoleErrors = [];
page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
page.on("pageerror", (error) => consoleErrors.push(error.message));

async function reset() {
  await page.goto("http://127.0.0.1:4173/", { waitUntil: "networkidle" });
  await page.waitForTimeout(120);
}
async function scene(name) {
  await page.locator(".prototype-switcher").getByRole("button", { name }).click();
  await page.waitForTimeout(120);
}
async function setPrototypeState(label) {
  await page.getByLabel("原型状态").selectOption({ label });
}

await reset();
await page.screenshot({ path: "qa-artifacts/library-1440.png" });
if (!(await page.getByRole("heading", { name: "全部话术" }).isVisible())) throw new Error("话术库默认场景未显示");
await page.keyboard.press("Control+K");
if (await page.evaluate(() => document.activeElement?.getAttribute("aria-label")) !== "主窗口搜索话术") throw new Error("Ctrl+K 未聚焦主窗口搜索");

await scene("Quick Launcher");
const launcherInput = page.getByLabel("搜索话术、拼音或首字母");
await launcherInput.fill("hfcc");
await page.keyboard.press("Tab");
if (!(await page.getByText("预览", { exact: true }).isVisible())) throw new Error("Tab 未打开 Launcher 预览");
await page.screenshot({ path: "qa-artifacts/launcher-1440.png" });
await page.keyboard.press("Enter");
if (!(await page.getByText(/已插入「恢复出厂设置」/).isVisible())) throw new Error("Enter 插入反馈缺失");

await reset();
await scene("Quick Launcher");
await page.getByLabel("搜索话术、拼音或首字母").fill("hf");
const resultCount = await page.locator(".launcher-result").count();
if (resultCount < 6) throw new Error(`hf 多结果不足: ${resultCount}`);
if (!(await page.locator(".launcher-window.is-expanded").count())) throw new Error("多结果未使用扩展高度");
const scrollState = await page.locator(".launcher-results").evaluate((node) => ({ clientHeight: node.clientHeight, scrollHeight: node.scrollHeight }));
if (scrollState.scrollHeight <= scrollState.clientHeight) throw new Error(`多结果未形成内部滚动: ${JSON.stringify(scrollState)}`);
await page.screenshot({ path: "qa-artifacts/launcher-multi-1440.png" });

await reset();
await scene("Quick Launcher");
await page.getByLabel("搜索话术、拼音或首字母").fill("abcxyz");
if (!(await page.getByText("没有找到“abcxyz”", { exact: true }).isVisible())) throw new Error("零结果状态缺失");
await page.getByRole("button", { name: "新建话术“abcxyz”" }).click();
if (!(await page.getByLabel("话术标题").inputValue() === "abcxyz")) throw new Error("零结果新建未带入查询");
if (await page.getByText("模板变量", { exact: true }).count()) throw new Error("V1 不应显示模板变量");

await reset();
await setPrototypeState("目标未识别");
await scene("Quick Launcher");
await page.getByLabel("搜索话术、拼音或首字母").fill("sn");
if (!(await page.getByText("当前应用暂不支持自动插入", { exact: true }).isVisible())) throw new Error("应用降级提示缺失");
await page.keyboard.press("Enter");
if (!(await page.getByText(/Ctrl \+ V/).isVisible())) throw new Error("剪贴板降级反馈缺失");
await page.screenshot({ path: "qa-artifacts/launcher-unsupported-1440.png" });

await reset();
await setPrototypeState("快捷键冲突");
await scene("设置");
if (!(await page.getByText("Alt + Space 已被其他应用占用", { exact: true }).isVisible())) throw new Error("快捷键冲突提示缺失");
await page.getByRole("button", { name: "重新设置" }).click();
if (!(await page.getByText("可用", { exact: true }).count())) throw new Error("快捷键冲突修复后未恢复可用");
await page.screenshot({ path: "qa-artifacts/settings-conflict-1440.png" });

await reset();
await setPrototypeState("首次使用");
if (!(await page.getByText("把常用回复，", { exact: false }).isVisible())) throw new Error("首次使用引导缺失");
await page.getByRole("button", { name: /试一下/ }).click();
if (!(await page.locator(".scene-launcher").count())) throw new Error("首次使用试用未打开 Launcher");
if (await page.getByLabel("搜索话术、拼音或首字母").inputValue() !== "hf") throw new Error("首次使用未带入示例查询");
await page.screenshot({ path: "qa-artifacts/onboarding-1440.png" });

await reset();
await scene("编辑话术");
await page.getByLabel("话术标题").fill("请求设备序列号");
await page.getByLabel("话术正文").fill("请提供设备序列号（SN），方便我们进一步确认设备信息。");
if (await page.getByText("模板变量", { exact: true }).count()) throw new Error("编辑器仍暴露模板变量入口");
await page.screenshot({ path: "qa-artifacts/editor-1440.png" });
await page.getByRole("button", { name: "保存话术" }).click();
if (!(await page.getByText("话术已保存").isVisible())) throw new Error("编辑保存反馈缺失");

await reset();
await scene("设置");
await page.getByText("剪贴板兼容模式").click();
await page.screenshot({ path: "qa-artifacts/settings-1440.png" });
await page.getByRole("button", { name: "完成" }).click();
if (!(await page.getByText("设置已应用").isVisible())) throw new Error("设置应用反馈缺失");
await page.getByLabel("QuickPhrase 托盘菜单").click();
if (!(await page.getByText("快速搜索", { exact: false }).isVisible())) throw new Error("托盘菜单未打开");
await page.getByRole("button", { name: "QuickPhrase 托盘菜单" }).click();

for (const viewport of [{ width: 1200, height: 760 }, { width: 1024, height: 768 }]) {
  await page.setViewportSize(viewport);
  await reset();
  const overflow = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: window.innerWidth }));
  if (overflow.width > overflow.viewport + 2) throw new Error(`视口 ${viewport.width} 存在横向溢出: ${JSON.stringify(overflow)}`);
  await page.screenshot({ path: `qa-artifacts/library-${viewport.width}.png` });
}

if (consoleErrors.length) throw new Error(`浏览器控制台错误: ${consoleErrors.join(" | ")}`);
await browser.close();
console.log("QA_PASS", JSON.stringify({ consoleErrors, screenshots: ["library-1440.png", "launcher-1440.png", "launcher-multi-1440.png", "launcher-unsupported-1440.png", "onboarding-1440.png", "editor-1440.png", "settings-1440.png", "settings-conflict-1440.png", "library-1200.png", "library-1024.png"] }));
