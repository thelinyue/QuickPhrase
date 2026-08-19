using System.Collections.Immutable;
using System.Text;
using Pinyin.NET;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>对 PinyinM.NET 的薄适配层；第三方 API 不向 Core 泄漏，并限制多音字组合数量。</summary>
public sealed class PinyinMProvider : IPinyinProvider
{
    private const int MaxVariants = 32;
    private readonly PinyinProcessor _processor = new(PinyinFormat.WithoutTone);

    public PinyinSearchTerms BuildTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new PinyinSearchTerms([], []);
        var tokens = _processor.GetTokens(text);
        var fullChoices = tokens.Select(token => token.Full.Select(Normalize).Where(value => value.Length > 0).ToArray()).Where(values => values.Length > 0).ToArray();
        var initialChoices = tokens.Select(token => token.First.Select(character => Normalize(character.ToString())).Where(value => value.Length > 0).ToArray()).Where(values => values.Length > 0).ToArray();
        return new PinyinSearchTerms(BuildCombinations(fullChoices), BuildCombinations(initialChoices));
    }

    private static ImmutableArray<string> BuildCombinations(IReadOnlyList<string[]> choices)
    {
        if (choices.Count == 0) return [];
        var results = new List<string>(MaxVariants);
        Build(choices, 0, new StringBuilder(), results);
        return results.Distinct(StringComparer.Ordinal).Take(MaxVariants).ToImmutableArray();
    }

    private static void Build(IReadOnlyList<string[]> choices, int index, StringBuilder current, ICollection<string> results)
    {
        if (results.Count >= MaxVariants) return;
        if (index == choices.Count)
        {
            results.Add(current.ToString());
            return;
        }
        foreach (var choice in choices[index])
        {
            var length = current.Length;
            current.Append(choice);
            Build(choices, index + 1, current, results);
            current.Length = length;
            if (results.Count >= MaxVariants) return;
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return string.Concat(normalized.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)));
    }
}
