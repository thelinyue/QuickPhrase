using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class EnterpriseSyncSettingsViewModelTests
{
    [Fact]
    public async Task ConnectClearsPasswordAndRefreshesAccountState()
    {
        var accounts = new FakeAccounts();
        var provider = new FakeProvider();
        var vm = new EnterpriseSyncSettingsViewModel(accounts, provider)
        {
            HubAddress = "http://server:5105",
            Account = "alice",
            Password = "temporary-password",
            DeviceName = "测试设备",
        };

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.Password);
        Assert.True(vm.IsConnected);
        Assert.Equal("alice", accounts.LastRequest?.Account);
        Assert.Equal("http://server:5105/", accounts.LastRequest?.HubAddress.ToString());
        Assert.Equal("企业话术已同步。", vm.StatusMessage);
    }

    [Fact]
    public async Task LoadAndManualSyncDoNotChangeLocalSettings()
    {
        var accounts = new FakeAccounts { State = new SyncAccountState(true, new Uri("http://server:5105"), "alice", "客服", "device", SyncStatus.Succeeded, 3, DateTimeOffset.UtcNow, "已同步", null) };
        var provider = new FakeProvider();
        var vm = new EnterpriseSyncSettingsViewModel(accounts, provider);

        await vm.LoadAsync();
        await vm.SynchronizeCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Equal(3, vm.ReleaseNumber);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class FakeProvider : ISyncProvider
    {
        public int Calls { get; private set; }
        public string ProviderId => "fake";
        public SyncProviderCapabilities Capabilities { get; } = new(true);
        public Task<SyncResult> SynchronizeAsync(SyncRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncResult(SyncStatus.Succeeded, 1, null, "企业话术已同步。", false, null, now, now));
        }
    }

    private sealed class FakeAccounts : ISyncAccountService
    {
        public HubConnectionRequest? LastRequest { get; private set; }
        public SyncAccountState State { get; set; } = new(false, null, null, null, null, SyncStatus.Disabled, 0, null, null, null);
        public Task<SyncAccountState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<SyncResult> ConnectAsync(HubConnectionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            State = new SyncAccountState(true, request.HubAddress, request.Account, "客服", "device", SyncStatus.Succeeded, 1, DateTimeOffset.UtcNow, "企业话术已同步。", null);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncResult(SyncStatus.Succeeded, 2, null, "企业话术已同步。", false, null, now, now));
        }
        public Task DisconnectAsync(bool retainEnterpriseCache = true, CancellationToken cancellationToken = default) { State = State with { Connected = false, Status = SyncStatus.Disabled }; return Task.CompletedTask; }
        public Task ClearEnterpriseCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
