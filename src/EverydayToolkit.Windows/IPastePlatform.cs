namespace EverydayToolkit.Windows;

internal interface IPastePlatform
{
    IntPtr ForegroundWindow { get; }
    (uint ProcessId,uint ThreadId) GetIdentity(IntPtr window);
    bool IsWindow(IntPtr window);
    bool ModifiersDown();
    bool RestoreForeground(IntPtr window);
    bool SendPaste();
    void ReleasePasteKeys();
}
