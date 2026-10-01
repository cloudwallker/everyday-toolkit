using System;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
namespace EverydayToolkit.Installer {
    public static class Registration {
        public static void Install(string target,string keyPath,string shortcutPath) {
            using(var existing=Registry.CurrentUser.OpenSubKey(keyPath)) {
                if(existing!=null && !String.Equals(existing.GetValue("InstallLocation") as string,target,StringComparison.OrdinalIgnoreCase)) throw new IOException("注册项已属于另一个安装位置。");
            }
            if(File.Exists(shortcutPath)) throw new IOException("开始菜单快捷方式已存在，请先检查旧安装。");
            using(var key=Registry.CurrentUser.CreateSubKey(keyPath)) {
                key.SetValue("DisplayName","日用工具箱 / Everyday Toolkit"); key.SetValue("DisplayVersion","0.1.0"); key.SetValue("InstallLocation",target);
                key.SetValue("UninstallString","\""+Path.Combine(target,"Uninstall.exe")+"\""); key.SetValue("DisplayIcon",Path.Combine(target,"EverydayToolkit.App.exe")); key.SetValue("NoModify",1); key.SetValue("NoRepair",1);
            }
            object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); object shortcut=null;
            try {
                shortcut=shell.GetType().InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{shortcutPath});
                var flags=System.Reflection.BindingFlags.SetProperty;
                shortcut.GetType().InvokeMember("TargetPath",flags,null,shortcut,new object[]{Path.Combine(target,"EverydayToolkit.App.exe")});
                shortcut.GetType().InvokeMember("WorkingDirectory",flags,null,shortcut,new object[]{target});
                shortcut.GetType().InvokeMember("Description",flags,null,shortcut,new object[]{"日用工具箱"});
                shortcut.GetType().InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,shortcut,null);
            } finally { if(shortcut!=null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
        public static void Remove(string target,string keyPath,string shortcutPath) {
            bool owns=false; using(var key=Registry.CurrentUser.OpenSubKey(keyPath)) owns=key!=null && String.Equals(key.GetValue("InstallLocation") as string,target,StringComparison.OrdinalIgnoreCase);
            if(!owns) return;
            Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);
            if(File.Exists(shortcutPath)) {
                object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); object shortcut=null;
                try {
                    shortcut=shell.GetType().InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{shortcutPath});
                    string shortcutTarget=shortcut.GetType().InvokeMember("TargetPath",System.Reflection.BindingFlags.GetProperty,null,shortcut,null) as string;
                    if(String.Equals(shortcutTarget,Path.Combine(target,"EverydayToolkit.App.exe"),StringComparison.OrdinalIgnoreCase)) File.Delete(shortcutPath);
                } finally { if(shortcut!=null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
            }
        }
    }
}
