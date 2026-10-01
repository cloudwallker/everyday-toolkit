using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace EverydayToolkit.App;

public static class Theme
{
    public static void Apply()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        Set("WindowBrush", dark ? "#171C26" : "#F3F6FB");
        Set("SurfaceBrush", dark ? "#232B39" : "#FFFFFF");
        Set("BorderBrush", dark ? "#394559" : "#DCE3ED");
        Set("TextBrush", dark ? "#EEF2F9" : "#172238");
        Set("MutedBrush", dark ? "#ADB9CB" : "#607087");
        Set("AccentBrush", dark ? "#6D8CFA" : "#345BDB");
        Set("AccentSoftBrush", dark ? "#304160" : "#E9EFFF");
    }
    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
