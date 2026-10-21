using System.Runtime.InteropServices;

namespace TimeBossClone.Services;

public static class LockService
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static void Lock()
    {
        if (!LockWorkStation())
            AppLog.Info($"LockWorkStation не удалось, код {Marshal.GetLastWin32Error()}");
    }
}
