using System.Diagnostics;
using System.Runtime.InteropServices;
namespace EverydayToolkit.Windows;
public sealed class PasteService
{
    private readonly IPastePlatform platform;
    public PasteService() : this(new NativePastePlatform()) { }
    internal PasteService(IPastePlatform platform) { this.platform=platform; }
    private readonly object targetGate=new();
    private PasteTarget target;
    public void CaptureTarget()
    {
        IntPtr window=platform.ForegroundWindow;
        var (pid,thread)=platform.GetIdentity(window);
        var captured=new PasteTarget(window,pid,thread);
        if(!ValidTarget(captured)) captured=default;
        lock(targetGate) target=captured;
    }
    public PasteTarget GetTargetSnapshot() { lock(targetGate) return target; }
    public Task<string?> PasteAsync()=>PasteAsync(GetTargetSnapshot());
    public async Task<string?> PasteAsync(PasteTarget snapshot)
    {
        if(!ValidTarget(snapshot)) return "无法确认原窗口，请手动按 Ctrl+V 粘贴。";
        var watch=Stopwatch.StartNew();
        while(platform.ModifiersDown()) { if(watch.ElapsedMilliseconds>=1000) return "修饰键尚未释放，请手动粘贴。"; await Task.Delay(20); }
        if(!ValidTarget(snapshot) || !platform.RestoreForeground(snapshot.Window)) return "无法恢复原窗口，请手动粘贴。";
        await Task.Delay(60);
        if(!ValidTarget(snapshot) || platform.ForegroundWindow!=snapshot.Window || platform.ModifiersDown()) return "原窗口或按键状态已变化，请手动粘贴。";
        if(platform.SendPaste()) return null;
        platform.ReleasePasteKeys();
        return "系统未接受自动粘贴，请手动按 Ctrl+V。";
    }
    private bool ValidTarget(PasteTarget snapshot)
    { if(snapshot.Window==IntPtr.Zero || !platform.IsWindow(snapshot.Window)) return false; var (pid,thread)=platform.GetIdentity(snapshot.Window); return thread==snapshot.ThreadId && pid==snapshot.ProcessId && pid!=0 && pid!=Environment.ProcessId; }
    private sealed class NativePastePlatform : IPastePlatform
    {
        public IntPtr ForegroundWindow=>GetForegroundWindow();
        public (uint ProcessId,uint ThreadId) GetIdentity(IntPtr window) { uint thread=GetWindowThreadProcessId(window,out uint pid); return(pid,thread); }
        public bool IsWindow(IntPtr window)=>PasteService.IsWindow(window);
        public bool ModifiersDown()=>PasteService.ModifiersDown();
        public bool RestoreForeground(IntPtr window)=>SetForegroundWindow(window);
        public bool SendPaste() { INPUT[] input=[Key(0x11,false),Key(0x56,false),Key(0x56,true),Key(0x11,true)]; return SendInput((uint)input.Length,input,Marshal.SizeOf<INPUT>())==(uint)input.Length; }
        public void ReleasePasteKeys() { INPUT[] release=[Key(0x56,true),Key(0x11,true)]; SendInput((uint)release.Length,release,Marshal.SizeOf<INPUT>()); }
    }
    private static bool ModifiersDown()=>new[] {0x11,0x12,0x10,0x5B,0x5C}.Any(key=>(GetAsyncKeyState(key)&0x8000)!=0);
    private static INPUT Key(ushort key,bool up)=>new() { Type=1, Data=new InputUnion { Keyboard=new KEYBDINPUT { VirtualKey=key, Flags=up?2u:0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT Keyboard; [FieldOffset(0)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort VirtualKey,Scan; public uint Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X,Y; public uint MouseData,Flags,Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,INPUT[] input,int size);
}
