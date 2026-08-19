/**
 * WebView2 版本化桥接。浏览器/Sites 环境不会创建宿主副作用；正式管理页只通过 request 调用 Desktop。
 * 所有请求都带 requestId 和相对 timeoutMs，超时会尝试发送 system.cancel。
 */
export class ManagementClient {
  constructor(webview) {
    this.webview = webview;
    this.pending = new Map();
    this.listeners = new Set();
    this.onMessage = (event) => this.handleMessage(event);
    webview?.addEventListener("message", this.onMessage);
  }

  dispose() {
    this.webview?.removeEventListener("message", this.onMessage);
    for (const item of this.pending.values()) item.reject(new Error("管理界面已关闭"));
    this.pending.clear();
  }

  onEvent(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  request(type, payload = {}, timeoutMs = 5000) {
    if (!this.webview) return Promise.reject(new Error("当前不是 WebView2 宿主"));
    const requestId = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(requestId);
        this.webview.postMessage(JSON.stringify({ protocolVersion: 1, requestId: crypto.randomUUID(), type: "system.cancel", payload: { requestId }, timeoutMs: 1000 }));
        reject(new Error("IPC_TIMEOUT"));
      }, timeoutMs + 250);
      this.pending.set(requestId, { resolve, reject, timer });
      this.webview.postMessage(JSON.stringify({ protocolVersion: 1, requestId, type, payload, timeoutMs }));
    });
  }

  handleMessage(event) {
    try {
      const message = JSON.parse(event.data);
      if (message.event) {
        for (const listener of this.listeners) listener(message);
        return;
      }
      const pending = this.pending.get(message.requestId);
      if (!pending) return;
      this.pending.delete(message.requestId);
      window.clearTimeout(pending.timer);
      if (message.ok) pending.resolve(message.data);
      else pending.reject(Object.assign(new Error(message.error?.message || "管理界面请求失败"), { code: message.error?.code }));
    } catch {
      document.documentElement.dataset.hostStatus = "error";
    }
  }
}

export function installManagementBridge() {
  const webview = window.chrome?.webview;
  const hostMode = webview ? "webview2" : "browser";
  document.documentElement.dataset.hostMode = hostMode;
  document.documentElement.dataset.hostStatus = webview ? "connecting" : "mock";
  const client = new ManagementClient(webview);
  window.__quickPhraseBridge = client;
  if (webview) {
    client.request("system.ping", {}, 5000)
      .then(() => { document.documentElement.dataset.hostStatus = "ready"; })
      .catch(() => { document.documentElement.dataset.hostStatus = "error"; });
  }
  return () => {
    client.dispose();
    delete window.__quickPhraseBridge;
  };
}
