using EverydayToolkit.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace EverydayToolkit.Windows;
public sealed class ClipboardMonitor : IDisposable
{
    private readonly Window window;
    private readonly ClipboardService clipboard;
    private HwndSource? source;
    private IntPtr handle;
    private int generation;
    private bool disposed;
    public ClipboardMonitor(Window window, ClipboardService clipboard)
    {
        this.window=window; this.clipboard=clipboard;
        window.SourceInitialized+=Initialized; window.Closed+=Closed;
        if(new WindowInteropHelper(window).Handle != IntPtr.Zero) Attach();
    }
    public Func<bool> IsRecording { get; set; } = ()=>false;
    public Func<IReadOnlyList<string>> ExcludedApps { get; set; } = ()=>Array.Empty<string>();
    public event Action<ClipboardContent>? Captured;
    public event Action<string>? Status;
    private void Initialized(object? sender,EventArgs e)=>Attach();
    private void Closed(object? sender,EventArgs e)=>Dispose();
    private void Attach()
    {
        if(disposed || source!=null) return;
        handle=new WindowInteropHelper(window).Handle; source=HwndSource.FromHwnd(handle); source?.AddHook(Hook);
        if(!AddClipboardFormatListener(handle)) ReportStatus("ClipboardListenerRegistrationFailed");
    }
    private IntPtr Hook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x031D && !disposed) { int current=++generation; if(IsRecording()) window.Dispatcher.BeginInvoke(new Action(()=> { _=ReadAsync(current); })); }
        return IntPtr.Zero;
    }
    private async Task ReadAsync(int current)
    {
        for(int attempt=0;attempt<4;attempt++)
        {
            if(disposed || current!=generation) return;
            try
            {
                var snapshot=await window.Dispatcher.InvokeAsync(()=> !disposed && current==generation && IsRecording() ? clipboard.ReadSnapshot(ExcludedApps()) : null);
                if(snapshot==null) return;
                var content=await Task.Run(()=>clipboard.Convert(snapshot));
                await window.Dispatcher.InvokeAsync(()=> { if(!disposed && current==generation && IsRecording() && content!=null) Captured?.Invoke(content); });
                return;
            }
            catch(ExternalException) { if(attempt==3) { ReportStatus("ClipboardBusy"); return; } }
            catch(ContentValidationException) { ReportStatus("ClipboardContentLimitOrInvalidImage"); return; }
            catch(Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or NotSupportedException)
            { ReportStatus("ClipboardReadFailed"); return; }
            await Task.Delay(60);
        }
    }
    private void ReportStatus(string category)=>window.Dispatcher.BeginInvoke(new Action(()=> { if(!disposed) Status?.Invoke(category); }));
    public void Dispose()
    {
        if(disposed) return; disposed=true; generation++;
        window.SourceInitialized-=Initialized; window.Closed-=Closed;
        if(handle!=IntPtr.Zero) RemoveClipboardFormatListener(handle); source?.RemoveHook(Hook); source=null;
    }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
