using System.Globalization;
using System.Text;

namespace QuickPhrase.Core;

/// <summary>
/// CSV 批量导入模板的固定格式。该类型只处理纯文本与领域数据转换，
/// 不访问文件系统，因此 Desktop 和 Windows 平台可通过同一规则保持模板与导入行为一致。
/// </summary>
public static class PhraseBatchImportCsv
{
    public const string PrimaryCategoryHeader = "一级分类";
    public const string SecondaryCategoryHeader = "二级分类";
    public const string TitleHeader = "标题";
    public const string ContentHeader = "正文";

    public const string SamplePrimaryCategory = "示例分类";
    public const string SampleSecondaryCategory = "示例子分类";
    public const string SampleTitle = "示例话术标题";
    public const string SampleContent = "这是一条示例话术，请修改后再导入。";

    private static readonly string[] RequiredHeaders =
    [
        PrimaryCategoryHeader,
        SecondaryCategoryHeader,
        TitleHeader,
        ContentHeader,
    ];

    private static readonly string[] SampleValues =
    [
        SamplePrimaryCategory,
        SampleSecondaryCategory,
        SampleTitle,
        SampleContent,
    ];

    /// <summary>生成供 Excel 直接编辑的模板文本；文件写入方应使用 UTF-8 BOM 编码。</summary>
    public static string CreateTemplate()
    {
        var builder = new StringBuilder();
        AppendRecord(builder, RequiredHeaders);
        AppendRecord(builder, SampleValues);
        return builder.ToString();
    }

    /// <summary>
    /// 将固定四列表格转换为既有话术包领域文档。
    /// 空二级分类表示话术直接落在一级分类；完全未改动的固定示例行会自动跳过。
    /// </summary>
    public static PhrasePackageDocument Parse(string csv, DateTimeOffset? createdAtUtc = null, Guid? packageId = null)
    {
        if (string.IsNullOrEmpty(csv))
            throw Error("CSV_EMPTY", 1, "CSV 文件为空，请使用下载的模板填写后再导入。");

        var records = ReadRecords(csv);
        if (records.Count == 0)
            throw Error("CSV_EMPTY", 1, "CSV 文件为空，请使用下载的模板填写后再导入。");

        var header = records[0];
        if (header.Values.Count != RequiredHeaders.Length ||
            !header.Values.Select((value, index) => index == 0 ? value.TrimStart('\uFEFF') : value)
                .SequenceEqual(RequiredHeaders, StringComparer.Ordinal))
        {
            throw Error("CSV_HEADER_INVALID", header.Line, "第 1 行表头必须依次为：一级分类、二级分类、标题、正文。");
        }

        var categories = new List<PhrasePackageCategory>();
        var phrases = new List<PhrasePackagePhrase>();
        var categoryIds = new Dictionary<(Guid? ParentId, string Name), Guid>();

        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var record = records[recordIndex];
            if (record.Values.Count != RequiredHeaders.Length)
            {
                throw Error("CSV_COLUMN_COUNT_INVALID", record.Line,
                    $"第 {record.Line} 行应包含 4 列，请检查逗号和双引号是否正确。");
            }

            if (record.Values.SequenceEqual(SampleValues, StringComparer.Ordinal)) continue;

            var primary = NormalizeCategory(record.Values[0]);
            var secondary = NormalizeCategory(record.Values[1]);
            var title = record.Values[2].Trim();
            var content = record.Values[3];

            if (primary.Length == 0)
                throw Error("CSV_PRIMARY_CATEGORY_REQUIRED", record.Line, $"第 {record.Line} 行的一级分类不能为空。");
            if (primary.Length > PhrasePackageFormat.MaxNameLength)
                throw Error("CSV_PRIMARY_CATEGORY_TOO_LONG", record.Line, $"第 {record.Line} 行的一级分类不能超过 {PhrasePackageFormat.MaxNameLength} 个字。");
            if (secondary.Length > PhrasePackageFormat.MaxNameLength)
                throw Error("CSV_SECONDARY_CATEGORY_TOO_LONG", record.Line, $"第 {record.Line} 行的二级分类不能超过 {PhrasePackageFormat.MaxNameLength} 个字。");
            if (title.Length == 0)
                throw Error("CSV_TITLE_REQUIRED", record.Line, $"第 {record.Line} 行的标题不能为空。");
            if (title.Length > PhrasePackageFormat.MaxTitleLength)
                throw Error("CSV_TITLE_TOO_LONG", record.Line, $"第 {record.Line} 行的标题不能超过 {PhrasePackageFormat.MaxTitleLength} 个字。");
            if (string.IsNullOrWhiteSpace(content))
                throw Error("CSV_CONTENT_REQUIRED", record.Line, $"第 {record.Line} 行的正文不能为空。");
            if (content.Length > PhrasePackageFormat.MaxContentLength)
                throw Error("CSV_CONTENT_TOO_LONG", record.Line, $"第 {record.Line} 行的正文不能超过 {PhrasePackageFormat.MaxContentLength} 个字。");

            var primaryId = EnsureCategory(primary, null, record.Line, categories, categoryIds);
            var targetCategoryId = secondary.Length == 0
                ? primaryId
                : EnsureCategory(secondary, primaryId, record.Line, categories, categoryIds);

            if (phrases.Count >= PhrasePackageFormat.MaxPhraseCount)
                throw Error("CSV_PHRASE_LIMIT_EXCEEDED", record.Line, $"话术数量不能超过 {PhrasePackageFormat.MaxPhraseCount} 条。");

            phrases.Add(new PhrasePackagePhrase(Guid.NewGuid(), title, PhraseBody.FromText(content), targetCategoryId, phrases.Count));
        }

        if (phrases.Count == 0)
            throw Error("CSV_NO_IMPORTABLE_ROWS", 2, "CSV 中没有可导入的话术，请删除示例行后填写至少一条话术。");

        var document = new PhrasePackageDocument(
            new PhrasePackageManifest(
                PhrasePackageFormat.Format,
                PhrasePackageFormat.Version,
                packageId ?? Guid.NewGuid(),
                "CSV 批量导入",
                createdAtUtc ?? DateTimeOffset.UtcNow,
                phrases.Count,
                categories.Count,
                0),
            categories,
            phrases,
            []);
        var errors = PhrasePackagePlanner.Validate(document);
        if (errors.Count > 0) throw Error("CSV_DOCUMENT_INVALID", 0, errors[0]);
        return document;
    }

    private static Guid EnsureCategory(
        string name,
        Guid? parentId,
        int line,
        List<PhrasePackageCategory> categories,
        Dictionary<(Guid? ParentId, string Name), Guid> categoryIds)
    {
        var key = (parentId, NormalizeKey(name));
        if (categoryIds.TryGetValue(key, out var existing)) return existing;
        if (categories.Count >= PhrasePackageFormat.MaxCategoryCount)
            throw Error("CSV_CATEGORY_LIMIT_EXCEEDED", line, $"分类数量不能超过 {PhrasePackageFormat.MaxCategoryCount} 个。");

        var id = Guid.NewGuid();
        categoryIds.Add(key, id);
        categories.Add(new PhrasePackageCategory(id, name, parentId, categories.Count));
        return id;
    }

    private static List<CsvRecord> ReadRecords(string csv)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        var quotedFieldClosed = false;
        var recordHasData = false;
        var line = 1;
        var recordLine = 1;

        for (var index = 0; index < csv.Length; index++)
        {
            var current = csv[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        quotedFieldClosed = true;
                    }
                    continue;
                }

                if (current == '\r')
                {
                    field.Append(current);
                    if (index + 1 < csv.Length && csv[index + 1] == '\n') field.Append(csv[++index]);
                    line++;
                    continue;
                }
                if (current == '\n') line++;
                field.Append(current);
                continue;
            }

            if (current == '"')
            {
                if (fieldStarted || field.Length > 0)
                    throw Error("CSV_QUOTE_INVALID", recordLine, $"第 {recordLine} 行 CSV 引号格式不正确。");
                inQuotes = true;
                fieldStarted = true;
                quotedFieldClosed = false;
                recordHasData = true;
                continue;
            }

            if (current == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                quotedFieldClosed = false;
                recordHasData = true;
                continue;
            }

            if (current == '\r' || current == '\n')
            {
                fields.Add(field.ToString());
                records.Add(new CsvRecord(recordLine, fields.ToArray()));
                fields.Clear();
                field.Clear();
                fieldStarted = false;
                quotedFieldClosed = false;
                recordHasData = false;
                if (current == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                line++;
                recordLine = line;
                continue;
            }

            if (quotedFieldClosed)
                throw Error("CSV_QUOTE_INVALID", recordLine, $"第 {recordLine} 行 CSV 引号格式不正确。");

            field.Append(current);
            fieldStarted = true;
            recordHasData = true;
        }

        if (inQuotes)
            throw Error("CSV_QUOTE_UNCLOSED", recordLine, $"第 {recordLine} 行 CSV 存在未闭合的双引号。");
        if (recordHasData || fields.Count > 0 || field.Length > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, fields.ToArray()));
        }
        return records;
    }

    private static void AppendRecord(StringBuilder builder, IEnumerable<string> values)
    {
        builder.Append(string.Join(',', values.Select(Escape)));
        builder.Append("\r\n");
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string NormalizeCategory(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeKey(string value) => NormalizeCategory(value).ToUpper(CultureInfo.InvariantCulture);

    private static PhraseBatchImportCsvException Error(string code, int line, string message) => new(code, line, message);

    private sealed record CsvRecord(int Line, IReadOnlyList<string> Values);
}

/// <summary>CSV 模板解析错误，向界面提供不包含话术正文的结果码、行号和可读中文提示。</summary>
public sealed class PhraseBatchImportCsvException : InvalidOperationException
{
    public PhraseBatchImportCsvException(string code, int line, string message)
        : base(message)
    {
        Code = code;
        Line = line;
    }

    public string Code { get; }
    public int Line { get; }
}
