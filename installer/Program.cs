using System;
using System.IO;
using System.Windows.Forms;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Win32;
namespace EverydayToolkit.Installer {
    static class Program {
        const string RegistryPath="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EverydayToolkit";
        static string DefaultInstall { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","EverydayToolkit"); } }
        static string DefaultData { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EverydayToolkit"); } }
        [STAThread] static int Main(string[] args) {
            bool quiet=false,noRegistration=false,deleteData=false,helper=false; string install=null,uninstall=null,dataOverride=null; int parentPid=0; string phase="arguments";
            try {
                for(int i=0;i<args.Length;i++) switch(args[i]) {
                    case "--quiet": quiet=true; break;
                    case "--no-registration": noRegistration=true; break;
                    case "--delete-data": deleteData=true; break;
                    case "--install-dir": if(++i>=args.Length) throw new InvalidDataException("缺少安装目录。"); install=args[i]; break;
                    case "--uninstall-dir": if(++i>=args.Length) throw new InvalidDataException("缺少卸载目录。"); uninstall=args[i]; break;
                    case "--data-dir": if(++i>=args.Length) throw new InvalidDataException("缺少测试数据目录。"); dataOverride=args[i]; break;
                    case "--helper": helper=true; break;
                    case "--parent-pid": if(++i>=args.Length || !Int32.TryParse(args[i],out parentPid) || parentPid<=0) throw new InvalidDataException("卸载助手参数无效。"); break;
                    default: throw new InvalidDataException("未知参数。");
                }
                if(install!=null && uninstall!=null) throw new InvalidDataException("不能同时安装和卸载。");
                if(install!=null || uninstall!=null) {
                    string controlled=install ?? uninstall;
                    if(helper && !noRegistration) {
                        if(install!=null || !Path.GetFullPath(controlled).Equals(DefaultInstall,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("卸载助手目标无效。");
                    } else {
                        string boundary=Path.Combine(Environment.CurrentDirectory,"artifacts")+Path.DirectorySeparatorChar;
                        if(!noRegistration || !Path.GetFullPath(controlled).StartsWith(boundary,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("受控安装/卸载目录必须位于项目 artifacts，且指定 --no-registration。");
                    }
                } else if(quiet || noRegistration || helper || deleteData) throw new InvalidDataException("静默模式需要明确的受控目录。");
                if(dataOverride!=null && (!noRegistration || uninstall==null)) throw new InvalidDataException("测试数据目录只用于受控卸载。");
                phase="windows";
                Application.EnableVisualStyles();
                phase="resources";
                var assembly=Assembly.GetExecutingAssembly();
                using(var payload=assembly.GetManifestResourceStream("payload.zip")) {
                    bool removing=uninstall!=null || payload==null;
                    if(removing) {
                        string target=Path.GetFullPath(uninstall ?? Path.GetDirectoryName(assembly.Location));
                        if(uninstall==null && !target.Equals(DefaultInstall,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装位置与默认位置不符。请按清单手动检查。");
                        if(!quiet && !helper) {
                            string question=noRegistration
                                ? "退出日用工具箱后卸载。\n是否删除显式 --data-dir 指定的 artifacts 合成数据？\n未指定合成数据目录时选择删除将停止卸载，真实默认数据始终保留。\n是：删除合成数据；否：保留；取消：停止。"
                                : "退出日用工具箱后卸载。\n是否同时删除默认用户数据（历史、收藏、常用语和设置）？\n是：删除；否：保留；取消：停止。\n自定义数据目录请手动管理；未知文件始终保留。";
                            var choice=MessageBox.Show(question,"卸载日用工具箱",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
                            if(choice==DialogResult.Cancel) return 2; deleteData=choice==DialogResult.Yes;
                        }
                        string selectedData=UninstallPolicy.ResolveDataDirectory(noRegistration,deleteData,dataOverride,DefaultData,Environment.CurrentDirectory);
                        if(Path.GetDirectoryName(assembly.Location).Equals(target,StringComparison.OrdinalIgnoreCase)) {
                            string temp=Path.Combine(Path.GetTempPath(),"EverydayToolkit-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");
                            File.Copy(assembly.Location,temp);
                            string helperArgs="--helper --parent-pid "+Process.GetCurrentProcess().Id+" --uninstall-dir \""+target+"\""+(quiet?" --quiet":"")+(noRegistration?" --no-registration":"")+(deleteData?" --delete-data":"")+(dataOverride!=null?" --data-dir \""+dataOverride+"\"":"");
                            Process.Start(new ProcessStartInfo(temp,helperArgs){UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory}); return 0;
                        }
                        if(helper && parentPid>0) { try { using(var parent=Process.GetProcessById(parentPid)) if(!parent.WaitForExit(10000)) throw new IOException("卸载程序未及时退出，请重试。"); } catch(ArgumentException) { } }
                        InstallerEngine.Uninstall(target,selectedData,deleteData);
                        if(!noRegistration) Unregister(target);
                        if(!quiet) MessageBox.Show("卸载完成。未知文件和自定义数据请手动管理。","日用工具箱");
                        return 0;
                    }
                    if(deleteData) throw new InvalidDataException("删除数据仅适用于卸载。");
                    string targetInstall=Path.GetFullPath(install ?? DefaultInstall);
                    if(!quiet && MessageBox.Show("安装日用工具箱 0.1.0 到：\n"+targetInstall+"\n仅为当前用户安装，无需管理员权限。\n应用首次启动由你选择是否开启记录。","安装日用工具箱",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK) return 2;
                    byte[] remover;
                    phase="uninstaller";
                    using(var stream=assembly.GetManifestResourceStream("uninstaller.exe")) using(var output=new MemoryStream()) { if(stream==null) throw new InvalidDataException("安装包缺少卸载程序。"); stream.CopyTo(output); remover=output.ToArray(); }
                    phase="install";
                    InstallerEngine.Install(payload,targetInstall,remover);
                    if(!noRegistration) {
                        try { Register(targetInstall); } catch { try { Unregister(targetInstall); InstallerEngine.Uninstall(targetInstall,null,false); } catch { } throw; }
                    }
                    if(!quiet) MessageBox.Show("安装完成。请从开始菜单打开日用工具箱。","日用工具箱");
                    return 0;
                }
            } catch(Exception ex) {
                Exception cause=ex is InstallerIoException && ex.InnerException!=null ? ex.InnerException : ex;
                string failurePhase=ex is InstallerIoException ? ((InstallerIoException)ex).Stage : phase;
                string site=cause.TargetSite==null ? "none" : cause.TargetSite.Name;
                site=System.Text.RegularExpressions.Regex.Replace(site,"[^A-Za-z0-9_.]","_");
                try { Console.Error.WriteLine("INSTALLER_FAILURE phase="+failurePhase+" type="+cause.GetType().Name+" hresult=0x"+((uint)cause.HResult).ToString("X8")+" site="+site); } catch { }
                if(!quiet) MessageBox.Show("操作未完成："+ex.Message,"日用工具箱",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"日用工具箱.lnk"); } }
        static void Register(string target) {
            Registration.Install(target,RegistryPath,Shortcut);
        }
        static void Unregister(string target) {
            Registration.Remove(target,RegistryPath,Shortcut);
            using(var run=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run",true)) {
                string value=run==null?null:run.GetValue("EverydayToolkit") as string;
                if(value!=null && value.IndexOf(Path.Combine(target,"EverydayToolkit.App.exe"),StringComparison.OrdinalIgnoreCase)>=0) run.DeleteValue("EverydayToolkit",false);
            }
        }
    }
}
