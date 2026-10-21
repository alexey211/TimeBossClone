namespace TimeBossClone.Services;

/// <summary>Простой файловый лог. Раньше все ошибки глотались молча через catch { }.</summary>
public static class AppLog
{
    private const long MaxSizeBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static string LogPath => Path.Combine(ConfigManager.DataDir, "timeboss.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(ConfigManager.DataDir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxSizeBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Лог не должен ронять приложение.
        }
    }
}
