using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickPhrase.Desktop.Services
{
    /// <summary>
    /// 管理应用 Light/Dark 主题切换。订阅 <see cref="PropertyChanged"/> 后，
    /// 业务侧可实时切换。切换只替换 QuickPhraseTheme 聚合器中的颜色主题字典，
    /// 不重建 Typography、间距、圆角、尺寸、动效和控件 Style。
    /// </summary>
    public sealed class ThemeService : INotifyPropertyChanged
    {
        public const string LightDictionaryPath = "DesignSystem/Themes/QuickPhraseTheme.Light.xaml";
        public const string DarkDictionaryPath = "DesignSystem/Themes/QuickPhraseTheme.Dark.xaml";

        private static ThemeService? _instance;
        private static readonly object SyncRoot = new();

        public static ThemeService Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (SyncRoot)
                {
                    _instance ??= new ThemeService();
                }
                return _instance;
            }
        }

        private AppTheme _theme = AppTheme.Light;

        public AppTheme Theme
        {
            get => _theme;
            set
            {
                if (_theme == value) return;
                _theme = value;
                Apply(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDark));
            }
        }

        public bool IsDark => _theme == AppTheme.Dark;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 启动时根据当前 <see cref="Theme"/> 应用对应资源字典。
        /// 由 App.OnStartup 调用，确保启动即加载正确主题。
        /// </summary>
        public void Initialize()
        {
            Apply(_theme);
        }

        private static void Apply(AppTheme theme)
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return;

            var themeAggregator = app.Resources.MergedDictionaries.FirstOrDefault(dictionary =>
                dictionary.Source?.ToString().EndsWith(
                    "Themes/QuickPhraseTheme.xaml",
                    StringComparison.OrdinalIgnoreCase) == true);
            if (themeAggregator is null)
                return;

            var dictionaries = themeAggregator.MergedDictionaries;
            var themeIndex = -1;
            for (var index = 0; index < dictionaries.Count; index++)
            {
                var source = dictionaries[index].Source?.ToString();
                if (source?.EndsWith("QuickPhraseTheme.Light.xaml", StringComparison.OrdinalIgnoreCase) == true
                    || source?.EndsWith("QuickPhraseTheme.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true)
                {
                    themeIndex = index;
                    break;
                }
            }

            var sourcePath = theme == AppTheme.Dark ? DarkDictionaryPath : LightDictionaryPath;
            var themeDictionary = new System.Windows.ResourceDictionary
            {
                Source = new Uri($"/QuickPhrase;component/{sourcePath}", UriKind.Relative),
            };

            // 只替换聚合字典中的颜色/Brush/Shadow 层；Typography、Thickness、Radius、Size 和 Motion 保持同一实例。
            if (themeIndex >= 0)
                dictionaries[themeIndex] = themeDictionary;
            else
                dictionaries.Add(themeDictionary);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public enum AppTheme
    {
        Light = 0,
        Dark = 1,
    }
}