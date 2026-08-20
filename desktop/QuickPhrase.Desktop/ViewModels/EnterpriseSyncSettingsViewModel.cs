using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 企业同步设置只依赖 Core 契约。密码仅用于当前连接命令，命令结束后立即清空，状态中不保存正文或认证秘密。
/// </summary>
public partial class EnterpriseSyncSettingsViewModel : ObservableObject
{
    private readonly ISyncAccountService _accounts;
    private readonly ISyncProvider _provider;

    [ObservableProperty] private string _hubAddress = "http://127.0.0.1:5105";
    [ObservableProperty] private string _account = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _deviceName = Environment.MachineName;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private long _releaseNumber;
    [ObservableProperty] private string _statusMessage = "当前为本地模式，未连接闪语中心。";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _traceId;
    [ObservableProperty] private DateTimeOffset? _lastSynchronizedAtUtc;

    public string HttpRiskMessage => "Hub 使用内网明文 HTTP；公网或跨网段访问必须由企业网关提供 HTTPS。";
    public string LastSynchronizedText => LastSynchronizedAtUtc is null ? "尚未同步" : LastSynchronizedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public EnterpriseSyncSettingsViewModel(ISyncAccountService accounts, ISyncProvider provider)
    {
        _accounts = accounts;
        _provider = provider;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default) => ApplyState(await _accounts.GetStateAsync(cancellationToken));

    [RelayCommand]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(HubAddress.Trim(), UriKind.Absolute, out var address))
        {
            ErrorMessage = "Hub 地址格式无效，请输入完整的 http:// 内网地址。";
            return;
        }
        IsBusy = true; ErrorMessage = null; TraceId = null;
        try
        {
            var result = await _accounts.ConnectAsync(new HubConnectionRequest(address, Account.Trim(), Password, DeviceName.Trim(), "0.3.0"), cancellationToken);
            Password = string.Empty;
            await LoadAsync(cancellationToken);
            StatusMessage = result.Message;
            ErrorMessage = result.Status is SyncStatus.Succeeded or SyncStatus.Partial ? null : result.Message;
            TraceId = result.TraceId;
        }
        finally { Password = string.Empty; IsBusy = false; }
    }

    [RelayCommand]
    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            var result = await _provider.SynchronizeAsync(new SyncRequest(), cancellationToken);
            await LoadAsync(cancellationToken);
            StatusMessage = result.Message;
            ErrorMessage = result.Status is SyncStatus.Succeeded or SyncStatus.Partial ? null : result.Message;
            TraceId = result.TraceId;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try { await _accounts.DisconnectAsync(retainEnterpriseCache: true, cancellationToken); await LoadAsync(cancellationToken); }
        finally { IsBusy = false; }
    }

    public async Task ClearEnterpriseCacheAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try { await _accounts.ClearEnterpriseCacheAsync(cancellationToken); await LoadAsync(cancellationToken); StatusMessage = "企业缓存已清除，本地个人话术不受影响。"; }
        finally { IsBusy = false; }
    }

    private void ApplyState(SyncAccountState state)
    {
        IsConnected = state.Connected;
        if (state.HubAddress is not null) HubAddress = state.HubAddress.ToString().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(state.Account)) Account = state.Account;
        ReleaseNumber = state.ReleaseNumber;
        LastSynchronizedAtUtc = state.LastSynchronizedAtUtc;
        OnPropertyChanged(nameof(LastSynchronizedText));
        StatusMessage = state.LastMessage ?? (state.Connected ? "已连接闪语中心。" : "当前为本地模式，未连接闪语中心。");
        TraceId = state.TraceId;
    }
}
