using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using EverydayToolkit.Core;

namespace EverydayToolkit.App;

/// <summary>Stores only the selected directory in the default profile; it never moves content.</summary>
public sealed class DataDirectoryPreferences
{
    private const string Marker = "EverydayToolkit-v1";
    private readonly SettingsStore defaultSettings;
    public string DefaultRoot { get; }

    public DataDirectoryPreferences(string defaultRoot)
    {
        DefaultRoot = DataDirectoryIdentity.Normalize(defaultRoot);
        defaultSettings = new SettingsStore(Path.Combine(DefaultRoot, "settings.json"));
    }

    public string Resolve(string? explicitDirectory)
    {
        var chosen = explicitDirectory ?? defaultSettings.Load().DataDirectory ?? DefaultRoot;
        return DataDirectoryIdentity.Normalize(chosen);
    }

    public string ValidateSelection(string requestedDirectory, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedDirectory) || !Path.IsPathFullyQualified(requestedDirectory)
            || requestedDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ContentValidationException("请选择本机上的完整数据目录路径。");
        var target = DataDirectoryIdentity.Normalize(requestedDirectory);
        var current = DataDirectoryIdentity.Normalize(currentDirectory);
        if (string.Equals(target, Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new ContentValidationException("不能把磁盘根目录用作工具箱数据目录。");
        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase)) return target;
        if (File.Exists(target)) throw new ContentValidationException("所选数据位置是文件，请选择目录。");
        if (!Directory.Exists(target)) return target;

        var marker = Path.Combine(target, "toolkit-data.marker");
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker).Trim() == Marker) return target;
            throw new ContentValidationException("所选目录的工具箱数据标识不匹配。");
        }
        // The default directory may contain the pointer settings alone while another profile is active.
        if (string.Equals(target, DefaultRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.GetFileSystemEntries(target).Length == 1
            && File.Exists(Path.Combine(target, "settings.json"))) return target;
        if (Directory.GetFileSystemEntries(target).Length != 0)
            throw new ContentValidationException("所选目录已有其他文件；请选择空目录或带有效工具箱标识的既有目录。");
        return target;
    }

    public void SaveSelection(string requestedDirectory)
    {
        Synchronize(() =>
        {
            var selected = ValidateSelection(requestedDirectory, Resolve(null));
            var settings = defaultSettings.Load();
            settings.DataDirectory = string.Equals(selected, DefaultRoot, StringComparison.OrdinalIgnoreCase) ? null : selected;
            defaultSettings.Save(settings);
            return true;
        });
    }
    public void SaveCurrentSettings(ToolSettings settings, string currentDirectory, SettingsStore currentSettingsStore)
    {
        Synchronize(() =>
        {
            var updated = JsonSerializer.Deserialize<ToolSettings>(JsonSerializer.Serialize(settings))!;
            if (string.Equals(DataDirectoryIdentity.Normalize(currentDirectory), DefaultRoot, StringComparison.OrdinalIgnoreCase))
                updated.DataDirectory = defaultSettings.Load().DataDirectory;
            currentSettingsStore.Save(updated);
            settings.DataDirectory = updated.DataDirectory;
            return true;
        });
    }
    internal T Synchronize<T>(Func<T> action)
    {
        using var preferenceMutex = new Mutex(false, "Local\\EverydayToolkit-Preferences-" + DataDirectoryIdentity.InstanceId(DefaultRoot));
        var acquired = false;
        try
        {
            try { acquired = preferenceMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new ContentValidationException("另一个工具箱正在保存设置，请稍后重试。");
            return action();
        }
        finally { if (acquired) preferenceMutex.ReleaseMutex(); }
    }
}
