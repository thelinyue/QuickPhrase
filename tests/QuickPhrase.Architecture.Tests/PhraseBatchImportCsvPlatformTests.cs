using System.Text;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>CSV 批量导入的 Windows 文件与 SQLite 链路测试。</summary>
public sealed class PhraseBatchImportCsvPlatformTests
{
    [Fact]
    public async Task TemplateFile_UsesUtf8BomAndCsvDocumentImportsThroughExistingTransaction()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var path = Path.Combine(temp.Path, "template.csv");

        await runtime.WriteBatchImportTemplateAsync(path);
        var bytes = await File.ReadAllBytesAsync(path);
        await File.AppendAllTextAsync(path, "客户,售前,欢迎,您好\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var document = await runtime.ReadBatchImportCsvAsync(path);
        var result = await runtime.ImportAsync(PhrasePackagePlanner.BuildImportPlan(document, await runtime.CaptureSnapshotAsync()));

        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.NewCategoryCount);
        Assert.Equal(1, result.NewPhraseCount);
        Assert.Contains(runtime.Search.Search(new SearchRequest("欢迎", 8)).Items, item => item.Phrase.Title == "欢迎");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickPhrase-csv-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
