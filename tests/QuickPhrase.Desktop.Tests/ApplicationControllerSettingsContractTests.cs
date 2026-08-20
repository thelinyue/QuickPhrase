using System.IO;

namespace QuickPhrase.Desktop.Tests;

public sealed class ApplicationControllerSettingsContractTests
{
    [Fact]
    public void ApplySettingsAsync_SynchronizesRestoredSnapshotEvenWhenShortcutChangeFails()
    {
        var method = ReadApplySettingsMethod().Replace("\r\n", "\n");
        const string failureStart = "if (!result.IsSuccess || result.Value is null)";
        var start = method.IndexOf(failureStart, StringComparison.Ordinal);
        var end = method.IndexOf("return result;", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "无法定位快捷键保存失败分支。");
        var failureBranch = method[start..(end + "return result;".Length)];
        Assert.Contains("if (result.Value is not null)\n                    _settings = result.Value;", failureBranch, StringComparison.Ordinal);
        Assert.Contains("return result;", failureBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateLauncherScope();", failureBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySettingsAsync_PropagatesCancellationInsteadOfConvertingItToSaveFailure()
    {
        var method = ReadApplySettingsMethod();
        var cancellationCatch = method.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal);
        var generalCatch = method.IndexOf("catch (Exception exception)", cancellationCatch, StringComparison.Ordinal);

        Assert.True(cancellationCatch >= 0, "ApplySettingsAsync 必须显式处理 OperationCanceledException。");
        Assert.True(generalCatch > cancellationCatch, "取消处理必须位于泛化异常处理之前。");
        var cancellationBranch = method[cancellationCatch..generalCatch];
        Assert.Contains("_startupRegistration.SetRawCommand(previousCommand)", cancellationBranch, StringComparison.Ordinal);
        Assert.Contains("throw;", cancellationBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("SETTINGS_SAVE_FAILED", cancellationBranch, StringComparison.Ordinal);
        Assert.Contains("SETTINGS_SAVE_FAILED", method[generalCatch..], StringComparison.Ordinal);
    }

    private static string ReadApplySettingsMethod()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));
        var start = source.IndexOf("private async Task<RepositoryResult<AppSettings>> ApplySettingsAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static string GetStartupExecutablePath", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "无法定位 ApplySettingsAsync 方法源码。");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位 QuickPhrase 仓库根目录。");
    }
}
