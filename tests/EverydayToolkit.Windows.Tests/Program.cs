using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

internal static class Program
{
    static int failures;
    static string? runOnly;
    static bool delayListenerWorker;
    [STAThread] static int Main(string[] args)
    {
        if(args.Contains("--startup-registry")) return StartupRegistrationTests.Run();
        if(args.Contains("--pure")) return RegressionTests.Run();
        if(args.Contains("--listener")) runOnly="clipboard listener";
        if(args.Contains("--listener-worker-delay")) { runOnly="clipboard listener"; delayListenerWorker=true; }
        Run("DPAPI protects, persists and rejects corrupted bytes", () => {
            var protector = new DpapiProtector();
            byte[] plain = [1, 2, 3, 4, 5];
            var encrypted = protector.Protect(plain);
            Check(!encrypted.SequenceEqual(plain), "plaintext must be encrypted");
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".synthetic");
            try { File.WriteAllBytes(path, encrypted); Check(new DpapiProtector().Unprotect(File.ReadAllBytes(path)).SequenceEqual(plain), "persisted roundtrip"); }
            finally { File.Delete(path); }
            encrypted[encrypted.Length / 2] ^= 127;
            Throws(() => protector.Unprotect(encrypted));
        });
        Run("PNG preserves transparent BGRA pixels and freezes", () => {
            var codec = new ImageCodec();
            var content = codec.Encode(Pixels());
            Check(content.Kind == EntryKind.Image && content.Width == 2 && content.Height == 1, "image metadata");
            var decoded = codec.Decode(content.Png!);
            byte[] actual = new byte[8]; decoded.CopyPixels(actual, 8, 0);
            Check(actual.SequenceEqual(new byte[] { 30, 20, 10, 128, 0, 0, 0, 0 }), "alpha preserved");
            Check(decoded.IsFrozen && codec.Decode(content.Thumbnail!).IsFrozen, "thread safe images");
        });
        Run("PNG dimensions and encoded bytes are limited before decoding", () => {
            var codec = new ImageCodec();
            byte[] header = codec.Encode(Pixels()).Png!;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 50000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 50000);
            RejectImage(() => codec.Decode(header)); RejectImage(() => codec.Decode(new byte[8*1024*1024+1]));
            RejectImage(() => codec.Decode([1,2,3]));
        });
        Run("thumbnail stays within 256 pixels", () => {
            var codec = new ImageCodec();
            var image = BitmapSource.Create(512, 128, 96, 96, PixelFormats.Bgra32, null, new byte[512*128*4], 512*4);
            var content = codec.Encode(image); var thumb = codec.Decode(content.Thumbnail!);
            Check(thumb.PixelWidth == 256 && thumb.PixelHeight == 64, "scaled thumbnail");
        });
        Run("cache reuses, invalidates and evicts", () => {
            var png = new ImageCodec().Encode(Pixels()).Png!; var cache = new ImageCache(); var id = Guid.NewGuid(); int loads=0;
            cache.GetThumbnail(id, () => { loads++; return png; }); cache.GetThumbnail(id, () => { loads++; return png; });
            Check(loads == 1 && cache.ThumbnailCount == 1 && cache.ThumbnailBytes == 8, "cached pixel accounting");
            cache.Remove(id); cache.GetThumbnail(id, () => { loads++; return png; }); Check(loads==2, "remove reloads");
            for (int i=0;i<101;i++) cache.GetThumbnail(Guid.NewGuid(), () => png);
            Check(cache.ThumbnailCount == 100, "bounded thumbnails");
            var fullId=Guid.NewGuid(); cache.GetFullImage(fullId,()=>png); cache.GetFullImage(fullId,()=>{throw new Exception("cached full image");});
            cache.Clear(); Check(cache.ThumbnailCount==0 && cache.ThumbnailBytes==0,"clear releases");
        });
        var original = Clipboard.GetDataObject();
        try
        {
            var service = new ClipboardService(new ImageCodec());
            Run("real clipboard text write/read", () => { Check(service.WriteText("synthetic-test"), "write text"); Check(service.ReadCurrent()?.Text == "synthetic-test", "read text"); });
            Run("real clipboard PNG beats text", () => {
                var data=new DataObject(); data.SetText("synthetic-text"); data.SetData("PNG", new MemoryStream(new ImageCodec().Encode(Pixels()).Png!)); Clipboard.SetDataObject(data,true);
                Check(service.ReadCurrent()?.Kind == EntryKind.Image, "prefer PNG");
            });
            Run("real clipboard bitmap beats text", () => { var data=new DataObject(); data.SetText("synthetic-text"); data.SetImage(Pixels()); Clipboard.SetDataObject(data,true); Check(service.ReadCurrent()?.Kind==EntryKind.Image,"prefer bitmap"); });
            Run("real clipboard image write/read", () => { Check(service.WriteImage(new ImageCodec().Encode(Pixels()).Png!),"write image"); Check(service.ReadCurrent()?.Kind==EntryKind.Image,"read image"); });
            Run("async image clipboard write keeps dispatcher responsive during lock retries",()=> {
                var lockWindow=new Window(); var owner=new System.Windows.Interop.WindowInteropHelper(lockWindow).EnsureHandle();
                using var ready=new ManualResetEventSlim(); using var release=new ManualResetEventSlim(); bool locked=false;
                var lockTask=Task.Run(()=> { locked=OpenClipboard(owner); ready.Set(); if(locked) { try { release.Wait(TimeSpan.FromSeconds(3)); } finally { CloseClipboard(); } } }); ready.Wait();
                int ticks=0; var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)}; timer.Tick+=(_,_)=>ticks++;
                try {
                    Check(locked,"controlled lock"); timer.Start(); var pending=service.WriteImageAsync(new ImageCodec().Encode(Pixels()).Png!);
                    Pump(100); Check(ticks>=3 && !pending.IsCompleted,"dispatcher runs while clipboard writer waits");
                    var second=service.WriteImageAsync(new ImageCodec().Encode(Pixels()).Png!); Check(second.IsCompleted && !second.GetAwaiter().GetResult(),"busy writer rejects second allocation");
                    release.Set(); lockTask.GetAwaiter().GetResult();
                    for(int i=0;i<40 && !pending.IsCompleted;i++) Pump(50);
                    Check(pending.IsCompleted && pending.GetAwaiter().GetResult(),"async clipboard write completes");
                    var result=service.ReadCurrent(); Check(result?.Kind==EntryKind.Image && result.Width==2 && result.Height==1,"synthetic image roundtrip");
                    byte[] roundtrip=new byte[8]; new ImageCodec().Decode(result!.Png!).CopyPixels(roundtrip,8,0); Check(roundtrip.SequenceEqual(new byte[]{30,20,10,128,0,0,0,0}),"async alpha preserved");
                } finally { timer.Stop(); release.Set(); lockTask.GetAwaiter().GetResult(); lockWindow.Close(); }
            });
            Run("sensitive clipboard is not captured", () => { var data=new DataObject(); data.SetText("synthetic-sensitive"); data.SetData("ExcludeClipboardContentFromMonitorProcessing",new MemoryStream([1,0,0,0])); Clipboard.SetDataObject(data,true); Check(service.ReadCurrent()==null,"sensitive marker honored"); });
            Run("file drop does not load files", () => { var data=new DataObject(); data.SetData(DataFormats.FileDrop,new[] { "C:\\synthetic-not-a-file.png" }); Clipboard.SetDataObject(data,true); Check(service.ReadCurrent()==null,"file list ignored"); });
            Run("source process exclusion ignores case and exe", () => { Check(service.WriteText("synthetic-exclusion"),"write"); var name=Process.GetCurrentProcess().ProcessName; Check(service.ReadCurrent([name.ToUpperInvariant()+".EXE"])==null,"excluded source"); Check(service.ReadCurrent([name],false)?.Text=="synthetic-exclusion","explicit override"); });
            Run("native clipboard listener captures new events only when enabled", () => {
                var window=new Window { Width=1, Height=1, ShowInTaskbar=false, WindowStyle=WindowStyle.None };
                try {
                    window.Show(); using var monitor=new ClipboardMonitor(window,service); int captures=0; ClipboardContent? last=null;
                    monitor.Captured+=content=>{ captures++; last=content; };
                    service.WriteText("synthetic-disabled"); Pump(160); Check(captures==0,"disabled by default");
                    monitor.IsRecording=()=>true; Check(service.WriteText("synthetic-event"),"new text write succeeds"); WaitUntil(()=>last?.Text=="synthetic-event","new event captured"); Check(captures==1,"one captured text event");
                    using var workerDelay=delayListenerWorker ? new BackgroundWorkerDelay() : null;
                    CheckClipboardWrite(service.WriteImage(new ImageCodec().Encode(Pixels()).Png!),"new image write succeeds");
                    WaitUntil(()=>last?.Kind==EntryKind.Image && last.Width==2 && last.Height==1,"async image capture"); Check(captures==2,"one captured image event");
                    byte[] capturedPixels=new byte[8]; new ImageCodec().Decode(last!.Png!).CopyPixels(capturedPixels,8,0); Check(capturedPixels.SequenceEqual(new byte[]{30,20,10,128,0,0,0,0}),"listener image pixels preserved");
                    monitor.ExcludedApps=()=>[Process.GetCurrentProcess().ProcessName]; service.WriteText("synthetic-excluded-event"); Pump(200); Check(last?.Text!="synthetic-excluded-event","listener source exclusion");
                    int beforeDispose=captures; monitor.Dispose(); service.WriteText("synthetic-disposed"); Pump(160); Check(captures==beforeDispose,"dispose removes listener");
                } finally { window.Close(); }
            });
            Run("clipboard listener stops bounded retries and reports only category", () => {
                var window=new Window { Width=1,Height=1,ShowInTaskbar=false,WindowStyle=WindowStyle.None };
                try {
                    CheckClipboardWrite(service.WriteText("synthetic-lock-test"),"write before lock");
                    window.Show(); Pump(100); using var monitor=new ClipboardMonitor(window,service) { IsRecording=()=>true }; int captured=0; var statuses=new List<string>(); monitor.Captured+=_=>captured++; monitor.Status+=statuses.Add;
                    using var ready=new ManualResetEventSlim(); using var release=new ManualResetEventSlim(); bool locked=false;
                    var lockOwner=new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    var lockTask=Task.Run(()=> { locked=OpenClipboard(lockOwner); ready.Set(); if(locked) { try { release.Wait(TimeSpan.FromSeconds(2)); } finally { CloseClipboard(); } } });
                    ready.Wait();
                    try { Check(locked,"controlled clipboard lock"); PostMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle,0x031D,IntPtr.Zero,IntPtr.Zero); Pump(360); Check(captured==0 && statuses.Contains("ClipboardBusy"),"bounded retry reports busy"); Check(statuses.All(x=>!x.Contains("synthetic")),"no content in status"); }
                    finally { release.Set(); lockTask.GetAwaiter().GetResult(); }
                    Pump(100); Check(captured==0,"no retries after exhaustion");
                } finally { window.Close(); }
            });
        }
        finally {
            bool restored=ClipboardTestCleanup.TryRestore(()=> { if(original!=null) Clipboard.SetDataObject(original,true); else Clipboard.Clear(); },()=>Thread.Sleep(100),DiagnoseClipboardBusy);
            if(!restored) { failures++; Console.WriteLine("FAIL original clipboard restoration: ClipboardBusy budget exhausted"); }
        }
        Run("hotkey parses modifiers and preserves registration on conflict", () => {
            var a=new Window(); var b=new Window();
            try { using var first=new HotkeyManager(a); using var second=new HotkeyManager(b);
                Check(first.Register("Ctrl+Alt+Shift+F11",out _),"valid gesture registers");
                Check(!second.Register("Ctrl+Alt+Shift+F11",out var error) && error.Length>0,"conflict reported");
                Check(!first.Register("Ctrl+Ctrl+V",out _),"duplicate modifiers rejected");
                Check(!second.Register("Ctrl+Alt+Shift+F11",out _),"invalid update preserves old binding");
                first.Dispose(); Check(second.Register("Ctrl+Alt+Shift+F11",out _),"unregister releases binding");
            } finally { a.Close(); b.Close(); }
        });
        Run("paste without external target safely requests manual paste", () => { var service=new PasteService(); Check(service.PasteAsync().GetAwaiter().GetResult()!=null,"invalid target rejected"); });
        Run("encoding rejects decoded pixel memory above 64 MiB", () => {
            var source=BitmapSource.Create(4097,4096,96,96,PixelFormats.Bgra32,null,new byte[4097*4096*4],4097*4);
            RejectImage(()=>new ImageCodec().Encode(source));
        });
        Run("encoding rejects PNG larger than 8 MiB", () => {
            var pixels=new byte[2048*2048*4]; new Random(731).NextBytes(pixels);
            var source=BitmapSource.Create(2048,2048,96,96,PixelFormats.Bgra32,null,pixels,2048*4);
            RejectImage(()=>new ImageCodec().Encode(source));
        });
        if(runOnly==null && RegressionTests.Run()!=0) failures++;
        if(runOnly==null && StartupRegistrationTests.Run()!=0) failures++;
        Console.WriteLine($"Windows tests: {failures} failed"); return failures==0 ? 0 : 1;
    }
    static BitmapSource Pixels() { var image=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[] {30,20,10,128,0,0,0,0},8); image.Freeze(); return image; }
    static void Run(string name, Action action) { if(runOnly!=null && !name.Contains(runOnly)) return; try { action(); Console.WriteLine("PASS "+name); } catch(Exception ex) { failures++; Console.WriteLine("FAIL "+name+": "+ex.GetType().Name+" "+ex.Message); } }
    static void Check(bool value,string reason) { if(!value) throw new Exception(reason); }
    static void CheckClipboardWrite(bool value,string reason) { if(!value) DiagnoseClipboardBusy(); Check(value,reason); }
    static void DiagnoseClipboardBusy()
    {
        IntPtr holder=GetOpenClipboardWindow(); uint pid=0; if(holder!=IntPtr.Zero) GetWindowThreadProcessId(holder,out pid);
        Console.WriteLine($"ClipboardBusy diagnostic: holderWindowPresent={holder!=IntPtr.Zero} holderIdentityKnown={pid!=0} holderIsCurrentProcess={pid==(uint)Environment.ProcessId}");
    }
    static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("expected rejection"); }
    static void RejectImage(Action action) { try { action(); } catch(ContentValidationException) { return; } throw new Exception("expected image validation rejection"); }
    static void Pump(int milliseconds) { var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;}; timer.Start(); Dispatcher.PushFrame(frame); }
    static void WaitUntil(Func<bool> condition,string description,int timeoutMs=3000)
    {
        var wait=Stopwatch.StartNew();
        while(!condition()) { if(wait.ElapsedMilliseconds>=timeoutMs) throw new Exception(description+" timed out"); Pump(10); }
    }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    private sealed class BackgroundWorkerDelay : IDisposable
    {
        private readonly int maxWorkers,maxIo,minWorkers,minIo;
        private readonly ManualResetEventSlim release=new();
        private readonly Task blocked;
        private readonly DispatcherTimer timer;
        internal BackgroundWorkerDelay()
        {
            ThreadPool.GetMaxThreads(out maxWorkers,out maxIo); ThreadPool.GetMinThreads(out minWorkers,out minIo);
            ThreadPool.SetMinThreads(1,minIo); Check(ThreadPool.SetMaxThreads(1,maxIo),"controlled worker limit");
            using var ready=new ManualResetEventSlim();
            blocked=Task.Run(()=> { ready.Set(); release.Wait(); }); ready.Wait();
            timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(750)}; timer.Tick+=(_,_)=> { timer.Stop(); release.Set(); }; timer.Start();
        }
        public void Dispose()
        {
            timer.Stop(); release.Set(); blocked.GetAwaiter().GetResult();
            ThreadPool.SetMaxThreads(maxWorkers,maxIo); ThreadPool.SetMinThreads(minWorkers,minIo); release.Dispose();
        }
    }
}
