using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Onboarding;

/// <summary>闪语首次使用向导在 Desktop 内部使用的步骤，不写入设置数据库。</summary>
public enum OnboardingStep
{
    Welcome = 0,
    Category = 1,
    Phrase = 2,
    Practice = 3,
    Complete = 4,
}

/// <summary>闪念调用模式。Practice 只把选中的话术返回给向导，不进入正式投递链。</summary>
public enum LauncherInvocationMode
{
    Normal,
    Practice,
}

/// <summary>Launcher 的通用调用上下文，避免 LauncherWindow 直接依赖向导类型。</summary>
public sealed record LauncherInvocationContext(
    LauncherInvocationMode Mode,
    Func<Phrase, Task<bool>>? SelectionHandler = null);
