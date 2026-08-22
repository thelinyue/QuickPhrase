using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>多段/图片话术的整批预览与显式发送确认；预览本身绝不触发投递。</summary>
public partial class BatchPreviewWindow : Window
{
    private readonly IMediaAssetStore? _media;
    public ObservableCollection<BatchPreviewItem> Items { get; } = new();
    public bool Confirmed { get; private set; }

    public BatchPreviewWindow(Phrase phrase, IMediaAssetStore? media, bool confirmation, AdapterCapabilities capabilities)
    {
        InitializeComponent();
        _media = media;
        HeadingText.Text = confirmation ? "确认整批发送" : "整批预览";
        SummaryText.Text = $"共 {phrase.Body.SegmentCount} 条消息，{phrase.Body.ImageCount} 张图片；将严格按下列顺序处理。";
        CapabilityText.Text = FormatCapabilities(capabilities);
        ConfirmButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = confirmation && CanDeliver(phrase, capabilities);
        var index = 1;
        foreach (var segment in phrase.Body.Segments) Items.Add(new BatchPreviewItem(index++, segment));
        SegmentsList.ItemsSource = Items;
        Loaded += async (_, _) => await LoadThumbnailsAsync();
    }

    private static bool CanDeliver(Phrase phrase, AdapterCapabilities capabilities)
    {
        var hasText = phrase.Body.Segments.Any(segment => segment.Kind == PhraseSegmentKind.Text);
        var textReady = !hasText
            || capabilities.InsertText == CapabilityStatus.Verified
            && capabilities.VerifyTextInsert == CapabilityStatus.Verified;
        var imageReady = phrase.Body.ImageCount == 0
            || capabilities.InsertImage == CapabilityStatus.Verified
            && capabilities.VerifyImageInsert == CapabilityStatus.Verified;
        return textReady && imageReady && capabilities.TriggerSend == CapabilityStatus.Verified;
    }

    private static string FormatCapabilities(AdapterCapabilities capabilities) =>
        $"InsertText：{FormatCapability(capabilities.InsertText)}；"
        + $"VerifyTextInsert：{FormatCapability(capabilities.VerifyTextInsert)}；"
        + $"InsertImage：{FormatCapability(capabilities.InsertImage)}；"
        + $"VerifyImageInsert：{FormatCapability(capabilities.VerifyImageInsert)}；"
        + $"TriggerSend：{FormatCapability(capabilities.TriggerSend)}；"
        + $"VerifySend：{FormatCapability(capabilities.VerifySend)}。";

    private static string FormatCapability(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Verified => "已验证",
        CapabilityStatus.Unsupported => "不支持",
        _ => "未验证",
    };

    private async Task LoadThumbnailsAsync()
    {
        foreach (var item in Items.Where(item => item.Image is not null))
        {
            item.LoadError = null;
            item.Thumbnail = null;
            try
            {
                if (_media is null)
                {
                    item.LoadError = "图片加载失败，媒体库不可用。";
                    continue;
                }

                var content = await _media.ReadAsync(item.Image!.AssetId);
                if (content is null)
                {
                    item.LoadError = "图片加载失败，无法读取媒体内容。";
                    continue;
                }

                using var stream = new MemoryStream(content.Bytes, false);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.DecodePixelWidth = 160; bitmap.EndInit(); bitmap.Freeze();
                item.Thumbnail = bitmap;
            }
            catch (Exception)
            {
                // Loaded 是 async void 事件入口，所有媒体异常必须在此转换为可见状态，不能逃逸到 Dispatcher。
                item.LoadError = "图片加载失败，媒体内容可能已损坏。";
            }
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) { Confirmed = true; DialogResult = true; }
    private void Close_Click(object sender, RoutedEventArgs e) { Confirmed = false; DialogResult = false; }
}

public sealed class BatchPreviewItem : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private string? _loadError;
    public BatchPreviewItem(int index, PhraseSegment segment) { Index = index; Segment = segment; }
    public int Index { get; }
    public PhraseSegment Segment { get; }
    public PhraseImageReference? Image => Segment.Image;
    public string TypeLabel => Segment.Kind == PhraseSegmentKind.Text ? "文字" : "图片";
    public string Text => Segment.Text is null ? string.Empty : (Segment.Text.Length <= 160 ? Segment.Text : Segment.Text[..160] + "…");
    public string DimensionText => Image is null ? string.Empty : $"{Image.PixelWidth} × {Image.PixelHeight}";
    public string AutomationName => Image is null ? $"文字，第 {Index} 段" : $"图片，第 {Index} 段，{Image.PixelWidth} × {Image.PixelHeight}";
    public Visibility ImageVisibility => Image is null ? Visibility.Collapsed : Visibility.Visible;
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public string? LoadError
    {
        get => _loadError;
        set
        {
            _loadError = value;
            PropertyChanged?.Invoke(this, new(nameof(LoadError)));
            PropertyChanged?.Invoke(this, new(nameof(HasLoadError)));
        }
    }
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
