using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
namespace EverydayToolkit.Windows;
public sealed class HotkeyManager : IDisposable
{
    private readonly Window window;
    private readonly HwndSource source;
    private readonly IntPtr handle;
    private static int nextId=1200;
    private int? id;
    private (uint Modifiers,uint Key)? binding;
    private bool disposed;
    public HotkeyManager(Window window)
    {
        this.window=window; handle=new WindowInteropHelper(window).EnsureHandle(); source=HwndSource.FromHwnd(handle)!; source.AddHook(Hook); window.Closed+=Closed;
    }
    public bool Register(string gesture,out string error)
    {
        error="";
        if(disposed) { error="热键管理器已关闭。"; return false; }
        if(!TryParse(gesture,out var value)) { error="快捷键格式无效，请使用 Ctrl+Alt+V 等格式。"; return false; }
        if(binding==value) return true;
        int replacement=Interlocked.Increment(ref nextId);
        if(!RegisterHotKey(handle,replacement,value.Modifiers|0x4000,value.Key)) { error="快捷键已被占用或不可用。"; return false; }
        if(id.HasValue) UnregisterHotKey(handle,id.Value);
        id=replacement; binding=value; return true;
    }
    private static bool TryParse(string? gesture,out (uint Modifiers,uint Key) value)
    {
        value=default; if(string.IsNullOrWhiteSpace(gesture)) return false;
        var parts=gesture.Split('+',StringSplitOptions.TrimEntries); if(parts.Length<2) return false;
        uint modifiers=0;
        for(int i=0;i<parts.Length-1;i++)
        {
            uint bit=parts[i].ToUpperInvariant() switch {"CTRL" or "CONTROL"=>2,"ALT"=>1,"SHIFT"=>4,"WIN" or "WINDOWS"=>8,_=>0};
            if(bit==0 || (modifiers&bit)!=0) return false; modifiers|=bit;
        }
        string token=parts[^1]; if(token.Length==1 && char.IsDigit(token[0])) token="D"+token;
        if(!Enum.TryParse<Key>(token,true,out var key) || !Enum.IsDefined(key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        int virtualKey=KeyInterop.VirtualKeyFromKey(key); if(virtualKey<=0) return false;
        value=(modifiers,(uint)virtualKey); return true;
    }
    public event Action? Invoked;
    private IntPtr Hook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    { if(message==0x0312 && id.HasValue && wParam.ToInt32()==id.Value) { handled=true; Invoked?.Invoke(); } return IntPtr.Zero; }
    private void Closed(object? sender,EventArgs e)=>Dispose();
    public void Dispose() { if(disposed) return; disposed=true; if(id.HasValue) UnregisterHotKey(handle,id.Value); source.RemoveHook(Hook); window.Closed-=Closed; }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window,int id);
}
