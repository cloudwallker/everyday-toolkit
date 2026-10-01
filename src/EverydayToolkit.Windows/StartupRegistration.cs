using Microsoft.Win32;
using System.Text;
namespace EverydayToolkit.Windows;
public static class StartupRegistration
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName="EverydayToolkit";
    private static string Executable
    {
        get
        {
            string path=Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
            if(!string.Equals(System.IO.Path.GetFileName(path),"EverydayToolkit.App.exe",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("开机启动只能从日用工具箱应用注册。");
            return path;
        }
    }
    internal static string BuildCommand(string executable,string? dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        string command=Quote(executable)+" --tray";
        return string.IsNullOrEmpty(dataDirectory) ? command : command+" --data-dir "+Quote(dataDirectory);
    }
    private static string Quote(string value)
    {
        var quoted=new StringBuilder("\""); int slashes=0;
        foreach(char character in value)
        {
            if(character=='\\') { slashes++; continue; }
            if(character=='\"') quoted.Append('\\',slashes*2+1).Append(character);
            else quoted.Append('\\',slashes).Append(character);
            slashes=0;
        }
        return quoted.Append('\\',slashes*2).Append('"').ToString();
    }
    public static void SetEnabled(bool enabled,string? dataDirectory=null)
    {
        string? command=enabled ? BuildCommand(Executable,dataDirectory) : null;
        using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
        if(enabled) key.SetValue(ValueName,command!,RegistryValueKind.String); else key.DeleteValue(ValueName,false);
    }
    public static bool IsEnabled()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RunKey);
        if(key?.GetValue(ValueName) is not string value) return false;
        try
        {
            string command=BuildCommand(Executable,null);
            return string.Equals(value,command,StringComparison.OrdinalIgnoreCase) || value.StartsWith(command+" --data-dir ",StringComparison.OrdinalIgnoreCase);
        }
        catch(InvalidOperationException) { return false; }
    }

    public static Action CaptureRestoreAction()
    {
        StartupSnapshot snapshot;
        using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) snapshot=Capture(key,ValueName);
        return () =>
        {
            using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
            Restore(key,ValueName,snapshot);
        };
    }
    internal sealed record StartupSnapshot(bool Exists,object? Value,RegistryValueKind Kind);
    internal static StartupSnapshot Capture(RegistryKey? key,string valueName)
    {
        if(key is null || !key.GetValueNames().Contains(valueName,StringComparer.OrdinalIgnoreCase)) return new(false,null,RegistryValueKind.None);
        return new(true,key.GetValue(valueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames),key.GetValueKind(valueName));
    }
    internal static void Restore(RegistryKey key,string valueName,StartupSnapshot snapshot)
    {
        if(snapshot.Exists) key.SetValue(valueName,snapshot.Value!,snapshot.Kind);
        else key.DeleteValue(valueName,false);
    }
}
