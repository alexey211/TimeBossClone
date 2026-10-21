namespace TimeBossClone.Models;

public class AppConfig
{
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Публичный ключ для проверки подписанных запросов «добавить время».</summary>
    public string RequestPublicKey { get; set; } = string.Empty;

    public int WeekdayLimitMinutes { get; set; } = 120;
    public int WeekendLimitMinutes { get; set; } = 240;
    public bool IsFirstRun { get; set; } = true;
    public List<string> MonitoredAccounts { get; set; } = new();
    public bool BlockTaskManager { get; set; } = false;
    public bool BlockRegistryEditor { get; set; } = false;
}
