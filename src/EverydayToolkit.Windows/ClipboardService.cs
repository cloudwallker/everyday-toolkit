using EverydayToolkit.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
namespace EverydayToolkit.Windows;

internal sealed record ClipboardSnapshot(string? Text = null, BitmapSource? Image = null, byte[]? Png = null);

public sealed class ClipboardService
{
    private readonly ImageCodec codec;
    private readonly IClipboardReader reader;
    public ClipboardService(ImageCodec codec) : this(codec,new NativeClipboardReader()) { }
    internal ClipboardService(ImageCodec codec,IClipboardReader reader) { this.codec=codec; this.reader=reader; }
    private static uint ownWriteSequence;
    private int asyncImageWrite;
    public ClipboardContent? ReadCurrent(IReadOnlyList<string>? exclusions = null, bool respectExclusions = true)
    {
        for(int attempt=0;attempt<4;attempt++)
        { try { return Convert(ReadSnapshot(exclusions,respectExclusions)); } catch(ExternalException) { if(attempt<3) Thread.Sleep(60); } }
        return null;
    }
    internal ClipboardContent? Convert(ClipboardSnapshot? snapshot)
    {
        if (snapshot?.Png != null) { try { return codec.Encode(codec.Decode(snapshot.Png)); } catch(ContentValidationException) { if(snapshot.Image==null && snapshot.Text==null) throw; } }
        if (snapshot?.Image != null) return codec.Encode(snapshot.Image);
        return snapshot?.Text == null ? null : new ClipboardContent(EntryKind.Text, Text: snapshot.Text);
    }
    internal ClipboardSnapshot? ReadSnapshot(IReadOnlyList<string>? exclusions, bool respectExclusions = true)
    {
        uint sequence=reader.Sequence;
        try
        {
            var snapshot=ReadSnapshotCore(exclusions,respectExclusions);
            if(reader.Sequence!=sequence) throw new ExternalException("ClipboardVersionChanged");
            return snapshot;
        }
        catch
        {
            if(reader.Sequence!=sequence) throw new ExternalException("ClipboardVersionChanged");
            throw;
        }
    }
    private ClipboardSnapshot? ReadSnapshotCore(IReadOnlyList<string>? exclusions, bool respectExclusions)
    {
        if (respectExclusions && reader.IsExcluded(exclusions)) return null;
        var data = reader.GetDataObject(); if(data==null || Sensitive(data)) return null;
        byte[]? png = null;
        if (data.GetDataPresent("PNG", false) || reader.FormatPresent("PNG"))
        {
            png = reader.ReadPng();
            if(png==null || png.Length==0) throw new ExternalException("ClipboardImagePending");
        }
        try
        {
          if (data.GetDataPresent(DataFormats.Bitmap) || reader.BitmapPresent)
          {
            object? bitmap = data.GetData(DataFormats.Bitmap);
            if (bitmap is BitmapSource source)
            {
                ImageCodec.ValidateDimensions(source.PixelWidth, source.PixelHeight);
                var clone = source.IsFrozen ? source : source.Clone(); clone.Freeze(); return new ClipboardSnapshot(Image: clone, Png:png);
            }
            if (bitmap is System.Drawing.Bitmap drawing)
            {
                ImageCodec.ValidateDimensions(drawing.Width, drawing.Height);
                // GetHbitmap strips alpha; copying BGRA bytes keeps the original transparency.
                using var normalized = drawing.Clone(new System.Drawing.Rectangle(0,0,drawing.Width,drawing.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bits=normalized.LockBits(new System.Drawing.Rectangle(0,0,normalized.Width,normalized.Height),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    var pixels=new byte[normalized.Width*normalized.Height*4];
                    for(int y=0;y<normalized.Height;y++) Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),pixels,y*normalized.Width*4,normalized.Width*4);
                    var image=BitmapSource.Create(normalized.Width,normalized.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,normalized.Width*4); image.Freeze(); return new ClipboardSnapshot(Image:image,Png:png);
                }
                finally { normalized.UnlockBits(bits); }
            }
            if(bitmap==null) throw new ExternalException("ClipboardImagePending");
          }
        }
        // PNG is independently validated in the worker. Failure of a secondary
        // representation cannot prevent that preferred payload from reaching it.
        catch(Exception ex) when (png!=null && ex is ExternalException or ContentValidationException or InvalidOperationException or ArgumentException or NotSupportedException or IOException) { }
        string? text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
        return png==null && text==null ? null : new ClipboardSnapshot(Text:text,Png:png);
    }
    private static byte[]? Bytes(object? value)
    {
        if(value is byte[] bytes) { if(bytes.Length>ImageCodec.MaxPngBytes) throw new ContentValidationException("PNG 超过限制。"); return bytes.ToArray(); }
        if(value is Stream stream)
        {
            long original=stream.CanSeek ? stream.Position : 0;
            try
            {
                if(stream.CanSeek) { if(stream.Length>ImageCodec.MaxPngBytes) throw new ContentValidationException("PNG 超过限制。"); stream.Position=0; }
                using var copy=new MemoryStream(); byte[] buffer=new byte[8192]; int read;
                while((read=stream.Read(buffer))>0) { if(copy.Length+read>ImageCodec.MaxPngBytes) throw new ContentValidationException("PNG 超过限制。"); copy.Write(buffer,0,read); }
                return copy.ToArray();
            }
            finally { if(stream.CanSeek) stream.Position=original; }
        }
        return null;
    }
    private static byte[]? ReadNativeBytes(string format)
    {
        if(!OpenClipboard(IntPtr.Zero)) throw new ExternalException("ClipboardBusy");
        try
        {
            var handle=GetClipboardData(RegisterClipboardFormat(format)); if(handle==IntPtr.Zero) throw new ExternalException("ClipboardImagePending");
            ulong size=GlobalSize(handle).ToUInt64(); if(size>ImageCodec.MaxPngBytes) throw new ContentValidationException("PNG 超过限制。");
            var pointer=GlobalLock(handle); if(pointer==IntPtr.Zero) throw new ExternalException("ClipboardImagePending");
            try { var bytes=new byte[(int)size]; Marshal.Copy(pointer,bytes,0,bytes.Length); return bytes; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    private bool Sensitive(System.Windows.IDataObject data)
    {
        if(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing",false) || reader.FormatPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
        foreach(var format in new[] {"CanIncludeInClipboardHistory","CanUploadToCloudClipboard"})
        { if(data.GetDataPresent(format,false) || reader.FormatPresent(format)) { var value=data.GetData(format,false); if(value==null) throw new ExternalException("ClipboardMarkerPending"); if(value is int i && i==0 || value is uint u && u==0 || value is bool b && !b) return true; var bytes=Bytes(value); if(bytes is {Length:>=4} && BitConverter.ToUInt32(bytes)==0) return true; } }
        return false;
    }
    private static bool IsExcluded(IReadOnlyList<string>? exclusions)
    {
        if(exclusions==null || exclusions.Count==0) return false;
        GetWindowThreadProcessId(GetClipboardOwner(),out uint pid);
        if(pid==0 && ownWriteSequence!=0 && GetClipboardSequenceNumber()==ownWriteSequence) pid=(uint)Environment.ProcessId;
        if(pid==0) return false;
        try { using var process=Process.GetProcessById((int)pid); return exclusions.Any(name=>string.Equals(NormalizeProcessName(name),process.ProcessName,StringComparison.OrdinalIgnoreCase)); }
        catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; }
    }
    private static string NormalizeProcessName(string name) { string value=Path.GetFileName(name.Trim()); return value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) ? value[..^4] : value; }
    public bool WriteText(string text) => Write(()=>Clipboard.SetText(text ?? ""));
    public bool WriteImage(byte[] png)
    {
        try { var image=codec.Decode(png); return Write(()=> { var data=new DataObject(); data.SetImage(image); data.SetData("PNG",new MemoryStream(png,false)); Clipboard.SetDataObject(data,true); }); }
        catch(ContentValidationException) { return false; }
    }
    public async Task<bool> WriteImageAsync(byte[] png)
    {
        if(Interlocked.CompareExchange(ref asyncImageWrite,1,0)!=0) return false;
        try
        {
            BitmapSource image;
            try { image=await Task.Run(()=>codec.Decode(png)).ConfigureAwait(false); }
            catch(ContentValidationException) { return false; }
            var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker=new Thread(()=>
            {
                try
                {
                    completion.SetResult(Write(()=>
                    {
                        var data=new DataObject(); data.SetImage(image); data.SetData("PNG",new MemoryStream(png,false)); Clipboard.SetDataObject(data,true);
                    }));
                }
                catch(Exception ex) { completion.SetException(ex); }
            }) { IsBackground=true,Name="EverydayToolkit clipboard image writer" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally { Volatile.Write(ref asyncImageWrite,0); }
    }
    private static bool Write(Action action)
    { for(int i=0;i<4;i++) { try { action(); ownWriteSequence=GetClipboardSequenceNumber(); return true; } catch(ExternalException) { if(i<3) Thread.Sleep(60); } } return false; }
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    private static bool NativeFormatPresent(string format)=>IsClipboardFormatAvailable(RegisterClipboardFormat(format));
    private sealed class NativeClipboardReader : IClipboardReader
    {
        public uint Sequence=>GetClipboardSequenceNumber();
        public bool IsExcluded(IReadOnlyList<string>? exclusions)=>ClipboardService.IsExcluded(exclusions);
        public System.Windows.IDataObject? GetDataObject()
        {
            if(!OpenClipboard(IntPtr.Zero)) throw new ExternalException("ClipboardBusy"); CloseClipboard(); return Clipboard.GetDataObject();
        }
        public bool FormatPresent(string format)=>NativeFormatPresent(format);
        public byte[]? ReadPng()=>ReadNativeBytes("PNG");
        public bool BitmapPresent=>IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
}
