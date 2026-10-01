namespace EverydayToolkit.Windows;

public readonly record struct PasteTarget(IntPtr Window,uint ProcessId,uint ThreadId);
