using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

public sealed class EnterpriseSyncVisualCaptureTests
{
    [Fact]
    public void CaptureEnterpriseSyncSettingsWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("QPH_M3_UI_CAPTURE");
        if (string.IsNullOrWhiteSpace(output)) return;
        WpfTestApplicationHost.Invoke(_ =>
        {
            var accounts = new FakeAccounts();
            var view = new SettingsView(new FakeCommandService(), accounts, new FakeProvider());
            view.ViewModel.EnterpriseSync!.LoadAsync().GetAwaiter().GetResult();
            var host = new Grid { Width = 1100, Height = 760, Background = Brushes.White };
            host.Children.Add(view);
            var window = new Window { Content = host, Width = 1100, Height = 760, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var navigation = (ListBox)view.FindName("SettingsNavigation"); navigation.SelectedIndex = 4; window.UpdateLayout();
            var visibleControls = Descendants<Control>(view).Where(control => control.IsVisible && control.Focusable).ToArray();
            Assert.Contains(visibleControls, control => control is TextBox && System.Windows.Automation.AutomationProperties.GetName(control) == "Hub 地址");
            Assert.Contains(visibleControls, control => control is PasswordBox);
            Assert.Contains(visibleControls, control => control is Button button && Equals(button.Content, "连接 / 重新认证"));
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); using var stream = File.Create(output); encoder.Save(stream); window.Close(); host.Children.Clear();
        });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject { for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T value)yield return value;foreach(var nested in Descendants<T>(child))yield return nested;} }
    private sealed class FakeProvider:ISyncProvider { public string ProviderId=>"fake";public SyncProviderCapabilities Capabilities{get;}=new(true);public Task<SyncResult> SynchronizeAsync(SyncRequest request,CancellationToken cancellationToken=default){var now=DateTimeOffset.UtcNow;return Task.FromResult(new SyncResult(SyncStatus.Succeeded,0,null,"企业话术已同步。",false,null,now,now));} }
    private sealed class FakeAccounts:ISyncAccountService { public Task<SyncAccountState> GetStateAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new SyncAccountState(true,new Uri("http://192.168.1.20:5105"),"alice","客服员工","device",SyncStatus.Succeeded,4,DateTimeOffset.UtcNow,"企业话术已同步。",null));public Task<SyncResult> ConnectAsync(HubConnectionRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();public Task DisconnectAsync(bool retainEnterpriseCache=true,CancellationToken cancellationToken=default)=>Task.CompletedTask;public Task ClearEnterpriseCacheAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask; }
}
