using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using Clipboard = System.Windows.Clipboard;

// A separate WPF process receives actual Ctrl+V input. These tests never start
// the product's clipboard monitor, inspect an unrelated application, or write
// arbitrary clipboard contents to their artifacts.
internal static class DesktopIntegration
{
    public static int Run()
    {
        var failures = 0;
        var thread = new Thread(() => failures = RunOnSta());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine($"Desktop paste integration tests: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    public static int RunTarget(string directory)
    {
        var result = 0;
        var thread = new Thread(() =>
        {
            try { RunTargetOnSta(ArtifactsDirectory(directory)); }
            catch (Exception error)
            {
                // Only the exception type is safe to report from this fixture.
                Console.Error.WriteLine("Desktop target failed: " + error.GetType().Name);
                result = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int RunOnSta()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var failures = 0;
        var directory = ArtifactsDirectory(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", "desktop-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var parent = new Window
        {
            Title = "Everyday Toolkit 桌面验证（合成数据）", Width = 470, Height = 180,
            Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new TextBox
            {
                Text = "测试将在独立收件窗口验证粘贴。\n结束后恢复原剪贴板。",
                IsReadOnly = true, AcceptsReturn = true, Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap
            }
        };
        Process? child = null;
        System.Windows.IDataObject? previousClipboard = null;
        var clipboardCaptured = false;
        var initialForeground = GetForegroundWindow();
        GetWindowThreadProcessId(initialForeground, out var initialForegroundPid);
        var shiftInjected = false;
        var stage = "setup";
        try
        {
            previousClipboard = ClipboardRetry(() => Clipboard.GetDataObject());
            clipboardCaptured = true;
            RequireNoModifiers();
            parent.Show();
            var parentHandle = new WindowInteropHelper(parent).Handle;
            stage = "initial-parent-focus";
            Focus(parentHandle, "parent");
            child = StartTarget(directory);
            Wait(() => File.Exists(Path.Combine(directory, "ready.json")), child, 8000);
            var ready = ReadJson<Ready>(Path.Combine(directory, "ready.json"));
            var targetHandle = new IntPtr(ready.Handle);
            Equal(child.Id, ready.ProcessId);
            if (ready.ProcessId == Environment.ProcessId) throw new InvalidOperationException("Target must be an external process");
            GetWindowThreadProcessId(targetHandle, out var targetPid);
            Equal((uint)child.Id, targetPid);
            // Hidden process startup may override the first WPF ShowWindow call.
            // Display only this fixture's PID-validated receiver, without activation.
            stage = "target-visibility";
            Console.WriteLine("DESKTOP target-ready visible=" + IsWindowVisible(targetHandle));
            if (!IsWindowVisible(targetHandle)) ShowWindow(targetHandle, 4 /* SW_SHOWNOACTIVATE */);
            Wait(() => IsWindowVisible(targetHandle), child, 1000);
            Console.WriteLine("DESKTOP target-shown visible=" + IsWindowVisible(targetHandle));
            var codec = new ImageCodec();
            var clipboard = new ClipboardService(codec);

            void Check(string name, Action action)
            {
                stage = "start";
                try { action(); Console.WriteLine("PASS " + name); }
                catch (Exception error)
                {
                    failures++;
                    // No clipboard content or process output is printed on failure.
                    Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " stage=" + stage + " " + SafeMessage(error));
                }
            }

            PasteService CaptureExternalTarget()
            {
                RequireNoModifiers();
                stage = "target-focus";
                Focus(targetHandle, "target");
                var paste = new PasteService();
                paste.CaptureTarget();
                stage = "parent-focus";
                Focus(parentHandle, "parent");
                stage = "clipboard-and-paste";
                return paste;
            }

            Check("Desktop_TextPasteReachesExternalProcess", () =>
            {
                var text = "跨进程粘贴验证：办公样例\r\n第二行 😀 " + Guid.NewGuid().ToString("N");
                var expected = Expectation.ForText(text);
                WriteJson(Path.Combine(directory, "expected.json"), expected);
                var paste = CaptureExternalTarget();
                Equal(true, clipboard.WriteText(text));
                Equal<string?>(null, Complete(paste.PasteAsync(), child));
                stage = "text-receipt";
                var receipt = ReceiptFor(directory, expected.Id, child);
                Equal(child.Id, receipt.ProcessId);
                Equal("text", receipt.Kind);
                Equal<string?>(null, receipt.Error);
                Equal(text, receipt.Text);
            });

            Check("Desktop_ImagePasteKeepsDimensionsTransparencyAndPixels", () =>
            {
                var pixels = ImagePixels();
                var image = BitmapSource.Create(37, 23, 96, 96, PixelFormats.Bgra32, null, pixels, 37 * 4);
                var content = codec.Encode(image);
                var expected = new Expectation(Guid.NewGuid().ToString("N"), "image", null, Convert.ToHexString(SHA256.HashData(pixels)), 37, 23);
                WriteJson(Path.Combine(directory, "expected.json"), expected);
                var paste = CaptureExternalTarget();
                Equal(true, clipboard.WriteImage(content.Png!));
                Equal<string?>(null, Complete(paste.PasteAsync(), child));
                stage = "image-receipt";
                var receipt = ReceiptFor(directory, expected.Id, child);
                Equal(child.Id, receipt.ProcessId);
                Equal("image", receipt.Kind);
                Equal<string?>(null, receipt.Error);
                Equal(true, receipt.HasPng);
                Equal(37, receipt.Width);
                Equal(23, receipt.Height);
                Equal("140,60,45,120", receipt.FirstPixel);
                Equal(expected.PixelHash, receipt.PixelHash);
                if (!File.Exists(Path.Combine(directory, "received-" + expected.Id + ".png"))) throw new InvalidOperationException("Validated image was not rendered by receiver");
            });

            Check("Desktop_PasteWaitsForModifierRelease", () =>
            {
                var expected = Expectation.ForText("等待修饰键释放的合成文字 " + Guid.NewGuid().ToString("N"));
                WriteJson(Path.Combine(directory, "expected.json"), expected);
                var paste = CaptureExternalTarget();
                Equal(true, clipboard.WriteText(expected.Text!));
                RequireForeground(parentHandle);
                try
                {
                    Shift(false);
                    shiftInjected = true;
                    Wait(() => (GetAsyncKeyState(0x10) & 0x8000) != 0, child, 1000);
                    var pending = paste.PasteAsync();
                    PumpFor(150);
                    if (pending.IsCompleted || File.Exists(ReceiptPath(directory, expected.Id))) throw new InvalidOperationException("Paste did not wait for a held modifier");
                    RequireForeground(parentHandle);
                    Shift(true);
                    shiftInjected = false;
                    Equal<string?>(null, Complete(pending, child));
                    stage = "released-modifier-receipt";
                    var receipt = ReceiptFor(directory, expected.Id, child);
                    Equal<string?>(null, receipt.Error);
                    Equal(expected.Text, receipt.Text);
                }
                finally
                {
                    if (shiftInjected) { Shift(true); shiftInjected = false; }
                }
            });

            Check("Desktop_HeldModifierFallsBackWithoutSendingPaste", () =>
            {
                var expected = Expectation.ForText("超时后手动粘贴的合成文字 " + Guid.NewGuid().ToString("N"));
                WriteJson(Path.Combine(directory, "expected.json"), expected);
                var paste = CaptureExternalTarget();
                Equal(true, clipboard.WriteText(expected.Text!));
                RequireForeground(parentHandle);
                try
                {
                    Shift(false);
                    shiftInjected = true;
                    Wait(() => (GetAsyncKeyState(0x10) & 0x8000) != 0, child, 1000);
                    var watch = Stopwatch.StartNew();
                    var message = Complete(paste.PasteAsync(), child);
                    if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Held modifier did not return a manual paste fallback");
                    if (watch.ElapsedMilliseconds < 850 || watch.ElapsedMilliseconds > 3000) throw new InvalidOperationException("Modifier wait was outside its bounded timeout");
                    PumpFor(100);
                    Equal(false, File.Exists(ReceiptPath(directory, expected.Id)));
                    RequireForeground(parentHandle);
                }
                finally
                {
                    if (shiftInjected) { Shift(true); shiftInjected = false; }
                }
                Equal(expected.Text, ClipboardRetry(() => Clipboard.GetText()));
            });

            Check("Desktop_ClosedTargetFallsBackAndKeepsClipboard", () =>
            {
                var expected = Expectation.ForText("原窗口已关闭的合成文字 " + Guid.NewGuid().ToString("N"));
                WriteJson(Path.Combine(directory, "expected.json"), expected);
                var paste = CaptureExternalTarget();
                Equal(true, clipboard.WriteText(expected.Text!));
                File.WriteAllText(Path.Combine(directory, "exit.signal"), "exit");
                Wait(() => child.HasExited, null, 4000);
                Equal(0, child.ExitCode);
                if (string.IsNullOrWhiteSpace(Complete(paste.PasteAsync(), null))) throw new InvalidOperationException("Closed target did not return a manual paste fallback");
                Equal(expected.Text, ClipboardRetry(() => Clipboard.GetText()));
                Equal(false, File.Exists(ReceiptPath(directory, expected.Id)));
            });
        }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine("FAIL Desktop_Setup: " + error.GetType().Name + " stage=" + stage + " " + SafeMessage(error));
        }
        finally
        {
            if (shiftInjected)
            {
                try { Shift(true); }
                catch { failures++; Console.WriteLine("FAIL Desktop_ModifierRelease"); }
            }
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        File.WriteAllText(Path.Combine(directory, "exit.signal"), "exit");
                        if (!child.WaitForExit(4000))
                        {
                            child.Kill();
                            child.WaitForExit(2000);
                            failures++;
                            Console.WriteLine("FAIL Desktop_TargetDidNotExitNormally");
                        }
                    }
                }
                catch { failures++; Console.WriteLine("FAIL Desktop_TargetCleanup"); }
                finally { child.Dispose(); }
            }
            if (clipboardCaptured)
            {
                try
                {
                    ClipboardRetry(() =>
                    {
                        if (previousClipboard is null) Clipboard.Clear();
                        else Clipboard.SetDataObject(previousClipboard, true);
                        return true;
                    });
                }
                catch { failures++; Console.WriteLine("FAIL Desktop_ClipboardRestore"); }
            }
            parent.Close();
            app.Shutdown();
            GetWindowThreadProcessId(initialForeground, out var currentPid);
            if (IsWindow(initialForeground) && currentPid == initialForegroundPid) SetForegroundWindow(initialForeground);
        }
        return failures;
    }

    private static void RunTargetOnSta(string directory)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var preview = new Image { Stretch = Stretch.None, Margin = new Thickness(8), MaxHeight = 90 };
        var label = new TextBlock { Text = "仅接收本测试的合成内容", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Focusable = true };
        panel.Children.Add(label);
        panel.Children.Add(preview);
        var window = new Window
        {
            Title = "Everyday Toolkit 外部收件测试（合成数据）", Width = 470, Height = 220,
            Left = 80, Top = 285, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = panel
        };
        var codec = new ImageCodec();
        var clipboard = new ClipboardService(codec);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            var expected = ReadJson<Expectation>(Path.Combine(directory, "expected.json"));
            var receipt = new Receipt(Environment.ProcessId, expected.Kind);
            try
            {
                // Observed clipboard content is never persisted unless it is
                // exactly the approved synthetic payload from expected.json.
                var content = clipboard.ReadCurrent(respectExclusions: false);
                if (expected.Kind == "text" && content?.Kind == EntryKind.Text && content.Text == expected.Text)
                {
                    label.Text = content.Text;
                    receipt = receipt with { Text = content.Text };
                }
                else if (expected.Kind == "image" && content?.Kind == EntryKind.Image)
                {
                    var image = codec.Decode(content.Png!);
                    var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
                    image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                    var hash = Convert.ToHexString(SHA256.HashData(pixels));
                    if (image.PixelWidth != expected.Width || image.PixelHeight != expected.Height || hash != expected.PixelHash)
                        receipt = receipt with { Error = "UnexpectedClipboard" };
                    else
                    {
                        preview.Source = image;
                        receipt = receipt with
                        {
                            HasPng = Clipboard.ContainsData("PNG"), Width = image.PixelWidth, Height = image.PixelHeight,
                            FirstPixel = string.Join(",", pixels[0], pixels[1], pixels[2], pixels[3]), PixelHash = hash
                        };
                        window.UpdateLayout();
                        var rendered = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                        var surface = new DrawingVisual();
                        using (var drawing = surface.RenderOpen()) drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                        rendered.Render(surface);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(rendered));
                        using var output = File.Create(Path.Combine(directory, "received-" + expected.Id + ".png"));
                        encoder.Save(output);
                    }
                }
                else receipt = receipt with { Error = "UnexpectedClipboard" };
            }
            catch (Exception error) { receipt = receipt with { Error = error.GetType().Name }; }
            WriteJson(ReceiptPath(directory, expected.Id), receipt);
        };
        var watch = Stopwatch.StartNew();
        var control = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        control.Tick += (_, _) =>
        {
            if (File.Exists(Path.Combine(directory, "exit.signal")) || watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                control.Stop();
                window.Close();
            }
        };
        window.Loaded += (_, _) =>
        {
            panel.Focus();
            WriteJson(Path.Combine(directory, "ready.json"), new Ready(Environment.ProcessId, new WindowInteropHelper(window).Handle.ToInt64()));
            control.Start();
        };
        app.Run(window);
    }

    private static Process StartTarget(string directory)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Current process host is unavailable");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Directory.GetCurrentDirectory(), RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--desktop-target");
        start.ArgumentList.Add(directory);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start controlled receiver");
    }

    private static string ArtifactsDirectory(string path)
    {
        var artifacts = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts")) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Desktop fixture directory must remain within project artifacts");
        return resolved;
    }

    private static Receipt ReceiptFor(string directory, string id, Process child)
    {
        var path = ReceiptPath(directory, id);
        Wait(() => File.Exists(path), child, 4000);
        return ReadJson<Receipt>(path);
    }
    private static string ReceiptPath(string directory, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid fixture receipt identifier");
        return Path.Combine(directory, "receipt-" + id + ".json");
    }
    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Missing fixture JSON value");
    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, true);
    }
    private static T ClipboardRetry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 3) { Thread.Sleep(60); }
        }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Controlled fixture assertion failed");
    }
    private static string SafeMessage(Exception error) => error is TimeoutException ? "Controlled desktop action timed out" : "Controlled desktop assertion or setup failed";
    private static T Complete<T>(Task<T> task, Process? child)
    {
        Wait(() => task.IsCompleted, child, 4000);
        return task.GetAwaiter().GetResult();
    }
    private static void Wait(Func<bool> predicate, Process? child, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (child?.HasExited == true) throw new InvalidOperationException("Controlled receiver exited unexpectedly");
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException();
            PumpFor(20);
        }
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Focus(IntPtr handle, string role)
    {
        if (!IsWindow(handle)) throw new InvalidOperationException("Controlled fixture window no longer exists");
        var accepted = SetForegroundWindow(handle);
        Console.WriteLine("DESKTOP focus role=" + role + " visible=" + IsWindowVisible(handle) + " accepted=" + accepted + " foreground=" + (GetForegroundWindow() == handle));
        Wait(() => GetForegroundWindow() == handle, null, 2000);
    }
    private static void RequireForeground(IntPtr handle)
    {
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("Controlled window lost foreground; input will not be injected");
    }
    private static void RequireNoModifiers()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("A physical modifier is held; desktop test cannot run");
    }
    private static byte[] ImagePixels()
    {
        var pixels = new byte[37 * 23 * 4];
        for (var y = 0; y < 23; y++) for (var x = 0; x < 37; x++)
        {
            var offset = (y * 37 + x) * 4;
            pixels[offset] = (byte)(140 + x); pixels[offset + 1] = (byte)(60 + y);
            pixels[offset + 2] = 45; pixels[offset + 3] = (byte)(x < 7 ? 120 : 255);
        }
        return pixels;
    }
    private static void Shift(bool up)
    {
        var input = new[] { new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = 0x10, Flags = up ? 2u : 0u } } } };
        if (SendInput(1, input, Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Controlled modifier injection failed");
    }
    private sealed record Ready(int ProcessId, long Handle);
    private sealed record Expectation(string Id, string Kind, string? Text, string? PixelHash, int Width, int Height)
    {
        public static Expectation ForText(string text) => new(Guid.NewGuid().ToString("N"), "text", text, null, 0, 0);
    }
    private sealed record Receipt(int ProcessId, string Kind, string? Error = null, string? Text = null, bool HasPng = false, int Width = 0, int Height = 0, string? FirstPixel = null, string? PixelHash = null);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
