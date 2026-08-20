using System.Text;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class DpapiTokenStoreTests
{
    [Fact]
    public async Task CurrentUserDpapiRoundTripsWithoutWritingPlaintext()
    {
        using var temp = new TemporaryDirectory();
        var store = new DpapiTokenStore(temp.Path);
        const string token = "sensitive-device-token";

        await store.SaveAsync("test-reference", token);

        var path = Path.Combine(temp.Path, "hub-token-test-reference.bin");
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        Assert.Equal(token, await store.ReadAsync("test-reference"));
        await store.DeleteAsync("test-reference");
        Assert.False(File.Exists(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-M3-DPAPI-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
