using EverydayToolkit.Core;
using System.Security.Cryptography;
using System.Text;

var tests = new (string, Action)[]
{
    ("清洗断行、空白与去重", () => {
        Equal("甲\n\n乙", TextCleaner.Apply("甲\r\n\r乙\n甲", TextRule.DeduplicateLines));
        Equal("甲\n乙", TextCleaner.Apply("甲\r\n \r乙\n", TextRule.RemoveBlankLines));
        Equal("甲\n乙", TextCleaner.Apply(" 甲\r\n乙 ", TextRule.Trim));
        Equal("ab", TextCleaner.Apply("a\r\nb", TextRule.JoinLines));
        Equal("a b", TextCleaner.Apply("a\rb", TextRule.JoinWithSpaces));
    }),
    ("全半角只改 ASCII", () => {
        Equal("Ａ　！中文🙂", TextCleaner.Apply("A !中文🙂", TextRule.ToFullWidth));
        Equal("A !中文🙂", TextCleaner.Apply("Ａ　！中文🙂", TextRule.ToHalfWidth));
    }),
    ("设置默认、往返、无效文件保护", () => WithDirectory(dir => {
        var path = Path.Combine(dir, "settings.json"); var settings = new SettingsStore(path);
        Equal(false, settings.Load().RecordingEnabled);
        var value = new ToolSettings { FirstRunComplete = true, Hotkey = "Ctrl+Shift+V", ExcludedApps = ["synthetic.exe"] };
        settings.Save(value); Equal(value.Hotkey, settings.Load().Hotkey); Equal(1, settings.Load().ExcludedApps.Count);
        File.WriteAllText(path, "bad json"); Throws(() => settings.Load()); Equal("bad json", File.ReadAllText(path));
    })),
    ("空白、UTF8文本限制及图片验证", () => WithStore((store, settings, clock) => {
        Equal(CaptureStatus.Empty, store.Capture(Text(" \r\n")).Status);
        settings.MaxTextBytes = 3; Equal(CaptureStatus.TooLarge, store.Capture(Text("汉字")).Status);
        Equal(CaptureStatus.Empty, store.Capture(new(EntryKind.Image)).Status);
        Equal(CaptureStatus.TooLarge, store.Capture(new(EntryKind.Image, Png: [1], Thumbnail: [2], Width: int.MaxValue, Height: 2)).Status);
        Equal(CaptureStatus.Empty, store.Capture(new(EntryKind.Image, Png: [1], Width: 1, Height: 1)).Status);
    })),
    ("文字图片混合去重及最近使用时间", () => WithStore((store, settings, clock) => {
        var a = store.Capture(Text("alpha")); Equal(CaptureStatus.Added, a.Status);
        clock.Advance(TimeSpan.FromHours(1)); var duplicate = store.Capture(Text("alpha"));
        Equal(CaptureStatus.Duplicate, duplicate.Status); Equal(a.EntryId, duplicate.EntryId);
        Equal(clock.GetUtcNow(), store.GetHistory()[0].LastUsedUtc);
        Equal(CaptureStatus.Added, store.Capture(Image()).Status); Equal(CaptureStatus.Duplicate, store.Capture(Image()).Status);
        Equal(2, store.GetHistory().Count);
    })),
    ("查询筛选与图片懒加载", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        store.Capture(Text("a'; DROP TABLE history; --中文")); var imageId = store.Capture(Image()).EntryId!.Value;
        fixture.Protector.UnprotectCount = 0;
        Equal(1, store.GetHistory(new(Kind: EntryKind.Image)).Count); Equal(0, fixture.Protector.UnprotectCount);
        Equal(1, store.GetHistory(new(Search: "中文")).Count);
        Equal(3, store.GetImage(imageId).Png.Length); Equal(2, store.GetThumbnail(imageId).Length);
        Equal(0, store.GetHistory(new(Since: fixture.Clock.GetUtcNow().AddDays(1))).Count);
    }),
    ("数量配额淘汰最早未收藏", () => WithStore((store, settings, clock) => {
        settings.HistoryMaxCount = 2;
        var pinned = store.Capture(Text("pinned")).EntryId!.Value; store.SetFavorite(pinned, true);
        clock.Advance(TimeSpan.FromMinutes(1)); var oldest = store.Capture(Text("oldest")).EntryId;
        clock.Advance(TimeSpan.FromMinutes(1)); store.Capture(Text("newest"));
        Equal(2, store.GetHistory().Count); Check(store.GetHistory().All(x => x.Id != oldest));
        Equal(1, store.GetHistory(new(FavoritesOnly: true)).Count);
    })),
    ("收藏占满配额阻止新增且不删除现有记录", () => WithStore((store, settings, clock) => {
        settings.HistoryMaxCount = 1; var id = store.Capture(Text("kept")).EntryId!.Value; store.SetFavorite(id, true);
        Equal(CaptureStatus.QuotaBlocked, store.Capture(Text("blocked")).Status); Equal(id, store.GetHistory().Single().Id);
        store.SetFavorite(id, false); Equal(CaptureStatus.Added, store.Capture(Text("allowed")).Status);
    })),
    ("加密真实字节配额与事务回滚", () => WithStore((store, settings, clock) => {
        var id = store.Capture(Text("first")).EntryId!.Value; var bytes = store.GetStatistics().HistoryBytes;
        Check(bytes > 5); settings.HistoryMaxBytes = bytes;
        Equal(CaptureStatus.QuotaBlocked, store.Capture(Text(new string('z', 100))).Status);
        Equal(id, store.GetHistory().Single().Id); Equal(bytes, store.GetStatistics().HistoryBytes);
    })),
    ("保留期依据使用时间、收藏免删", () => WithStore((store, settings, clock) => {
        var old = store.Capture(Text("old")).EntryId!.Value;
        var pinned = store.Capture(Text("pin")).EntryId!.Value; store.SetFavorite(pinned, true);
        var active = store.Capture(Text("active")).EntryId!.Value;
        clock.Advance(TimeSpan.FromDays(6)); store.Touch(active); clock.Advance(TimeSpan.FromDays(2)); store.Prune();
        Check(store.GetHistory().All(x => x.Id != old)); Equal(2, store.GetHistory().Count);
    })),
    ("独立常用语增改删与名称分类搜索", () => WithStore((store, settings, clock) => {
        var item = store.SaveSnippet("名字", "类别", "正文"); Equal(0, store.GetHistory().Count);
        Equal(1, store.GetSnippets("类别").Count); Equal(1, store.GetSnippets("正文").Count);
        clock.Advance(TimeSpan.FromDays(1)); var updated = store.SaveSnippet("新名", "分类", "新正文", item.Id);
        Equal(item.Id, updated.Id); Equal(clock.GetUtcNow(), updated.UpdatedUtc);
        ThrowsValidation(() => store.SaveSnippet(" ", "", "valid")); ThrowsValidation(() => store.SaveSnippet("valid", "", " "));
        store.DeleteSnippet(item.Id); Equal(0, store.GetSnippets().Count);
    })),
    ("常用语独立配额", () => WithStore((store, settings, clock) => {
        settings.MaxSnippetCount = 1; var item = store.SaveSnippet("a", "", "text");
        ThrowsValidation(() => store.SaveSnippet("b", "", "text")); Equal(1, store.GetSnippets().Count);
        settings.MaxSnippetBytes = 1; ThrowsValidation(() => store.SaveSnippet("changed", "", "changed", item.Id));
        Equal("a", store.GetSnippets().Single().Name);
    })),
    ("JSON导入重名改名及导出往返", () => WithStore((store, settings, clock) => {
        store.SaveSnippet("a", "", "one");
        var result = store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"a\",\"category\":\"test\",\"text\":\"two\"},{\"name\":\"a\",\"category\":\"\",\"text\":\"three\"}]}"));
        Equal(2, result.ImportedCount); Equal(2, result.RenamedCount); Check(store.GetSnippets().Any(x => x.Name == "a (3)"));
        using var output = new MemoryStream(); store.ExportSnippets(output); output.Position = 0;
        store.DeleteAllData(); Equal(3, store.ImportSnippets(output).ImportedCount);
    })),
    ("导入非法、配额和16MiB限制完全原子", () => WithStore((store, settings, clock) => {
        store.SaveSnippet("keep", "", "text");
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"valid\",\"category\":\"\",\"text\":\"x\"},{\"name\":\"bad\",\"category\":\"\",\"text\":\" \"}]}")));
        Equal(1, store.GetSnippets().Count); settings.MaxSnippetCount = 1;
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"valid\",\"category\":\"\",\"text\":\"x\"}]}")));
        ThrowsValidation(() => store.ImportSnippets(new MemoryStream(new byte[16 * 1024 * 1024 + 1])));
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":2,\"snippets\":[]}"))); Equal(1, store.GetSnippets().Count);
    })),
    ("保护器中途失败导入事务回滚", () => {
        using var fixture = new Fixture(); fixture.Store.SaveSnippet("keep", "", "text");
        fixture.Protector.FailProtectAfter = fixture.Protector.ProtectCount + 3;
        Throws(() => fixture.Store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"one\",\"category\":\"\",\"text\":\"1\"},{\"name\":\"two\",\"category\":\"\",\"text\":\"2\"}]}")));
        Equal(1, fixture.Store.GetSnippets().Count);
    }),
    ("重启恢复、SQLite文件不包含正文名称分类", () => {
        using var fixture = new Fixture(); var id = fixture.Store.Capture(Image()).EntryId!.Value;
        fixture.Store.Capture(Text("synthetic-private-payload")); fixture.Store.SaveSnippet("synthetic-private-name", "synthetic-private-category", "synthetic-private-text");
        fixture.Store.Dispose(); var disk = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.DatabasePath));
        Check(!disk.Contains("synthetic-private")); using var reopened = new ContentStore(fixture.DatabasePath, fixture.Protector, fixture.Settings, fixture.Clock);
        Equal(2, reopened.GetHistory().Count); Equal(3, reopened.GetImage(id).Png.Length); Equal(1, reopened.GetSnippets().Count);
    }),
    ("清空未收藏、删除和删除全部", () => WithStore((store, settings, clock) => {
        var pin = store.Capture(Text("pin")).EntryId!.Value; store.SetFavorite(pin, true); store.Capture(Text("other")); store.SaveSnippet("a", "", "b");
        store.ClearUnpinnedHistory(); Equal(1, store.GetHistory().Count); Equal(1, store.GetSnippets().Count);
        store.DeleteHistory(pin); Equal(0, store.GetHistory().Count); store.Capture(Text("new"));
        store.DeleteAllData(); Equal(0, store.GetStatistics().HistoryCount); Equal(0, store.GetStatistics().SnippetCount);
    })),
    ("捕获淘汰后异常回滚恢复已有记录", () => WithStore((store, settings, clock) => {
        settings.HistoryMaxCount = 1; var old = store.Capture(Text("old")).EntryId!.Value;
        clock.FailAtCall = clock.CallCount + 2;
        Throws(() => store.Capture(Text("new"))); Equal(old, store.GetHistory().Single().Id);
    })),
    ("损坏数据库与解密失败均保留原文件", () => {
        WithDirectory(dir => {
            var path = Path.Combine(dir, "corrupt.db"); var bytes = Encoding.UTF8.GetBytes("synthetic-corrupt-database"); File.WriteAllBytes(path, bytes);
            using var protector = new TestProtector(); Throws(() => { using var store = new ContentStore(path, protector, new()); });
            Check(bytes.SequenceEqual(File.ReadAllBytes(path)));
        });
        using var fixture = new Fixture(); fixture.Store.Capture(Text("synthetic-value")); fixture.Store.GetHistory(); fixture.Store.Dispose();
        var before = File.ReadAllBytes(fixture.DatabasePath); using var wrongProtector = new TestProtector();
        using (var reopened = new ContentStore(fixture.DatabasePath, wrongProtector, fixture.Settings)) Throws(() => reopened.GetHistory());
        Check(before.SequenceEqual(File.ReadAllBytes(fixture.DatabasePath)));
    }),
    ("双实例并发SQLite事务去重", () => {
        using var fixture = new Fixture(); using var second = new ContentStore(fixture.DatabasePath, fixture.Protector, fixture.Settings, fixture.Clock);
        Parallel.For(0, 32, i => (i % 2 == 0 ? fixture.Store : second).Capture(Text("shared" + i % 4)));
        Equal(4, fixture.Store.GetHistory().Count); Equal(4, second.GetHistory().Count);
    }),
    ("图片加密载荷计量包含缩略图", () => WithStore((store, settings, clock) => {
        store.Capture(Image()); Equal(3L + 2 + 28 + 28, store.GetStatistics().HistoryBytes);
        settings.MaxImagePngBytes = 2; Equal(CaptureStatus.TooLarge, store.Capture(Image()).Status);
        settings.MaxImagePngBytes = 100; settings.MaxImageDecodedBytes = 23; Equal(CaptureStatus.TooLarge, store.Capture(Image()).Status);
    })),
    ("无效设置保存不覆盖现有文件", () => WithDirectory(dir => {
        var path = Path.Combine(dir, "settings.json"); var store = new SettingsStore(path); store.Save(new()); var before = File.ReadAllBytes(path);
        ThrowsValidation(() => store.Save(new() { HistoryDays = 0 })); Check(before.SequenceEqual(File.ReadAllBytes(path)));
        File.WriteAllText(path, "{\"HistoryMaxCount\":-1}"); ThrowsValidation(() => store.Load()); Equal("{\"HistoryMaxCount\":-1}", File.ReadAllText(path));
    })),
    ("非法JSON版本类型返回可提示验证错误", () => WithStore((store, settings, clock) => {
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":\"one\",\"snippets\":[]}")));
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":null,\"snippets\":[]}")));
        ThrowsValidation(() => store.ImportSnippets(Json("{\"version\":true,\"snippets\":[]}")));
        Equal(0, store.GetSnippets().Count);
    })),
    ("历史重复检索驻留缓存不重复解密且图片查询仍懒加载", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        store.Capture(Text("cached-alpha")); store.Capture(Text("cached-beta")); store.Capture(Image());
        store.GetHistory(); var calls = fixture.Protector.UnprotectCount;
        Equal(1, store.GetHistory(new(Search: "alpha")).Count); Equal(1, store.GetHistory(new(Search: "beta")).Count);
        Equal(calls, fixture.Protector.UnprotectCount);
        Equal(1, store.GetHistory(new(Kind: EntryKind.Image)).Count); Equal(calls, fixture.Protector.UnprotectCount);
    }),
    ("常用语重复检索与相同时间戳更新正确失效", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        var old = store.SaveSnippet("cached-name", "cached-category", "cached-body"); store.GetSnippets();
        var calls = fixture.Protector.UnprotectCount; Equal(1, store.GetSnippets("body").Count); Equal(calls, fixture.Protector.UnprotectCount);
        var updated = store.SaveSnippet("new-name", "new-category", "new-body", old.Id); Equal(old.UpdatedUtc, updated.UpdatedUtc);
        Equal(0, store.GetSnippets("cached-body").Count); Equal(1, store.GetSnippets("new-body").Count);
        calls = fixture.Protector.UnprotectCount; store.GetSnippets(); Equal(calls, fixture.Protector.UnprotectCount);
    }),
    ("缓存覆盖捕获删除淘汰清空和导入", () => {
        using var fixture = new Fixture(); var store = fixture.Store; fixture.Settings.HistoryMaxCount = 2;
        var pin = store.Capture(Text("pin")).EntryId!.Value; store.SetFavorite(pin, true);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1)); store.Capture(Text("old")); store.GetHistory();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1)); store.Capture(Text("new")); Equal(0, store.GetHistory(new(Search: "old")).Count);
        Equal(1, store.GetHistory(new(Search: "new")).Count); store.ClearUnpinnedHistory(); Equal(pin, store.GetHistory().Single().Id);
        store.DeleteHistory(pin); Equal(0, store.GetHistory().Count);
        store.Capture(Text("expire")); store.GetHistory(); fixture.Clock.Advance(TimeSpan.FromDays(8)); store.Prune(); Equal(0, store.GetHistory().Count);
        var snippet = store.SaveSnippet("saved", "", "body"); store.GetSnippets(); store.DeleteSnippet(snippet.Id); Equal(0, store.GetSnippets().Count);
        store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"imported\",\"category\":\"\",\"text\":\"body\"}]}")); Equal(1, store.GetSnippets("imported").Count);
        store.DeleteAllData(); Equal(0, store.GetSnippets().Count); Equal(0, store.GetHistory().Count);
        var calls = fixture.Protector.UnprotectCount; store.GetHistory(); store.GetSnippets(); Equal(calls, fixture.Protector.UnprotectCount);
    }),
    ("外部连接提交使缓存失效且相同时间戳编辑不陈旧", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        var history = store.Capture(Text("external-old")).EntryId!.Value; var snippet = store.SaveSnippet("external-name", "", "external-old");
        store.GetHistory(); store.GetSnippets(); using var second = new ContentStore(fixture.DatabasePath, fixture.Protector, fixture.Settings, fixture.Clock);
        second.DeleteHistory(history); second.Capture(Text("external-new")); second.SaveSnippet("edited-name", "", "external-new", snippet.Id);
        Equal(0, store.GetHistory(new(Search: "external-old")).Count); Equal(1, store.GetHistory(new(Search: "external-new")).Count);
        Equal(0, store.GetSnippets("external-old").Count); Equal(1, store.GetSnippets("external-new").Count);
        var calls = fixture.Protector.UnprotectCount; store.GetHistory(); store.GetSnippets(); Equal(calls, fixture.Protector.UnprotectCount);
        second.DeleteAllData(); Equal(0, store.GetHistory().Count); Equal(0, store.GetSnippets().Count);
    }),
    ("事务失败保留真实缓存且不缓存未提交内容", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        fixture.Settings.HistoryMaxCount = 1; var old = store.Capture(Text("kept")).EntryId!.Value; store.GetHistory();
        fixture.Clock.FailAtCall = fixture.Clock.CallCount + 2; Throws(() => store.Capture(Text("rollback"))); Equal(old, store.GetHistory().Single().Id);
        var snippet = store.SaveSnippet("kept", "", "kept-body"); store.GetSnippets();
        fixture.Protector.FailProtectAfter = fixture.Protector.ProtectCount + 3;
        Throws(() => store.ImportSnippets(Json("{\"version\":1,\"snippets\":[{\"name\":\"one\",\"category\":\"\",\"text\":\"1\"},{\"name\":\"two\",\"category\":\"\",\"text\":\"2\"}]}")));
        var calls = fixture.Protector.UnprotectCount; Equal(snippet.Id, store.GetSnippets().Single().Id); Equal(old, store.GetHistory().Single().Id);
        Equal(calls, fixture.Protector.UnprotectCount);
    }),
    ("驻留缓存按设置条数有界", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        store.Capture(Text("bounded-one")); store.Capture(Text("bounded-two")); fixture.Settings.HistoryMaxCount = 1;
        store.GetHistory(new(Search: "one")); var calls = fixture.Protector.UnprotectCount;
        store.GetHistory(new(Search: "one")); Check(fixture.Protector.UnprotectCount > calls);
        store.SaveSnippet("bounded-one", "", "1"); store.SaveSnippet("bounded-two", "", "2"); fixture.Settings.MaxSnippetCount = 1;
        store.GetSnippets(); calls = fixture.Protector.UnprotectCount; store.GetSnippets(); Check(fixture.Protector.UnprotectCount > calls);
    }),
    ("驻留缓存按加密载荷字节预算有界", () => {
        using var fixture = new Fixture(); var store = fixture.Store;
        store.Capture(Text("one")); store.Capture(Text("two")); fixture.Settings.HistoryMaxBytes = store.GetStatistics().HistoryBytes / 2;
        store.GetHistory(); var calls = fixture.Protector.UnprotectCount; store.GetHistory(); Check(fixture.Protector.UnprotectCount > calls);
        store.SaveSnippet("one", "", "1"); store.SaveSnippet("two", "", "2"); fixture.Settings.MaxSnippetBytes = store.GetStatistics().SnippetBytes / 2;
        store.GetSnippets(); calls = fixture.Protector.UnprotectCount; store.GetSnippets(); Check(fixture.Protector.UnprotectCount > calls);
    }),
    ("外部密钥重新保护内容不可返回旧缓存正文", () => {
        using var fixture = new Fixture(); var store = fixture.Store; var item = store.SaveSnippet("name", "", "cached-private"); store.GetSnippets();
        using var differentProtector = new TestProtector(); using var second = new ContentStore(fixture.DatabasePath, differentProtector, fixture.Settings, fixture.Clock);
        second.SaveSnippet("name", "", "new-private", item.Id); Throws(() => store.GetSnippets());
    }),
    ("多线程并发捕获串行去重", () => WithStore((store, settings, clock) => {
        Parallel.For(0, 80, i => store.Capture(Text("value" + i % 8))); Equal(8, store.GetHistory().Count);
        Check(store.GetStatistics().DiskBytes > 0);
    }))
};
var failures = 0;
foreach (var (name, test) in tests) { try { test(); Console.WriteLine("PASS " + name); } catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + " [" + exception.GetType().Name + "]"); } }
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed"); return failures == 0 ? 0 : 1;

static ClipboardContent Text(string value) => new(EntryKind.Text, Text: value);
static ClipboardContent Image() => new(EntryKind.Image, Png: [1, 2, 3], Thumbnail: [4, 5], Width: 2, Height: 3);
static MemoryStream Json(string value) => new(Encoding.UTF8.GetBytes(value));
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Assertion mismatch"); }
static void Check(bool value) { if (!value) throw new Exception("Assertion false"); }
static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure missing"); }
static void ThrowsValidation(Action action) { try { action(); } catch (ContentValidationException) { return; } throw new Exception("Expected validation failure missing"); }
static void WithStore(Action<ContentStore, ToolSettings, ManualClock> action) { using var fixture = new Fixture(); action(fixture.Store, fixture.Settings, fixture.Clock); }
static void WithDirectory(Action<string> action) { var dir = Path.Combine(Path.GetTempPath(), "EverydayToolkitTests-" + Guid.NewGuid()); Directory.CreateDirectory(dir); try { action(dir); } finally { Directory.Delete(dir, true); } }

sealed class Fixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "EverydayToolkitTests-" + Guid.NewGuid());
    public string DatabasePath => Path.Combine(DirectoryPath, "content.db");
    public TestProtector Protector { get; } = new();
    public ToolSettings Settings { get; } = new();
    public ManualClock Clock { get; } = new();
    public ContentStore Store { get; }
    public Fixture() { Directory.CreateDirectory(DirectoryPath); Store = new(DatabasePath, Protector, Settings, Clock); }
    public void Dispose() { Store.Dispose(); Protector.Dispose(); Directory.Delete(DirectoryPath, true); }
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public int CallCount { get; private set; }
    public int FailAtCall { get; set; } = int.MaxValue;
    public override DateTimeOffset GetUtcNow() { if (++CallCount == FailAtCall) throw new InvalidOperationException(); return now; }
    public void Advance(TimeSpan duration) => now += duration;
}
sealed class TestProtector : IContentProtector, IDisposable
{
    private readonly object gate = new();
    private readonly AesGcm aes = new(RandomNumberGenerator.GetBytes(32), 16);
    public int UnprotectCount { get; set; }
    public int ProtectCount { get; private set; }
    public int FailProtectAfter { get; set; } = int.MaxValue;
    public byte[] Protect(byte[] plaintext) {
        lock (gate) {
        if (ProtectCount++ >= FailProtectAfter) throw new CryptographicException();
        var result = new byte[plaintext.Length + 28]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16)); return result;
        }
    }
    public byte[] Unprotect(byte[] payload) { lock (gate) { UnprotectCount++; var result = new byte[payload.Length - 28]; aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), result); return result; } }
    public void Dispose() => aes.Dispose();
}
