using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using Forms = System.Windows.Forms;

namespace EverydayToolkit.App;

public partial class MainWindow : Window
{
    private readonly ContentStore store;
    private readonly SettingsStore settingsStore;
    private readonly ToolSettings settings;
    private readonly ToolkitController controller;
    private readonly string dataRoot;
    private readonly bool startInTray;
    private readonly DataDirectoryPreferences directoryPreferences;
    private readonly bool allowLocationChange;
    private readonly ImageCache imageCache = new();
    private readonly ClipboardService clipboard = new(new ImageCodec());
    private readonly PasteService paste = new();
    private readonly TextWorkspace textWorkspace = new();
    private readonly ObservableCollection<HistoryRow> historyRows = new();
    private readonly ObservableCollection<SnippetEntry> snippets = new();
    private ClipboardMonitor? monitor;
    private HotkeyManager? hotkeys;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? trayRecording;
    private bool ready;
    private bool exiting;
    private bool resourcesReleased;
    private int historyRevision;
    private int snippetRevision;
    private string? initializationWarning;
    private bool recordingUnavailable;
    private bool clipboardOperationBusy;
    private bool retentionRefreshBusy;
    private DispatcherTimer? retentionTimer;

    public MainWindow(ContentStore store, SettingsStore settingsStore, ToolSettings settings, string dataRoot, bool startInTray = false, DataDirectoryPreferences? directoryPreferences = null, bool allowLocationChange = true)
    {
        this.store = store;
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.dataRoot = dataRoot;
        this.startInTray = startInTray;
        this.directoryPreferences = directoryPreferences ?? new DataDirectoryPreferences(dataRoot);
        this.allowLocationChange = allowLocationChange;
        controller = new ToolkitController(store, settingsStore, settings, draft => this.directoryPreferences.SaveCurrentSettings(draft, dataRoot, settingsStore));
        paste.CaptureTarget();
        InitializeComponent();
        SizeChanged += (_, _) => UpdateCompactLayout();
        UpdateCompactLayout();
        HistoryList.ItemsSource = historyRows;
        SnippetList.ItemsSource = snippets;
        UpdateRecordingLabel();
        UpdateHistoryActions(null);
    }

    private void UpdateCompactLayout()
    {
        var compact = Width < 920;
        foreach (var pair in new[] { (HistoryLayout, HistoryPreviewPanel), (SnippetLayout, SnippetPreviewPanel), (CleanupLayout, CleanupResultPanel) })
        {
            var (layout, preview) = pair;
            layout.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : layout == CleanupLayout ? 1 : 2, GridUnitType.Star);
            layout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 18);
            layout.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(layout == CleanupLayout ? 1 : 3, GridUnitType.Star);
            layout.RowDefinitions[0].Height = new GridLength(compact ? 2 : 1, GridUnitType.Star);
            layout.RowDefinitions[1].Height = compact ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(preview, compact ? 0 : 2);
            Grid.SetRow(preview, compact ? 1 : 0);
            preview.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        ready = true;
        try
        {
            if (!settings.FirstRunComplete)
            {
                var answer = MessageBox.Show(this, "工具箱可以保存你之后复制的文字和静态图片，方便搜索、收藏和再次使用。内容仅在本机加密保存，可随时暂停、排除应用或删除。\n\n现在开启历史记录吗？", "开始使用日用工具箱", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
                controller.SetRecording(answer == MessageBoxResult.Yes);
            }
            hotkeys = new HotkeyManager(this);
            hotkeys.Invoked += () => ShowPane();
            if (!hotkeys.Register(settings.Hotkey, out var error)) { initializationWarning = "快捷键冲突：" + error + "；仍可通过托盘打开。"; SetStatus(initializationWarning); }
            monitor = new ClipboardMonitor(this, clipboard)
            {
                IsRecording = () => settings.RecordingEnabled,
                ExcludedApps = () => settings.ExcludedApps
            };
            monitor.Captured += OnCaptured;
            monitor.Status += OnMonitorStatus;
            CreateTray();
            await Task.Run(store.Prune);
            await RefreshHistoryAsync();
            await RefreshSnippetsAsync();
            UpdateRecordingLabel();
            SetStatus(initializationWarning ?? (settings.RecordingEnabled ? $"正在记录新复制的内容 · {settings.Hotkey} 打开面板" : "记录已暂停 · 已保存的内容仍可使用"));
            retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            retentionTimer.Tick += RetentionTimer_Tick;
            retentionTimer.Start();
            if (startInTray) Hide();
        }
        catch (Exception error)
        {
            settings.RecordingEnabled = false;
            recordingUnavailable = true;
            UpdateRecordingLabel();
            SetStatus("加载失败，原数据已保留：" + UserError(error));
        }
    }

    private async void OnCaptured(ClipboardContent content)
    {
        try
        {
            var result = await controller.RecordAsync(content);
            await Dispatcher.InvokeAsync(async () =>
            {
                if (exiting) return;
                SetStatus(result.Message);
                await RefreshHistoryAsync();
            }).Task.Unwrap();
        }
        catch (Exception error) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(() => SetStatus("记录失败，原数据已保留：" + UserError(error))); }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开剪贴板", null, (_, _) => Dispatcher.Invoke(() => ShowPane(0)));
        menu.Items.Add("常用语", null, (_, _) => Dispatcher.Invoke(() => ShowPane(1)));
        menu.Items.Add("文本清洗", null, (_, _) => Dispatcher.Invoke(() => ShowPane(2)));
        trayRecording = new Forms.ToolStripMenuItem();
        trayRecording.Click += (_, _) => Dispatcher.Invoke(ToggleRecording);
        menu.Items.Add(trayRecording);
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { ShowPane(); Settings_Click(this, new RoutedEventArgs()); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "日用工具箱", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowPane());
        UpdateRecordingLabel();
    }

    public void ShowPane(int tab = 0)
    {
        paste.CaptureTarget();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Tabs.SelectedIndex = tab;
        if (tab == 0) HistorySearch.Focus();
        if (ready) _ = GuardAsync(RefreshHistoryAsync);
    }

    private async void RetentionTimer_Tick(object? sender, EventArgs e)
    {
        if (exiting || retentionRefreshBusy) return;
        retentionRefreshBusy = true;
        try { await GuardAsync(RefreshHistoryAsync); }
        finally { retentionRefreshBusy = false; }
    }

    private bool TryBeginClipboardOperation()
    {
        if (clipboardOperationBusy) { SetStatus("正在复制或粘贴，请等待当前操作完成。"); return false; }
        clipboardOperationBusy = true;
        return true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        if (tray is null) { exiting = true; Application.Current.Shutdown(); return; }
        e.Cancel = true;
        Hide();
    }
    private void ExitApplication() { exiting = true; ReleaseResources(); Application.Current.Shutdown(); }
    public void ReleaseResources()
    {
        if (resourcesReleased) return;
        exiting = true;
        resourcesReleased = true;
        retentionTimer?.Stop();
        if (retentionTimer is not null) retentionTimer.Tick -= RetentionTimer_Tick;
        monitor?.Dispose();
        hotkeys?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        imageCache.Clear();
        PreviewImage.Source = null;
        foreach (var row in historyRows) row.Thumbnail = null;
    }
    private void Recording_Click(object sender, RoutedEventArgs e) => ToggleRecording();
    private void ToggleRecording()
    {
        if (recordingUnavailable && !settings.RecordingEnabled) { SetStatus(initializationWarning ?? "记录暂不可用，请检查数据和系统权限后重新启动。"); return; }
        try { controller.SetRecording(!settings.RecordingEnabled); UpdateRecordingLabel(); SetStatus(settings.RecordingEnabled ? "已开启记录，只保存之后的新复制事件。" : "已暂停记录，保存的内容仍可使用。"); }
        catch (Exception error) { SetStatus("无法保存记录设置：" + UserError(error)); }
    }
    private void OnMonitorStatus(string category)
    {
        if (exiting) return;
        var message = category switch
        {
            "ClipboardBusy" => "剪贴板被其他应用占用，当前内容未能保存。",
            "ClipboardContentLimitOrInvalidImage" => "图片超过限制或无法读取，已跳过。",
            "ClipboardReadFailed" => "无法读取当前剪贴板内容，已跳过。",
            "ClipboardListenerRegistrationFailed" => "剪贴板监听连接失败，记录已暂停；请重新启动后再试。",
            _ => "当前剪贴板内容无法处理，已跳过。"
        };
        if (category == "ClipboardListenerRegistrationFailed")
        {
            recordingUnavailable = true;
            initializationWarning = message;
            try { controller.SetRecording(false); } catch (Exception) { settings.RecordingEnabled = false; }
            UpdateRecordingLabel();
        }
        SetStatus(message);
    }
    private void UpdateRecordingLabel()
    {
        RecordingButton.Content = settings.RecordingEnabled ? "●  暂停记录" : "○  开启记录";
        if (trayRecording is not null) trayRecording.Text = settings.RecordingEnabled ? "暂停记录" : "开启记录";
    }
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || e.Source != Tabs) return;
        if (Tabs.SelectedIndex == 0) await GuardAsync(RefreshHistoryAsync);
        if (Tabs.SelectedIndex == 1) await GuardAsync(RefreshSnippetsAsync);
    }
    private void SetStatus(string message) => StatusText.Text = message;
    private static string UserError(Exception error) => error is ContentValidationException ? error.Message : error is System.IO.IOException ? "文件无法读取或写入，请检查位置和权限。" : "操作无法完成，请检查本地数据和 Windows 权限后重试。";
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { SetStatus(UserError(error)); }
    }
}
