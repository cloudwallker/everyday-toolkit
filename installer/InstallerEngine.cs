using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace EverydayToolkit.Installer {
    public sealed class InstallerIoException : IOException {
        public string Stage { get; private set; }
        public InstallerIoException(string stage, IOException cause) : base("安装载荷 IO 操作失败。", cause) { Stage=stage; }
    }
    public static class InstallerEngine {
        public const string Marker = "EverydayToolkit-v1";
        internal static void CommitStagedDirectory(string stage,string target) {
            const int maxAttempts=12;
            for(int attempt=0;attempt<maxAttempts;attempt++) {
                CheckDirectory(stage); CheckDirectory(target);
                try { Directory.Move(stage,target); return; }
                catch(IOException error) {
                    bool transientCode=error.HResult==unchecked((int)0x80070005) || error.HResult==unchecked((int)0x80070020);
                    if(!transientCode || attempt==maxAttempts-1 || !Directory.Exists(stage) || Directory.Exists(target)) throw;
                    Thread.Sleep(300);
                }
            }
        }
        public static void CheckDirectory(string target) {
            target=Path.GetFullPath(target);
            if(target.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(target).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不能使用磁盘根目录。");
            for(var current=new DirectoryInfo(target); current!=null; current=current.Parent)
                if(current.Exists && (current.Attributes & FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("不能使用符号链接或重解析目录。");
        }
        static string SafePath(string root,string relative) {
            if(String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':')>=0) throw new InvalidDataException("安装清单路径无效。");
            relative=relative.Replace('/', '\\');
            foreach(string part in relative.Split('\\')) {
                if(part=="" || part=="." || part==".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new InvalidDataException("载荷路径无效。");
                string basename=part.Split('.')[0].ToUpperInvariant();
                if(basename=="CON" || basename=="PRN" || basename=="AUX" || basename=="NUL" || (basename.Length==4 && (basename.StartsWith("COM") || basename.StartsWith("LPT")) && basename[3]>='1' && basename[3]<='9')) throw new InvalidDataException("载荷含保留设备路径。");
            }
            string path=Path.GetFullPath(Path.Combine(root,relative));
            if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("载荷超出安装目录。");
            CheckDirectory(Path.GetDirectoryName(path));
            return path;
        }
        static string Hash(string path) { using(var input=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""); }
        public static void Install(Stream payload, string target, byte[] uninstaller) {
            target=Path.GetFullPath(target); CheckDirectory(target);
            if(Directory.Exists(target) || File.Exists(target)) throw new IOException("安装目录已存在。请先卸载旧版本，或选择新目录。");
            if(uninstaller==null || uninstaller.Length==0) throw new InvalidDataException("卸载器缺失。");
            string stage=target+".install-"+Guid.NewGuid().ToString("N");
            var files=new List<string>();
            string phase="validate";
            try {
                using(var archive=new ZipArchive(payload,ZipArchiveMode.Read,true)) {
                    var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total=0;
                    foreach(var entry in archive.Entries) {
                        if(entry.FullName.EndsWith("/")) { SafePath(stage,entry.FullName.TrimEnd('/')); continue; }
                        string relative=entry.FullName.Replace('/','\\'); SafePath(stage,relative);
                        if(!seen.Add(relative) || relative.Equals("install-manifest.txt",StringComparison.OrdinalIgnoreCase) || relative.Equals("Uninstall.exe",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("载荷含重复或保留文件。");
                        total=checked(total+entry.Length); if(total>2L*1024*1024*1024) throw new InvalidDataException("载荷过大。");
                    }
                    if(!seen.Contains("EverydayToolkit.App.exe")) throw new InvalidDataException("载荷缺少应用程序。");
                    phase="create";
                    Directory.CreateDirectory(stage);
                    phase="extract";
                    foreach(var entry in archive.Entries) {
                        if(entry.FullName.EndsWith("/")) continue;
                        string relative=entry.FullName.Replace('/','\\'); string path=SafePath(stage,relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)); files.Add(relative);
                        using(var input=entry.Open()) using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write)) input.CopyTo(output);
                    }
                }
                phase="uninstaller";
                File.WriteAllBytes(Path.Combine(stage,"Uninstall.exe"),uninstaller); files.Add("Uninstall.exe");
                phase="hash";
                var manifest=new StringBuilder(Marker+"\n");
                foreach(string file in files) manifest.Append(file).Append('\t').Append(Hash(SafePath(stage,file))).Append('\n');
                phase="manifest";
                File.WriteAllText(Path.Combine(stage,"install-manifest.txt"),manifest.ToString(),new UTF8Encoding(false));
                phase="commit";
                CommitStagedDirectory(stage,target);
            } catch(IOException error) {
                try { RemoveKnown(stage,files); }
                catch(IOException cleanup) { throw new InstallerIoException("rollback",cleanup); }
                throw new InstallerIoException(phase,error);
            } catch { RemoveKnown(stage,files); throw; }
        }
        static void RemoveKnown(string target,IEnumerable<string> files) {
            if(!Directory.Exists(target)) return;
            var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string relative in files) {
                string path=SafePath(target,relative); if(File.Exists(path)) File.Delete(path);
                for(string dir=Path.GetDirectoryName(path); dir!=null && dir.StartsWith(target+"\\",StringComparison.OrdinalIgnoreCase);dir=Path.GetDirectoryName(dir)) directories.Add(dir);
            }
            var ordered=new List<string>(directories); ordered.Sort((a,b)=>b.Length.CompareTo(a.Length));
            foreach(string dir in ordered) if(Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length==0) Directory.Delete(dir);
            string manifest=Path.Combine(target,"install-manifest.txt"); if(File.Exists(manifest)) File.Delete(manifest);
            if(Directory.GetFileSystemEntries(target).Length==0) Directory.Delete(target);
        }
        public static void Uninstall(string target, string dataDirectory, bool deleteData) {
            target=Path.GetFullPath(target); CheckDirectory(target);
            string[] lines=File.ReadAllLines(Path.Combine(target,"install-manifest.txt"));
            if(lines.Length<2 || lines[0]!=Marker) throw new InvalidDataException("此目录不属于日用工具箱安装。");
            var files=new List<string>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int i=1;i<lines.Length;i++) {
                string[] values=lines[i].Split('\t');
                if(values.Length!=2 || values[1].Length!=64 || !seen.Add(values[0])) throw new InvalidDataException("安装清单无效。");
                string path=SafePath(target,values[0]);
                if(File.Exists(path) && Hash(path)!=values[1]) throw new InvalidDataException("安装文件已改变，卸载已停止。请备份并手动检查。");
                files.Add(values[0]);
            }
            if(!seen.Contains("EverydayToolkit.App.exe") || !seen.Contains("Uninstall.exe")) throw new InvalidDataException("安装清单不完整。");
            if(deleteData) {
                if(String.IsNullOrEmpty(dataDirectory)) throw new InvalidDataException("数据目录未指定。");
                dataDirectory=Path.GetFullPath(dataDirectory); CheckDirectory(dataDirectory);
                if(Directory.Exists(dataDirectory) && (!File.Exists(Path.Combine(dataDirectory,"toolkit-data.marker")) || File.ReadAllText(Path.Combine(dataDirectory,"toolkit-data.marker")).Trim()!=Marker)) throw new InvalidDataException("数据目录没有有效应用标识，不能删除。");
            }
            RemoveKnown(target,files);
            if(deleteData && Directory.Exists(dataDirectory)) {
                foreach(string name in new[]{"content.db","content.db-wal","content.db-shm","settings.json","toolkit-data.marker"}) { string path=SafePath(dataDirectory,name); if(File.Exists(path)) File.Delete(path); }
                if(Directory.GetFileSystemEntries(dataDirectory).Length==0) Directory.Delete(dataDirectory);
            }
        }
    }
}
