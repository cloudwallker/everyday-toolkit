using System;
using System.Threading.Tasks;
using System.Windows;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;

namespace EverydayToolkit.App;

public partial class MainWindow
{
    private async void Settings_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var dialog = new Views.SettingsDialog(settings, dataRoot, allowLocationChange) { Owner = this };
        dialog.DeleteContents = async () =>
        {
            controller.SetRecording(false);
            UpdateRecordingLabel();
            await Task.Run(store.DeleteAllData);
            imageCache.Clear();
            await RefreshHistoryAsync();
            await RefreshSnippetsAsync();
            SetStatus("全部保存内容已删除，记录已暂停。");
        };
        if (dialog.ShowDialog() != true || dialog.UpdatedSettings is not { } draft) return;
        if (recordingUnavailable && draft.RecordingEnabled) throw new ContentValidationException(initializationWarning ?? "记录暂不可用，请重新启动后再试。");
        var changes = new SettingsChangeCoordinator(dataRoot, settingsStore, settings, directoryPreferences, allowLocationChange, new WindowSettingsSystem(this));
        var switching = controller.ApplySettings(draft, changes.Commit);
        UpdateRecordingLabel();
        if (switching)
        {
            ExitApplication();
            return;
        }
        await Task.Run(store.Prune);
        imageCache.Clear();
        await RefreshHistoryAsync();
        SetStatus("设置已保存；已按期限和配额清理未收藏历史。");
    });

    private sealed class WindowSettingsSystem(MainWindow window) : ISettingsSystemChanges
    {
        public bool RegisterHotkey(string gesture, out string error)
        {
            if (window.hotkeys is not null) return window.hotkeys.Register(gesture, out error);
            error = "";
            return true;
        }
        public void SetStartup(bool enabled, string? directory) => StartupRegistration.SetEnabled(enabled, directory);
        public Action CaptureStartupRestore() => StartupRegistration.CaptureRestoreAction();
    }
}
