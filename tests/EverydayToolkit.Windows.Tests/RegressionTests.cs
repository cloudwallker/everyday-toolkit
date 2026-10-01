using EverydayToolkit.Core;
using EverydayToolkit.Windows;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IDataObject=System.Windows.IDataObject;
using DataObject=System.Windows.DataObject;
using DataFormats=System.Windows.DataFormats;

internal static class RegressionTests
{
    private static int failures;
    internal static int Run()
    {
        Case("clipboard restoration retries transient busy then succeeds",()=> {
            int attempts=0,pauses=0,busy=0;
            bool restored=ClipboardTestCleanup.TryRestore(()=> { attempts++; if(attempts<3) throw new ExternalException("synthetic busy"); },()=>pauses++,()=>busy++);
            Check(restored && attempts==3 && pauses==2 && busy==2,"restore reattempts after transient contention");
        });
        Case("clipboard restoration stops at bounded budget and reports failure",()=> {
            int attempts=0,pauses=0,busy=0;
            bool restored=ClipboardTestCleanup.TryRestore(()=> { attempts++; throw new ExternalException("synthetic sustained busy"); },()=>pauses++,()=>busy++);
            Check(!restored && attempts==10 && pauses==9 && busy==10,"restore must stop without crashing or claiming success");
        });
        Case("paste keeps original A when capture changes to B during modifier wait",()=> {
            var platform=new PastePlatform(); var service=new PasteService(platform); service.CaptureTarget();
            platform.OnFirstModifierCheck=()=> { platform.Foreground=platform.B; service.CaptureTarget(); };
            Check(service.PasteAsync().GetAwaiter().GetResult()==null && platform.PastedTo.SequenceEqual(new[]{platform.A}),"in-flight paste must target original A");
        });
        Case("paste degrades when original A disappears during target recapture",()=> {
            var platform=new PastePlatform(); var service=new PasteService(platform); service.CaptureTarget();
            platform.OnFirstModifierCheck=()=> { platform.AExists=false; platform.Foreground=platform.B; service.CaptureTarget(); };
            Check(service.PasteAsync().GetAwaiter().GetResult()!=null && platform.PastedTo.Count==0,"lost original A cannot silently switch to B");
        });
        Case("explicit target survives new capture before clipboard preparation finishes",()=> {
            var platform=new PastePlatform(); var service=new PasteService(platform); service.CaptureTarget(); var snapshot=service.GetTargetSnapshot();
            platform.Foreground=platform.B; service.CaptureTarget();
            Check(service.PasteAsync(snapshot).GetAwaiter().GetResult()==null && platform.PastedTo.SequenceEqual(new[]{platform.A}),"snapshot taken before preparation remains the destination");
        });
        Case("paste keeps A after capture changes during foreground settle delay",()=> {
            var platform=new PastePlatform(); var service=new PasteService(platform); service.CaptureTarget();
            platform.OnRestore=()=> { platform.Foreground=platform.B; service.CaptureTarget(); platform.Foreground=platform.A; };
            Check(service.PasteAsync().GetAwaiter().GetResult()==null && platform.PastedTo.SequenceEqual(new[]{platform.A}),"settle delay must validate the operation snapshot");
        });
        Case("startup command keeps custom directory containing spaces",()=> {
            string command=StartupRegistration.BuildCommand(@"C:\Toolkit folder\EverydayToolkit.App.exe",@"D:\My data");
            Check(Parse(command).SequenceEqual(new[] {@"C:\Toolkit folder\EverydayToolkit.App.exe","--tray","--data-dir",@"D:\My data"}),"startup keeps selected database path");
        });
        Case("startup command escapes trailing separators",()=> {
            string command=StartupRegistration.BuildCommand(@"C:\Toolkit\EverydayToolkit.App.exe",@"D:\My data\\");
            Check(Parse(command).SequenceEqual(new[] {@"C:\Toolkit\EverydayToolkit.App.exe","--tray","--data-dir",@"D:\My data\\"}),"trailing backslashes survive parsing");
        });
        Case("startup command escapes quotes without argument injection",()=> {
            string directory="D:\\synthetic\\\" --other-option ";
            Check(Parse(StartupRegistration.BuildCommand(@"C:\Toolkit\EverydayToolkit.App.exe",directory)).SequenceEqual(new[] {@"C:\Toolkit\EverydayToolkit.App.exe","--tray","--data-dir",directory}),"untrusted path remains one argument");
        });
        Case("startup without custom path preserves default command",()=>Check(Parse(StartupRegistration.BuildCommand(@"C:\Toolkit\EverydayToolkit.App.exe",null)).SequenceEqual(new[] {@"C:\Toolkit\EverydayToolkit.App.exe","--tray"}),"default startup"));
        Case("async invalid PNG rejects before touching clipboard",()=>Check(!new ClipboardService(new ImageCodec()).WriteImageAsync([1,2,3]).GetAwaiter().GetResult(),"invalid PNG returns false"));
        Case("16-bit RGBA source above 64 MiB is rejected",()=>Reject(()=>new ImageCodec().Decode(Png(3072,3072,PixelFormats.Rgba64))));
        Case("16-bit RGB source above 64 MiB is rejected",()=>Reject(()=>new ImageCodec().Decode(Png(4096,3072,PixelFormats.Rgb48))));
        Case("16-bit RGBA at 64 MiB decodes to independent BGRA",()=> {
            var image=new ImageCodec().Decode(Png(2048,4096,PixelFormats.Rgba64));
            Check(image.IsFrozen && image.Format==PixelFormats.Bgra32,"normalized frozen image");
            Check(image is not FormatConvertedBitmap,"does not retain the 64-bit source frame");
        });
        Case("16-bit RGB below 64 MiB decodes independently",()=> {
            var image=new ImageCodec().Decode(Png(3072,3072,PixelFormats.Rgb48));
            Check(image.IsFrozen && image.Format==PixelFormats.Bgra32 && image is not FormatConvertedBitmap,"independent BGRA pixels");
        });
        Case("PNG read race rechecks sensitive replacement",()=>Race(true));
        Case("PNG read race rechecks excluded replacement",()=>Race(false));
        foreach(var kind in new[] {"null","failure","oversized"})
            Case("valid PNG survives secondary bitmap "+kind,()=> {
                byte[] png=SmallPng(); var data=new BitmapData(png,()=>kind switch {
                    "failure"=>throw new ExternalException("synthetic secondary failure"),
                    "oversized"=>BitmapSource.Create(4097,4096,96,96,PixelFormats.Bgra32,null,new byte[4097*4096*4],4097*4),
                    _=>null });
                var reader=new Reader {Data=data,Png=png};
                var result=new ClipboardService(new ImageCodec(),reader).ReadCurrent();
                Check(result?.Kind==EntryKind.Image && result.Width==2 && result.Height==1,"valid primary PNG is captured");
            });
        Case("invalid PNG falls back to valid bitmap",()=> {
            var bitmap=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{1,2,3,255},4);
            var reader=new Reader {Data=new BitmapData([1,2,3],()=>bitmap),Png=[1,2,3]};
            var result=new ClipboardService(new ImageCodec(),reader).ReadCurrent();
            Check(result?.Kind==EntryKind.Image && result.Width==1,"bitmap fallback");
        });
        Console.WriteLine($"Pure Windows regression tests: {failures} failed"); return failures==0 ? 0 : 1;
    }
    private static void Race(bool sensitive)
    {
        var initial=new DataObject(); initial.SetData("PNG",SmallPng());
        var replacement=new DataObject(); replacement.SetData("PNG",SmallPng());
        if(sensitive) replacement.SetData("ExcludeClipboardContentFromMonitorProcessing",new byte[]{1,0,0,0});
        var reader=new Reader {Data=initial,Png=SmallPng()}; bool switched=false;
        reader.ReadAction=()=> { if(!switched) { switched=true; reader.Version++; reader.Data=replacement; reader.Excluded=!sensitive; } return reader.Png; };
        var result=new ClipboardService(new ImageCodec(),reader).ReadCurrent(["synthetic-excluded.exe"]);
        Check(result==null,"replacement must not escape stale source and marker checks");
    }
    private static byte[] SmallPng()=>Png(2,1,PixelFormats.Bgra32);
    private static byte[] Png(int width,int height,PixelFormat format)
    {
        int stride=width*(format.BitsPerPixel/8); var image=BitmapSource.Create(width,height,96,96,format,null,new byte[stride*height],stride);
        using var stream=new MemoryStream(); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); return stream.ToArray();
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static string[] Parse(string command)
    {
        IntPtr pointer=CommandLineToArgvW(command,out int count); if(pointer==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { var args=new string[count]; for(int i=0;i<count;i++) args[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size))!; return args; }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static void Reject(Action action) { try {action();} catch(ContentValidationException) {return;} throw new Exception("decoded source should exceed limit"); }
    private static void Case(string name,Action action) { try { action(); Console.WriteLine("PASS "+name); } catch(Exception ex) { failures++; Console.WriteLine("FAIL "+name+": "+ex.GetType().Name+" "+ex.Message); } }
    private sealed class Reader : IClipboardReader
    {
        internal uint Version=1; internal IDataObject? Data; internal byte[]? Png; internal bool Excluded; internal Func<byte[]?>? ReadAction;
        public uint Sequence=>Version;
        public bool IsExcluded(IReadOnlyList<string>? exclusions)=>Excluded && exclusions?.Count>0;
        public IDataObject? GetDataObject()=>Data;
        public bool FormatPresent(string format)=>false;
        public byte[]? ReadPng()=>ReadAction==null ? Png : ReadAction();
        public bool BitmapPresent=>false;
    }
    private sealed class PastePlatform : IPastePlatform
    {
        internal readonly IntPtr A=new(101),B=new(102); internal IntPtr Foreground=new(101); internal bool AExists=true;
        internal Action? OnFirstModifierCheck,OnRestore; internal readonly List<IntPtr> PastedTo=[]; private bool checkedModifiers;
        public IntPtr ForegroundWindow=>Foreground;
        public (uint ProcessId,uint ThreadId) GetIdentity(IntPtr window)=>(window==A ? (uint)Environment.ProcessId+1000 : (uint)Environment.ProcessId+2000,window==A ? 11u : 22u);
        public bool IsWindow(IntPtr window)=>window==A ? AExists : window==B;
        public bool ModifiersDown() { if(checkedModifiers) return false; checkedModifiers=true; OnFirstModifierCheck?.Invoke(); return true; }
        public bool RestoreForeground(IntPtr window) { Foreground=window; OnRestore?.Invoke(); return IsWindow(window); }
        public bool SendPaste() { PastedTo.Add(Foreground); return true; }
        public void ReleasePasteKeys() { }
    }
    private sealed class BitmapData : IDataObject
    {
        private readonly DataObject data=new(); private readonly Func<object?> bitmap;
        internal BitmapData(byte[] png,Func<object?> bitmap) { this.bitmap=bitmap; data.SetData("PNG",png); data.SetData(DataFormats.Bitmap,"synthetic-bitmap-provider"); }
        public object? GetData(string format,bool autoConvert)=>format==DataFormats.Bitmap ? bitmap() : data.GetData(format,autoConvert);
        public object? GetData(string format)=>GetData(format,true);
        public object? GetData(Type format)=>data.GetData(format);
        public bool GetDataPresent(string format,bool autoConvert)=>data.GetDataPresent(format,autoConvert);
        public bool GetDataPresent(string format)=>data.GetDataPresent(format);
        public bool GetDataPresent(Type format)=>data.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert)=>data.GetFormats(autoConvert);
        public string[] GetFormats()=>data.GetFormats();
        public void SetData(string format,object value,bool autoConvert)=>data.SetData(format,value,autoConvert);
        public void SetData(string format,object value)=>data.SetData(format,value);
        public void SetData(Type format,object value)=>data.SetData(format,value);
        public void SetData(object value)=>data.SetData(value);
    }
}
