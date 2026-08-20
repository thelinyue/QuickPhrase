namespace QuickPhrase.Core;

public enum SyncStatus
{
    Succeeded,
    Partial,
    Offline,
    AuthenticationRequired,
    Disabled,
    Failed,
}

public sealed record SyncProviderCapabilities(bool EnterpriseSync);
public sealed record SyncRequest(bool ForceFull = false);
public sealed record SyncResult(
    SyncStatus Status,
    int EnterpriseChangesApplied,
    string? ErrorCode,
    string Message,
    bool Retryable,
    string? TraceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public interface ISyncProvider
{
    string ProviderId { get; }
    SyncProviderCapabilities Capabilities { get; }
    Task<SyncResult> SynchronizeAsync(SyncRequest request, CancellationToken cancellationToken = default);
}

public sealed record HubConnectionRequest(
    Uri HubAddress,
    string Account,
    string Password,
    string DeviceName,
    string ClientVersion);

public sealed record SyncAccountState(
    bool Connected,
    Uri? HubAddress,
    string? Account,
    string? DisplayName,
    string? DeviceId,
    SyncStatus Status,
    long ReleaseNumber,
    DateTimeOffset? LastSynchronizedAtUtc,
    string? LastMessage,
    string? TraceId);

public interface ISyncAccountService
{
    Task<SyncAccountState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> ConnectAsync(HubConnectionRequest request, CancellationToken cancellationToken = default);
    Task DisconnectAsync(bool retainEnterpriseCache = true, CancellationToken cancellationToken = default);
    Task ClearEnterpriseCacheAsync(CancellationToken cancellationToken = default);
}

public interface IEnterpriseCatalog
{
    Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Phrase>> ListPhrasesAsync(CancellationToken cancellationToken = default);
}
