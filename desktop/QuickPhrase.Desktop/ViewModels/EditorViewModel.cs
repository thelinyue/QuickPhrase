using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.DesignSystem.Components;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 首发图文话术编辑器。正文由可键盘操作的有序段列表组成；图片先进入应用媒体库，保存时只提交 AssetId 和脱敏元数据。
/// 分类选择仍沿用一级必选、二级可选的既有交互，所有持久化读写经 ICommandService 完成。
/// </summary>
public partial class EditorViewModel : ObservableObject, INavigationGuard
{
    public static readonly IReadOnlyList<ColorKeyOption> ColorKeys =
    [
        new("default", "无颜色", "#FFFFFF"), new("orange", "橙色", "#FF8839"),
        new("blue", "蓝色", "#178BFF"), new("magenta", "洋红", "#FF73FF"),
        new("purple", "紫色", "#AF60FF"), new("green", "绿色", "#41C028"),
        new("pink", "粉色", "#F67E91"), new("teal", "青色", "#00A8A8"),
        new("tan", "卡其色", "#CB9563"), new("gray", "灰色", "#5C6772"),
    ];

    private readonly ICommandService _commands;
    private readonly Guid _id;
    private readonly bool _isNew;
    private readonly Guid? _defaultCategoryId;
    private long _version;
    private string _baseTitle = "";
    private PhraseBody _baseBody = new([]);
    private Guid _baseCategoryId;
    private string _baseColorKey = "orange";
    private bool _synchronizingCategorySelection;
    private bool _hasInvalidDocumentDraft;
    private readonly HashSet<Guid> _sessionImportedAssetIds = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private ObservableCollection<PhraseSegmentItemViewModel> _segments = new();
    [ObservableProperty] private Guid _selectedCategoryId;
    [ObservableProperty] private ObservableCollection<CategoryItem> _categories = new();
    [ObservableProperty] private ObservableCollection<CategoryItem> _primaryCategories = new();
    [ObservableProperty] private ObservableCollection<SecondaryCategoryOption> _secondaryCategoryOptions = new();
    [ObservableProperty] private CategoryItem? _selectedPrimaryCategory;
    [ObservableProperty] private SecondaryCategoryOption? _selectedSecondaryCategory;
    [ObservableProperty] private string _colorKey = "orange";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _documentError;
    [ObservableProperty] private int _documentCharacterCount;
    [ObservableProperty] private int _documentImageCount;
    [ObservableProperty] private int _documentSegmentCount;
    [ObservableProperty] private string _headerTitle = "编辑话术";
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private bool _isReadOnly;

    public bool IsNew => _isNew;
    public bool HasCategories => Categories.Count > 0;
    public bool HasSecondaryCategories => SecondaryCategoryOptions.Any(option => option.CategoryId.HasValue);
    public string CompositionSummary => $"{DocumentCharacterCount} 字 · {DocumentSegmentCount} 段 · {DocumentImageCount} 图";
    public string? VisibleErrorMessage => DocumentError ?? ErrorMessage;
    public bool HasDocumentError => !string.IsNullOrWhiteSpace(DocumentError);

    partial void OnDocumentErrorChanged(string? value) { OnPropertyChanged(nameof(VisibleErrorMessage)); OnPropertyChanged(nameof(HasDocumentError)); }
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(VisibleErrorMessage));

    public event EventHandler<Phrase>? Saved;
    public event EventHandler? Cancelled;
    public event EventHandler? Deleted;

    public EditorViewModel(ICommandService commands, PhraseItemViewModel? existing, Guid? defaultCategoryId = null)
    {
        _commands = commands;
        _isNew = existing is null;
        _id = existing?.Id ?? Guid.NewGuid();
        _version = existing?.Version ?? 0;
        _defaultCategoryId = defaultCategoryId;
        _baseCategoryId = existing?.CategoryId ?? Guid.Empty;
        HeaderTitle = _isNew ? "新建话术" : existing?.IsEnterprise == true ? "企业话术详情" : "编辑话术";
        IsReadOnly = existing?.IsEnterprise == true;
        CanDelete = !_isNew && !IsReadOnly;

        if (existing is not null)
        {
            var phrase = existing.ToPhrase();
            _baseTitle = phrase.Title;
            _baseBody = phrase.Body;
            _baseColorKey = NormalizeColorKey(phrase.ColorKey);
            Title = _baseTitle;
            ReplaceSegments(phrase.Body.Segments);
            SelectedCategoryId = _baseCategoryId;
            ColorKey = _baseColorKey;
            _ = LoadSegmentPreviewsAsync();
        }
        else
        {
            ReplaceSegments([]);
            _baseBody = BuildBody();
        }
    }

    public async Task LoadCategoriesAsync(Guid? preferredCategoryId = null)
    {
        var list = await _commands.ListCategoriesAsync();
        Categories = new ObservableCollection<CategoryItem>(list.Select(c => new CategoryItem(c.Id, c.Name, c.ParentId, c.SortOrder, Version: c.Version, Scope: c.Scope)));
        PrimaryCategories = new ObservableCollection<CategoryItem>(Categories.Where(category => category.ParentId is null).OrderBy(category => category.SortOrder).ThenBy(category => category.Name, StringComparer.CurrentCulture));
        var targetCategoryId = preferredCategoryId.HasValue && Categories.Any(c => c.Id == preferredCategoryId.Value)
            ? preferredCategoryId.Value
            : _defaultCategoryId.HasValue && Categories.Any(c => c.Id == _defaultCategoryId.Value)
                ? _defaultCategoryId.Value
                : SelectedCategoryId != Guid.Empty && Categories.Any(c => c.Id == SelectedCategoryId)
                    ? SelectedCategoryId
                    : PrimaryCategories.FirstOrDefault()?.Id ?? Guid.Empty;
        SynchronizeCategorySelectors(targetCategoryId);
    }

    partial void OnCategoriesChanged(ObservableCollection<CategoryItem> value) => OnPropertyChanged(nameof(HasCategories));
    partial void OnSegmentsChanged(ObservableCollection<PhraseSegmentItemViewModel> value) => OnPropertyChanged(nameof(CompositionSummary));

    partial void OnSelectedPrimaryCategoryChanged(CategoryItem? value)
    {
        if (_synchronizingCategorySelection || value is null) return;
        SynchronizeCategorySelectors(value.Id);
    }

    partial void OnSelectedSecondaryCategoryChanged(SecondaryCategoryOption? value)
    {
        if (_synchronizingCategorySelection || SelectedPrimaryCategory is null || value is null) return;
        _synchronizingCategorySelection = true;
        try { SelectedCategoryId = value.CategoryId ?? SelectedPrimaryCategory.Id; }
        finally { _synchronizingCategorySelection = false; }
    }

    partial void OnSelectedCategoryIdChanged(Guid value)
    {
        if (_synchronizingCategorySelection || Categories.Count == 0 || value == Guid.Empty) return;
        SynchronizeCategorySelectors(value);
    }

    private void SynchronizeCategorySelectors(Guid targetCategoryId)
    {
        var target = Categories.FirstOrDefault(category => category.Id == targetCategoryId);
        var primary = target?.ParentId is Guid parentId
            ? PrimaryCategories.FirstOrDefault(category => category.Id == parentId)
            : PrimaryCategories.FirstOrDefault(category => category.Id == targetCategoryId) ?? PrimaryCategories.FirstOrDefault();
        _synchronizingCategorySelection = true;
        try
        {
            SelectedPrimaryCategory = primary;
            var options = new List<SecondaryCategoryOption> { new(null, "不选择二级分类") };
            if (primary is not null)
                options.AddRange(Categories.Where(category => category.ParentId == primary.Id).OrderBy(category => category.SortOrder).ThenBy(category => category.Name, StringComparer.CurrentCulture).Select(category => new SecondaryCategoryOption(category.Id, category.Name)));
            SecondaryCategoryOptions = new ObservableCollection<SecondaryCategoryOption>(options);
            var selectedSecondaryId = target?.ParentId is not null ? target.Id : (Guid?)null;
            SelectedSecondaryCategory = SecondaryCategoryOptions.First(option => option.CategoryId == selectedSecondaryId);
            SelectedCategoryId = selectedSecondaryId ?? primary?.Id ?? Guid.Empty;
            OnPropertyChanged(nameof(HasSecondaryCategories));
        }
        finally { _synchronizingCategorySelection = false; }
    }

    /// <summary>
    /// 接收富文本控件的单次文档投影。该方法只更新草稿和统计，不要求控件反向重建文档，
    /// 因而不会打断光标、选区或原生撤销栈。
    /// </summary>
    internal void ApplyDocumentDraft(PhraseRichDocumentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        DocumentCharacterCount = draft.CharacterCount;
        DocumentImageCount = draft.ImageCount;
        DocumentSegmentCount = draft.Segments.Length;
        DocumentError = draft.IsValid ? ValidateDraftLimits(draft) : draft.ErrorMessage;
        _hasInvalidDocumentDraft = !draft.IsValid;

        if (draft.IsValid)
            ReplaceSegmentsPreservingImages(draft.Segments);
        OnPropertyChanged(nameof(CompositionSummary));
    }

    private static string? ValidateDraftLimits(PhraseRichDocumentDraft draft)
    {
        if (draft.Segments.Length > PhraseRules.MaxSegmentCount)
            return $"每条话术最多包含 {PhraseRules.MaxSegmentCount} 个内容段。";
        if (draft.ImageCount > PhraseRules.MaxImageCount)
            return $"每条话术最多包含 {PhraseRules.MaxImageCount} 张图片。";
        if (draft.CharacterCount > PhraseRules.MaxTextLength)
            return $"全部文字段合计不能超过 {PhraseRules.MaxTextLength} 个字符。";
        return null;
    }

    private void ReplaceSegmentsPreservingImages(IEnumerable<PhraseSegment> segments)
    {
        var existingImages = Segments
            .Where(item => item.Image is not null)
            .GroupBy(item => item.Image!.AssetId)
            .ToDictionary(group => group.Key, group => group.First());
        Segments = new ObservableCollection<PhraseSegmentItemViewModel>(segments.Select(segment =>
            segment.Image is not null && existingImages.TryGetValue(segment.Image.AssetId, out var existing)
                ? existing
                : PhraseSegmentItemViewModel.From(segment)));
        NotifySegmentsChanged();
    }

    public bool HasUnsavedChanges =>
        _hasInvalidDocumentDraft || Title != _baseTitle || !BodiesEqual(BuildBody(), _baseBody) || SelectedCategoryId != _baseCategoryId || NormalizeColorKey(ColorKey) != _baseColorKey;

    public void DiscardChanges()
    {
        // INavigationGuard 当前是同步契约。显式放弃时必须先完成本次编辑导入资产的释放，
        // Task.Run 避免在 WPF 同步上下文上阻塞异步 SQLite 续体造成死锁。
        ReleaseSessionImportsSynchronously();
        Title = _baseTitle;
        DocumentError = null;
        _hasInvalidDocumentDraft = false;
        ReplaceSegments(_baseBody.Segments);
        SelectedCategoryId = _baseCategoryId;
        ColorKey = _baseColorKey;
    }

    public async Task SaveAsync()
    {
        if (IsBusy || IsReadOnly) return;
        if (!string.IsNullOrWhiteSpace(DocumentError)) { ErrorMessage = DocumentError; return; }
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var body = BuildBody();
            var colorKey = NormalizeColorKey(ColorKey);
            RepositoryResult<Phrase> result = _isNew
                ? await _commands.CreatePhraseAsync(new CreatePhraseCommand(_id, Title.Trim(), body, SelectedCategoryId, ShortcutMode.None, null, colorKey))
                : await _commands.UpdatePhraseAsync(new UpdatePhraseCommand(_id, _version, Title.Trim(), body, SelectedCategoryId, ShortcutMode.None, null, colorKey));
            if (result.IsSuccess && result.Value is { } phrase)
            {
                _version = phrase.Version;
                _baseTitle = phrase.Title;
                _baseBody = phrase.Body;
                _baseCategoryId = phrase.CategoryId;
                _baseColorKey = NormalizeColorKey(phrase.ColorKey);
                ColorKey = _baseColorKey;
                await ReleaseSessionImportsAfterSaveAsync(phrase.Body);
                Saved?.Invoke(this, phrase);
            }
            else ErrorMessage = result.Error?.Message ?? "保存失败。";
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsBusy = false; }
    }

    private PhraseBody BuildBody() => new(Segments.Select(item => item.ToModel()).ToImmutableArray());

    private void ReplaceSegments(IEnumerable<PhraseSegment> segments)
    {
        Segments = new ObservableCollection<PhraseSegmentItemViewModel>(segments.Select(PhraseSegmentItemViewModel.From));
        NotifySegmentsChanged();
    }

    private void NotifySegmentsChanged()
    {
        for (var index = 0; index < Segments.Count; index++)
            Segments[index].Index = index + 1;
        DocumentCharacterCount = Segments.Where(item => item.Kind == PhraseSegmentKind.Text).Sum(item => item.Text?.Length ?? 0);
        DocumentImageCount = Segments.Count(item => item.Kind == PhraseSegmentKind.Image);
        DocumentSegmentCount = Segments.Count;
        OnPropertyChanged(nameof(CompositionSummary));
    }

    /// <summary>
    /// 导入图片但不改变段集合；只有富文本控件在光标处完成插入后，文档投影才成为正文草稿。
    /// </summary>
    internal async Task<PhraseSegmentItemViewModel?> ImportImageItemAsync(string path)
    {
        if (IsReadOnly || IsBusy) return null;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _commands.ImportImageAsync(path);
            if (!result.IsSuccess || result.Image is null)
            {
                ErrorMessage = result.ErrorMessage ?? "图片导入失败。";
                return null;
            }

            _sessionImportedAssetIds.Add(result.Image.AssetId);
            var item = PhraseSegmentItemViewModel.From(PhraseSegment.CreateImage(result.Image));
            await LoadPreviewAsync(item);
            return item;
        }
        catch (Exception)
        {
            ErrorMessage = "图片处理失败，请确认图片格式和大小后重试。";
            return null;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 剪贴板位图先写入随机临时 PNG，再复用正式媒体导入链路完成解码、清洗和重新编码；临时文件始终尽力删除。
    /// </summary>
    internal async Task<PhraseSegmentItemViewModel?> ImportClipboardImageAsync(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var tempPath = Path.Combine(Path.GetTempPath(), $"QuickPhrase-Clipboard-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                encoder.Save(stream);
            return await ImportImageItemAsync(tempPath);
        }
        catch (Exception)
        {
            ErrorMessage = "无法处理剪贴板图片，请重新复制后再试。";
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"剪贴板临时图片清理失败，将由系统临时目录策略回收。阶段：EDITOR_CLIPBOARD_CLEANUP；结果码：TEMP_CLEANUP_FAILED；异常类型：{exception.GetType().Name}");
            }
        }
    }

    private async Task ReleaseSessionImportsAfterSaveAsync(PhraseBody savedBody)
    {
        var retained = savedBody.Segments
            .Where(segment => segment.Kind == PhraseSegmentKind.Image)
            .Select(segment => segment.Image!.AssetId)
            .ToHashSet();
        await ReleaseAssetsAsync(_sessionImportedAssetIds.Where(assetId => !retained.Contains(assetId)).ToArray());
        _sessionImportedAssetIds.Clear();
    }

    private void ReleaseSessionImportsSynchronously()
    {
        var assetIds = _sessionImportedAssetIds.ToArray();
        if (assetIds.Length == 0) return;
        try
        {
            Task.Run(() => ReleaseAssetsAsync(assetIds)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"放弃编辑时媒体清理失败，将在下次启动重试。阶段：EDITOR_MEDIA_CLEANUP；结果码：MEDIA_CLEANUP_FAILED；异常类型：{exception.GetType().Name}");
        }
        finally
        {
            _sessionImportedAssetIds.Clear();
        }
    }

    private async Task ReleaseAssetsAsync(IEnumerable<Guid> assetIds)
    {
        foreach (var assetId in assetIds.Distinct())
        {
            try
            {
                await _commands.DeleteMediaIfUnreferencedAsync(assetId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"编辑器媒体清理失败，将在下次启动重试。阶段：EDITOR_MEDIA_CLEANUP；结果码：MEDIA_CLEANUP_FAILED；异常类型：{exception.GetType().Name}");
            }
        }
    }
    private async Task LoadSegmentPreviewsAsync()
    {
        foreach (var item in Segments.Where(item => item.Kind == PhraseSegmentKind.Image)) await LoadPreviewAsync(item);
    }

    private async Task LoadPreviewAsync(PhraseSegmentItemViewModel item)
    {
        if (item.Image is null) return;
        item.LoadError = null;
        item.Thumbnail = null;
        try
        {
            var content = await _commands.ReadMediaAsync(item.Image.AssetId);
            if (content is null)
            {
                item.LoadError = "图片加载失败，无法读取媒体内容。";
                return;
            }

            using var stream = new MemoryStream(content.Bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.DecodePixelWidth = 160; bitmap.EndInit(); bitmap.Freeze();
            item.Thumbnail = bitmap;
        }
        catch (Exception)
        {
            // 图片读取和解码都属于非关键预览路径，异常必须转换为行内中文状态，不能逃逸到 WPF Dispatcher。
            item.LoadError = "图片加载失败，媒体内容可能已损坏。";
        }
    }

    private static bool BodiesEqual(PhraseBody left, PhraseBody right)
    {
        if (left.Segments.Length != right.Segments.Length) return false;
        for (var index = 0; index < left.Segments.Length; index++)
        {
            var leftSegment = left.Segments[index];
            var rightSegment = right.Segments[index];
            if (leftSegment.Kind != rightSegment.Kind || leftSegment.Text != rightSegment.Text || !Equals(leftSegment.Image, rightSegment.Image))
                return false;
        }
        return true;
    }
    private static string NormalizeColorKey(string? colorKey) => string.IsNullOrWhiteSpace(colorKey) ? "default" : colorKey.Trim().ToLowerInvariant();

    [RelayCommand] private async Task Save() => await SaveAsync();
    [RelayCommand] private void Cancel() { DiscardChanges(); Cancelled?.Invoke(this, EventArgs.Empty); }
    [RelayCommand] private async Task Delete()
    {
        if (_isNew) { DiscardChanges(); Cancelled?.Invoke(this, EventArgs.Empty); return; }
        if (IsReadOnly) return;
        var ok = await _commands.DeletePhraseAsync(_id, _version);
        if (ok) { DiscardChanges(); Deleted?.Invoke(this, EventArgs.Empty); } else ErrorMessage = "删除失败。";
    }
}

/// <summary>编辑器段显示模型；缩略图只存在于 Desktop，不进入 Core 或日志。</summary>
public partial class PhraseSegmentItemViewModel : ObservableObject
{
    private PhraseSegmentItemViewModel(PhraseSegment model)
    {
        Id = model.Id; Kind = model.Kind; Text = model.Text; Image = model.Image;
    }
    public Guid Id { get; }
    public PhraseSegmentKind Kind { get; }
    public PhraseImageReference? Image { get; }
    public string DimensionText => Image is null ? string.Empty : $"{Image.PixelWidth} × {Image.PixelHeight}";
    public string ImageAutomationName => $"图片，第 {Index} 段，{DimensionText}";
    public string DeleteImageAutomationName => $"删除第 {Index} 段图片";
    [ObservableProperty] private int _index;
    [ObservableProperty] private string? _text;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private string? _loadError;
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoadError));
    partial void OnIndexChanged(int value) { OnPropertyChanged(nameof(ImageAutomationName)); OnPropertyChanged(nameof(DeleteImageAutomationName)); }
    public PhraseSegment ToModel() => new(Id, Kind, Kind == PhraseSegmentKind.Text ? Text : null, Image);
    public static PhraseSegmentItemViewModel From(PhraseSegment model) => new(model);
}

public sealed record ColorKeyOption(string Key, string Label, string Hex)
{
    public SolidColorBrush Brush { get; } = CreateBrush(Hex);
    private static SolidColorBrush CreateBrush(string hex) { var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!); brush.Freeze(); return brush; }
}

public sealed record SecondaryCategoryOption(Guid? CategoryId, string Name);
