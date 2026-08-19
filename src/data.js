/**
 * QuickPhrase 的示例话术只服务于当前交互原型。
 * 产品边界是固定标准话术的快速搜索与插入，不记录客户信息，也不做模板变量替换。
 */
export const initialPhrases = [
  { id: "factory-reset", title: "恢复出厂设置", body: "恢复出厂设置前请先备份重要数据。", category: "设备问题", tags: ["客服", "设备"], keywords: ["hfcc", "恢复出厂", "出厂设置"], shortcutMode: "quick", shortcut: "Alt + 1", favorite: true, usageCount: 128, lastUsed: "刚刚" },
  { id: "network-error", title: "网络连接异常", body: "请检查网络连接是否正常，或重启设备后重试。", category: "设备问题", tags: ["客服", "网络"], keywords: ["wl", "网络", "连接"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 86, lastUsed: "今天 09:42" },
  { id: "password-reset", title: "密码重置", body: "您可以通过绑定的手机号或邮箱重置密码。", category: "账户问题", tags: ["客服", "账户"], keywords: ["mmcz", "密码", "重置"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 73, lastUsed: "今天 09:18" },
  { id: "after-sales", title: "售后处理说明", body: "如需售后支持，请提供订单号和问题描述。", category: "售后服务", tags: ["客服", "售后"], keywords: ["sh", "售后", "处理"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 64, lastUsed: "昨天 18:21" },
  { id: "order-confirm", title: "订单信息确认", body: "为了尽快为您处理，请提供订单号和收货手机号。", category: "订单问题", tags: ["客服", "订单"], keywords: ["dd", "订单", "确认"], shortcutMode: "quick", shortcut: "Alt + 2", favorite: true, usageCount: 52, lastUsed: "昨天 16:08" },
  { id: "serial-number", title: "请提供设备序列号", body: "请提供设备序列号（SN），方便我们进一步确认设备信息。", category: "信息收集", tags: ["设备", "SN"], keywords: ["sn", "sbxlh", "序列号", "设备"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 43, lastUsed: "昨天 14:32" },
  { id: "hello", title: "您好，请问有什么可以帮助您的？", body: "您好，请问有什么可以帮助您的？", category: "通用话术", tags: ["客服", "开场"], keywords: ["nh", "您好", "帮助"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 38, lastUsed: "昨天 11:06" },
  { id: "factory-steps", title: "恢复出厂操作步骤", body: "请进入设置 > 系统 > 重置，按照页面提示完成操作。", category: "设备问题", tags: ["设备", "步骤"], keywords: ["hfczbz", "恢复出厂", "操作步骤"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 31, lastUsed: "周一 16:42" },
  { id: "factory-notice", title: "恢复出厂注意事项", body: "操作前请备份照片、联系人等重要数据，并保持设备电量充足。", category: "设备问题", tags: ["设备", "注意事项"], keywords: ["hfczzysx", "恢复出厂", "注意事项"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 27, lastUsed: "周一 14:18" },
  { id: "restore-config", title: "恢复配置", body: "配置恢复完成后，请重新打开应用确认设置是否生效。", category: "设备问题", tags: ["设备", "配置"], keywords: ["hfpeizhi", "恢复配置"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 22, lastUsed: "周一 11:06" },
  { id: "restore-network", title: "恢复网络配置", body: "请在网络设置中选择恢复默认配置，然后重新连接当前网络。", category: "网络问题", tags: ["网络", "配置"], keywords: ["hfwlpz", "恢复网络", "网络配置"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 19, lastUsed: "上周五 17:30" },
  { id: "default-password", title: "恢复默认密码", body: "设备恢复默认密码后，请及时设置新的登录密码。", category: "账户问题", tags: ["账户", "密码"], keywords: ["hfmrmm", "恢复默认", "默认密码"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 16, lastUsed: "上周五 15:06" },
  { id: "fault-time", title: "请提供故障发生时间", body: "请提供故障发生的具体时间，方便我们对照日志进一步确认。", category: "信息收集", tags: ["排查", "时间"], keywords: ["gzsj", "故障时间", "发生时间"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 14, lastUsed: "上周四 10:20" },
  { id: "software-version", title: "请提供当前软件版本号", body: "请提供当前软件版本号，方便我们确认对应的处理方案。", category: "信息收集", tags: ["排查", "版本"], keywords: ["rjbb", "版本号", "软件版本"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 12, lastUsed: "上周三 16:40" },
  { id: "error-screenshot", title: "请提供完整的报错截图", body: "请提供完整的报错截图，确保错误提示和页面上下文都清晰可见。", category: "信息收集", tags: ["排查", "截图"], keywords: ["bcjt", "报错截图", "截图"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 11, lastUsed: "上周三 14:12" },
  { id: "log-file", title: "请提供问题发生后的日志文件", body: "请提供问题发生后的日志文件，我们会根据日志进一步定位原因。", category: "信息收集", tags: ["排查", "日志"], keywords: ["rzwj", "日志文件", "日志"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 9, lastUsed: "上周二 18:03" },
  { id: "network-status", title: "请确认设备当前网络连接状态", body: "请确认设备当前是否可以正常连接网络，以及使用的是 Wi-Fi 还是移动网络。", category: "信息收集", tags: ["排查", "网络"], keywords: ["wllj", "网络状态", "连接状态"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 8, lastUsed: "上周二 15:26" },
  { id: "operation-steps", title: "请描述具体操作步骤和异常现象", body: "请描述一下具体的操作步骤和异常现象，方便我们复现并定位问题。", category: "信息收集", tags: ["排查", "现象"], keywords: ["czbz", "异常现象", "操作步骤"], shortcutMode: "none", shortcut: null, favorite: false, usageCount: 7, lastUsed: "上周一 11:45" },
];

export const categoryOptions = ["设备问题", "网络问题", "账户问题", "售后服务", "订单问题", "信息收集", "通用话术"];

export const appAdapters = [
  { name: "微信", hint: "输入框", status: "已识别", tone: "good" },
  { name: "企业微信", hint: "输入框", status: "已识别", tone: "good" },
  { name: "QQ", hint: "剪贴板", status: "兼容", tone: "soft" },
  { name: "Chrome", hint: "网页输入框", status: "已识别", tone: "good" },
  { name: "Edge", hint: "网页输入框", status: "已识别", tone: "good" },
];

export function normalizeQuery(value) { return value.trim().toLowerCase().replace(/\s+/g, ""); }

export function searchPhrases(phrases, value) {
  const query = normalizeQuery(value);
  if (!query) return phrases;
  return phrases
    .map((phrase) => {
      const haystack = [phrase.title, phrase.body, phrase.category, ...phrase.tags, ...phrase.keywords].join(" ").toLowerCase();
      const title = phrase.title.toLowerCase();
      const keywordIndex = phrase.keywords.findIndex((keyword) => keyword.toLowerCase().startsWith(query));
      let score = haystack.includes(query) ? 30 : 0;
      if (title.startsWith(query)) score += 25;
      if (keywordIndex >= 0) score += 40;
      if (phrase.title.includes(value.trim())) score += 55;
      return { phrase, score };
    })
    .filter(({ score }) => score > 0)
    .sort((a, b) => b.score - a.score || b.phrase.usageCount - a.phrase.usageCount)
    .map(({ phrase }) => phrase);
}
