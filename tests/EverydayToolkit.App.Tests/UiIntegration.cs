using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EverydayToolkit.App;
using EverydayToolkit.App.Views;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;

internal static class UiIntegration
{
    public static int Run()
    {
        var failures = 0;
        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = UiTestApplication.Create();
            Theme.Apply();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "toolkit-data.marker"), "EverydayToolkit-v1");
            var settings = new ToolSettings { FirstRunComplete = true, Hotkey = "Ctrl+Alt+Shift+F12" };
            var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            settingsStore.Save(settings);
            using var store = new ContentStore(Path.Combine(root, "content.db"), new DpapiProtector(), settings);
            var sourceText = "甲\n\n乙\n甲";
            var textId = store.Capture(new ClipboardContent(EntryKind.Text, sourceText)).EntryId!.Value;
            store.Capture(new ClipboardContent(EntryKind.Text, "项目文档 https://example.org\n下周整理版本反馈"));
            store.SaveSnippet("收到，我会尽快处理", "办公回复", "收到，谢谢。我会核对资料后回复你。");
            var codec = new ImageCodec();
            var pixels = new byte[600 * 380 * 4];
            for (var y = 0; y < 380; y++) for (var x = 0; x < 600; x++)
            {
                var offset = (y * 600 + x) * 4;
                pixels[offset] = (byte)(140 + x / 10); pixels[offset + 1] = (byte)(60 + y / 3); pixels[offset + 2] = 45; pixels[offset + 3] = (byte)(x < 70 ? 120 : 255);
            }
            var bitmap = BitmapSource.Create(600, 380, 96, 96, PixelFormats.Bgra32, null, pixels, 2400);
            var imageId = store.Capture(codec.Encode(bitmap)).EntryId!.Value;
            var window = new MainWindow(store, settingsStore, settings, root) { ShowActivated = false, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual };
            var priorClipboard = System.Windows.Clipboard.GetDataObject();
            try
            {
                window.Show();
                var history = (ListBox)window.FindName("HistoryList");
                Wait(() => history.Items.Count == 3);
                void Check(string name, Action action)
                {
                    var filter = Environment.GetEnvironmentVariable("TOOLKIT_UI_TEST_FILTER");
                    if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
                    try { action(); Console.WriteLine("PASS " + name); }
                    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
                }
                Check("UI_TextPreviewAndCleanupPreserveOriginal", () =>
                {
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Id == textId);
                    Equal(sourceText, ((TextBox)window.FindName("PreviewText")).Text);
                    Click(window, "HistoryCleanButton");
                    var original = (TextBox)window.FindName("OriginalText");
                    var result = (TextBox)window.FindName("ResultText");
                    Equal(sourceText, original.Text);
                    FindRule(window, "RemoveBlankLines").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    FindRule(window, "DeduplicateLines").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Equal("甲\n乙", result.Text);
                    Equal(sourceText, store.GetHistory().Single(row => row.Id == textId).Text);
                    Snapshot(window, "cleanup", 1);
                });
                Check("UI_HistorySearchFindsText", () =>
                {
                    ((TabControl)window.FindName("Tabs")).SelectedIndex = 0;
                    ((TextBox)window.FindName("HistorySearch")).Text = "项目文档";
                    Wait(() => history.Items.Count == 1);
                    Equal("项目文档 https://example.org\n下周整理版本反馈", ((HistoryRow)history.Items[0]).Entry.Text);
                    ((TextBox)window.FindName("HistorySearch")).Text = "";
                    Wait(() => history.Items.Count == 3);
                });
                Check("UI_ImagePreviewAndCopyRestorePixels", () =>
                {
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Id == imageId);
                    var preview = (Image)window.FindName("PreviewImage");
                    Wait(() => preview.Source is BitmapSource);
                    Equal(600, ((BitmapSource)preview.Source).PixelWidth);
                    Click(window, "HistoryCopyButton");
                    Wait(() => System.Windows.Clipboard.ContainsData("PNG"));
                    var content = new ClipboardService(codec).ReadCurrent(respectExclusions: false)!;
                    Equal(EntryKind.Image, content.Kind);
                    var restored = codec.Decode(content.Png!);
                    var firstPixel = new byte[4];
                    restored.CopyPixels(new Int32Rect(0, 0, 1, 1), firstPixel, 4, 0);
                    Equal("140,60,45,120", string.Join(",", firstPixel));
                    Snapshot(window, "clipboard", 1);
                    Snapshot(window, "clipboard-150", 1.5);
                    Snapshot(window, "clipboard-200", 2);
                    DarkTheme();
                    Snapshot(window, "clipboard-dark", 1);
                    Theme.Apply();
                });
                Check("UI_PausedClipboardDoesNotRecord", () =>
                {
                    var count = store.GetStatistics().HistoryCount;
                    System.Windows.Clipboard.SetText("暂停时的合成样例");
                    PumpFor(250);
                    Equal(count, store.GetStatistics().HistoryCount);
                });
                Check("UI_CopyActionsDoNotInterleaveWhileImageIsPreparing", () =>
                {
                    const string before = "准备复制时的合成内容";
                    System.Windows.Clipboard.SetText(before);
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Id == imageId);
                    Click(window, "HistoryCopyButton");
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Id == textId);
                    Click(window, "HistoryCopyButton");
                    try { Equal(before, System.Windows.Clipboard.GetText()); }
                    finally { Wait(() => System.Windows.Clipboard.ContainsData("PNG")); PumpFor(100); }
                });
                Check("UI_RecordingButtonStartsRealListenerAndPersists", () =>
                {
                    Click(window, "RecordingButton");
                    Equal(true, settingsStore.Load().RecordingEnabled);
                    System.Windows.Clipboard.SetText("UI监听合成样例");
                    Wait(() => store.GetHistory(new HistoryQuery("UI监听合成样例")).Count == 1);
                    Wait(() => history.Items.Count == 4);
                    var count = store.GetStatistics().HistoryCount;
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Text == "UI监听合成样例");
                    Click(window, "HistoryCopyButton");
                    PumpFor(250);
                    Equal(count, store.GetStatistics().HistoryCount);
                    Click(window, "RecordingButton");
                    Equal(false, settingsStore.Load().RecordingEnabled);
                });
                Check("UI_SettingsRecordingSwitchUpdatesLiveRecorderAndPanel", () =>
                {
                    void SaveRecordingChoice(bool enabled)
                    {
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                        Exception? dialogError = null;
                        timer.Tick += (_, _) =>
                        {
                            var dialog = Application.Current.Windows.OfType<SettingsDialog>().FirstOrDefault();
                            if (dialog is null) return;
                            timer.Stop();
                            try
                            {
                                var recording = Descendants(dialog).OfType<CheckBox>().Single(check => Equals(check.Content, "记录新复制的内容"));
                                recording.IsChecked = enabled;
                                Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception error) { dialogError = error; dialog.DialogResult = false; }
                        };
                        timer.Start();
                        try { Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        finally { timer.Stop(); }
                        if (dialogError is not null) throw dialogError;
                        Wait(() => settingsStore.Load().RecordingEnabled == enabled);
                    }

                    SaveRecordingChoice(true);
                    Equal(true, settings.RecordingEnabled);
                    if (!((Button)window.FindName("RecordingButton")).Content!.ToString()!.Contains("暂停记录")) throw new Exception("主面板没有显示正在记录");
                    var text = "设置中开启的合成记录";
                    System.Windows.Clipboard.SetText(text);
                    Wait(() => store.GetHistory(new HistoryQuery(text)).Count == 1);
                    SaveRecordingChoice(false);
                    Equal(false, settings.RecordingEnabled);
                    if (!((Button)window.FindName("RecordingButton")).Content!.ToString()!.Contains("开启记录")) throw new Exception("主面板没有显示已暂停");
                });
                Check("UI_SettingsAllowsSelectingAnotherDataDirectory", () =>
                {
                    var chosen = Path.Combine(root, "new-empty-profile");
                    var dialog = new SettingsDialog(settings, root) { ShowActivated = false, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual };
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    Exception? dialogError = null;
                    timer.Tick += (_, _) =>
                    {
                        if (!dialog.IsLoaded) return;
                        timer.Stop();
                        try
                        {
                            var directory = Descendants(dialog).OfType<TextBox>().Last();
                            if (directory.IsReadOnly) throw new Exception("数据位置仍为只读");
                            directory.Text = chosen;
                            Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception error) { dialogError = error; dialog.DialogResult = false; }
                    };
                    timer.Start();
                    try
                    {
                        Equal(true, dialog.ShowDialog());
                        if (dialogError is not null) throw dialogError;
                        Equal(chosen, dialog.UpdatedSettings?.DataDirectory);
                    }
                    finally { timer.Stop(); dialog.Close(); }
                });
                Check("UI_SnippetPageShowsPersistentTemplate", () =>
                {
                    ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
                    var list = (ListBox)window.FindName("SnippetList");
                    Wait(() => list.Items.Count == 1);
                    list.SelectedIndex = 0;
                    Equal("收到，谢谢。我会核对资料后回复你。", ((TextBox)window.FindName("SnippetPreview")).Text);
                    Snapshot(window, "snippets", 1);
                });
                Check("UI_EditedHistoryTemplateUsesFinalBodyAndKeepsHistory", () =>
                {
                    ((TabControl)window.FindName("Tabs")).SelectedIndex = 0;
                    history.SelectedItem = history.Items.Cast<HistoryRow>().Single(row => row.Entry.Id == textId);
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = Application.Current.Windows.OfType<SnippetDialog>().FirstOrDefault();
                        if (dialog is null) return;
                        timer.Stop();
                        var inputs = Descendants(dialog).OfType<TextBox>().ToList();
                        inputs[0].Text = "对话框编辑样例";
                        inputs[2].Text = "用户在保存前修改的正文";
                        Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                    timer.Start();
                    try
                    {
                        Click(window, "HistorySnippetButton");
                        Wait(() => store.GetSnippets("对话框编辑样例").Count == 1);
                        Equal("用户在保存前修改的正文", store.GetSnippets("对话框编辑样例").Single().Text);
                        Equal(sourceText, store.GetHistory().Single(row => row.Id == textId).Text);
                    }
                    finally { timer.Stop(); }
                });
                Check("UI_ClosingHidesAndCanReopen", () =>
                {
                    window.Close();
                    Equal(false, window.IsVisible);
                    window.Show();
                    Equal(true, window.IsVisible);
                });
                Check("UI_HotkeyConflictRemainsVisibleAfterLoading", () =>
                {
                    using var blocker = new HotkeyManager(window);
                    Equal(true, blocker.Register("Ctrl+Alt+Shift+F11", out _));
                    var conflictSettings = new ToolSettings { FirstRunComplete = true, Hotkey = "Ctrl+Alt+Shift+F11" };
                    var conflictWindow = new MainWindow(store, settingsStore, conflictSettings, root) { ShowActivated = false, Left = -19000, Top = -19000, WindowStartupLocation = WindowStartupLocation.Manual };
                    try
                    {
                        conflictWindow.Show();
                        Wait(() => ((ListBox)conflictWindow.FindName("HistoryList")).Items.Count > 0);
                        PumpFor(150);
                        if (!((TextBlock)conflictWindow.FindName("StatusText")).Text.Contains("快捷键")) throw new Exception("快捷键冲突提示被加载完成状态覆盖");
                    }
                    finally { conflictWindow.ReleaseResources(); conflictWindow.Close(); }
                });
                Check("UI_SettingsDirectorySwitchCommitsAndReleasesResourcesBeforeExit", () =>
                {
                    var chosen = Path.Combine(root, "new-empty-profile");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    Exception? dialogError = null;
                    timer.Tick += (_, _) =>
                    {
                        var dialog = Application.Current.Windows.OfType<SettingsDialog>().FirstOrDefault();
                        if (dialog is null) return;
                        timer.Stop();
                        try
                        {
                            Descendants(dialog).OfType<TextBox>().Last().Text = chosen;
                            Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception error) { dialogError = error; dialog.DialogResult = false; }
                    };
                    timer.Start();
                    try { Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    finally { timer.Stop(); }
                    if (dialogError is not null) throw dialogError;
                    Equal(chosen, settingsStore.Load().DataDirectory);
                    Equal(false, Directory.Exists(chosen));
                    if (typeof(MainWindow).GetField("resourcesReleased", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window) is not true)
                        throw new Exception("directory switch did not immediately release clipboard listener and hotkey resources");
                    Equal(true, File.Exists(Path.Combine(root, "content.db")));
                });
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL UI_Setup: " + error.GetType().Name); }
            finally
            {
                window.ReleaseResources();
                window.Close();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try { if (priorClipboard is not null) System.Windows.Clipboard.SetDataObject(priorClipboard, true); else System.Windows.Clipboard.Clear(); break; }
                    catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(60); if (attempt == 3) { failures++; Console.WriteLine("FAIL UI_ClipboardRestore: clipboard is busy"); } }
                }
                app.Shutdown();
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        Console.WriteLine($"UI integration tests: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button FindRule(DependencyObject parent, string tag)
    {
        if (parent is Button { Tag: string value } button && value == tag) return button;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) { try { return FindRule(child, tag); } catch (InvalidOperationException) { } }
        throw new InvalidOperationException("rule button not found");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI behavior did not complete"); PumpFor(25); }
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Snapshot(Window window, string name, double scale)
    {
        window.UpdateLayout();
        PumpFor(50);
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        image.Render(surface);
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "screenshots");
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static void DarkTheme()
    {
        // Test-only palette; never modify the user's Windows theme setting.
        var colors = new Dictionary<string, string>
        {
            ["WindowBrush"] = "#171C26", ["SurfaceBrush"] = "#232B39", ["BorderBrush"] = "#394559",
            ["TextBrush"] = "#EEF2F9", ["MutedBrush"] = "#ADB9CB", ["AccentBrush"] = "#6D8CFA", ["AccentSoftBrush"] = "#304160"
        };
        foreach (var item in colors) Application.Current.Resources[item.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Value));
    }
}
