using System.Collections.Immutable;

namespace QuickPhrase.Core;

/// <summary>Core 程序集只包含平台无关的领域模型、校验规则和数据契约。</summary>
public static class CoreAssemblyMarker
{
    public const string Phase = "Phase 3 — Search";
}

public enum ShortcutMode
{
    None,
    Quick,
    Custom,
}

public sealed record ShortcutValue(string Display, string Normalized);

public sealed record ShortcutNormalizationResult(
    bool IsValid,
    ShortcutValue? Value,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ShortcutNormalizationResult Valid(ShortcutValue value) => new(true, value, null, null);
    public static ShortcutNormalizationResult Invalid(string message) => new(false, null, "VALIDATION_FAILED", message);
}

public sealed record Category(
    Guid Id,
    Guid? ParentId,
    string Name,
    int SortOrder,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record Phrase(
    Guid Id,
    string Title,
    string Content,
    Guid CategoryId,
    bool Favorite,
    ShortcutMode ShortcutMode,
    ShortcutValue? Shortcut,
    int UsageCount,
    DateTimeOffset? LastUsedAtUtc,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ColorKey = "default",
    int SortOrder = 0);

/// <summary>
/// 应用设置聚合。Launcher 快捷键只以平台无关的 <see cref="ShortcutChord"/> 作为领域真值；
/// 持久化行版本仍独立负责乐观并发，不能与设置文档 schemaVersion 混用。
/// </summary>
public sealed record AppSettings(
    long Version,
    bool LaunchOnStartup,
    bool StartMinimized,
    bool StayInTrayOnClose,
    ShortcutChord LauncherShortcut,
    bool AutoSend,
    bool ClipboardCompatibilityMode,
    bool HasCompletedOnboarding = false,
    int OnboardingVersion = 0)
{
    /// <summary>只保存开发者登记的 Adapter 开关；具体前台准入仍由 Desktop 重新校验。</summary>
    public Dictionary<string, bool> LauncherEnabledAdapters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CreatePhraseCommand(
    Guid Id,
    string Title,
    string Content,
    Guid CategoryId,
    bool Favorite,
    ShortcutMode ShortcutMode,
    string? Shortcut,
    string ColorKey = "default",
    int SortOrder = 0);

public sealed record UpdatePhraseCommand(
    Guid Id,
    long ExpectedVersion,
    string Title,
    string Content,
    Guid CategoryId,
    bool Favorite,
    ShortcutMode ShortcutMode,
    string? Shortcut,
    string ColorKey = "default",
    int SortOrder = 0);

public sealed record CreateCategoryCommand(Guid Id, string Name, Guid? ParentId = null, int SortOrder = 0);
public sealed record RenameCategoryCommand(Guid Id, long ExpectedVersion, string Name, int SortOrder);
public sealed record MoveCategoryCommand(Guid Id, long ExpectedVersion, Guid? ParentId, int SortOrder);

/// <summary>
/// 删除提交结果。分类级联删除时同时返回实际删除的话术 ID，确保数据库提交成功后搜索索引可以精确移除对应条目。
/// </summary>
public sealed record DeleteResult(
    bool Deleted,
    CommittedDataChange? Change,
    IReadOnlyList<Guid>? DeletedPhraseIds = null);

public sealed record CommittedDataChange(
    string EntityType,
    Guid EntityId,
    string Operation,
    DateTimeOffset CommittedAtUtc);

public sealed record DataError(string Code, string Message, Guid? RelatedEntityId = null, string? RelatedTitle = null);

public sealed record RepositoryResult<T>(T? Value, DataError? Error, CommittedDataChange? Change)
{
    public bool IsSuccess => Error is null;
    public static RepositoryResult<T> Success(T value, CommittedDataChange? change = null) => new(value, null, change);
    public static RepositoryResult<T> Failure(DataError error) => new(default, error, null);
}

/// <summary>一次成功搜索后保存的历史关键词；时间按当前本机时间记录。</summary>
public sealed record SearchHistoryEntry(
    string Query,
    DateTimeOffset LastSearchedAtUtc);

/// <summary>本机搜索历史的持久化契约。实现负责去重、排序和最多十条的容量限制。</summary>
public interface ISearchHistoryRepository
{
    Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default);
    Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default);
    Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default);
}

public interface IPhraseRepository
{
    Task<IReadOnlyList<Phrase>> ListAsync(CancellationToken cancellationToken = default);
    Task<Phrase?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Phrase>> CreateAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Phrase>> UpdateAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Phrase>> IncrementUsageAsync(Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> RenameAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> MoveAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task<RepositoryResult<AppSettings>> SaveAsync(AppSettings settings, long expectedVersion, CancellationToken cancellationToken = default);
}

public interface IShortcutNormalizer
{
    ShortcutNormalizationResult Normalize(string? shortcut, ShortcutMode mode);
}

public enum SearchMatchKind
{
    EmptyQuery,
    TitleExact,
    TitlePrefix,
    TitleContains,
    PinyinInitialsPrefix,
    PinyinInitialsContains,
    PinyinFullPrefix,
    PinyinFullContains,
    ContentContains,
    FuzzyTitle,
}

public enum SearchIndexState
{
    Ready,
    Dirty,
    Rebuilding,
}

public sealed record SearchRequest(string Query, int Limit = 8);

public sealed record PinyinSearchTerms(
    ImmutableArray<string> FullSpellings,
    ImmutableArray<string> Initials);

public sealed record SearchIndexStatus(
    SearchIndexState State,
    long SnapshotVersion,
    string? ErrorCode = null,
    string? Message = null);

public sealed record SearchResult(Phrase Phrase, SearchMatchKind MatchKind);

public sealed record SearchResponse(
    ImmutableArray<SearchResult> Items,
    SearchIndexStatus Status);

public interface ISearchService
{
    SearchResponse Search(SearchRequest request);
    SearchIndexStatus Status { get; }
}

public interface IPinyinProvider
{
    PinyinSearchTerms BuildTerms(string text);
}
/// <summary>能力状态采用悲观默认：只有经过当前客户端版本验收的能力才允许进入 Verified。</summary>
public enum CapabilityStatus
{
    Verified,
    Unverified,
    Unsupported,
}

/// <summary>
/// 进程内投递目标的逻辑身份。RuntimeKey 是由平台实现生成的短期不透明键，
/// Core 不解释它，也不保存 HWND、PID、Window 或 UI Automation 对象。
/// </summary>
public sealed record DeliveryTarget(
    string ApplicationId,
    string ApplicationKind,
    string AdapterId,
    string DisplayName,
    string RuntimeKey,
    DateTimeOffset CapturedAtUtc);

public sealed record TargetValidationResult(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DeliveryTarget? Expected = null)
{
    public static TargetValidationResult Valid { get; } = new(true);
    public static TargetValidationResult Invalid(string code, string message, DeliveryTarget? expected = null) => new(false, code, message, expected);
}

public sealed record AdapterCapabilities(
    CapabilityStatus InsertText,
    CapabilityStatus VerifyInsert,
    CapabilityStatus SendText,
    CapabilityStatus VerifySend);

public sealed record AdapterProfile(
    string AdapterId,
    string ApplicationId,
    string ProductVersionRange,
    string ProfileVersion,
    CapabilityStatus InsertTextStatus,
    CapabilityStatus VerifyInsertStatus,
    CapabilityStatus SendTextStatus,
    CapabilityStatus VerifySendStatus,
    string FallbackMode,
    DateTimeOffset? VerifiedAtUtc);

public sealed record DeliveryRequest(
    Phrase Phrase,
    DeliveryTarget? Target,
    bool SendRequested,
    bool UserAutoSendEnabled,
    bool ClipboardCompatibilityMode,
    TargetChangeBehavior TargetChangeBehavior = TargetChangeBehavior.CopyOnly);

/// <summary>目标失效后的处理必须由调用场景显式选择，连续投递绝不污染用户剪贴板。</summary>
public enum TargetChangeBehavior
{
    CopyOnly,
    Cancel,
}

public enum DeliveryStatus
{
    Success,
    Failed,
    Cancelled,
    Unsupported,
    Unknown,
}

public enum DeliveryEffect
{
    None,
    Inserted,
    Sent,
    Unknown,
}

public enum DeliveryConfidence
{
    Confirmed,
    Probable,
    Unknown,
}

public enum DeliveryStage
{
    NotStarted,
    ValidateTarget,
    ResolveAdapter,
    DetectCapabilities,
    TargetActivation,
    ControlFingerprint,
    ClipboardPaste,
    ClipboardRestore,
    UsageEnqueue,
    Insert,
    VerifyInsert,
    RevalidateBeforeSend,
    OptionalSend,
    VerifySend,
    Completed,
    Fallback,
}

public sealed record DeliverySubstage(string Name, string Code, double DurationMs);

public sealed record InsertResult(bool WasApplied, bool Inconclusive = false, string Code = "INSERTED", ImmutableArray<DeliverySubstage> Substages = default)
{
    public static InsertResult Applied { get; } = new(true);
}

public sealed record SendResult(bool WasApplied, string Code = "SENT")
{
    public static SendResult Applied { get; } = new(true);
}

public sealed record VerificationResult(bool IsVerified, bool IsInconclusive, string Code)
{
    public static VerificationResult Verified { get; } = new(true, false, "VERIFIED");
    public static VerificationResult Inconclusive(string code) => new(false, true, code);
    public static VerificationResult Failed(string code) => new(false, false, code);
}

/// <summary>
/// 投递结果采用正交字段表达状态、已经发生的副作用、执行阶段和可信度，
/// 避免用一个枚举同时表示过程、结果和不确定性。
/// </summary>
public sealed record DeliveryResult(
    DeliveryStatus Status,
    DeliveryEffect Effect,
    DeliveryStage Stage,
    DeliveryConfidence Confidence,
    string ErrorCode,
    string Message,
    bool Retryable,
    Guid TraceId)
{
    public bool Inserted => Effect is DeliveryEffect.Inserted or DeliveryEffect.Sent;
    public bool Sent => Effect is DeliveryEffect.Sent;
    public bool IsSuccess => Status == DeliveryStatus.Success;
}

/// <summary>脱敏投递诊断记录，禁止写入话术正文、剪贴板和 UIA 文本。</summary>
public sealed record DeliveryTrace(
    Guid TraceId,
    DeliveryStage Stage,
    string AdapterId,
    string ProfileVersion,
    string ApplicationId,
    string? ProductVersion,
    string ResultCode,
    double DurationMs,
    DateTimeOffset TimestampUtc);

public interface ITargetDetector
{
    DeliveryTarget? CaptureForeground();
    TargetValidationResult Validate(DeliveryTarget target, bool requireForeground);
}

public interface IApplicationAdapter
{
    string AdapterId { get; }
    string? DetectedProductVersion { get; }
    AdapterProfile Profile { get; }
    AdapterCapabilities DetectCapabilities();
    Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken);
    Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken);
    Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken);
    Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken);
}

public interface IAdapterResolver
{
    IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null);
}

public interface ITextDeliveryStateMachine
{
    Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default);
}










// TEMP_MARKER_0819

