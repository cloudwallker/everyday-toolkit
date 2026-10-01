using EverydayToolkit.App;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using System.IO;

if (args.Length == 2 && args[0] == "--desktop-target") return DesktopIntegration.RunTarget(args[1]);
if (args.Contains("--desktop")) return DesktopIntegration.Run();
if (args.Contains("--ui")) return UiIntegration.Run();
if (args.Contains("--settings-ui")) return SettingsUiIntegration.Run();
if (args.Contains("--perf")) return PerformanceIntegration.Run();

var failures = 0;
var selected = args.FirstOrDefault() ?? "";
void Check(string name, Action action)
{
    if (selected.Length > 0 && !name.Contains(selected, StringComparison.OrdinalIgnoreCase)) return;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
void Reject(Action action) { try { action(); } catch (ContentValidationException) { return; } throw new Exception("expected ContentValidationException"); }

Check("Workspace_LoadsEditableCopyWithoutChangingOriginal", () =>
{
    var workspace = new TextWorkspace();
    workspace.Load("  甲\n\n乙  ");
    workspace.Result = "新内容";
    Equal("  甲\n\n乙  ", workspace.Original);
    workspace.Reset();
    Equal("  甲\n\n乙  ", workspace.Result);
});
Check("Workspace_CleansResultAndKeepsSource", () =>
{
    var workspace = new TextWorkspace();
    workspace.Load("甲\n\n乙\n甲");
    workspace.Apply(TextRule.RemoveBlankLines);
    workspace.Apply(TextRule.DeduplicateLines);
    workspace.Apply(TextRule.JoinWithSpaces);
    Equal("甲 乙", workspace.Result);
    Equal("甲\n\n乙\n甲", workspace.Original);
});
Check("DirectoryIdentity_TrailingSeparatorAndCaseReferToSameStore", () =>
{
    var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity");
    Equal(DataDirectoryIdentity.InstanceId(directory), DataDirectoryIdentity.InstanceId(directory + Path.DirectorySeparatorChar));
    Equal(DataDirectoryIdentity.InstanceId(directory), DataDirectoryIdentity.InstanceId(directory.ToUpperInvariant()));
    Equal(Path.GetPathRoot(directory), DataDirectoryIdentity.Normalize(Path.GetPathRoot(directory)!));
});
Check("DataDirectoryPreferences_ExplicitOverrideAndStoredChoice", () =>
{
    var baseRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "preferences-" + Guid.NewGuid().ToString("N"));
    var defaultRoot = Path.Combine(baseRoot, "default");
    var chosen = Path.Combine(baseRoot, "chosen");
    Directory.CreateDirectory(defaultRoot);
    new SettingsStore(Path.Combine(defaultRoot, "settings.json")).Save(new ToolSettings { DataDirectory = chosen });
    var preferences = new DataDirectoryPreferences(defaultRoot);
    Equal(DataDirectoryIdentity.Normalize(chosen), preferences.Resolve(null));
    var explicitChoice = Path.Combine(baseRoot, "explicit");
    Equal(DataDirectoryIdentity.Normalize(explicitChoice), preferences.Resolve(explicitChoice));
});
Check("DataDirectoryPreferences_NewOrMarkedDirectoryWithoutMovingOldData", () =>
{
    var baseRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "selection-" + Guid.NewGuid().ToString("N"));
    var defaultRoot = Path.Combine(baseRoot, "default");
    var original = Path.Combine(baseRoot, "original");
    var empty = Path.Combine(baseRoot, "empty");
    var occupied = Path.Combine(baseRoot, "occupied");
    Directory.CreateDirectory(defaultRoot);
    Directory.CreateDirectory(original);
    Directory.CreateDirectory(occupied);
    File.WriteAllText(Path.Combine(original, "content.db"), "untouched synthetic old data");
    File.WriteAllText(Path.Combine(occupied, "private.txt"), "unrelated synthetic file");
    var preferences = new DataDirectoryPreferences(defaultRoot);
    Equal(DataDirectoryIdentity.Normalize(empty), preferences.ValidateSelection(empty, original));
    Reject(() => preferences.ValidateSelection(occupied, original));
    File.WriteAllText(Path.Combine(occupied, "toolkit-data.marker"), "EverydayToolkit-v1");
    Equal(DataDirectoryIdentity.Normalize(occupied), preferences.ValidateSelection(occupied, original));
    preferences.SaveSelection(empty);
    Equal(DataDirectoryIdentity.Normalize(empty), new SettingsStore(Path.Combine(defaultRoot, "settings.json")).Load().DataDirectory);
    Equal("untouched synthetic old data", File.ReadAllText(Path.Combine(original, "content.db")));
    Equal(false, Directory.Exists(empty));
    preferences.SaveSelection(defaultRoot);
    Equal<string?>(null, new SettingsStore(Path.Combine(defaultRoot, "settings.json")).Load().DataDirectory);
});

SettingsChangeTests.Run(Check);

if (selected.Length == 0 || selected.StartsWith("Controller", StringComparison.OrdinalIgnoreCase))
{
    var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "app-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var settings = new ToolSettings();
    var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
    using var store = new ContentStore(Path.Combine(root, "content.db"), new DpapiProtector(), settings);
    var controller = new ToolkitController(store, settingsStore, settings);
    Check("Controller_PausedRecorderLeavesStoreUntouched", () =>
    {
        controller.RecordAsync(new ClipboardContent(EntryKind.Text, "暂停样例")).GetAwaiter().GetResult();
        Equal(0, store.GetStatistics().HistoryCount);
    });
    Check("Controller_RecordingSwitchPersistsAcrossRestart", () =>
    {
        controller.SetRecording(true);
        Equal(true, settings.RecordingEnabled);
        Equal(true, settings.FirstRunComplete);
        Equal(true, settingsStore.Load().RecordingEnabled);
        controller.SetRecording(false);
        Equal(false, settingsStore.Load().RecordingEnabled);
    });
    Check("Controller_RecordsEnabledContentAndDeduplicates", () =>
    {
        controller.SetRecording(true);
        var first = controller.RecordAsync(new ClipboardContent(EntryKind.Text, "办公样例")).GetAwaiter().GetResult();
        var second = controller.RecordAsync(new ClipboardContent(EntryKind.Text, "办公样例")).GetAwaiter().GetResult();
        Equal(CaptureStatus.Added, first.Status);
        Equal(CaptureStatus.Duplicate, second.Status);
        Equal(1, store.GetStatistics().HistoryCount);
    });
    Check("Controller_HistoryTemplateSurvivesHistoryDeletion", () =>
    {
        var sample = store.Capture(new ClipboardContent(EntryKind.Text, "办公样例"));
        var entry = store.GetHistory().First(item => item.Id == sample.EntryId);
        controller.SaveFromHistoryAsync(entry, "回复", "办公").GetAwaiter().GetResult();
        store.DeleteHistory(entry.Id);
        Equal("办公样例", store.GetSnippets("回复").Single().Text);
    });
    Check("Controller_ImageCannotBecomeTextTemplate", () =>
    {
        var image = new HistoryEntry(Guid.NewGuid(), EntryKind.Image, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, null, 1, 1, 10);
        Reject(() => controller.SaveFromHistoryAsync(image, "图片", "").GetAwaiter().GetResult());
    });
    Check("Controller_SettingsFailureKeepsRecorderPaused", () =>
    {
        var invalidPath = Path.Combine(root, "settings-is-directory");
        Directory.CreateDirectory(invalidPath);
        var isolatedSettings = new ToolSettings();
        var invalidController = new ToolkitController(store, new SettingsStore(invalidPath), isolatedSettings);
        var failed = false;
        try { invalidController.SetRecording(true); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
        Equal(true, failed);
        Equal(false, isolatedSettings.RecordingEnabled);
        Equal(false, isolatedSettings.FirstRunComplete);
    });
    Check("Controller_QueryPrunesExpiredHistoryAfterLongRunningPause", () =>
    {
        var clock = new AppTestClock();
        var localSettings = new ToolSettings();
        using var history = new ContentStore(Path.Combine(root, "retention.db"), new DpapiProtector(), localSettings, clock);
        history.Capture(new ClipboardContent(EntryKind.Text, "应到期的合成历史"));
        var favorite = history.Capture(new ClipboardContent(EntryKind.Text, "应保留的合成收藏")).EntryId!.Value;
        history.SetFavorite(favorite, true);
        history.SaveSnippet("长期模板", "", "常用语不随历史到期");
        var localController = new ToolkitController(history, new SettingsStore(Path.Combine(root, "retention-settings.json")), localSettings);
        Equal(2, localController.GetHistoryAsync(new HistoryQuery()).GetAwaiter().GetResult().Count);
        clock.Current = clock.Current.AddDays(8);
        var remaining = localController.GetHistoryAsync(new HistoryQuery()).GetAwaiter().GetResult();
        Equal(1, remaining.Count);
        Equal(favorite, remaining.Single().Id);
        Equal(1, history.GetSnippets().Count);
        Equal(false, localSettings.RecordingEnabled);
    });
}
Console.WriteLine($"App behavior tests: {failures} failed");
return failures == 0 ? 0 : 1;

sealed class AppTestClock : TimeProvider
{
    public DateTimeOffset Current { get; set; } = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Current;
}
