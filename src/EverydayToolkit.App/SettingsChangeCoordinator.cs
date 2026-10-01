using System;
using System.IO;
using System.Text.Json;
using EverydayToolkit.Core;

namespace EverydayToolkit.App;

public interface ISettingsSystemChanges
{
    bool RegisterHotkey(string hotkey, out string error);
    void SetStartup(bool enabled, string? directory);
    Action CaptureStartupRestore();
}

public sealed class SettingsChangeCoordinator
{
    private readonly string currentDirectory;
    private readonly SettingsStore currentSettingsStore;
    private readonly ToolSettings currentSettings;
    private readonly DataDirectoryPreferences preferences;
    private readonly bool allowLocationChange;
    private readonly ISettingsSystemChanges system;

    public SettingsChangeCoordinator(string currentDirectory, SettingsStore currentSettingsStore, ToolSettings currentSettings, DataDirectoryPreferences preferences, bool allowLocationChange, ISettingsSystemChanges system)
    {
        this.currentDirectory = DataDirectoryIdentity.Normalize(currentDirectory);
        this.currentSettingsStore = currentSettingsStore;
        this.currentSettings = currentSettings;
        this.preferences = preferences;
        this.allowLocationChange = allowLocationChange;
        this.system = system;
    }

    /// <summary>Commits settings and the default directory pointer; destination content/settings are never copied.</summary>
    public bool Commit(ToolSettings draft) => preferences.Synchronize(() => CommitLocked(draft));
    private bool CommitLocked(ToolSettings draft)
    {
        var selected = draft.DataDirectory is null ? currentDirectory : preferences.ValidateSelection(draft.DataDirectory, currentDirectory);
        var switching = !string.Equals(selected, currentDirectory, StringComparison.OrdinalIgnoreCase);
        if (switching && !allowLocationChange) throw new ContentValidationException("本次数据目录由启动参数指定，请退出并修改启动命令。");
        if (switching) ProbeWritableDirectory(selected);
        var sameDefaultFile = string.Equals(currentDirectory, preferences.DefaultRoot, StringComparison.OrdinalIgnoreCase);
        var updated = JsonSerializer.Deserialize<ToolSettings>(JsonSerializer.Serialize(draft))!;
        updated.FirstRunComplete = true;
        // The default file owns the pointer. A custom profile does not redirect future default startups.
        updated.DataDirectory = sameDefaultFile && !switching ? currentSettingsStore.Load().DataDirectory : sameDefaultFile
            ? (string.Equals(selected, preferences.DefaultRoot, StringComparison.OrdinalIgnoreCase) ? null : selected)
            : currentSettings.DataDirectory;
        var currentSnapshot = new SettingsSnapshot(Path.Combine(currentDirectory, "settings.json"));
        var pointerSnapshot = switching && !sameDefaultFile ? new SettingsSnapshot(Path.Combine(preferences.DefaultRoot, "settings.json")) : null;
        var oldHotkey = currentSettings.Hotkey;
        var startupChanged = updated.StartWithWindows != currentSettings.StartWithWindows || (switching && updated.StartWithWindows);
        var hotkeyChanged = false;
        var startupAttempted = false;
        Action? restoreStartup = null;
        var currentSaved = false;
        var pointerSaved = false;
        try
        {
            if (!system.RegisterHotkey(updated.Hotkey, out var error)) throw new ContentValidationException("快捷键未生效：" + error);
            hotkeyChanged = true;
            if (startupChanged)
            {
                restoreStartup = system.CaptureStartupRestore();
                startupAttempted = true;
                system.SetStartup(updated.StartWithWindows, allowLocationChange ? null : selected);
            }
            currentSettingsStore.Save(updated);
            currentSaved = true;
            if (pointerSnapshot is not null)
            {
                preferences.SaveSelection(selected);
                pointerSaved = true;
            }
        }
        catch
        {
            var rollbackFailed = false;
            if (pointerSaved) try { pointerSnapshot!.Restore(); } catch { rollbackFailed = true; }
            if (currentSaved) try { currentSnapshot.Restore(); } catch { rollbackFailed = true; }
            if (startupAttempted) try { restoreStartup!(); } catch { rollbackFailed = true; }
            if (hotkeyChanged && !system.RegisterHotkey(oldHotkey, out _)) rollbackFailed = true;
            if (rollbackFailed) throw new ContentValidationException("设置未能保存，部分设置无法恢复。原内容未搬迁；请检查目录权限与登录启动设置后重试。");
            throw;
        }
        draft.DataDirectory = updated.DataDirectory;
        draft.FirstRunComplete = true;
        return switching;
    }

    private static void ProbeWritableDirectory(string selected)
    {
        var created = !Directory.Exists(selected);
        var probe = Path.Combine(selected, ".toolkit-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(selected);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Flush(true);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (created && Directory.Exists(selected) && Directory.GetFileSystemEntries(selected).Length == 0) Directory.Delete(selected);
        }
    }

    private sealed class SettingsSnapshot
    {
        private readonly string path;
        private readonly byte[]? contents;
        public SettingsSnapshot(string path) { this.path = path; contents = File.Exists(path) ? File.ReadAllBytes(path) : null; }
        public void Restore()
        {
            if (contents is null) { if (File.Exists(path)) File.Delete(path); return; }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(contents); stream.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
