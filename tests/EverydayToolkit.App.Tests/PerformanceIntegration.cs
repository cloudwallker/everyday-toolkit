using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;

internal static class PerformanceIntegration
{
    public static int Run()
    {
        var failure = 0;
        var worker = new Thread(() =>
        {
            try { Measure(); }
            catch (Exception error) { failure = 1; Console.WriteLine("FAIL pressure: " + error.GetType().Name + " " + error.Message); }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        return failure;
    }

    private static void Measure()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "pressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var metrics = new Dictionary<string, object>();
        var settings = new ToolSettings();
        var database = Path.Combine(root, "representative.db");
        using (var store = new ContentStore(database, new DpapiProtector(), settings))
        {
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 1000; index++)
            {
                var text = "sample-" + index.ToString("D4") + " " + new string('x', 960);
                Require(store.Capture(new ClipboardContent(EntryKind.Text, text)).Status == CaptureStatus.Added, "1000 text captures");
                store.SaveSnippet("snippet-" + index.ToString("D4"), "synthetic", text);
            }
            metrics["populate1000HistoryAnd1000SnippetsMs"] = timer.Elapsed.TotalMilliseconds;
            Require(store.GetStatistics().HistoryCount == 1000 && store.GetStatistics().SnippetCount == 1000, "full count");
            metrics["search1000HistoryMs"] = Samples(() => Require(store.GetHistory(new HistoryQuery("sample-0999")).Count == 1, "search result"));
            metrics["search1000SnippetsMs"] = Samples(() => Require(store.GetSnippets("snippet-0999").Count == 1, "snippet search"));
        }
        var cold = Stopwatch.StartNew();
        using (var reopened = new ContentStore(database, new DpapiProtector(), settings))
        {
            Require(reopened.GetHistory().Count == 1000 && reopened.GetSnippets().Count == 1000, "cold reopen");
            metrics["coldReopenAndQueryBothMs"] = cold.Elapsed.TotalMilliseconds;
        }
        var codec = new ImageCodec();
        var random = new Random(42);
        var pixels = new byte[1400 * 1400 * 4];
        var encodeMs = new List<double>();
        var previewMs = new List<double>();
        var maxPng = 0L;
        var cache = new ImageCache();
        using (var mixed = new ContentStore(Path.Combine(root, "mixed.db"), new DpapiProtector(), settings))
        {
            for (var index = 0; index < 36; index++)
            {
                random.NextBytes(pixels);
                for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
                var bitmap = BitmapSource.Create(1400, 1400, 96, 96, PixelFormats.Bgra32, null, pixels, 5600);
                var timer = Stopwatch.StartNew();
                var content = codec.Encode(bitmap);
                encodeMs.Add(timer.Elapsed.TotalMilliseconds);
                maxPng = Math.Max(maxPng, content.Png!.LongLength);
                var result = mixed.Capture(content);
                Require(result.Status == CaptureStatus.Added, "mixed image capture");
                mixed.Capture(new ClipboardContent(EntryKind.Text, "mixed-" + index));
                Require(mixed.GetStatistics().HistoryBytes <= settings.HistoryMaxBytes, "200 MiB quota");
                timer.Restart();
                var full = cache.GetFullImage(result.EntryId!.Value, () => mixed.GetImage(result.EntryId.Value).Png);
                full.CopyPixels(new byte[1400 * 1400 * 4], 5600, 0);
                previewMs.Add(timer.Elapsed.TotalMilliseconds);
                cache.GetThumbnail(result.EntryId.Value, () => mixed.GetThumbnail(result.EntryId.Value));
            }
            var stats = mixed.GetStatistics();
            metrics["mixedHistoryCount"] = stats.HistoryCount;
            metrics["mixedHistoryMiB"] = stats.HistoryBytes / 1048576d;
            metrics["mixedDiskMiB"] = stats.DiskBytes / 1048576d;
            Require(stats.HistoryBytes >= 190 * 1048576L, "near capacity fixture");
            metrics["mixedTextSearchMs"] = Samples(() => Require(mixed.GetHistory(new HistoryQuery("mixed-35")).Count == 1, "mixed query"));
        }
        metrics["maxPngMiB"] = maxPng / 1048576d;
        metrics["encodeImageMs"] = Summarize(encodeMs);
        metrics["decryptDecodePreviewMs"] = Summarize(previewMs);
        for (var index = 0; index < 120; index++)
        {
            var small = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, new byte[256 * 256 * 4], 1024);
            var png = codec.Encode(small).Png!;
            cache.GetThumbnail(Guid.NewGuid(), () => png);
            Require(cache.ThumbnailCount <= 100 && cache.ThumbnailBytes <= 16 * 1048576L, "thumbnail bounds");
        }
        metrics["thumbnailPeakMiB"] = cache.ThumbnailBytes / 1048576d;
        metrics["thumbnailCountAtEnd"] = cache.ThumbnailCount;
        cache.Clear();
        Require(cache.ThumbnailCount == 0 && cache.ThumbnailBytes == 0, "cache clear");
        GC.Collect(); GC.WaitForPendingFinalizers();
        metrics["processWorkingSetMiB"] = Process.GetCurrentProcess().WorkingSet64 / 1048576d;
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "performance.json");
        File.WriteAllText(output, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(output));
        Console.WriteLine("PASS real DPAPI/SQLite full-count and mixed-capacity pressure, bounded image cache");
    }

    private static object Samples(Action action)
    {
        action();
        var values = new List<double>();
        for (var index = 0; index < 12; index++) { var timer = Stopwatch.StartNew(); action(); values.Add(timer.Elapsed.TotalMilliseconds); }
        return Summarize(values);
    }
    private static object Summarize(List<double> values)
    {
        values.Sort();
        return new { Median = values[values.Count / 2], P95 = values[(int)Math.Ceiling(values.Count * 0.95) - 1], Max = values[^1] };
    }
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
}
