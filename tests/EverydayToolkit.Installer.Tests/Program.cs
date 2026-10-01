using System;
using System.IO;
using System.IO.Compression;
using EverydayToolkit.Installer;
using Microsoft.Win32;
using System.Threading;
using System.Diagnostics;

static class Program {
    static string root;
    static int failed;
    static void Main() {
        root = Path.Combine(Environment.CurrentDirectory, "artifacts", "installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Run("commit retries a temporary file lock and preserves payload", () => {
            string stage=Target(), destination=Target(); Directory.CreateDirectory(stage);
            string payload=Path.Combine(stage,"synthetic.bin"); File.WriteAllText(payload,"owned payload");
            var held=new FileStream(payload,FileMode.Open,FileAccess.Read,FileShare.None);
            var releaser=new Thread(() => { Thread.Sleep(650); held.Dispose(); }); releaser.Start();
            try { InstallerEngine.CommitStagedDirectory(stage,destination); }
            finally { releaser.Join(); held.Dispose(); }
            Assert(File.ReadAllText(Path.Combine(destination,"synthetic.bin"))=="owned payload","temporary lock prevented commit or changed bytes");
            Assert(!Directory.Exists(stage),"staging directory retained after commit");
        });
        Run("commit stops after a bounded persistent file lock", () => {
            string stage=Target(), destination=Target(); Directory.CreateDirectory(stage);
            string payload=Path.Combine(stage,"synthetic.bin"); File.WriteAllText(payload,"owned payload");
            var timer=Stopwatch.StartNew();
            using(var held=new FileStream(payload,FileMode.Open,FileAccess.Read,FileShare.None)) Reject(()=>InstallerEngine.CommitStagedDirectory(stage,destination));
            Assert(timer.Elapsed<TimeSpan.FromSeconds(10),"persistent lock retry exceeded bound");
            Assert(File.Exists(payload) && !Directory.Exists(destination),"failed commit moved or deleted files");
        });
        Run("controlled UI yes without override cannot delete default data", () => {
            string install=Target(); Install(install);
            string syntheticDefault=Target(); Directory.CreateDirectory(syntheticDefault);
            File.WriteAllText(Path.Combine(syntheticDefault,"toolkit-data.marker"),"EverydayToolkit-v1");
            File.WriteAllText(Path.Combine(syntheticDefault,"content.db"),"synthetic default sentinel");
            bool choiceAfterPrompt=true;
            try {
                string selected=UninstallPolicy.ResolveDataDirectory(true,choiceAfterPrompt,null,syntheticDefault,Environment.CurrentDirectory);
                InstallerEngine.Uninstall(install,selected,choiceAfterPrompt);
            } catch(InvalidDataException) { }
            Assert(File.Exists(Path.Combine(syntheticDefault,"content.db")),"UI yes deleted simulated default data");
            Assert(File.Exists(Path.Combine(install,"EverydayToolkit.App.exe")),"invalid post-prompt choice mutated installation");
        });
        Run("controlled retain decision never resolves default data", () => {
            Assert(UninstallPolicy.ResolveDataDirectory(true,false,null,Target(),Environment.CurrentDirectory)==null,"controlled retain fell back to default data");
        });
        Run("controlled final deletion rejects data outside artifacts", () => {
            Reject(()=>UninstallPolicy.ResolveDataDirectory(true,true,Path.Combine(Environment.CurrentDirectory,"outside-synthetic"),Target(),Environment.CurrentDirectory));
        });
        Run("controlled UI yes deletes explicit synthetic data only", () => {
            string install=Target(); Install(install); string syntheticData=Target(); Directory.CreateDirectory(syntheticData);
            File.WriteAllText(Path.Combine(syntheticData,"toolkit-data.marker"),"EverydayToolkit-v1"); File.WriteAllText(Path.Combine(syntheticData,"content.db"),"synthetic"); File.WriteAllText(Path.Combine(syntheticData,"mine.txt"),"keep");
            string syntheticDefault=Target(); Directory.CreateDirectory(syntheticDefault); File.WriteAllText(Path.Combine(syntheticDefault,"content.db"),"default sentinel");
            string selected=UninstallPolicy.ResolveDataDirectory(true,true,syntheticData,syntheticDefault,Environment.CurrentDirectory);
            InstallerEngine.Uninstall(install,selected,true);
            Assert(!File.Exists(Path.Combine(syntheticData,"content.db")),"explicit synthetic data retained");
            Assert(File.Exists(Path.Combine(syntheticData,"mine.txt")),"unknown synthetic file deleted");
            Assert(File.Exists(Path.Combine(syntheticDefault,"content.db")),"simulated default data touched");
        });
        Run("current-user registration and real start menu shortcut", () => {
            string target=Target(); string shortcut=Path.Combine(root,"synthetic.lnk"); string keyPath="Software\\EverydayToolkit.Installer.Tests\\"+Guid.NewGuid().ToString("N");
            try {
                Registration.Install(target,keyPath,shortcut);
                using(var key=Registry.CurrentUser.OpenSubKey(keyPath)) Assert(key!=null && (string)key.GetValue("InstallLocation")==target,"HKCU install location missing");
                Assert(File.Exists(shortcut),"shortcut missing");
                Registration.Remove(target,keyPath,shortcut);
                using(var key=Registry.CurrentUser.OpenSubKey(keyPath)) Assert(key==null,"HKCU registration retained");
                Assert(!File.Exists(shortcut),"shortcut retained");
            } finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath,false); if(File.Exists(shortcut)) File.Delete(shortcut); }
        });
        Run("complete payload and manifest", () => {
            string dir = Target(); Install(dir);
            Assert(File.ReadAllText(Path.Combine(dir, "EverydayToolkit.App.exe")) == "synthetic executable", "application not installed");
            Assert(File.ReadAllText(Path.Combine(dir, "sub", "sample.txt")) == "sample", "nested payload missing");
            Assert(File.Exists(Path.Combine(dir, "install-manifest.txt")), "manifest missing");
            Assert(File.Exists(Path.Combine(dir, "Uninstall.exe")), "uninstaller missing");
        });
        Run("traversal rejects before writes", () => {
            string dir = Target(); Reject(() => InstallerEngine.Install(Zip("../escaped.txt"), dir, new byte[] { 1 }));
            Assert(!Directory.Exists(dir), "partial installation survived");
            Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(dir), "escaped.txt")), "payload escaped");
        });
        Run("missing application rejects", () => { string dir = Target(); Reject(() => InstallerEngine.Install(Zip("sample.txt"), dir, new byte[] { 1 })); Assert(!Directory.Exists(dir), "partial install"); });
        Run("existing directory preserved", () => { string dir = Target(); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"mine.txt"),"keep"); Reject(() => Install(dir)); Assert(File.ReadAllText(Path.Combine(dir,"mine.txt"))=="keep","user file overwritten"); });
        Run("uninstall preserves data and unknown files", () => { string dir=Target(); Install(dir); string data=Target(); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data,"content.db"),"synthetic data"); File.WriteAllText(Path.Combine(dir,"mine.txt"),"keep"); InstallerEngine.Uninstall(dir,data,false); Assert(!File.Exists(Path.Combine(dir,"EverydayToolkit.App.exe")),"app retained"); Assert(File.Exists(Path.Combine(dir,"mine.txt")),"unknown file deleted"); Assert(File.Exists(Path.Combine(data,"content.db")),"data deleted"); });
        Run("delete data removes only known files", () => { string dir=Target(); Install(dir); string data=Target(); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data,"toolkit-data.marker"),"EverydayToolkit-v1"); File.WriteAllText(Path.Combine(data,"content.db"),"synthetic"); File.WriteAllText(Path.Combine(data,"mine.txt"),"keep"); InstallerEngine.Uninstall(dir,data,true); Assert(!File.Exists(Path.Combine(data,"content.db")),"data retained"); Assert(File.Exists(Path.Combine(data,"mine.txt")),"unknown data deleted"); });
        Run("invalid manifest refuses deletion", () => { string dir=Target(); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"install-manifest.txt"),"EverydayToolkit-v1\n../escaped.txt\t00\n"); File.WriteAllText(Path.Combine(dir,"mine.txt"),"keep"); Reject(()=>InstallerEngine.Uninstall(dir,null,false)); Assert(File.Exists(Path.Combine(dir,"mine.txt")),"unexpected deletion"); });
        Run("modified installation refuses deletion", () => { string dir=Target(); Install(dir); File.WriteAllText(Path.Combine(dir,"sub","sample.txt"),"modified"); Reject(()=>InstallerEngine.Uninstall(dir,null,false)); Assert(File.Exists(Path.Combine(dir,"EverydayToolkit.App.exe")),"partial uninstall"); });
        Console.WriteLine("Installer tests: " + (failed==0 ? "PASS" : "FAIL") + "; artifacts: " + root);
        Environment.ExitCode=failed==0?0:1;
    }
    static void Run(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
    static string Target() { return Path.Combine(root,Guid.NewGuid().ToString("N")); }
    static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch(InvalidDataException) { return; } catch(IOException) { return; } throw new Exception("unsafe operation accepted"); }
    static void Install(string target) { using(var zip=Zip(null)) InstallerEngine.Install(zip,target,new byte[]{1,2,3}); }
    static MemoryStream Zip(string bad) { var stream=new MemoryStream(); using(var archive=new ZipArchive(stream,ZipArchiveMode.Create,true)) { if(bad!=null) { using(var w=new StreamWriter(archive.CreateEntry(bad).Open())) w.Write("escape"); } else { using(var w=new StreamWriter(archive.CreateEntry("EverydayToolkit.App.exe").Open())) w.Write("synthetic executable"); using(var w=new StreamWriter(archive.CreateEntry("sub/sample.txt").Open())) w.Write("sample"); } } stream.Position=0; return stream; }
}
