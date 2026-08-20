using System.Reflection;
using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 锁定平台无关的快捷键契约：持久化数值、纯规则校验与服务边界都不能依赖 WPF/Win32。
/// </summary>
public sealed class ShortcutCoreTests
{
    [Fact]
    public void ShortcutModifiersUseStableFlagValues()
    {
        Assert.Equal(0, (int)ShortcutModifiers.None);
        Assert.Equal(1, (int)ShortcutModifiers.Ctrl);
        Assert.Equal(2, (int)ShortcutModifiers.Alt);
        Assert.Equal(4, (int)ShortcutModifiers.Shift);
        Assert.Equal(8, (int)ShortcutModifiers.Win);
    }

    [Fact]
    public void ShortcutKeysUseStablePersistenceValues()
    {
        var expected = new Dictionary<ShortcutKey, int>
        {
            [ShortcutKey.Space] = 1,
            [ShortcutKey.A] = 2,
            [ShortcutKey.B] = 3,
            [ShortcutKey.C] = 4,
            [ShortcutKey.D] = 5,
            [ShortcutKey.E] = 6,
            [ShortcutKey.F] = 7,
            [ShortcutKey.G] = 8,
            [ShortcutKey.H] = 9,
            [ShortcutKey.I] = 10,
            [ShortcutKey.J] = 11,
            [ShortcutKey.K] = 12,
            [ShortcutKey.L] = 13,
            [ShortcutKey.M] = 14,
            [ShortcutKey.N] = 15,
            [ShortcutKey.O] = 16,
            [ShortcutKey.P] = 17,
            [ShortcutKey.Q] = 18,
            [ShortcutKey.R] = 19,
            [ShortcutKey.S] = 20,
            [ShortcutKey.T] = 21,
            [ShortcutKey.U] = 22,
            [ShortcutKey.V] = 23,
            [ShortcutKey.W] = 24,
            [ShortcutKey.X] = 25,
            [ShortcutKey.Y] = 26,
            [ShortcutKey.Z] = 27,
            [ShortcutKey.Digit0] = 28,
            [ShortcutKey.Digit1] = 29,
            [ShortcutKey.Digit2] = 30,
            [ShortcutKey.Digit3] = 31,
            [ShortcutKey.Digit4] = 32,
            [ShortcutKey.Digit5] = 33,
            [ShortcutKey.Digit6] = 34,
            [ShortcutKey.Digit7] = 35,
            [ShortcutKey.Digit8] = 36,
            [ShortcutKey.Digit9] = 37,
            [ShortcutKey.F1] = 38,
            [ShortcutKey.F2] = 39,
            [ShortcutKey.F3] = 40,
            [ShortcutKey.F4] = 41,
            [ShortcutKey.F5] = 42,
            [ShortcutKey.F6] = 43,
            [ShortcutKey.F7] = 44,
            [ShortcutKey.F8] = 45,
            [ShortcutKey.F9] = 46,
            [ShortcutKey.F10] = 47,
            [ShortcutKey.F11] = 48,
            [ShortcutKey.F12] = 49,
        };

        Assert.Equal(49, expected.Count);
        Assert.Equal(expected.Count, Enum.GetValues<ShortcutKey>().Length);
        foreach (var (key, value) in expected)
            Assert.Equal(value, (int)key);
    }

    [Fact]
    public void EverySupportedKeyIsValidWithALegalModifier()
    {
        foreach (var key in Enum.GetValues<ShortcutKey>())
        {
            var result = ShortcutChordValidator.Validate(new ShortcutChord(ShortcutModifiers.Ctrl, key));

            Assert.True(result.IsValid, $"{key} 应当是受支持按键，但校验失败：{result.ErrorCode} {result.ErrorMessage}");
            Assert.Null(result.ErrorCode);
            Assert.Null(result.ErrorMessage);
        }
    }

    [Fact]
    public void MissingModifierIsRejectedWithStableChineseError()
    {
        var result = ShortcutChordValidator.Validate(new ShortcutChord(ShortcutModifiers.None, ShortcutKey.Space));

        Assert.False(result.IsValid);
        Assert.Equal("SHORTCUT_MODIFIER_REQUIRED", result.ErrorCode);
        Assert.Equal("快捷键至少需要一个修饰键。", result.ErrorMessage);
    }

    [Fact]
    public void UnknownModifierBitIsRejectedWithStableChineseError()
    {
        var result = ShortcutChordValidator.Validate(new ShortcutChord((ShortcutModifiers)16, ShortcutKey.Space));

        Assert.False(result.IsValid);
        Assert.Equal("SHORTCUT_MODIFIER_UNSUPPORTED", result.ErrorCode);
        Assert.Equal("快捷键包含不支持的修饰键。", result.ErrorMessage);
    }

    [Fact]
    public void MissingKeyIsRejectedAsModifierOnlyInput()
    {
        var result = ShortcutChordValidator.Validate(new ShortcutChord(ShortcutModifiers.Alt, (ShortcutKey)0));

        Assert.False(result.IsValid);
        Assert.Equal("SHORTCUT_KEY_REQUIRED", result.ErrorCode);
        Assert.Equal("快捷键不能只包含修饰键。", result.ErrorMessage);
    }

    [Fact]
    public void UnknownKeyIsRejectedWithStableChineseError()
    {
        var result = ShortcutChordValidator.Validate(new ShortcutChord(ShortcutModifiers.Alt, (ShortcutKey)50));

        Assert.False(result.IsValid);
        Assert.Equal("SHORTCUT_KEY_UNSUPPORTED", result.ErrorCode);
        Assert.Equal("快捷键包含不支持的普通按键。", result.ErrorMessage);
    }

    [Fact]
    public void ShortcutServiceContractExposesStageCommitRollbackLifecycle()
    {
        var contract = typeof(IShortcutService);
        Assert.Contains(typeof(IAsyncDisposable), contract.GetInterfaces());
        Assert.NotNull(contract.GetEvent(nameof(IShortcutService.Activated)));
        Assert.Equal(typeof(ShortcutChord), contract.GetProperty(nameof(IShortcutService.ActiveChord))!.PropertyType);
        AssertMethod(contract, nameof(IShortcutService.StageAsync), typeof(Task<ShortcutStageResult>), typeof(ShortcutChord), typeof(CancellationToken));
        AssertMethod(contract, nameof(IShortcutService.CommitAsync), typeof(Task<ShortcutApplyResult>), typeof(ShortcutStageToken), typeof(CancellationToken));
        AssertMethod(contract, nameof(IShortcutService.RollbackAsync), typeof(Task), typeof(ShortcutStageToken), typeof(CancellationToken));
        AssertMethod(contract, nameof(IShortcutService.SetEnabled), typeof(void), typeof(bool));
    }

    [Fact]
    public void StageTokenAndCoreAssemblyRemainPlatformAgnostic()
    {
        var tokenMembers = typeof(ShortcutStageToken)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Select(member => member switch
            {
                FieldInfo field => field.FieldType.FullName ?? field.FieldType.Name,
                PropertyInfo property => property.PropertyType.FullName ?? property.PropertyType.Name,
                _ => string.Empty,
            })
            .ToArray();

        Assert.DoesNotContain(tokenMembers, name =>
            name.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Presentation", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hwnd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VirtualKey", StringComparison.OrdinalIgnoreCase));

        var references = typeof(ShortcutChord).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, name => name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("QuickPhrase.Platform.Windows", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertMethod(Type contract, string name, Type returnType, params Type[] parameterTypes)
    {
        var method = contract.GetMethod(name, parameterTypes);
        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
    }
}
