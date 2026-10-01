using System;
using System.IO;
namespace EverydayToolkit.Installer {
    public static class UninstallPolicy {
        // Resolve the final decision after the UI has supplied its choice.
        public static string ResolveDataDirectory(bool controlled, bool deleteData, string dataOverride, string defaultData, string projectRoot) {
            if(controlled) {
                if(dataOverride!=null) {
                    string boundary=Path.Combine(Path.GetFullPath(projectRoot),"artifacts")+Path.DirectorySeparatorChar;
                    string selected=Path.GetFullPath(dataOverride);
                    if(!selected.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("受控卸载数据目录必须位于项目 artifacts。");
                    InstallerEngine.CheckDirectory(selected);
                }
                if(deleteData && dataOverride==null) throw new InvalidDataException("受控卸载删除数据需要显式指定 artifacts 下 --data-dir。");
                return deleteData ? Path.GetFullPath(dataOverride) : null;
            }
            if(dataOverride!=null) throw new InvalidDataException("测试数据目录只用于受控卸载。");
            return deleteData ? Path.GetFullPath(defaultData) : null;
        }
    }
}
