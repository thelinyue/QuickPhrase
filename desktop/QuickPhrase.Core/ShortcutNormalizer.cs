using System.Text;

namespace QuickPhrase.Core;

/// <summary>把用户输入转换为稳定的快捷键显示值与冲突键，不访问 Windows API。</summary>
public sealed class ShortcutNormalizer : IShortcutNormalizer
{
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    public ShortcutNormalizationResult Normalize(string? shortcut, ShortcutMode mode)
    {
        if (mode == ShortcutMode.None)
            return string.IsNullOrWhiteSpace(shortcut)
                ? ShortcutNormalizationResult.Valid(new ShortcutValue(string.Empty, string.Empty))
                : ShortcutNormalizationResult.Invalid("无快捷键模式不能包含快捷键文本。");

        if (string.IsNullOrWhiteSpace(shortcut))
            return ShortcutNormalizationResult.Invalid("快捷键不能为空。");

        var normalizedInput = shortcut.Normalize(NormalizationForm.FormKC).Trim();
        var tokens = normalizedInput
            .Split(['+', '-', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .ToArray();

        var modifiers = tokens.Where(IsModifier).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var keys = tokens.Where(x => !IsModifier(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length != 1 || modifiers.Length == 0)
            return ShortcutNormalizationResult.Invalid("快捷键必须包含修饰键和一个普通按键。");

        var orderedModifiers = ModifierOrder.Where(modifier => modifiers.Contains(modifier, StringComparer.OrdinalIgnoreCase)).ToArray();
        var key = keys[0].Length == 1 ? keys[0].ToUpperInvariant() : keys[0];
        if (mode == ShortcutMode.Quick && (orderedModifiers.Length != 1 || orderedModifiers[0] != "Alt" || !char.IsDigit(key[0]) || key.Length != 1 || key[0] is < '1' or > '9'))
            return ShortcutNormalizationResult.Invalid("高频快捷键只能使用 Alt + 1 至 Alt + 9。");

        var normalized = string.Join('+', orderedModifiers.Append(key));
        var display = string.Join(" + ", orderedModifiers.Append(key));
        return ShortcutNormalizationResult.Valid(new ShortcutValue(display, normalized));
    }

    private static bool IsModifier(string token) => token is "Ctrl" or "Alt" or "Shift" or "Win";

    private static string NormalizeToken(string token)
    {
        var compact = token.Trim().ToLowerInvariant();
        return compact switch
        {
            "ctrl" or "control" or "ctl" => "Ctrl",
            "alt" or "option" => "Alt",
            "shift" => "Shift",
            "win" or "windows" or "meta" => "Win",
            "space" => "Space",
            "enter" or "return" => "Enter",
            "tab" => "Tab",
            "esc" or "escape" => "Escape",
            _ => compact,
        };
    }
}
