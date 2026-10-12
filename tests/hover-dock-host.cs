using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Collections.Generic;

// 吸附与悬停的原生宿主：仅控制隔离窗口和鼠标位置，不访问剪贴板、不发送聊天内容。
public static class ShanyuHoverDockHost {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int size; public Rect monitor, work; public int flags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, uint attribute, out Rect r, int size);
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    static IntPtr originalForeground;
    static Point originalCursor, lastPointer;
    static bool movedPointer;
    // 只读实际 Z 顺序，窗口间存在辅助句柄时也能检查三者的相对前后关系。
    static int ZOrder(IntPtr h) {
        int order = 0;
        while ((h = GetWindow(h, 3)) != IntPtr.Zero) order++;
        return order;
    }
    static Dictionary<string, object> Inspect(IntPtr h) {
        Rect rect, frame; GetWindowRect(h, out rect); if (DwmGetWindowAttribute(h, 9, out frame, 16) != 0) frame = rect;
        var client = new Point(); ClientToScreen(h, ref client);
        var monitor = new MonitorInfo { size = Marshal.SizeOf(typeof(MonitorInfo)) }; GetMonitorInfo(MonitorFromWindow(h, 2), ref monitor);
        var style = GetWindowLong(h, -20);
        return new Dictionary<string, object> { {"handle", h.ToInt64()}, {"above", GetWindow(h, 3).ToInt64()}, {"below", GetWindow(h, 2).ToInt64()}, {"zOrder", ZOrder(h)}, {"rect", rect}, {"frame", frame}, {"client", client}, {"work", monitor.work}, {"monitor", monitor.monitor}, {"dpi", GetDpiForWindow(h)}, {"visible", IsWindowVisible(h)}, {"iconic", IsIconic(h)}, {"toolWindow", (style & 0x80) != 0}, {"appWindow", (style & 0x40000) != 0}, {"noActivate", (style & 0x08000000) != 0}, {"topmost", (style & 8) != 0} };
    }
    static long Number(Dictionary<string, object> value, string name, long fallback) { return value.ContainsKey(name) ? Convert.ToInt64(value[name]) : fallback; }
    [STAThread] public static void Main(string[] args) {
        originalForeground = GetForegroundWindow(); GetCursorPos(out originalCursor);
        Application.EnableVisualStyles();
        var form = new Form { Text = "吸附隔离验收宿主", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(700, 120, 600, 540), ShowInTaskbar = false };
        form.Controls.Add(new Label { Text = "仅验证窗口跟随、边缘可见性与正文悬停", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        // 使用真实 UI Automation 元数据区分消息编辑器与搜索框，不绕过产品的输入框检测。
        var input = new ShanyuChatInputBox { Name = "ShanyuChatInput", Multiline = true, Dock = DockStyle.Bottom, Height = 100 };
        var search = new ShanyuChatInputBox { Name = "ShanyuSearchInput", Dock = DockStyle.Top, Visible = false };
        form.Controls.Add(input); form.Controls.Add(search);
        Form viewer = null, secondChat = null;
        var timer = new Timer { Interval = 30 }; long nonce = 0;
        timer.Tick += delegate {
            Dictionary<string, object> command = null;
            try { if (File.Exists(args[1])) command = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[1])); } catch (IOException) {} catch (ArgumentException) {}
            if (command != null && Number(command, "nonce", 0) != nonce) {
                nonce = Number(command, "nonce", 0); var action = (string)command["command"]; var handle = new IntPtr(Number(command, "handle", form.Handle.ToInt64()));
                if (action == "move") SetWindowPos(handle, IntPtr.Zero, (int)Number(command, "x", 0), (int)Number(command, "y", 0), (int)Number(command, "width", 330), (int)Number(command, "height", 550), 0x0014);
                if (action == "focus") SetForegroundWindow(handle);
                if (action == "focus-list") { form.ActiveControl = null; SetFocus(form.Handle); }
                if (action == "minimize") ShowWindow(handle, 6);
                if (action == "restore") ShowWindow(handle, 4);
                if (action == "raise") SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                if (action == "unpin") SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                if (action == "page") {
                    var page = (string)command["page"];
                    input.Visible = page != "none" && page != "search" && page != "hidden";
                    input.Enabled = page != "disabled"; input.ReadOnly = page == "readonly"; input.UseSystemPasswordChar = page == "password";
                    input.ValuePatternAvailable = page != "unsupported";
                    search.Visible = page == "search";
                }
                if (action == "viewer") {
                    if (viewer == null || viewer.IsDisposed) {
                        viewer = new Form { Text = "图片", Bounds = new Rectangle(180, 150, 700, 500), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
                        viewer.Controls.Add(new Label { Text = "同进程图片查看页面：没有消息输入框", Dock = DockStyle.Fill });
                        viewer.Show(form);
                    }
                    SetForegroundWindow(viewer.Handle);
                }
                if (action == "second-chat") {
                    if (secondChat == null || secondChat.IsDisposed) {
                        secondChat = new Form { Text = "独立聊天", Bounds = new Rectangle(200, 100, 620, 500), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
                        secondChat.Controls.Add(new ShanyuChatInputBox { Name = "ShanyuChatInput", Multiline = true, Dock = DockStyle.Bottom, Height = 100 });
                        secondChat.Show();
                    }
                    SetForegroundWindow(secondChat.Handle);
                }
                if (action == "pointer" || action == "click") {
                    lastPointer = new Point { x = (int)Number(command, "x", 0), y = (int)Number(command, "y", 0) }; SetCursorPos(lastPointer.x, lastPointer.y); movedPointer = true;
                    // 真实鼠标点击不临时修改被点窗口的置顶属性，避免验收本身改变待验证的层级。
                    if (action == "click") { mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
                }
                if (action == "close") { form.Close(); return; }
            }
            var windows = new List<object> { Inspect(form.Handle) };
            if (viewer != null && !viewer.IsDisposed) windows.Add(Inspect(viewer.Handle));
            if (secondChat != null && !secondChat.IsDisposed) windows.Add(Inspect(secondChat.Handle));
            if (command != null && command.ContainsKey("handles")) foreach (var value in (System.Collections.IEnumerable)command["handles"]) windows.Add(Inspect(new IntPtr(Convert.ToInt64(value))));
            Point cursor; GetCursorPos(out cursor);
            var foreground = GetForegroundWindow(); uint foregroundPid; GetWindowThreadProcessId(foreground, out foregroundPid);
            var probe = command == null ? cursor : new Point { x = (int)Number(command, "pointX", cursor.x), y = (int)Number(command, "pointY", cursor.y) };
            // 移动命令后立即探测目标内部的四个边缘，不等待客户端的 30ms 跟随刷新。
            // 可直接检查旧面板位置与新目标相交时，目标是否仍先于面板显示。
            var targetProbes = new List<object>();
            if (command != null && command.ContainsKey("probeTarget") && (bool)command["probeTarget"]) {
                Rect target; GetWindowRect(form.Handle, out target);
                foreach (var point in new[] {
                    new Point { x = target.left + 18, y = target.top + 60 },
                    new Point { x = target.right - 18, y = target.top + 60 },
                    new Point { x = target.left + 18, y = target.bottom - 18 },
                    new Point { x = target.right - 18, y = target.bottom - 18 }
                }) targetProbes.Add(new Dictionary<string, object> { {"x", point.x}, {"y", point.y}, {"window", GetAncestor(WindowFromPoint(point), 2).ToInt64()} });
            }
            // 只读实际系统任务栏的范围与层级，验证遮挡关系；不移动、置顶或激活任务栏。
            var taskbar = FindWindow("Shell_TrayWnd", null);
            File.WriteAllText(args[0], json.Serialize(new Dictionary<string, object> { {"nonce", nonce}, {"hwnd", form.Handle.ToInt64()}, {"inputFocused", input.Focused}, {"viewer", viewer == null || viewer.IsDisposed ? 0 : viewer.Handle.ToInt64()}, {"secondChat", secondChat == null || secondChat.IsDisposed ? 0 : secondChat.Handle.ToInt64()}, {"pid", System.Diagnostics.Process.GetCurrentProcess().Id}, {"foreground", foreground.ToInt64()}, {"foregroundPid", foregroundPid}, {"cursor", cursor}, {"windowAtCursor", GetAncestor(WindowFromPoint(cursor), 2).ToInt64()}, {"probedWindow", GetAncestor(WindowFromPoint(probe), 2).ToInt64()}, {"targetProbes", targetProbes}, {"windows", windows}, {"taskbar", Inspect(taskbar)} }), new System.Text.UTF8Encoding(false));
        };
        form.FormClosed += delegate {
            if (viewer != null) viewer.Dispose(); if (secondChat != null) secondChat.Dispose();
            timer.Stop(); Point cursor; GetCursorPos(out cursor);
            if (movedPointer && cursor.x == lastPointer.x && cursor.y == lastPointer.y) SetCursorPos(originalCursor.x, originalCursor.y);
            if (GetForegroundWindow() == form.Handle) SetForegroundWindow(originalForeground);
        };
        form.Shown += delegate { SetForegroundWindow(form.Handle); timer.Start(); };
        Application.Run(form); timer.Dispose();
    }
}
