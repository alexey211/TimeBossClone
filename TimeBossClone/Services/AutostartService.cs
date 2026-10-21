using Microsoft.Win32;

namespace TimeBossClone.Services;

public static class AutostartService
{
    private const string AppName = "TimeBoss";
    private const string RegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath);
        return key?.GetValue(AppName) != null;
    }

    public static void Enable()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        key?.SetValue(AppName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    public static void Toggle()
    {
        if (IsEnabled()) Disable(); else Enable();
    }
}
