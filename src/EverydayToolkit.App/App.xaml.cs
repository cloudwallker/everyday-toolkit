using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using Microsoft.Win32;

namespace EverydayToolkit.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? activateSignal;
    private ContentStore? contentStore;
    private volatile bool stopping;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Theme.Apply();
            var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverydayToolkit");
            var overrideIndex = Array.IndexOf(e.Args, "--data-dir");
            if (overrideIndex >= 0 && overrideIndex + 1 >= e.Args.Length) throw new ContentValidationException("请为数据目录提供完整路径。");
            var preferences = new DataDirectoryPreferences(defaultRoot);
            var dataRoot = preferences.Resolve(overrideIndex >= 0 ? e.Args[overrideIndex + 1] : null);
            var identity = DataDirectoryIdentity.InstanceId(dataRoot);
            instanceMutex = new Mutex(true, "Local\\EverydayToolkit-" + identity, out var isFirst);
            if (!isFirst)
            {
                if (EventWaitHandle.TryOpenExisting("Local\\EverydayToolkit-Activate-" + identity, out var existing)) { using (existing) existing.Set(); }
                Shutdown();
                return;
            }
            Directory.CreateDirectory(dataRoot);
            var markerPath = Path.Combine(dataRoot, "toolkit-data.marker");
            if (File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() != "EverydayToolkit-v1") throw new ContentValidationException("数据目录标识不匹配，请选择其他目录。");
            if (!File.Exists(markerPath) && File.Exists(Path.Combine(dataRoot, "content.db"))) throw new ContentValidationException("该目录已有未标识的数据库。请备份并选择其他数据目录。");
            File.WriteAllText(markerPath, "EverydayToolkit-v1", new UTF8Encoding(false));
            var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
            var settings = settingsStore.Load();
            // Login startup is a user-wide OS preference, independent of the chosen content profile.
            settings.StartWithWindows = StartupRegistration.IsEnabled();
            contentStore = new ContentStore(Path.Combine(dataRoot, "content.db"), new DpapiProtector(), settings);
            var window = new MainWindow(contentStore, settingsStore, settings, dataRoot, e.Args.Contains("--tray"), preferences, overrideIndex < 0);
            MainWindow = window;
            activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EverydayToolkit-Activate-" + identity);
            _ = Task.Run(() =>
            {
                while (!stopping)
                {
                    if (!activateSignal.WaitOne(500)) continue;
                    if (!stopping) Dispatcher.BeginInvoke(() => window.ShowPane());
                }
            });
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("无法打开本地数据或初始化工具箱。原数据已保留。请检查目录权限、Windows 用户及数据库备份，再重新启动。", "日用工具箱", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(Theme.Apply);
    protected override void OnExit(ExitEventArgs e)
    {
        stopping = true;
        SystemEvents.UserPreferenceChanged -= ThemeChanged;
        (MainWindow as MainWindow)?.ReleaseResources();
        contentStore?.Dispose();
        activateSignal?.Set();
        // 等待线程只使用 AutoResetEvent；进程退出时操作系统释放句柄，避免销毁仍在等待的事件。
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
