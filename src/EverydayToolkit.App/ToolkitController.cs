using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using EverydayToolkit.Core;

namespace EverydayToolkit.App;

public sealed class ToolkitController
{
    private readonly ContentStore store;
    private readonly SettingsStore settingsStore;
    private readonly Action<ToolSettings> saveSettings;
    private readonly object recordingGate = new();
    public ToolSettings Settings { get; }
    public ToolkitController(ContentStore store, SettingsStore settingsStore, ToolSettings settings, Action<ToolSettings>? saveSettings = null)
    {
        this.store = store;
        this.settingsStore = settingsStore;
        this.saveSettings = saveSettings ?? settingsStore.Save;
        Settings = settings;
    }

    public void SetRecording(bool enabled)
    {
        lock (recordingGate)
        {
            var previous = Settings.RecordingEnabled;
            var previousFirstRun = Settings.FirstRunComplete;
            Settings.RecordingEnabled = enabled;
            Settings.FirstRunComplete = true;
            try { saveSettings(Settings); }
            catch { Settings.RecordingEnabled = previous; Settings.FirstRunComplete = previousFirstRun; throw; }
        }
    }
    public bool ApplySettings(ToolSettings draft, Func<ToolSettings, bool> commit)
    {
        lock (recordingGate)
        {
            var updated = JsonSerializer.Deserialize<ToolSettings>(JsonSerializer.Serialize(draft))!;
            updated.FirstRunComplete = true;
            var switching = commit(updated);
            Settings.RecordingEnabled = updated.RecordingEnabled;
            Settings.FirstRunComplete = updated.FirstRunComplete;
            Settings.StartWithWindows = updated.StartWithWindows;
            Settings.Hotkey = updated.Hotkey;
            Settings.HistoryMaxCount = updated.HistoryMaxCount;
            Settings.HistoryMaxBytes = updated.HistoryMaxBytes;
            Settings.HistoryDays = updated.HistoryDays;
            Settings.MaxTextBytes = updated.MaxTextBytes;
            Settings.MaxImagePngBytes = updated.MaxImagePngBytes;
            Settings.MaxImageDecodedBytes = updated.MaxImageDecodedBytes;
            Settings.MaxSnippetCount = updated.MaxSnippetCount;
            Settings.MaxSnippetBytes = updated.MaxSnippetBytes;
            Settings.ExcludedApps = updated.ExcludedApps;
            Settings.DataDirectory = updated.DataDirectory;
            return switching;
        }
    }
    public Task<CaptureResult> RecordAsync(ClipboardContent content) => Task.Run(() =>
    {
        lock (recordingGate)
            return Settings.RecordingEnabled ? store.Capture(content) : new CaptureResult(CaptureStatus.Empty, null, "记录已暂停");
    });
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(HistoryQuery query) => Task.Run(() => { store.Prune(); return store.GetHistory(query); });
    public Task<IReadOnlyList<SnippetEntry>> GetSnippetsAsync(string query) => Task.Run(() => store.GetSnippets(query));
    public Task<SnippetEntry> SaveFromHistoryAsync(HistoryEntry entry, string name, string category, string? editedText = null) => Task.Run(() =>
    {
        if (entry.Kind != EntryKind.Text || entry.Text is null) throw new ContentValidationException("图片不能保存为文本常用语。");
        return store.SaveSnippet(name, category, editedText ?? entry.Text);
    });
}

public sealed class TextWorkspace
{
    public string Original { get; private set; } = "";
    public string Result { get; set; } = "";
    public void Load(string text) { Original = text; Result = text; }
    public void Apply(TextRule rule) { Result = TextCleaner.Apply(Result, rule); }
    public void Reset() { Result = Original; }
}
