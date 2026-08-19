using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace QuickPhrase.Core;

/// <summary>
/// 只读搜索服务：所有可检索字段都提前编译到不可变快照，查询过程不访问 Repository 或磁盘。
/// </summary>
internal sealed class SearchService : ISearchService
{
    private ImmutableDictionary<Guid, SearchEntry> _snapshot = ImmutableDictionary<Guid, SearchEntry>.Empty;
    private SearchIndexStatus _status = new(SearchIndexState.Ready, 0);
    private readonly IPinyinProvider _pinyin;

    public SearchService(IPinyinProvider pinyin) => _pinyin = pinyin;

    public SearchIndexStatus Status => Volatile.Read(ref _status);

    public SearchResponse Search(SearchRequest request)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var status = Volatile.Read(ref _status);
        var query = Normalize(request.Query);
        var limit = Math.Clamp(request.Limit, 1, 100);

        if (query.Length == 0)
        {
            var popular = snapshot.Values
                .Select(entry => new SearchResult(entry.Phrase, SearchMatchKind.EmptyQuery))
                .OrderByDescending(result => result.Phrase.UsageCount)
                .ThenByDescending(result => result.Phrase.LastUsedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(result => result.Phrase.UpdatedAtUtc)
                .ThenBy(result => result.Phrase.Title, StringComparer.Ordinal)
                .ThenBy(result => result.Phrase.Id)
                .Take(limit)
                .ToImmutableArray();
            return new SearchResponse(popular, status);
        }

        var matches = new List<ScoredResult>();
        foreach (var entry in snapshot.Values)
        {
            var kind = FindStrongMatch(entry, query);
            if (kind is not null)
                matches.Add(new ScoredResult(entry.Phrase, kind.Value));
        }

        if (matches.Count == 0 && IsFuzzyQuery(query))
        {
            foreach (var entry in snapshot.Values)
            {
                var kind = FindFuzzyMatch(entry, query);
                if (kind is not null)
                    matches.Add(new ScoredResult(entry.Phrase, kind.Value));
            }
        }

        var results = matches
            .OrderBy(result => (int)result.MatchKind)
            .ThenByDescending(result => result.Phrase.UsageCount)
            .ThenByDescending(result => result.Phrase.LastUsedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(result => result.Phrase.UpdatedAtUtc)
            .ThenBy(result => result.Phrase.Title, StringComparer.Ordinal)
            .ThenBy(result => result.Phrase.Id)
            .Take(limit)
            .Select(result => new SearchResult(result.Phrase, result.MatchKind))
            .ToImmutableArray();
        return new SearchResponse(results, status);
    }

    internal bool TryBuildEntry(Phrase phrase, out SearchEntry entry)
    {
        try
        {
            entry = BuildEntry(phrase, includePinyin: true);
            return true;
        }
        catch
        {
            entry = default!;
            return false;
        }
    }

    internal SearchEntry BuildFallbackEntry(Phrase phrase) => BuildEntry(phrase, includePinyin: false);

    internal void Replace(IReadOnlyList<Phrase> phrases, bool allowPinyinFallback, out bool degraded)
    {
        degraded = false;
        var builder = ImmutableDictionary.CreateBuilder<Guid, SearchEntry>();
        foreach (var phrase in phrases)
        {
            if (TryBuildEntry(phrase, out var entry))
            {
                builder[phrase.Id] = entry;
                continue;
            }

            if (!allowPinyinFallback) throw new InvalidOperationException("拼音索引构建失败。");
            builder[phrase.Id] = BuildFallbackEntry(phrase);
            degraded = true;
        }

        Interlocked.Exchange(ref _snapshot, builder.ToImmutable());
        var nextVersion = Status.SnapshotVersion + 1;
        Interlocked.Exchange(ref _status, new SearchIndexStatus(degraded ? SearchIndexState.Dirty : SearchIndexState.Ready, nextVersion,
            degraded ? "SEARCH_INDEX_DIRTY" : null,
            degraded ? "拼音索引暂不可用，当前已降级为中文搜索。" : null));
    }

    internal void Upsert(SearchEntry entry)
    {
        var next = Volatile.Read(ref _snapshot).SetItem(entry.Phrase.Id, entry);
        Interlocked.Exchange(ref _snapshot, next);
        Interlocked.Exchange(ref _status, new SearchIndexStatus(SearchIndexState.Ready, Status.SnapshotVersion + 1));
    }

    internal void Remove(Guid id)
    {
        var next = Volatile.Read(ref _snapshot).Remove(id);
        Interlocked.Exchange(ref _snapshot, next);
        Interlocked.Exchange(ref _status, new SearchIndexStatus(SearchIndexState.Ready, Status.SnapshotVersion + 1));
    }

    internal void MarkDirty(string message)
    {
        Interlocked.Exchange(ref _status, new SearchIndexStatus(SearchIndexState.Dirty, Status.SnapshotVersion, "SEARCH_INDEX_DIRTY", message));
    }

    internal void MarkRebuilding() =>
        Interlocked.Exchange(ref _status, new SearchIndexStatus(SearchIndexState.Rebuilding, Status.SnapshotVersion, "SEARCH_INDEX_DIRTY", "正在从本地数据恢复搜索索引。"));

    private SearchEntry BuildEntry(Phrase phrase, bool includePinyin)
    {
        var title = Normalize(phrase.Title);
        var full = ImmutableArray.CreateBuilder<string>();
        var initials = ImmutableArray.CreateBuilder<string>();
        if (includePinyin)
        {
            AddTerms(_pinyin.BuildTerms(phrase.Title), full, initials);
            // 正文拼音：使搜索同时支持"正文内容的拼音"（标题/正文 + 标题/正文拼音 多维度匹配）。
            AddTerms(_pinyin.BuildTerms(phrase.Content), full, initials);
        }

        return new SearchEntry(phrase, title, Normalize(phrase.Content), full.ToImmutable(), initials.ToImmutable());
    }

    private static void AddTerms(PinyinSearchTerms terms, ImmutableArray<string>.Builder full, ImmutableArray<string>.Builder initials)
    {
        foreach (var value in terms.FullSpellings.Take(32))
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0 && !full.Contains(normalized)) full.Add(normalized);
        }
        foreach (var value in terms.Initials.Take(32))
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0 && !initials.Contains(normalized)) initials.Add(normalized);
        }
    }

    private static SearchMatchKind? FindStrongMatch(SearchEntry entry, string query)
    {
        if (entry.Title == query) return SearchMatchKind.TitleExact;
        if (entry.Title.StartsWith(query, StringComparison.Ordinal)) return SearchMatchKind.TitlePrefix;
        if (entry.Title.Contains(query, StringComparison.Ordinal)) return SearchMatchKind.TitleContains;
        if (entry.Initials.Any(value => value.StartsWith(query, StringComparison.Ordinal))) return SearchMatchKind.PinyinInitialsPrefix;
        if (entry.Initials.Any(value => value.Contains(query, StringComparison.Ordinal))) return SearchMatchKind.PinyinInitialsContains;
        if (entry.FullSpellings.Any(value => value.StartsWith(query, StringComparison.Ordinal))) return SearchMatchKind.PinyinFullPrefix;
        if (entry.FullSpellings.Any(value => value.Contains(query, StringComparison.Ordinal))) return SearchMatchKind.PinyinFullContains;
        if (entry.Content.Contains(query, StringComparison.Ordinal)) return SearchMatchKind.ContentContains;
        return null;
    }

    private static SearchMatchKind? FindFuzzyMatch(SearchEntry entry, string query)
    {
        if (FuzzyField(entry.Title, query)) return SearchMatchKind.FuzzyTitle;
        return null;
    }

    private static bool FuzzyField(string field, string query)
    {
        if (field.Length >= query.Length && EditDistanceAtMostOne(field, query)) return true;
        return field.Length >= query.Length && EditDistanceAtMostOne(field[..query.Length], query);
    }

    private static bool IsFuzzyQuery(string query)
    {
        var count = query.EnumerateRunes().Count();
        return count is >= 3 and <= 16;
    }

    private static bool EditDistanceAtMostOne(string left, string right)
    {
        if (left.Length == right.Length)
        {
            var differences = 0;
            var first = -1;
            var second = -1;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] == right[i]) continue;
                differences++;
                if (first < 0) first = i; else second = i;
                if (differences > 2) return false;
            }
            if (differences == 0) return true;
            if (differences == 1) return true;
            return differences == 2 && second == first + 1 && left[first] == right[second] && left[second] == right[first];
        }

        if (Math.Abs(left.Length - right.Length) != 1) return false;
        var longer = left.Length > right.Length ? left : right;
        var shorter = left.Length > right.Length ? right : left;
        var indexLong = 0;
        var indexShort = 0;
        var skipped = false;
        while (indexLong < longer.Length && indexShort < shorter.Length)
        {
            if (longer[indexLong] == shorter[indexShort])
            {
                indexLong++;
                indexShort++;
                continue;
            }
            if (skipped) return false;
            skipped = true;
            indexLong++;
        }
        return true;
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune)) builder.Append(rune);
        return builder.ToString();
    }

    internal sealed record SearchEntry(
        Phrase Phrase,
        string Title,
        string Content,
        ImmutableArray<string> FullSpellings,
        ImmutableArray<string> Initials);

    private sealed record ScoredResult(Phrase Phrase, SearchMatchKind MatchKind);
}
