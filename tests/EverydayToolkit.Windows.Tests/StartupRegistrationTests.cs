using EverydayToolkit.Windows;
using Microsoft.Win32;

internal static class StartupRegistrationTests
{
    public static int Run()
    {
        var failures = 0;
        var keyPath = @"Software\EverydayToolkitTests\Startup-" + Guid.NewGuid().ToString("N");
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name); }
        }
        try
        {
            Check("StartupSnapshot_RestoresRawExpandableCommandAndType", () =>
            {
                const string original = @"""%LOCALAPPDATA%\Programs\Synthetic\EverydayToolkit.App.exe"" --tray --data-dir ""D:\SyntheticProfileA""";
                key.SetValue("EverydayToolkit", original, RegistryValueKind.ExpandString);
                var snapshot = StartupRegistration.Capture(key, "EverydayToolkit");
                key.SetValue("EverydayToolkit", @"synthetic different command", RegistryValueKind.String);
                StartupRegistration.Restore(key, "EverydayToolkit", snapshot);
                if (key.GetValueKind("EverydayToolkit") != RegistryValueKind.ExpandString
                    || !Equals(original, key.GetValue("EverydayToolkit", null, RegistryValueOptions.DoNotExpandEnvironmentNames))) throw new Exception("raw command or kind changed");
            });
            Check("StartupSnapshot_RestoresAbsentValueWithoutChangingOtherValues", () =>
            {
                key.DeleteValue("EverydayToolkit", false);
                key.SetValue("OtherSyntheticValue", "preserved", RegistryValueKind.String);
                var snapshot = StartupRegistration.Capture(key, "EverydayToolkit");
                key.SetValue("EverydayToolkit", "temporary", RegistryValueKind.String);
                StartupRegistration.Restore(key, "EverydayToolkit", snapshot);
                if (key.GetValue("EverydayToolkit") is not null || !Equals("preserved", key.GetValue("OtherSyntheticValue"))) throw new Exception("restoration changed unrelated state");
            });
            Check("StartupSnapshot_RestoresUnexpectedValueTypeExactly", () =>
            {
                key.SetValue("EverydayToolkit", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
                var snapshot = StartupRegistration.Capture(key, "EverydayToolkit");
                key.SetValue("EverydayToolkit", "temporary", RegistryValueKind.String);
                StartupRegistration.Restore(key, "EverydayToolkit", snapshot);
                if (key.GetValueKind("EverydayToolkit") != RegistryValueKind.Binary || key.GetValue("EverydayToolkit") is not byte[] bytes || !bytes.SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("type not restored");
            });
        }
        finally { key.Dispose(); Registry.CurrentUser.DeleteSubKey(keyPath, false); }
        Console.WriteLine($"Startup registry tests: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
