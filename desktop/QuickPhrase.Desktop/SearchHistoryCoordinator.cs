using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>
/// 搜索历史共享协调器：应用级只创建一个实例，主窗口与 Launcher 绑定同一份内存快照。
/// 仓储失败只影响历史提示，不阻断当前搜索或话术投递。
/// </summary>
public sealed class SearchHistoryCoordinator
{
    private readonly ISearchHistoryRepository _repository;

    public SearchHistoryCoordinator(ISearchHistoryRepository repository)
    {
        _repository = repository;
        ViewModel = new SearchHistoryViewModel();
    }

    public SearchHistoryViewModel ViewModel { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ViewModel.SetLoading(true);
            var entries = await _repository.ListAsync(cancellationToken);
            ViewModel.Replace(entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"搜索历史初始化失败：{exception.GetType().Name}。已回退为空列表。");
            ViewModel.Replace([]);
            ViewModel.SetStatus("历史搜索暂时不可用，当前仍可正常搜索。", isError: true);
        }
        finally
        {
            ViewModel.SetLoading(false);
        }
    }

    public async Task<bool> RecordAsync(string? query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        try
        {
            var result = await _repository.RecordAsync(query.Trim(), cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                ViewModel.SetStatus($"历史搜索保存失败：{result.Error?.Message ?? "未知错误"}", isError: true);
                return false;
            }

            var entries = ViewModel.Entries
                .Where(entry => !string.Equals(entry.Query, result.Value.Query, StringComparison.OrdinalIgnoreCase))
                .Prepend(result.Value)
                .Take(10)
                .ToArray();
            ViewModel.Replace(entries);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"搜索历史保存失败：{exception.GetType().Name}。");
            ViewModel.SetStatus("历史搜索保存失败，当前搜索仍已完成。", isError: true);
            return false;
        }
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _repository.ClearAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                ViewModel.SetStatus($"清除历史搜索失败：{result.Error?.Message ?? "未知错误"}", isError: true);
                return false;
            }

            ViewModel.Replace([]);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"清除历史搜索失败：{exception.GetType().Name}。");
            ViewModel.SetStatus("清除历史搜索失败，请稍后重试。", isError: true);
            return false;
        }
    }
}
