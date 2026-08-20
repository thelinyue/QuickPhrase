using Microsoft.Win32;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 管理当前用户的 Windows 登录启动项。注册表只保存当前安装路径，
/// 不进入 Core，避免领域模型依赖 Windows；应用启动时由 Desktop 重新校准。
/// </summary>
internal sealed class WindowsStartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public WindowsStartupRegistration(string valueName = "QuickPhrase")
    {
        if (string.IsNullOrWhiteSpace(valueName)) throw new ArgumentException("启动项名称不能为空。", nameof(valueName));
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(GetCommand());
    }

    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public void SetEnabled(bool enabled, string? executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。", null);
        if (!enabled)
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("启用启动项时必须提供程序路径。", nameof(executablePath));
        var normalized = Path.GetFullPath(executablePath);
        key.SetValue(_valueName, $"\"{normalized}\" --background", RegistryValueKind.String);
    }

    public void SetRawCommand(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。", null);
        if (string.IsNullOrWhiteSpace(command)) key.DeleteValue(_valueName, throwOnMissingValue: false);
        else key.SetValue(_valueName, command, RegistryValueKind.String);
    }
}
