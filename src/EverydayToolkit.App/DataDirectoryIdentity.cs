using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EverydayToolkit.App;

public static class DataDirectoryIdentity
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static string InstanceId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path).ToUpperInvariant())))[..20];
}
