namespace EverydayToolkit.Core;

public enum EntryKind { Text, Image }
public enum CaptureStatus { Added, Duplicate, Empty, TooLarge, QuotaBlocked }
public enum TextRule { Trim, RemoveBlankLines, DeduplicateLines, JoinLines, JoinWithSpaces, ToHalfWidth, ToFullWidth }
public record ClipboardContent(EntryKind Kind, string? Text = null, byte[]? Png = null, byte[]? Thumbnail = null, int Width = 0, int Height = 0);
public record HistoryEntry(Guid Id, EntryKind Kind, DateTimeOffset CreatedUtc, DateTimeOffset LastUsedUtc, bool IsFavorite, string? Text, int Width, int Height, long PayloadBytes);
public record HistoryQuery(string Search = "", EntryKind? Kind = null, bool FavoritesOnly = false, DateTimeOffset? Since = null);
public record SnippetEntry(Guid Id, string Name, string Category, string Text, DateTimeOffset UpdatedUtc);
public record ImagePayload(byte[] Png, byte[] Thumbnail, int Width, int Height);
public record CaptureResult(CaptureStatus Status, Guid? EntryId, string Message);
public record StoreStatistics(int HistoryCount, long HistoryBytes, int FavoriteCount, int SnippetCount, long SnippetBytes, long DiskBytes);
public record ImportResult(int ImportedCount, int RenamedCount);
public interface IContentProtector { byte[] Protect(byte[] plaintext); byte[] Unprotect(byte[] payload); }
public sealed class ContentValidationException : Exception { public ContentValidationException(string message) : base(message) { } }

public sealed class ToolSettings
{
    public bool RecordingEnabled { get; set; }
    public bool FirstRunComplete { get; set; }
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Alt+V";
    public int HistoryMaxCount { get; set; } = 1000;
    public int HistoryDays { get; set; } = 7;
    public long HistoryMaxBytes { get; set; } = 200 * 1024 * 1024L;
    public long MaxTextBytes { get; set; } = 256 * 1024L;
    public long MaxImagePngBytes { get; set; } = 8 * 1024 * 1024L;
    public long MaxImageDecodedBytes { get; set; } = 64 * 1024 * 1024L;
    public int MaxSnippetCount { get; set; } = 1000;
    public long MaxSnippetBytes { get; set; } = 10 * 1024 * 1024L;
    public List<string> ExcludedApps { get; set; } = [];
    public string? DataDirectory { get; set; }
}
