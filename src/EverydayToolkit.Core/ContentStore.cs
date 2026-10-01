using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Text.Json;

namespace EverydayToolkit.Core;

public sealed class ContentStore : IDisposable
{
    private readonly object gate = new();
    private readonly NativeSqlite database;
    private readonly string path;
    private readonly IContentProtector protector;
    private readonly ToolSettings settings;
    private readonly TimeProvider clock;
    private readonly ResidentCache<string> historyText;
    private readonly ResidentCache<CachedSnippet> snippetText;
    private long dataVersion;
    private bool disposed;
    private sealed record CachedSnippet(SnippetEntry Entry, byte[] Fingerprint);

    public ContentStore(string databasePath, IContentProtector protector, ToolSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(protector); SettingsStore.Validate(settings);
        this.protector = protector; this.settings = settings; clock = timeProvider ?? TimeProvider.System;
        historyText = new(() => settings.HistoryMaxCount, () => settings.HistoryMaxBytes);
        snippetText = new(() => settings.MaxSnippetCount, () => settings.MaxSnippetBytes);
        path = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        database = new NativeSqlite(path);
        try
        {
            database.Query("PRAGMA secure_delete=ON");
            var version = database.Scalar("PRAGMA user_version");
            if (version > 1) throw new ContentValidationException("数据库来自较新的应用版本，请使用对应版本打开。");
            if (version == 0) Transaction(() =>
            {
                database.Execute("CREATE TABLE IF NOT EXISTS history (id TEXT PRIMARY KEY, kind INTEGER NOT NULL, created INTEGER NOT NULL, used INTEGER NOT NULL, favorite INTEGER NOT NULL DEFAULT 0, digest BLOB NOT NULL, payload BLOB NOT NULL, thumbnail BLOB, width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL, UNIQUE(kind,digest))");
                database.Execute("CREATE INDEX IF NOT EXISTS history_used ON history(used)");
                database.Execute("CREATE TABLE IF NOT EXISTS snippets (id TEXT PRIMARY KEY, name BLOB NOT NULL, category BLOB NOT NULL, text BLOB NOT NULL, updated INTEGER NOT NULL, bytes INTEGER NOT NULL)");
                database.Execute("PRAGMA user_version=1"); return 0;
            });
            dataVersion = database.Scalar("PRAGMA data_version");
        }
        catch { database.Dispose(); throw; }
    }

    public CaptureResult Capture(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Locked(() =>
        {
            byte[] plaintext;
            if (content.Kind == EntryKind.Text)
            {
                if (string.IsNullOrWhiteSpace(content.Text)) return Result(CaptureStatus.Empty);
                plaintext = Encoding.UTF8.GetBytes(content.Text);
                if (plaintext.LongLength > settings.MaxTextBytes) return Result(CaptureStatus.TooLarge);
            }
            else if (content.Kind == EntryKind.Image)
            {
                if (content.Png is not { Length: > 0 } || content.Thumbnail is not { Length: > 0 } || content.Width <= 0 || content.Height <= 0) return Result(CaptureStatus.Empty);
                if (content.Png.LongLength > settings.MaxImagePngBytes || content.Thumbnail.LongLength > settings.MaxImagePngBytes
                    || (long)content.Width * content.Height > settings.MaxImageDecodedBytes / 4) return Result(CaptureStatus.TooLarge);
                plaintext = content.Png;
            }
            else throw new ContentValidationException("未知剪贴板内容类型。");
            var digest = SHA256.HashData(plaintext);
            var result = Transaction(() =>
            {
                var existing = database.Query("SELECT id FROM history WHERE kind=? AND digest=?", (int)content.Kind, digest);
                var now = clock.GetUtcNow().UtcTicks;
                if (existing.Count > 0)
                {
                    var duplicateId = Guid.Parse((string)existing[0][0]!);
                    database.Execute("UPDATE history SET used=? WHERE id=?", now, duplicateId);
                    return Result(CaptureStatus.Duplicate, duplicateId);
                }
                var payload = protector.Protect(plaintext);
                var thumbnail = content.Kind == EntryKind.Image ? protector.Protect(content.Thumbnail!) : null;
                var bytes = payload.LongLength + (thumbnail?.LongLength ?? 0);
                if (bytes > settings.HistoryMaxBytes || settings.HistoryMaxCount <= 0) return Result(CaptureStatus.QuotaBlocked);
                // Plan all evictions first: blocked captures preserve every existing record.
                var counts = database.Query("SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM history")[0];
                var count = Number(counts[0]); var totalBytes = Number(counts[1]); var evictions = new List<string>();
                foreach (var row in database.Query("SELECT id,bytes FROM history WHERE favorite=0 ORDER BY used ASC, created ASC, id ASC"))
                {
                    if (count + 1 <= settings.HistoryMaxCount && totalBytes + bytes <= settings.HistoryMaxBytes) break;
                    evictions.Add((string)row[0]!); count--; totalBytes -= Number(row[1]);
                }
                if (count + 1 > settings.HistoryMaxCount || totalBytes + bytes > settings.HistoryMaxBytes) return Result(CaptureStatus.QuotaBlocked);
                foreach (var id in evictions) database.Execute("DELETE FROM history WHERE id=?", id);
                PruneExpired();
                var entryId = Guid.NewGuid();
                database.Execute("INSERT INTO history(id,kind,created,used,favorite,digest,payload,thumbnail,width,height,bytes) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    entryId, (int)content.Kind, now, now, false, digest, payload, thumbnail, content.Kind == EntryKind.Image ? content.Width : 0, content.Kind == EntryKind.Image ? content.Height : 0, bytes);
                return Result(CaptureStatus.Added, entryId);
            });
            if (result.Status == CaptureStatus.Added) RetainHistoryCache();
            return result;
        });
    }

    public IReadOnlyList<HistoryEntry> GetHistory(HistoryQuery? query = null) => Locked<IReadOnlyList<HistoryEntry>>(() =>
    {
        query ??= new(); var result = new List<HistoryEntry>();
        var rows = database.Query("SELECT id,kind,created,used,favorite,CASE WHEN kind=0 THEN payload ELSE NULL END,width,height,bytes FROM history WHERE (? IS NULL OR kind=?) AND (?=0 OR favorite=1) AND (? IS NULL OR used>=?) ORDER BY used DESC,created DESC,id ASC",
            query.Kind.HasValue ? (int)query.Kind.Value : null, query.Kind.HasValue ? (int)query.Kind.Value : null, query.FavoritesOnly, query.Since?.UtcTicks, query.Since?.UtcTicks);
        foreach (var row in rows)
        {
            var kind = (EntryKind)Number(row[1]);
            var id = Guid.Parse((string)row[0]!);
            var text = kind == EntryKind.Text ? historyText.GetOrAdd(id, ((byte[])row[5]!).LongLength, () => Decode((byte[])row[5]!)) : null;
            if (!string.IsNullOrEmpty(query.Search) && (text is null || !text.Contains(query.Search, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(id, kind, Date(Number(row[2])), Date(Number(row[3])), Number(row[4]) != 0, text, (int)Number(row[6]), (int)Number(row[7]), Number(row[8])));
        }
        return result;
    });

    public ImagePayload GetImage(Guid id) => Locked(() =>
    {
        var rows = database.Query("SELECT payload,thumbnail,width,height FROM history WHERE id=? AND kind=1", id);
        if (rows.Count == 0) throw new KeyNotFoundException("图片记录不存在。");
        var row = rows[0]; return new ImagePayload(protector.Unprotect((byte[])row[0]!), protector.Unprotect((byte[])row[1]!), (int)Number(row[2]), (int)Number(row[3]));
    });
    public byte[] GetThumbnail(Guid id) => Locked(() =>
    {
        var rows = database.Query("SELECT thumbnail FROM history WHERE id=? AND kind=1", id);
        if (rows.Count == 0) throw new KeyNotFoundException("图片记录不存在。");
        return protector.Unprotect((byte[])rows[0][0]!);
    });
    public void SetFavorite(Guid id, bool favorite) => Locked(() => database.Execute("UPDATE history SET favorite=? WHERE id=?", favorite, id));
    public void Touch(Guid id) => Locked(() => database.Execute("UPDATE history SET used=? WHERE id=?", clock.GetUtcNow().UtcTicks, id));
    public void DeleteHistory(Guid id) => Locked(() => { database.Execute("DELETE FROM history WHERE id=?", id); historyText.Remove(id); return 0; });
    public void ClearUnpinnedHistory() => Locked(() => { database.Execute("DELETE FROM history WHERE favorite=0"); RetainHistoryCache(); return 0; });
    public void DeleteAllData() => Locked(() =>
    {
        Transaction(() => { database.Execute("DELETE FROM history"); database.Execute("DELETE FROM snippets"); return 0; });
        ClearCaches();
        database.Execute("VACUUM"); return 0;
    });
    public void Prune() => Locked(() =>
    {
        Transaction(() =>
        {
            PruneExpired();
            var totals = database.Query("SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM history")[0];
            var count = Number(totals[0]); var bytes = Number(totals[1]);
            foreach (var row in database.Query("SELECT id,bytes FROM history WHERE favorite=0 ORDER BY used ASC,created ASC,id ASC"))
            {
                if (count <= settings.HistoryMaxCount && bytes <= settings.HistoryMaxBytes) break;
                database.Execute("DELETE FROM history WHERE id=?", row[0]); count--; bytes -= Number(row[1]);
            }
            return 0;
        });
        RetainHistoryCache(); return 0;
    });
    private void PruneExpired()
    {
        var now = clock.GetUtcNow();
        var cutoff = settings.HistoryDays >= (now - DateTimeOffset.MinValue).TotalDays ? DateTimeOffset.MinValue : now.AddDays(-settings.HistoryDays);
        database.Execute("DELETE FROM history WHERE favorite=0 AND used<?", cutoff.UtcTicks);
    }
    public StoreStatistics GetStatistics() => Locked(() =>
    {
        var history = database.Query("SELECT COUNT(*),COALESCE(SUM(bytes),0),COALESCE(SUM(favorite),0) FROM history")[0];
        var snippets = database.Query("SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM snippets")[0];
        var diskBytes = new[] { path, path + "-wal", path + "-shm", path + "-journal" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
        return new StoreStatistics((int)Number(history[0]), Number(history[1]), (int)Number(history[2]), (int)Number(snippets[0]), Number(snippets[1]), diskBytes);
    });

    public IReadOnlyList<SnippetEntry> GetSnippets(string query = "") => Locked<IReadOnlyList<SnippetEntry>>(() => ReadSnippets().Where(item => string.IsNullOrEmpty(query)
        || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

    private List<SnippetEntry> ReadSnippets()
    {
        var result = new List<SnippetEntry>();
        foreach (var row in database.Query("SELECT id,name,category,text,updated FROM snippets ORDER BY updated DESC,id ASC"))
        {
            var id = Guid.Parse((string)row[0]!); var updated = Date(Number(row[4]));
            var fingerprint = Fingerprint((byte[])row[1]!, (byte[])row[2]!, (byte[])row[3]!);
            // Row identity protects against a foreign commit between data_version and SELECT.
            // Time alone is insufficient: callers may save multiple edits at the same instant.
            var weight = ((byte[])row[1]!).LongLength + ((byte[])row[2]!).LongLength + ((byte[])row[3]!).LongLength;
            var cached = snippetText.GetOrAdd(id, weight,
                () => new(new(id, Decode((byte[])row[1]!), Decode((byte[])row[2]!), Decode((byte[])row[3]!), updated), fingerprint),
                item => item.Entry.UpdatedUtc == updated && fingerprint.AsSpan().SequenceEqual(item.Fingerprint));
            result.Add(cached.Entry);
        }
        return result;
    }

    public SnippetEntry SaveSnippet(string name, string category, string text, Guid? id = null) => Locked(() =>
    {
        var entry = Transaction(() => SaveSnippetCore(name, category, text, id));
        snippetText.Remove(entry.Id); return entry;
    });
    private SnippetEntry SaveSnippetCore(string name, string category, string text, Guid? id)
    {
        ValidateSnippet(name, category, text);
        if (id.HasValue && database.Scalar("SELECT COUNT(*) FROM snippets WHERE id=?", id.Value) == 0) throw new ContentValidationException("常用语不存在。");
        var encryptedName = Encode(name); var encryptedCategory = Encode(category); var encryptedText = Encode(text);
        var bytes = encryptedName.LongLength + encryptedCategory.LongLength + encryptedText.LongLength;
        var totals = database.Query("SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM snippets WHERE (? IS NULL OR id<>?)", id?.ToString("D"), id?.ToString("D"))[0];
        if (Number(totals[0]) + 1 > settings.MaxSnippetCount || Number(totals[1]) + bytes > settings.MaxSnippetBytes) throw new ContentValidationException("常用语容量已满，请删除部分内容后重试。");
        var entryId = id ?? Guid.NewGuid(); var now = clock.GetUtcNow();
        database.Execute("INSERT INTO snippets(id,name,category,text,updated,bytes) VALUES(?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name,category=excluded.category,text=excluded.text,updated=excluded.updated,bytes=excluded.bytes",
            entryId, encryptedName, encryptedCategory, encryptedText, now.UtcTicks, bytes);
        return new(entryId, name, category, text, now);
    }
    private void ValidateSnippet(string name, string category, string text)
    {
        if (string.IsNullOrWhiteSpace(name) || category is null || string.IsNullOrWhiteSpace(text)) throw new ContentValidationException("常用语名称和正文不能为空。");
        if ((long)Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(category) + Encoding.UTF8.GetByteCount(text) > settings.MaxSnippetBytes)
            throw new ContentValidationException("常用语超过容量限制。");
    }
    public void DeleteSnippet(Guid id) => Locked(() => { database.Execute("DELETE FROM snippets WHERE id=?", id); snippetText.Remove(id); return 0; });

    public ImportResult ImportSnippets(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Locked(() =>
        {
            const int limit = 16 * 1024 * 1024;
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            while (true)
            {
                var count = json.Read(chunk, 0, (int)Math.Min(chunk.Length, limit + 1L - buffer.Length));
                if (count == 0) break; buffer.Write(chunk, 0, count);
                if (buffer.Length > limit) throw new ContentValidationException("导入文件超过 16 MiB。");
            }
            var entries = new List<(string Name, string Category, string Text)>();
            try
            {
                using var document = JsonDocument.Parse(buffer.ToArray()); var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
                    || !root.TryGetProperty("snippets", out var snippets) || snippets.ValueKind != JsonValueKind.Array) throw new ContentValidationException("常用语导入格式或版本无效。");
                foreach (var item in snippets.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new ContentValidationException("常用语字段格式无效。");
                    var entry = (name.GetString()!, category.GetString()!, text.GetString()!);
                    ValidateSnippet(entry.Item1, entry.Item2, entry.Item3); entries.Add(entry);
                }
            }
            catch (JsonException) { throw new ContentValidationException("常用语 JSON 格式无效。"); }
            return Transaction(() =>
            {
                var names = ReadSnippets().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); var renamed = 0;
                foreach (var (original, category, text) in entries)
                {
                    var name = original; var suffix = 2;
                    while (names.Contains(name)) name = original + " (" + suffix++ + ")";
                    if (name != original) renamed++;
                    SaveSnippetCore(name, category, text, null); names.Add(name);
                }
                return new ImportResult(entries.Count, renamed);
            });
        });
    }
    public void ExportSnippets(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Locked(() => { JsonSerializer.Serialize(json, new { version = 1, snippets = ReadSnippets().Select(item => new { name = item.Name, category = item.Category, text = item.Text }) }, new JsonSerializerOptions { WriteIndented = true }); return 0; });
    }
    private byte[] Encode(string text) => protector.Protect(Encoding.UTF8.GetBytes(text));
    private string Decode(byte[] payload) => Encoding.UTF8.GetString(protector.Unprotect(payload));
    private static long Number(object? value) => (long)value!;
    private static DateTimeOffset Date(long ticks) => new(ticks, TimeSpan.Zero);
    private static CaptureResult Result(CaptureStatus status, Guid? id = null) => new(status, id, status switch
    {
        CaptureStatus.Added => "已保存。", CaptureStatus.Duplicate => "已更新现有记录。", CaptureStatus.Empty => "内容为空或图片无效。",
        CaptureStatus.TooLarge => "内容超过单条容量限制。", _ => "历史容量已满，收藏记录受到保留。"
    });
    private static byte[] Fingerprint(params byte[][] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields) { BinaryPrimitives.WriteInt32LittleEndian(length, field.Length); hash.AppendData(length); hash.AppendData(field); }
        return hash.GetHashAndReset();
    }
    private void RetainHistoryCache()
    {
        if (!historyText.IsEmpty) historyText.Retain(database.Query("SELECT id FROM history WHERE kind=0").Select(row => Guid.Parse((string)row[0]!)).ToHashSet());
    }
    private void ClearCaches() { historyText.Clear(); snippetText.Clear(); }
    private T Locked<T>(Func<T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var currentVersion = database.Scalar("PRAGMA data_version");
            if (currentVersion != dataVersion) { ClearCaches(); dataVersion = currentVersion; }
            return action();
        }
    }
    private T Transaction<T>(Func<T> action)
    {
        database.Execute("BEGIN IMMEDIATE");
        try { var result = action(); database.Execute("COMMIT"); return result; }
        catch { try { database.Execute("ROLLBACK"); } catch (IOException) { } throw; }
    }
    public void Dispose() { lock (gate) { if (disposed) return; ClearCaches(); database.Dispose(); disposed = true; } }
}
