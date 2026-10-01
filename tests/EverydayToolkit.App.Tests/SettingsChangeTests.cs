using EverydayToolkit.App;
using EverydayToolkit.Core;
using System.IO;

internal static class SettingsChangeTests
{
    public static void Run(Action<string, Action> check)
    {
        check("SettingsChange_SameDefaultFileCombinesPointerAndSettings", () =>
        {
            var fixture = new Fixture();
            var destination = Path.Combine(fixture.Root, "selected");
            var draft = new ToolSettings { RecordingEnabled = true, FirstRunComplete = true, HistoryDays = 14, DataDirectory = destination, StartWithWindows = true };
            Equal(true, fixture.Changes.Commit(draft));
            var saved = fixture.SettingsStore.Load();
            Equal(destination, saved.DataDirectory);
            Equal(14, saved.HistoryDays);
            Equal(true, saved.RecordingEnabled);
            Equal(destination, fixture.Preferences.Resolve(null));
            Equal<string?>(null, fixture.System.StartupDirectory);
            Equal(true, fixture.System.StartupEnabled);
            Equal(false, Directory.Exists(destination));
            Equal("synthetic original data", File.ReadAllText(Path.Combine(fixture.CurrentRoot, "content.db")));
        });
        check("SettingsChange_SwitchPreservesExistingDestinationAndOldData", () =>
        {
            var fixture = new Fixture(customCurrent: true);
            var destination = Path.Combine(fixture.Root, "existing");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "toolkit-data.marker"), "EverydayToolkit-v1");
            var destinationStore = new SettingsStore(Path.Combine(destination, "settings.json"));
            destinationStore.Save(new ToolSettings { Hotkey = "Ctrl+Alt+F8", HistoryDays = 30 });
            File.WriteAllText(Path.Combine(destination, "content.db"), "synthetic destination data");
            Equal(true, fixture.Changes.Commit(new ToolSettings { DataDirectory = destination, HistoryDays = 14 }));
            Equal(30, destinationStore.Load().HistoryDays);
            Equal("Ctrl+Alt+F8", destinationStore.Load().Hotkey);
            Equal("synthetic destination data", File.ReadAllText(Path.Combine(destination, "content.db")));
            Equal("synthetic original data", File.ReadAllText(Path.Combine(fixture.CurrentRoot, "content.db")));
            Equal(14, fixture.SettingsStore.Load().HistoryDays);
            Equal(destination, fixture.Preferences.Resolve(null));
        });
        check("SettingsChange_DefaultPointerFailureRollsBackCurrentSettingsAndSystem", () =>
        {
            var fixture = new Fixture(customCurrent: true);
            var pointerFile = Path.Combine(fixture.DefaultRoot, "settings.json");
            var before = File.ReadAllText(Path.Combine(fixture.CurrentRoot, "settings.json"));
            using var heldPointer = new FileStream(pointerFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws(() => fixture.Changes.Commit(new ToolSettings { DataDirectory = Path.Combine(fixture.Root, "selected"), RecordingEnabled = true, Hotkey = "Ctrl+Alt+F8", StartWithWindows = true }));
            Equal(before, File.ReadAllText(Path.Combine(fixture.CurrentRoot, "settings.json")));
            Equal("Ctrl+Alt+V", fixture.System.Hotkey);
            Equal(false, fixture.System.StartupEnabled);
            Equal(fixture.CurrentRoot, fixture.Preferences.Resolve(null));
        });
        check("SettingsChange_StartupFailurePreservesSettingsAndPointer", () =>
        {
            var fixture = new Fixture();
            var before = File.ReadAllText(Path.Combine(fixture.CurrentRoot, "settings.json"));
            fixture.System.FailNextStartup = true;
            Throws(() => fixture.Changes.Commit(new ToolSettings { DataDirectory = Path.Combine(fixture.Root, "selected"), Hotkey = "Ctrl+Alt+F8", StartWithWindows = true }));
            Equal(before, File.ReadAllText(Path.Combine(fixture.CurrentRoot, "settings.json")));
            Equal("Ctrl+Alt+V", fixture.System.Hotkey);
            Equal(fixture.CurrentRoot, fixture.Preferences.Resolve(null));
        });
        check("SettingsChange_UnwritableChoicePreservesSettingsAndPointer", () =>
        {
            var fixture = new Fixture();
            var blockingFile = Path.Combine(fixture.Root, "not-a-folder");
            File.WriteAllText(blockingFile, "synthetic file");
            Throws(() => fixture.Changes.Commit(new ToolSettings { DataDirectory = Path.Combine(blockingFile, "selected"), Hotkey = "Ctrl+Alt+F8" }));
            Equal(fixture.CurrentRoot, fixture.Preferences.Resolve(null));
            Equal("Ctrl+Alt+V", fixture.System.Hotkey);
            Equal("synthetic file", File.ReadAllText(blockingFile));
        });
        check("SettingsChange_ExplicitOverrideRetainsDefaultPointer", () =>
        {
            var fixture = new Fixture(customCurrent: true, explicitDirectory: true);
            var before = File.ReadAllText(Path.Combine(fixture.DefaultRoot, "settings.json"));
            Equal(false, fixture.Changes.Commit(new ToolSettings { RecordingEnabled = true, HistoryDays = 14, DataDirectory = fixture.CurrentRoot }));
            Equal(before, File.ReadAllText(Path.Combine(fixture.DefaultRoot, "settings.json")));
            Equal(true, fixture.SettingsStore.Load().RecordingEnabled);
            Throws(() => fixture.Changes.Commit(new ToolSettings { DataDirectory = Path.Combine(fixture.Root, "selected") }));
            Equal(before, File.ReadAllText(Path.Combine(fixture.DefaultRoot, "settings.json")));
        });
        check("SettingsChange_HotkeyFailureLeavesSettingsAndStartupUntouched", () =>
        {
            var fixture = new Fixture();
            fixture.System.FailNextHotkey = true;
            Throws(() => fixture.Changes.Commit(new ToolSettings { Hotkey = "Ctrl+Alt+F8", StartWithWindows = true, DataDirectory = fixture.CurrentRoot }));
            Equal("Ctrl+Alt+V", fixture.SettingsStore.Load().Hotkey);
            Equal(false, fixture.System.StartupEnabled);
        });
        check("SettingsChange_ExplicitDefaultProfilePreservesOtherDefaultPointer", () =>
        {
            var fixture = new Fixture(explicitDirectory: true);
            var selected = Path.Combine(fixture.Root, "other-selected");
            fixture.Preferences.SaveSelection(selected);
            Equal(false, fixture.Changes.Commit(new ToolSettings { RecordingEnabled = true, DataDirectory = fixture.CurrentRoot }));
            Equal(selected, fixture.Preferences.Resolve(null));
            Equal(true, fixture.SettingsStore.Load().RecordingEnabled);
        });
        check("SettingsChange_ControllerCommitsRecordingAndAllSettingsTogether", () =>
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "settings-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new ToolSettings();
            var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            settingsStore.Save(settings);
            using var store = new ContentStore(Path.Combine(root, "content.db"), new SyntheticProtector(), settings);
            var controller = new ToolkitController(store, settingsStore, settings);
            var changes = new SettingsChangeCoordinator(root, settingsStore, settings, new DataDirectoryPreferences(root), true, new SyntheticSystem());
            Equal(false, controller.ApplySettings(new ToolSettings { RecordingEnabled = true, HistoryDays = 14, DataDirectory = root }, changes.Commit));
            Equal(true, settings.RecordingEnabled);
            Equal(true, settings.FirstRunComplete);
            Equal(14, settings.HistoryDays);
            Equal(true, settingsStore.Load().RecordingEnabled);
            Equal(CaptureStatus.Added, controller.RecordAsync(new ClipboardContent(EntryKind.Text, "settings synthetic recording")).GetAwaiter().GetResult().Status);
            Equal(false, controller.ApplySettings(new ToolSettings { RecordingEnabled = false, DataDirectory = root }, changes.Commit));
            Equal(false, settings.RecordingEnabled);
            Equal(false, settingsStore.Load().RecordingEnabled);
            Equal(CaptureStatus.Empty, controller.RecordAsync(new ClipboardContent(EntryKind.Text, "settings synthetic paused")).GetAwaiter().GetResult().Status);
            Equal(1, store.GetStatistics().HistoryCount);
        });
        check("SettingsChange_ControllerFailurePreservesLiveRecordingAndFirstRun", () =>
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "settings-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new ToolSettings();
            var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            settingsStore.Save(settings);
            using var store = new ContentStore(Path.Combine(root, "content.db"), new SyntheticProtector(), settings);
            var controller = new ToolkitController(store, settingsStore, settings);
            var changes = new SettingsChangeCoordinator(root, settingsStore, settings, new DataDirectoryPreferences(root), true, new SyntheticSystem());
            using var heldSettings = new FileStream(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws(() => controller.ApplySettings(new ToolSettings { RecordingEnabled = true, Hotkey = "Ctrl+Alt+F8", HistoryDays = 14, DataDirectory = root }, changes.Commit));
            Equal(false, settings.RecordingEnabled);
            Equal(false, settings.FirstRunComplete);
            Equal(7, settings.HistoryDays);
            Equal("Ctrl+Alt+V", settings.Hotkey);
            Equal(false, settingsStore.Load().RecordingEnabled);
        });
        check("SettingsChange_FailedExplicitProfileRestoresExactOtherStartupDirectory", () =>
        {
            var fixture = new Fixture(customCurrent: true, explicitDirectory: true);
            var startupProfile = Path.Combine(fixture.Root, "startup-profile-A");
            fixture.System.SetStartup(true, startupProfile);
            using var heldSettings = new FileStream(Path.Combine(fixture.CurrentRoot, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws(() => fixture.Changes.Commit(new ToolSettings { StartWithWindows = true, DataDirectory = fixture.CurrentRoot }));
            Equal(true, fixture.System.StartupEnabled);
            Equal(startupProfile, fixture.System.StartupDirectory);
        });
        check("SettingsChange_ExplicitProfileStartupKeepsExplicitDirectory", () =>
        {
            var fixture = new Fixture(customCurrent: true, explicitDirectory: true);
            Equal(false, fixture.Changes.Commit(new ToolSettings { StartWithWindows = true, DataDirectory = fixture.CurrentRoot }));
            Equal(fixture.CurrentRoot, fixture.System.StartupDirectory);
        });
        check("SettingsChange_UnchangedLocationPreservesLatestOtherProfilePointer", () =>
        {
            var fixture = new Fixture();
            var newerSelected = Path.Combine(fixture.Root, "latest-other-selection");
            fixture.Preferences.SaveSelection(newerSelected);
            Equal(false, fixture.Changes.Commit(new ToolSettings { DataDirectory = fixture.CurrentRoot, HistoryDays = 14 }));
            Equal(newerSelected, fixture.Preferences.Resolve(null));
            Equal(14, fixture.SettingsStore.Load().HistoryDays);
        });
        check("SettingsChange_RecordingSavePreservesLatestOtherProfilePointer", () =>
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "settings-pointer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new ToolSettings();
            var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            settingsStore.Save(settings);
            using var store = new ContentStore(Path.Combine(root, "content.db"), new SyntheticProtector(), settings);
            var preferences = new DataDirectoryPreferences(root);
            var controller = new ToolkitController(store, settingsStore, settings, draft => preferences.SaveCurrentSettings(draft, root, settingsStore));
            var newerSelected = Path.Combine(root, "latest-other-selection");
            preferences.SaveSelection(newerSelected);
            controller.SetRecording(true);
            Equal(newerSelected, preferences.Resolve(null));
            Equal(newerSelected, settings.DataDirectory);
            Equal(true, settingsStore.Load().RecordingEnabled);
            controller.SetRecording(false);
            Equal(newerSelected, preferences.Resolve(null));
        });
        check("SettingsChange_EmptyLocationIsRejectedWithoutChangingPreferences", () =>
        {
            var fixture = new Fixture();
            Throws(() => fixture.Changes.Commit(new ToolSettings { DataDirectory = "   " }));
            Equal(fixture.CurrentRoot, fixture.Preferences.Resolve(null));
        });
    }

    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "settings-change-" + Guid.NewGuid().ToString("N"));
        public string DefaultRoot { get; }
        public string CurrentRoot { get; }
        public SettingsStore SettingsStore { get; }
        public DataDirectoryPreferences Preferences { get; }
        public SyntheticSystem System { get; } = new();
        public SettingsChangeCoordinator Changes { get; }
        public Fixture(bool customCurrent = false, bool explicitDirectory = false)
        {
            DefaultRoot = Path.Combine(Root, "default");
            CurrentRoot = customCurrent ? Path.Combine(Root, "current") : DefaultRoot;
            Directory.CreateDirectory(CurrentRoot);
            Directory.CreateDirectory(DefaultRoot);
            File.WriteAllText(Path.Combine(CurrentRoot, "toolkit-data.marker"), "EverydayToolkit-v1");
            File.WriteAllText(Path.Combine(CurrentRoot, "content.db"), "synthetic original data");
            var settings = new ToolSettings();
            SettingsStore = new SettingsStore(Path.Combine(CurrentRoot, "settings.json"));
            SettingsStore.Save(settings);
            Preferences = new DataDirectoryPreferences(DefaultRoot);
            if (customCurrent) Preferences.SaveSelection(CurrentRoot);
            Changes = new SettingsChangeCoordinator(CurrentRoot, SettingsStore, settings, Preferences, !explicitDirectory, System);
        }
    }
    private sealed class SyntheticSystem : ISettingsSystemChanges
    {
        public string Hotkey { get; private set; } = "Ctrl+Alt+V";
        public bool StartupEnabled { get; private set; }
        public string? StartupDirectory { get; private set; }
        public bool FailNextStartup { get; set; }
        public bool FailNextHotkey { get; set; }
        public bool RegisterHotkey(string hotkey, out string error)
        {
            error = "synthetic hotkey rejected";
            if (FailNextHotkey) { FailNextHotkey = false; return false; }
            Hotkey = hotkey; return true;
        }
        public void SetStartup(bool enabled, string? directory)
        {
            if (FailNextStartup) { FailNextStartup = false; throw new IOException("synthetic startup rejected"); }
            StartupEnabled = enabled; StartupDirectory = directory;
        }
        public Action CaptureStartupRestore()
        {
            var enabled = StartupEnabled; var directory = StartupDirectory;
            return () => { StartupEnabled = enabled; StartupDirectory = directory; };
        }
    }
    private sealed class SyntheticProtector : IContentProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] payload) => payload;
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
    private static void Throws(Action action) { try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; } catch (ContentValidationException) { return; } throw new Exception("expected commit rejection"); }
}
