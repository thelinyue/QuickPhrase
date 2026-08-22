using System.Text;
using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

/// <summary>CSV 批量导入领域格式测试，确保模板、字段校验和分类层级不依赖 Windows 文件系统。</summary>
public sealed class PhraseBatchImportCsvTests
{
    [Fact]
    public void Template_ContainsFixedHeadersAndExactSampleRow()
    {
        var template = PhraseBatchImportCsv.CreateTemplate();

        Assert.Equal(
            "一级分类,二级分类,话术标题,话术内容\r\n示例分类,示例子分类,示例话术标题,这是一条示例话术，请修改后再导入。\r\n",
            template);
    }

    [Fact]
    public void Parse_IgnoresOnlyTheUnchangedSampleAndBuildsTwoLevelCategoriesInRowOrder()
    {
        var csv = PhraseBatchImportCsv.CreateTemplate() +
                  "客户,售前,问候,您好\r\n" +
                  "客户,售后,回访,请问使用是否顺利\r\n" +
                  "客户,售前,报价,报价已发送\r\n" +
                  "内部,,通知,请查看群消息\r\n";

        var document = PhraseBatchImportCsv.Parse(csv, DateTimeOffset.UnixEpoch, Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.Equal(4, document.Phrases.Count);
        Assert.Equal(4, document.Categories.Count);
        Assert.Equal(["客户", "售前", "售后", "内部"], document.Categories.Select(item => item.Name));
        var customer = document.Categories[0];
        Assert.All(document.Categories.Where(item => item.Name is "售前" or "售后"), item => Assert.Equal(customer.Id, item.ParentId));
        Assert.Equal([0, 1, 2, 3], document.Phrases.Select(item => item.SortOrder));
        Assert.Equal(DateTimeOffset.UnixEpoch, document.Manifest.CreatedAt);
    }

    [Fact]
    public void Parse_SupportsQuotedCommasEscapedQuotesAndMultilineContent()
    {
        const string csv = "一级分类,二级分类,话术标题,话术内容\r\n客户,,\"报价,说明\",\"第一行\r\n第二行含 \"\"引号\"\"\"\r\n";

        var document = PhraseBatchImportCsv.Parse(csv);

        var phrase = Assert.Single(document.Phrases);
        Assert.Equal("报价,说明", phrase.Title);
        Assert.Equal("第一行\r\n第二行含 \"引号\"", phrase.Body.TextProjection);
        var segment = Assert.Single(phrase.Body.Segments);
        Assert.Equal(PhraseSegmentKind.Text, segment.Kind);
        Assert.Null(segment.Image);
    }

    [Theory]
    [InlineData("一级分类,二级分类,标题,错误正文\r\n客户,,标题,正文\r\n", "CSV_HEADER_INVALID", "表头")]
    [InlineData("一级分类,二级分类,标题,正文\r\n客户,,标题,正文\r\n", "CSV_HEADER_INVALID", "表头")]
    [InlineData("一级分类,二级分类,话术标题,话术内容\r\n,二级,标题,正文\r\n", "CSV_PRIMARY_CATEGORY_REQUIRED", "第 2 行")]
    [InlineData("一级分类,二级分类,话术标题,话术内容\r\n客户,,标题, \r\n", "CSV_CONTENT_REQUIRED", "第 2 行")]
    public void Parse_RejectsInvalidRowsWithCodeAndLine(string csv, string code, string messagePart)
    {
        var exception = Assert.Throws<PhraseBatchImportCsvException>(() => PhraseBatchImportCsv.Parse(csv));

        Assert.Equal(code, exception.Code);
        Assert.Contains(messagePart, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_AllowsEmptyTitleAndNormalizesItToEmptyString(string title)
    {
        var csv = $"一级分类,二级分类,话术标题,话术内容\r\n客户,,{title},正文\r\n";

        var document = PhraseBatchImportCsv.Parse(csv);

        Assert.Equal(string.Empty, Assert.Single(document.Phrases).Title);
    }

    [Fact]
    public void Parse_RejectsInvalidQuotedFieldAndTooManyCategories()
    {
        const string invalidQuoted = "一级分类,二级分类,话术标题,话术内容\r\n客户,,\"标题\"尾随字符,正文\r\n";
        var quoteException = Assert.Throws<PhraseBatchImportCsvException>(() => PhraseBatchImportCsv.Parse(invalidQuoted));
        Assert.Equal("CSV_QUOTE_INVALID", quoteException.Code);

        var categories = new StringBuilder("一级分类,二级分类,话术标题,话术内容\r\n");
        for (var index = 0; index <= PhrasePackageFormat.MaxCategoryCount; index++)
            categories.Append("分类").Append(index).Append(",,标题,正文\r\n");

        var categoryException = Assert.Throws<PhraseBatchImportCsvException>(() => PhraseBatchImportCsv.Parse(categories.ToString()));
        Assert.Equal("CSV_CATEGORY_LIMIT_EXCEEDED", categoryException.Code);
    }

    [Fact]
    public void Parse_RejectsTooLongFieldsAndMoreThanMaximumRows()
    {
        var tooLong = $"一级分类,二级分类,话术标题,话术内容\r\n客户,,{new string('标', PhrasePackageFormat.MaxTitleLength + 1)},正文\r\n";
        var tooLongException = Assert.Throws<PhraseBatchImportCsvException>(() => PhraseBatchImportCsv.Parse(tooLong));
        Assert.Equal("CSV_TITLE_TOO_LONG", tooLongException.Code);

        var builder = new StringBuilder("一级分类,二级分类,话术标题,话术内容\r\n");
        for (var index = 0; index <= PhrasePackageFormat.MaxPhraseCount; index++)
            builder.Append("客户,,标题").Append(index).Append(",正文\r\n");

        var countException = Assert.Throws<PhraseBatchImportCsvException>(() => PhraseBatchImportCsv.Parse(builder.ToString()));
        Assert.Equal("CSV_PHRASE_LIMIT_EXCEEDED", countException.Code);
    }
}
