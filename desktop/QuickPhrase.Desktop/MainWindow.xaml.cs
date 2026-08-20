using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>绠＄悊绐楀彛鍘熺敓澶栧３锛氭爣棰樻爮 + 瀵艰埅闈㈡澘 + 鍐呭鍖恒€傝瘽鏈簱锛圥hase D锟? 缂栬緫锟?/ 璁剧疆锛圥hase E锛夊潎涓虹函 WPF锟?/summary>
public partial class MainWindow : Window
{
    private ManagementWindowLayout? _appliedLayout;
    private readonly ICommandService _commands = null!;
    private readonly SearchHistoryCoordinator _searchHistory;

    private LibraryView? _libraryView;
    private INavigationGuard? _currentGuard;

    /// <summary>涓荤晫闈㈡垨鎵樼洏璇锋眰鎵撳紑鐙珛璁剧疆绐楀彛锟?/summary>
    public event EventHandler? SettingsRequested;

    public MainWindow(ICommandService commands, SearchHistoryCoordinator searchHistory, string initialScene)
    {
        InitializeComponent();
        _commands = commands;
        _searchHistory = searchHistory;
        ApplyScene(initialScene);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>鎸夌鐞嗛〉鍦烘櫙璋冩暣瀹夸富灏哄骞堕噸鏂板眳涓紱闈炴硶鍦烘櫙涓嶄細鏀瑰彉褰撳墠绐楀彛锟?/summary>
    public void ApplyScene(string scene)
    {
        if (!ManagementWindowLayout.TryGet(scene, out var layout)) return;
        if (_appliedLayout == layout) return;
        _appliedLayout = layout;
        if (IsVisible && WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        MinWidth = layout.MinWidth;
        MinHeight = layout.MinHeight;
        Width = layout.Width;
        Height = layout.Height;
        if (IsVisible) CenterOnCurrentMonitor();
    }

    /// <summary>锟?ApplicationController 璋冪敤锛屽垏鎹㈠凡鎵撳紑绐楀彛鐨勫唴瀹硅〃闈紙library/editor/settings 绛夛級锟?/summary>
    public void NavigateTo(string key)
    {
        _ = OnNavigateAsync(key);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupTrace.Mark("native-first-paint");
        if (ContentRegion.Content is null) _ = OnNavigateAsync("library");
    }

    private async Task OnNavigateAsync(string key)
    {
        switch (key)
        {
            case "library":
                await SwitchToAsync(EnsureLibrary(), null);
                break;
            case "settings":
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "editor":
                ShowEditorModal(null);
                break;
            default:
                SetActiveTitle(key switch
                {
                    "shortcuts" => "快捷键",
                    "trash" => "回收站",
                    _ => key
                });
                ContentRegion.Content = BuildPlaceholder(key);
                _currentGuard = null;
                break;
        }
    }

    private LibraryView EnsureLibrary()
    {
        if (_libraryView is null)
        {
            _libraryView = new LibraryView(_commands, _searchHistory);
            _libraryView.RequestEdit += (_, item) => ShowEditorModal(item);
            _libraryView.RequestNew += (_, _) => _ = ShowNewPhraseAsync();
            _libraryView.RequestMove += (_, item) => ShowMoveDialog(item);
            _libraryView.RequestNewCategory += (_, _) => ShowNewCategoryDialog();
            _libraryView.RequestNewSubCategory += (_, c) => ShowNewCategoryDialog(c.Id);
            _libraryView.RequestNewPhraseInCategory += (_, c) => ShowEditorModal(null, c.Id);
            _libraryView.RequestRenameCategory += (_, c) => _ = ShowRenameCategoryDialogAsync(c);
            _libraryView.RequestDeleteCategory += (_, c) => _ = ShowDeleteCategoryDialogAsync(c);
            _libraryView.RequestOpenSettings += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        }
        return _libraryView;
    }

    private Guid? ShowNewCategoryDialog(Guid? parentId = null)
    {
        var dlg = new CategoryDialog(_commands, parentId: parentId) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _ = _libraryView?.ReloadAsync();
            return dlg.CreatedCategoryId;
        }
        return null;
    }

    /// <summary>
    /// 新建话术必须先拥有一级分类。没有分类时给出明确的分支选择；新建分类成功后直接把
    /// 实际创建的分类传给编辑器，避免用户再次手动选择或误落到未分类状态。
    /// </summary>
    private async Task ShowNewPhraseAsync()
    {
        var categories = await _commands.ListCategoriesAsync();
        var topCategory = categories.FirstOrDefault(c => c.ParentId is null);
        if (topCategory is not null)
        {
            ShowEditorModal(null, topCategory.Id);
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            this,
            "还没有可用分类。\n\n创建话术前，请先创建一个一级分类。\n\n点击“确定”新建分类，点击“取消”返回。",
            "还没有可用分类",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK) return;

        var createdCategoryId = ShowNewCategoryDialog();
        if (createdCategoryId is null) return;
        await (_libraryView?.ReloadAsync() ?? Task.CompletedTask);
        ShowEditorModal(null, createdCategoryId);
    }

    private async Task ShowRenameCategoryDialogAsync(CategoryItem category)
    {
        var cats = await _commands.ListCategoriesAsync();
        var existing = cats.FirstOrDefault(c => c.Id == category.Id);
        if (existing is null) return;
        var dlg = new CategoryDialog(_commands, existing) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = _libraryView?.ReloadAsync();
    }

    /// <summary>
    /// 鍒犻櫎鍒嗙被鍓嶆墽琛屼袱娆＄嫭绔嬬‘璁ゃ€傜浜屾纭鏄庣‘鍒楀嚭绾ц仈鍒犻櫎鑼冨洿锛岄伩鍏嶇敤鎴锋妸鏅€氬垹闄よ瑙ｄ负浠呯Щ闄ゅ垎绫昏妭鐐癸拷?    /// </summary>
    private async Task ShowDeleteCategoryDialogAsync(CategoryItem category)
    {
        try
        {
            var deleteResult = await ConfirmAndDeleteCategoryAsync(
                _commands,
                category,
                () => System.Windows.MessageBox.Show(
                    $"确定删除分类“{category.Name}”吗？",
                    "鍒犻櫎鍒嗙被",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK,
                () => System.Windows.MessageBox.Show(
                    $"该操作将永久删除分类 {category.Name}、所有子分类及其中的话术，且无法恢复。\n\n确定继续吗？",
                    "纭姘镐箙鍒犻櫎",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK,
                () => _libraryView?.ReloadAsync() ?? Task.CompletedTask);
            if (deleteResult.IsSuccess && deleteResult.Value?.Deleted == true) return;

            if (deleteResult.Error is not null)
                System.Windows.MessageBox.Show(
                    deleteResult.Error.Message,
                     $"确定删除分类“{category.Name}”吗？",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(
                "删除分类时发生错误，请稍后重试。",
                "删除分类时发生错误，请稍后重试。",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 鍙湁涓ゆ纭閮介€氳繃锛屾墠杩涘叆鏁版嵁搴撳垹闄ょ紪鎺掞紱鍙栨秷鏃惰繑鍥炴湭鍒犻櫎缁撴灉涓斾笉瑙﹀彂鍒锋柊锟?    /// </summary>
    internal static async Task<RepositoryResult<DeleteResult>> ConfirmAndDeleteCategoryAsync(
        ICommandService commands,
        CategoryItem category,
        Func<bool> firstConfirmation,
        Func<bool> secondConfirmation,
        Func<Task> reload)
    {
        if (!firstConfirmation() || !secondConfirmation())
            return RepositoryResult<DeleteResult>.Success(new DeleteResult(false, null));
        return await DeleteCategoryAndReloadAsync(commands, category, reload);
    }

    /// <summary>
    /// 鍒嗙被鍒犻櫎鐨勬渶灏忎簨鍔＄紪鎺掞細鍙湁鍒犻櫎鎻愪氦鎴愬姛鍚庢墠鍒锋柊 UI锛岄伩鍏嶅紓姝ュ啓鍏ュ皻鏈畬鎴愭椂鐢ㄦ棫鏁版嵁瑕嗙洊鐣岄潰锟?    /// </summary>
    internal static async Task<RepositoryResult<DeleteResult>> DeleteCategoryAndReloadAsync(
        ICommandService commands,
        CategoryItem category,
        Func<Task> reload)
    {
        var result = await commands.DeleteCategoryAsync(category.Id, category.Version);
        if (result.IsSuccess && result.Value?.Deleted == true) await reload();
        return result;
    }

    /// <summary>锟?520px 妯℃€佸脊绐楁墦寮€缂栬緫鍣紙瀵归綈 design-system.md 5.7锛夈€備繚锟?鍙栨秷鍚庡叧闂脊绐楋紝搴撹鍥句繚鎸佸師鏍凤拷?/summary>
    private void ShowEditorModal(PhraseItemViewModel? existing, Guid? defaultCategoryId = null)
    {
        var editor = new EditorView(_commands, existing, defaultCategoryId);
        var ownerHandle = new WindowInteropHelper(this).Handle;
        var screen = ownerHandle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen
            : System.Windows.Forms.Screen.FromHandle(ownerHandle);
        var workingArea = screen?.WorkingArea ?? System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, 960, 720);

        var dialog = new Window
        {
            Style = (Style)FindResource("Style.Dialog.Window"),
            Width = 520,
            MinHeight = 360,
            MaxHeight = Math.Max(400, workingArea.Height * 0.85),
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Content = editor,
            Title = editor.ViewModel.HeaderTitle,
            ResizeMode = ResizeMode.CanResize,
        };
        editor.PhraseSaved += (_, phrase) => _libraryView?.RefreshPhrase(phrase);
        editor.CloseRequested += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private void ShowMoveDialog(PhraseItemViewModel item)
    {
        var dlg = new PhraseMoveDialog(_commands, item) { Owner = this };
        if (dlg.ShowDialog() == true)
            _libraryView?.RefreshPhrase(item.ToPhrase());
    }

    private async Task SwitchToAsync(FrameworkElement next, INavigationGuard? nextGuard)
    {
        if (_currentGuard is { HasUnsavedChanges: true })
        {
            var decision = ShowNavigationConfirm();
            if (decision == NavigationDecision.ContinueEditing) return;
            if (decision == NavigationDecision.SaveAndLeave) await _currentGuard.SaveAsync();
        }
        SwitchTo(next, nextGuard);
    }

    private void SwitchTo(FrameworkElement next, INavigationGuard? nextGuard)
    {
        ContentRegion.Content = next;
        _currentGuard = nextGuard;
        SetActiveTitle(next is EditorView ? "编辑话术" : "话术库");
    }

    private void SetActiveTitle(string pageTitle)
    {
        TitleBarControl.PageTitle = pageTitle;
        Title = $"闪语 · {pageTitle}";
    }

    private NavigationDecision ShowNavigationConfirm()
    {
        var dlg = new NavigationConfirmDialog { Owner = this };
        _ = dlg.ShowDialog();
        return dlg.Decision;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
         // 关闭后的驻留和退出逻辑由 ApplicationController 统一处理。
    }

    private UIElement BuildPlaceholder(string key)
    {
        var names = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
["shortcuts"] = "快捷键", ["trash"] = "回收站",
        };
        var name = names.TryGetValue(key, out var n) ? n : key;
        var placeholder = new Border
        {
            Child = new TextBlock
            {
                Text = $"{name}（后续 Phase 实现）",
                Style = (Style)FindResource("Style.Text.Title.Medium"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        placeholder.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Brush.Surface.Default");
        return placeholder;
    }

    private void CenterOnCurrentMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var workingArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new System.Windows.Point(workingArea.Left, workingArea.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(workingArea.Right, workingArea.Bottom));
        Left = topLeft.X + Math.Max(16, (bottomRight.X - topLeft.X - Width) / 2);
        Top = topLeft.Y + Math.Max(16, (bottomRight.Y - topLeft.Y - Height) / 2);
    }
}









