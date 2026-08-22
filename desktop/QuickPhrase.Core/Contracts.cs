using System.Collections.Immutable;

namespace QuickPhrase.Core;

/// <summary>Core 程序集只包含平台无关的领域模型、校验规则和数据契约。</summary>
public static class CoreAssemblyMarker
{
    public const string Phase = "Phase 3 — Search";
}

public enum PhraseScope
{
    Personal,
    Enterprise,
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
    DateTimeOffset UpdatedAtUtc,
    PhraseScope Scope = PhraseScope.Personal);

public enum PhraseSegmentKind
{
    Text,
    Image,
}

/// <summary>
/// 图片段在 Core 中只保存媒体资产标识和投递所需的脱敏元数据，不保存原文件名、绝对路径或 WPF 图片类型。
/// </summary>
public sealed record PhraseImageReference(
    Guid AssetId,
    string MimeType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight);

/// <summary>话术的一个原子内容段；段数组顺序即预览和分批投递顺序。</summary>
public sealed record PhraseSegment(
    Guid Id,
    PhraseSegmentKind Kind,
    string? Text,
    PhraseImageReference? Image)
{
    public static PhraseSegment CreateText(string text) =>
        new(Guid.NewGuid(), PhraseSegmentKind.Text, text, null);

    public static PhraseSegment CreateImage(PhraseImageReference image) =>
        new(Guid.NewGuid(), PhraseSegmentKind.Image, null, image);
}

/// <summary>
/// 首发图文话术正文。该类型刻意不包含文件系统信息；所有派生值均由不可变有序段计算，避免出现第二份正文事实源。
/// </summary>
public sealed record PhraseBody
{
    public const string DefaultBatchSeparator = "---";

    public PhraseBody(ImmutableArray<PhraseSegment> segments, string batchSeparator)
    {
        Segments = segments;
        BatchSeparator = NormalizeBatchSeparator(batchSeparator);
    }

    public ImmutableArray<PhraseSegment> Segments { get; }
    public string BatchSeparator { get; }
    public int SegmentCount => Segments.IsDefault ? 0 : Segments.Length;
    public int ImageCount => Segments.IsDefault ? 0 : Segments.Count(segment => segment.Kind == PhraseSegmentKind.Image);
    public bool RequiresBatchDelivery => SegmentCount > 1 || ImageCount > 0;
    public bool IsSingleText => SegmentCount == 1 && ImageCount == 0;

    /// <summary>列表摘要只取顺序中的第一段文字；完整搜索文本继续使用 TextProjection。</summary>
    public string FirstText => Segments.IsDefault
        ? string.Empty
        : Segments.FirstOrDefault(segment => segment.Kind == PhraseSegmentKind.Text)?.Text ?? string.Empty;

    public string TextProjection => Segments.IsDefault
        ? string.Empty
        : string.Join('\n', Segments
            .Where(segment => segment.Kind == PhraseSegmentKind.Text && segment.Text is not null)
            .Select(segment => segment.Text));

    public static string NormalizeBatchSeparator(string? separator) => (separator ?? string.Empty).Trim();

    public static PhraseBody FromText(string text, string batchSeparator = DefaultBatchSeparator) =>
        new([PhraseSegment.CreateText(text)], batchSeparator);
}

/// <summary>媒体导入结果只返回脱敏资产引用；错误信息不得包含原文件名或绝对路径。</summary>
public sealed record MediaImportResult(
    bool IsSuccess,
    PhraseImageReference? Image,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static MediaImportResult Success(PhraseImageReference image) => new(true, image, null, null);
    public static MediaImportResult Failure(string code, string message) => new(false, null, code, message);
}

public sealed record MediaAssetContent(PhraseImageReference Image, byte[] Bytes);

/// <summary>平台无关媒体库契约；Desktop 只接触资产引用和规范化字节，不接触内部存储路径。</summary>
public interface IMediaAssetStore
{
    Task<MediaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<MediaAssetContent?> ReadAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅当持久化事实源能证明资产未被任何话术段引用时删除媒体；文件删除失败必须保留元数据，以便启动时重试。
    /// </summary>
    Task DeleteIfUnreferencedAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public sealed record Phrase(
    Guid Id,
    string Title,
    PhraseBody Body,
    Guid CategoryId,
    ShortcutMode ShortcutMode,
    ShortcutValue? Shortcut,
    int UsageCount,
    DateTimeOffset? LastUsedAtUtc,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ColorKey = "default",
    int SortOrder = 0,
    PhraseScope Scope = PhraseScope.Personal);

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
    bool QuickSendWithoutConfirmation,
    bool ClipboardCompatibilityMode,
    bool HasCompletedOnboarding = false,
    int OnboardingVersion = 0)
{
}

public sealed record CreatePhraseCommand(
    Guid Id,
    string Title,
    PhraseBody Body,
    Guid CategoryId,
    ShortcutMode ShortcutMode,
    string? Shortcut,
    string ColorKey = "default",
    int SortOrder = 0);

public sealed record UpdatePhraseCommand(
    Guid Id,
    long ExpectedVersion,
    string Title,
    PhraseBody Body,
    Guid CategoryId,
    ShortcutMode ShortcutMode,
    string? Shortcut,
    string ColorKey = "default",
    int SortOrder = 0);

/// <summary>
/// 创建分类请求。未指定 SortOrder 的一级分类由持久化层追加到现有一级分类末尾；
/// 二级分类仍使用默认排序 0。调用方传入明确排序值时，持久化层原样保留。
/// </summary>
public sealed record CreateCategoryCommand(Guid Id, string Name, Guid? ParentId = null, int? SortOrder = null);
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
    CategoryContains,
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

/// <summary>
/// 内存搜索命中项。分类路径由索引快照在重建时写入，供展示层识别结果来源；
/// 搜索调用本身不访问分类仓储或任何持久化实现。
/// </summary>
public sealed record SearchResult(Phrase Phrase, SearchMatchKind MatchKind, string CategoryPath = "未分类");

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

/// <summary>
/// Adapter 的首发能力快照。文字、图片、发送及各自验证能力必须独立表达，
/// 禁止用客户端版本号推导图片准入，也禁止把“已触发发送”误写成最终已发送。
/// </summary>
public sealed record AdapterCapabilities(
    CapabilityStatus InsertText,
    CapabilityStatus VerifyTextInsert,
    CapabilityStatus InsertImage,
    CapabilityStatus VerifyImageInsert,
    CapabilityStatus TriggerSend,
    CapabilityStatus VerifySend);

public sealed record AdapterProfile(
    string AdapterId,
    string ApplicationId,
    string ProfileVersion,
    CapabilityStatus InsertTextStatus,
    CapabilityStatus VerifyTextInsertStatus,
    CapabilityStatus InsertImageStatus,
    CapabilityStatus VerifyImageInsertStatus,
    CapabilityStatus TriggerSendStatus,
    CapabilityStatus VerifySendStatus,
    string FallbackMode,
    DateTimeOffset? VerifiedAtUtc);

public sealed record DeliveryRequest(
    Phrase Phrase,
    DeliveryTarget? Target,
    SendMode Mode,
    bool ClipboardCompatibilityMode,
    TargetChangeBehavior TargetChangeBehavior = TargetChangeBehavior.CopyOnly,
    bool RecordUsageOnSuccess = true);

/// <summary>
/// 用户本次投递的明确意图。该枚举不描述快捷键，也不规定 Adapter 采用何种发送协议。
/// </summary>
public enum SendMode
{
    InsertOnly,
    InsertAndSend,
}

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
    SendTriggered,
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
    AdapterStabilityWait,
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

public sealed record SendResult(bool WasApplied, bool Inconclusive = false, string Code = "SEND_TRIGGERED")
{
    public static SendResult Applied { get; } = new(true);
    public static SendResult Unknown(string code) => new(false, true, code);
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
    public bool Inserted => Effect is DeliveryEffect.Inserted or DeliveryEffect.SendTriggered or DeliveryEffect.Sent;
    public bool SendTriggered => Effect is DeliveryEffect.SendTriggered or DeliveryEffect.Sent;
    public bool Sent => Effect is DeliveryEffect.Sent;
    public bool IsSuccess => Status == DeliveryStatus.Success;
}

/// <summary>分批投递结果；FailedSegmentIndex 使用从 1 开始的用户可见序号，空值表示没有失败段。</summary>
public sealed record BatchDeliveryResult(
    DeliveryStatus Status,
    DeliveryEffect Effect,
    int TotalSegments,
    int CompletedSegments,
    int? FailedSegmentIndex,
    ImmutableArray<DeliveryResult> SegmentResults,
    Guid TraceId)
{
    public bool IsSuccess => Status == DeliveryStatus.Success && CompletedSegments == TotalSegments;
}

public interface IBatchDeliveryStateMachine
{
    Task<BatchDeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default);
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

/// <summary>
/// 图片段的 Adapter 执行契约。规范化图片字节仍是平台无关数据；具体剪贴板、窗口与焦点操作只由 Platform.Windows 实现。
/// 能力字段保持独立门控：只有 InsertImage 与 VerifyImageInsert 均为 Verified 时，批次状态机才会调用本接口。
/// </summary>
public interface IImageApplicationAdapter
{
    Task<InsertResult> InsertImageAsync(DeliveryRequest request, MediaAssetContent image, CancellationToken cancellationToken);
    Task<VerificationResult> VerifyImageInsertAsync(DeliveryRequest request, CancellationToken cancellationToken);
}

public interface IAdapterResolver
{
    IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null);
}

/// <summary>
/// Adapter 的不可配置段间稳定等待。实现只能依据运行时目标、前台窗口和脱敏焦点指纹判断，
/// 不读取聊天正文，也不以客户端版本号作为准入条件。
/// </summary>
public interface IAdapterBatchStabilityWaiter
{
    Task<VerificationResult> WaitForStabilityAsync(DeliveryTarget target, CancellationToken cancellationToken);
}

public interface ITextDeliveryStateMachine
{
    Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default);
}










// TEMP_MARKER_0819
