using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickPhrase.Desktop.Services
{
    /// <summary>
    /// 管理应用 Light/Dark 主题切换。订阅 <see cref="PropertyChanged"/> 后，
    /// 业务侧（设置页/快捷键）可实时切换。切换时合并对应 ResourceDictionary
    /// 到 Application.Current.Resources.MergedDictionaries 中，
    /// 晚合并的同名 key 会覆盖早合并的，因此暗色字典放在亮色之后即可生效。
    /// </summary>
    public sealed class ThemeService : INotifyPropertyChanged
    {
        public const string LightDictionaryPath = "Themes/QuickPhraseTheme.xaml";
        public const string DarkDictionaryPath = "Themes/QuickPhraseTheme.Dark.xaml";

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
            if (app == null) return;

            var source = theme == AppTheme.Dark
                ? DarkDictionaryPath
                : LightDictionaryPath;

            // 切换到 Dark 时移除 Light 颜色覆盖；切换到 Light 时移除 Dark 覆盖
            // 仅移除追加的覆盖字典，保留 App.xaml 中最初加载的 QuickPhraseTheme.xaml
            for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var src = app.Resources.MergedDictionaries[i].Source?.ToString();
                if (src == null) continue;
                var isDarkOverlay = src.EndsWith("QuickPhraseTheme.Dark.xaml", StringComparison.OrdinalIgnoreCase);
                var isLightOverlay = src.EndsWith("QuickPhraseTheme.xaml", StringComparison.OrdinalIgnoreCase);
                if (!isDarkOverlay && !isLightOverlay) continue;

                // 只移除追加的同名覆盖；App.xaml 里那一份原始 Theme 字典始终保留
                // 作为 Typography/Spacing/Radius/Height 的兜底来源。
                // 由于 App.xaml 直接加载的 Theme 与 ThemeService 加载的 source 字符串一致，
                // 这里无法直接区分。采取保守策略：移除追加的（位置 > 0）的同名 Dark 字典。
                if (i > 0 && theme == AppTheme.Light && isDarkOverlay)
                {
                    app.Resources.MergedDictionaries.RemoveAt(i);
                }
            }

            // 仅在 Dark 时追加覆盖字典；Light 已在 App.xaml 中加载
            if (theme == AppTheme.Dark)
            {
                var overlay = new System.Windows.ResourceDictionary
                {
                    Source = new Uri(source, UriKind.Relative)
                };
                app.Resources.MergedDictionaries.Add(overlay);
            }
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