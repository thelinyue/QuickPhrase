namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// Phase 4 的安全模拟投递。它只验证 Launcher 的键盘路径和状态反馈，绝不触碰目标窗口、剪贴板或发送 API。
/// 仅用于架构测试，不进入桌面程序发布包。
/// </summary>
public sealed record MockDeliveryResult(bool Inserted, bool Sent, string Code, string Message);

public static class MockDeliverySession
{
    public static MockDeliveryResult Execute(string title, bool send)
    {
        if (send)
            return new MockDeliveryResult(true, false, "CAPABILITY_UNVERIFIED", $"已模拟选择「{title}」，未验证目标应用，未发送。");

        return new MockDeliveryResult(true, false, "MOCK_INSERTED", $"已模拟插入「{title}」，未写入目标应用。");
    }
}
