namespace QuickPhrase.Core;

/// <summary>
/// 平台无关的快捷键修饰键。数值会进入持久化配置，禁止调整既有成员的稳定值。
/// </summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// QuickPhrase 支持的普通按键。每个成员显式指定稳定值，不能替换为 Win32 Virtual Key。
/// </summary>
public enum ShortcutKey
{
    Space = 1,
    A = 2,
    B = 3,
    C = 4,
    D = 5,
    E = 6,
    F = 7,
    G = 8,
    H = 9,
    I = 10,
    J = 11,
    K = 12,
    L = 13,
    M = 14,
    N = 15,
    O = 16,
    P = 17,
    Q = 18,
    R = 19,
    S = 20,
    T = 21,
    U = 22,
    V = 23,
    W = 24,
    X = 25,
    Y = 26,
    Z = 27,
    Digit0 = 28,
    Digit1 = 29,
    Digit2 = 30,
    Digit3 = 31,
    Digit4 = 32,
    Digit5 = 33,
    Digit6 = 34,
    Digit7 = 35,
    Digit8 = 36,
    Digit9 = 37,
    F1 = 38,
    F2 = 39,
    F3 = 40,
    F4 = 41,
    F5 = 42,
    F6 = 43,
    F7 = 44,
    F8 = 45,
    F9 = 46,
    F10 = 47,
    F11 = 48,
    F12 = 49,
}

/// <summary>
/// 一个可比较、可持久化的快捷键组合，仅表达 Core 认识的修饰键和普通按键。
/// </summary>
public readonly record struct ShortcutChord(ShortcutModifiers Modifiers, ShortcutKey Key);

/// <summary>快捷键纯规则校验使用的稳定错误码。</summary>
public static class ShortcutValidationErrorCodes
{
    public const string ModifierRequired = "SHORTCUT_MODIFIER_REQUIRED";
    public const string ModifierUnsupported = "SHORTCUT_MODIFIER_UNSUPPORTED";
    public const string KeyRequired = "SHORTCUT_KEY_REQUIRED";
    public const string KeyUnsupported = "SHORTCUT_KEY_UNSUPPORTED";
}

/// <summary>快捷键纯规则校验结果，不包含平台注册或持久化状态。</summary>
public readonly record struct ShortcutValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ShortcutValidationResult Valid() => new(true, null, null);

    public static ShortcutValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

/// <summary>
/// 校验快捷键的跨平台领域规则。Windows 键码映射和系统占用检测必须留在 Platform.Windows。
/// </summary>
public static class ShortcutChordValidator
{
    private const ShortcutModifiers SupportedModifiers =
        ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win;

    public static ShortcutValidationResult Validate(ShortcutChord chord)
    {
        if ((chord.Modifiers & ~SupportedModifiers) != ShortcutModifiers.None)
        {
            return ShortcutValidationResult.Invalid(
                ShortcutValidationErrorCodes.ModifierUnsupported,
                "快捷键包含不支持的修饰键。");
        }

        if (chord.Modifiers == ShortcutModifiers.None)
        {
            return ShortcutValidationResult.Invalid(
                ShortcutValidationErrorCodes.ModifierRequired,
                "快捷键至少需要一个修饰键。");
        }

        if ((int)chord.Key == 0)
        {
            return ShortcutValidationResult.Invalid(
                ShortcutValidationErrorCodes.KeyRequired,
                "快捷键不能只包含修饰键。");
        }

        if (!Enum.IsDefined(chord.Key))
        {
            return ShortcutValidationResult.Invalid(
                ShortcutValidationErrorCodes.KeyUnsupported,
                "快捷键包含不支持的普通按键。");
        }

        return ShortcutValidationResult.Valid();
    }
}

/// <summary>
/// 暂存注册的匿名令牌。内部值只用于服务实现关联暂存状态，不暴露 HWND、Virtual Key 或其他平台类型。
/// </summary>
public readonly record struct ShortcutStageToken
{
    private readonly Guid value;

    private ShortcutStageToken(Guid value)
    {
        this.value = value;
    }

    public bool IsEmpty => value == Guid.Empty;

    public static ShortcutStageToken Create() => new(Guid.NewGuid());
}

/// <summary>暂存新快捷键的结果；失败时 Token 为空，并返回可稳定识别的错误信息。</summary>
public readonly record struct ShortcutStageResult(
    bool IsSuccess,
    ShortcutStageToken Token,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ShortcutStageResult Success(ShortcutStageToken token) => new(true, token, null, null);

    public static ShortcutStageResult Failure(string errorCode, string errorMessage) =>
        new(false, default, errorCode, errorMessage);
}

/// <summary>提交暂存快捷键的结果，不泄漏平台注册句柄或实现细节。</summary>
public readonly record struct ShortcutApplyResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ShortcutApplyResult Success() => new(true, null, null);

    public static ShortcutApplyResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

/// <summary>
/// 应用级快捷键服务契约。Stage 只暂存新注册，Commit 才替换当前注册，Rollback 负责释放暂存注册。
/// </summary>
public interface IShortcutService : IAsyncDisposable
{
    event EventHandler? Activated;

    ShortcutChord ActiveChord { get; }

    Task<ShortcutStageResult> StageAsync(
        ShortcutChord chord,
        CancellationToken cancellationToken = default);

    Task<ShortcutApplyResult> CommitAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default);

    void SetEnabled(bool enabled);
}
