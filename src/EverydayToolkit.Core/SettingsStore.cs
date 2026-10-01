using System.Text.Json;
namespace EverydayToolkit.Core;
public sealed class SettingsStore
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public SettingsStore(string settingsPath) => path = Path.GetFullPath(settingsPath);
    public ToolSettings Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return new();
            try
            {
                using var input = File.OpenRead(path);
                if (input.Length > 1024 * 1024) throw new ContentValidationException("设置文件过大。");
                var settings = JsonSerializer.Deserialize<ToolSettings>(input, Options) ?? throw new ContentValidationException("设置文件无效。");
                Validate(settings); return settings;
            }
            catch (JsonException) { throw new ContentValidationException("设置文件格式无效，请修复后重试。"); }
        }
    }
    public void Save(ToolSettings settings)
    {
        lock (gate)
        {
            Validate(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(output, settings, Options); output.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal static void Validate(ToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Hotkey) || settings.ExcludedApps is null || settings.ExcludedApps.Any(string.IsNullOrWhiteSpace)
            || settings.HistoryMaxCount <= 0 || settings.HistoryDays <= 0 || settings.HistoryMaxBytes <= 0
            || settings.MaxTextBytes <= 0 || settings.MaxImagePngBytes <= 0 || settings.MaxImageDecodedBytes <= 0
            || settings.MaxSnippetCount <= 0 || settings.MaxSnippetBytes <= 0)
            throw new ContentValidationException("设置中的容量、保留期或快捷键无效。");
    }
}
