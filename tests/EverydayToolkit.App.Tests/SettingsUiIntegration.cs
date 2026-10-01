using System;
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

internal static class SettingsUiIntegration
{
    public static int Run()
    {
        var failures = 0;
        var worker = new Thread(() =>
        {
            var app = UiTestApplication.Create();
            Theme.Apply();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "settings-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            void Check(string name, Action action)
            {
                try { action(); Console.WriteLine("PASS " + name); }
                catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
            }
            Check("SettingsUi_LoadsRealApplicationResources", () =>
            {
                if (app.Resources["PrimaryButton"] is not Style) throw new Exception("PrimaryButton missing from App.xaml resources");
            });
            Check("SettingsUi_RecordingAndLocationControlsAreEditable", () =>
            {
                var current = Path.Combine(root, "current");
                var dialog = new SettingsDialog(new ToolSettings(), current) { ShowActivated = false, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual };
                try
                {
                    var fields = (StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
                    var recording = fields.Children.OfType<CheckBox>().Single(item => Equals(item.Content, "记录新复制的内容"));
                    if (recording.IsChecked != false) throw new Exception("recording default must be paused");
                    var location = fields.Children.OfType<TextBox>().Last();
                    if (location.IsReadOnly) throw new Exception("data location is read only");
                }
                finally { dialog.Close(); }
            });
            Check("SettingsUi_SaveProducesRecordingAndLocationDraftWithoutChangingOriginal", () =>
            {
                var settings = new ToolSettings { HistoryDays = 7 };
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Data");
                var chosen = @"D:\ToolkitDemo\NextProfile";
                ShowAndAct(dialog, () =>
                {
                    Fields(dialog).Children.OfType<CheckBox>().Single(item => Equals(item.Content, "记录新复制的内容")).IsChecked = true;
                    Fields(dialog).Children.OfType<TextBox>().Last().Text = chosen;
                    Save(dialog);
                });
                if (dialog.UpdatedSettings is not { RecordingEnabled: true, FirstRunComplete: true } updated || updated.DataDirectory != chosen)
                    throw new Exception("setting controls did not produce the expected persisted draft");
                if (settings.RecordingEnabled || settings.FirstRunComplete || settings.DataDirectory is not null) throw new Exception("dialog modified original settings before commit");
                if (updated.HistoryDays != 7) throw new Exception("unmodified quota changed");
            });
            Check("SettingsUi_ExplicitDirectoryCannotBeOverriddenBySettings", () =>
            {
                var settings = new ToolSettings { DataDirectory = @"D:\ToolkitDemo\DefaultSelected" };
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Explicit", allowLocationChange: false);
                ShowAndAct(dialog, () =>
                {
                    var fields = Fields(dialog);
                    if (!fields.Children.OfType<TextBox>().Last().IsReadOnly) throw new Exception("explicit location must be read only");
                    if (fields.Children.OfType<Button>().Any(item => Equals(item.Content, "选择数据目录"))) throw new Exception("explicit mode exposes a directory picker");
                    fields.Children.OfType<TextBox>().Last().Text = @"D:\ToolkitDemo\AttemptedOverride";
                    Save(dialog);
                });
                if (dialog.UpdatedSettings?.DataDirectory != @"D:\ToolkitDemo\Explicit") throw new Exception("readonly dialog must submit the current explicit location as intent");
            });
            Check("SettingsUi_CancelKeepsOriginalSettingsAndNoDraft", () =>
            {
                var settings = new ToolSettings();
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Data");
                ShowAndAct(dialog, () =>
                {
                    Fields(dialog).Children.OfType<CheckBox>().Single(item => Equals(item.Content, "记录新复制的内容")).IsChecked = true;
                    dialog.DialogResult = false;
                });
                if (settings.RecordingEnabled || dialog.UpdatedSettings is not null) throw new Exception("cancel changed settings");
            });
            Check("SettingsUi_ExplicitDefaultDialogCommitKeepsOtherDefaultPointer", () =>
            {
                var defaultRoot = Path.Combine(root, "explicit-default");
                var pointerTarget = Path.Combine(root, "other-selected");
                Directory.CreateDirectory(defaultRoot);
                var settingsStore = new SettingsStore(Path.Combine(defaultRoot, "settings.json"));
                settingsStore.Save(new ToolSettings { DataDirectory = pointerTarget });
                var original = settingsStore.Load();
                var dialog = CreateDialog(original, defaultRoot, allowLocationChange: false);
                ShowAndAct(dialog, () =>
                {
                    Fields(dialog).Children.OfType<CheckBox>().Single(item => Equals(item.Content, "记录新复制的内容")).IsChecked = true;
                    Save(dialog);
                });
                var preferences = new DataDirectoryPreferences(defaultRoot);
                var changes = new SettingsChangeCoordinator(defaultRoot, settingsStore, original, preferences, false, new InactiveSystem());
                if (changes.Commit(dialog.UpdatedSettings!) || preferences.Resolve(null) != pointerTarget || !settingsStore.Load().RecordingEnabled)
                    throw new Exception("explicit default-profile settings lost the existing default pointer");
            });
            Check("SettingsUi_SyntheticWindowFitsAndExportsPreview", () =>
            {
                var dialog = CreateDialog(new ToolSettings(), @"D:\ToolkitDemo\Data");
                ShowAndAct(dialog, () =>
                {
                    dialog.Measure(new Size(dialog.Width, dialog.Height)); dialog.Arrange(new Rect(0, 0, dialog.Width, dialog.Height)); dialog.UpdateLayout();
                    var layout = (DockPanel)dialog.Content;
                    var actions = layout.Children.OfType<StackPanel>().Single();
                    var save = actions.Children.OfType<Button>().Single(item => Equals(item.Content, "保存设置"));
                    var location = save.TransformToAncestor(dialog).TransformBounds(new Rect(save.RenderSize));
                    if (location.Width <= 0 || location.Height <= 0 || location.Bottom > dialog.ActualHeight || location.Right > dialog.ActualWidth) throw new Exception("save action is clipped");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui");
                    Directory.CreateDirectory(directory);
                    using var output = File.Create(Path.Combine(directory, "settings.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
                    var scroll = layout.Children.OfType<ScrollViewer>().Single();
                    scroll.ScrollToEnd(); dialog.UpdateLayout();
                    var lower = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    lower.Render(dialog);
                    using var lowerOutput = File.Create(Path.Combine(directory, "settings-location.png"));
                    var lowerEncoder = new PngBitmapEncoder(); lowerEncoder.Frames.Add(BitmapFrame.Create(lower)); lowerEncoder.Save(lowerOutput);
                    dialog.DialogResult = false;
                });
            });
            Check("SettingsUi_DeleteThenSaveDoesNotResumeRecording", () =>
            {
                var settings = new ToolSettings { RecordingEnabled = true };
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Data", messages: new SyntheticMessages());
                dialog.DeleteContents = () => { settings.RecordingEnabled = false; return Task.CompletedTask; };
                ShowAndAct(dialog, () => { Delete(dialog); Save(dialog); });
                if (dialog.UpdatedSettings?.RecordingEnabled != false) throw new Exception("saving after deletion resumed recording");
            });
            Check("SettingsUi_DeletionInProgressPreventsSavingStaleRecordingChoice", () =>
            {
                var settings = new ToolSettings { RecordingEnabled = true };
                var messages = new SyntheticMessages();
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Data", messages: messages);
                var completion = new TaskCompletionSource();
                dialog.DeleteContents = () => { settings.RecordingEnabled = false; return completion.Task; };
                try
                {
                    ShowAndAct(dialog, () =>
                    {
                        Delete(dialog);
                        if (SaveButton(dialog).IsEnabled) throw new Exception("save remains enabled during deletion");
                        Save(dialog);
                        if (dialog.UpdatedSettings is not null) throw new Exception("pending deletion committed stale settings");
                        completion.SetResult();
                        PumpUntil(() => messages.ResultShown);
                        if (!SaveButton(dialog).IsEnabled) throw new Exception("save did not recover after deletion");
                        Save(dialog);
                    });
                }
                finally { completion.TrySetResult(); }
                if (dialog.UpdatedSettings?.RecordingEnabled != false) throw new Exception("completed deletion resumed recording");
            });
            Check("SettingsUi_FailedDeletionKeepsActualPausedRecordingChoice", () =>
            {
                var settings = new ToolSettings { RecordingEnabled = true };
                var messages = new SyntheticMessages();
                var dialog = CreateDialog(settings, @"D:\ToolkitDemo\Data", messages: messages);
                dialog.DeleteContents = () => { settings.RecordingEnabled = false; return Task.FromException(new IOException("synthetic deletion blocked")); };
                ShowAndAct(dialog, () => { Delete(dialog); Save(dialog); });
                if (!messages.ResultShown || messages.Completed || dialog.UpdatedSettings?.RecordingEnabled != false) throw new Exception("failed deletion allowed stale recording authorization");
            });
            app.Shutdown();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        Console.WriteLine($"Settings UI integration tests: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
    private sealed class InactiveSystem : ISettingsSystemChanges
    {
        public bool RegisterHotkey(string hotkey, out string error) { error = ""; return true; }
        public void SetStartup(bool enabled, string? directory) => throw new Exception("unexpected real startup change in settings UI fixture");
        public Action CaptureStartupRestore() => throw new Exception("unexpected real startup snapshot in settings UI fixture");
    }

    private sealed class SyntheticMessages : ISettingsDialogMessages
    {
        public bool ResultShown { get; private set; }
        public bool Completed { get; private set; }
        public bool ConfirmDeletion(Window owner) => true;
        public void ShowDeletionResult(Window owner, bool completed) { ResultShown = true; Completed = completed; }
    }
    private static SettingsDialog CreateDialog(ToolSettings settings, string root, bool allowLocationChange = true, ISettingsDialogMessages? messages = null) => new(settings, root, allowLocationChange, messages)
    { ShowActivated = false, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual };
    private static StackPanel Fields(SettingsDialog dialog) => (StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
    private static Button SaveButton(SettingsDialog dialog) => ((DockPanel)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(item => Equals(item.Content, "保存设置"));
    private static void Save(SettingsDialog dialog) => SaveButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Delete(SettingsDialog dialog) => Fields(dialog).Children.OfType<Button>().Single(item => Equals(item.Content, "删除全部已保存内容")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("deletion UI completion timed out");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    private static void ShowAndAct(SettingsDialog dialog, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        Exception? actionError = null;
        timer.Tick += (_, _) =>
        {
            if (!dialog.IsLoaded) return;
            timer.Stop();
            try { action(); }
            catch (Exception error) { actionError = error; dialog.DialogResult = false; }
        };
        timer.Start();
        try { dialog.ShowDialog(); if (actionError is not null) throw actionError; }
        finally { timer.Stop(); dialog.Close(); }
    }
}
