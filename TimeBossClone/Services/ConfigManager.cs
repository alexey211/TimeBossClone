using System.Globalization;
using TimeBossClone.Models;

namespace TimeBossClone.Services;

public class ConfigManager
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TimeBossClone");

    private const string DateFormat = "yyyy-MM-dd";
    private const int UsageHistoryDays = 60;

    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");

    private AppConfig _config = new();

    public AppConfig Config => _config;

    public string CurrentUser { get; } = Environment.UserName;

    /// <summary>Вызывается после каждого сохранения настроек.</summary>
    public event Action? Changed;

    public ConfigManager()
    {
        Directory.CreateDirectory(DataDir);
        Load();
        CleanupOldUsage();
    }

    /// <summary>Первый запуск: флаг или отсутствие пароля (например, после ручного удаления хэша).</summary>
    public bool NeedsSetup => _config.IsFirstRun || string.IsNullOrEmpty(_config.PasswordHash);

    public void Load()
    {
        _config = JsonFileStore.Load<AppConfig>(ConfigPath) ?? new AppConfig();
        _config.MonitoredAccounts ??= new List<string>();
    }

    public void Save()
    {
        JsonFileStore.Save(ConfigPath, _config);
        Changed?.Invoke();
    }

    // ---------- Учёт времени ----------

    public static string DateKey(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public UsageRecord LoadUsage(DateTime date)
    {
        var key = DateKey(date);
        var record = JsonFileStore.Load<UsageRecord>(GetUsagePath(key)) ?? new UsageRecord();
        record.Date = key;
        record.WarnedAtMinutes ??= new List<int>();
        record.ConsumedRequestIds ??= new List<string>();
        return record;
    }

    /// <summary>
    /// Сохраняет запись в файл её собственной даты. Раньше путь брался из DateTime.Today,
    /// и сохранение около полуночи перезаписывало файл нового дня вчерашними данными.
    /// </summary>
    public void SaveUsage(UsageRecord record)
    {
        if (string.IsNullOrEmpty(record.Date)) record.Date = DateKey(DateTime.Today);
        JsonFileStore.Save(GetUsagePath(record.Date), record);
    }

    private static string GetUsagePath(string dateKey) => Path.Combine(DataDir, $"usage_{dateKey}.json");

    private static void CleanupOldUsage()
    {
        try
        {
            var threshold = DateTime.Today.AddDays(-UsageHistoryDays);
            foreach (var file in Directory.GetFiles(DataDir, "usage_*.json*"))
            {
                var name = Path.GetFileName(file);
                if (name.Length < 16) continue;
                if (DateTime.TryParseExact(name.Substring(6, 10), DateFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) && date < threshold)
                    File.Delete(file);
            }
        }
        catch (Exception ex) { AppLog.Error("Очистка старой статистики", ex); }
    }

    // ---------- Пароль ----------

    public bool VerifyPassword(string password)
    {
        if (!PasswordHasher.Verify(password, _config.PasswordHash, out bool needsUpgrade))
            return false;

        // Миграция со старого SHA256 и выдача ключа для запросов старым конфигам.
        if (needsUpgrade || string.IsNullOrEmpty(_config.RequestPublicKey))
        {
            SetPassword(password);
            Save();
        }
        return true;
    }

    /// <summary>
    /// Сохранить ключ запросов, полученный из подтверждённого запроса родителя
    /// (см. RequestSigner.VerifyLegacyKeyProof). Только если ключа ещё нет.
    /// </summary>
    public void ProvisionRequestKey(string publicKey)
    {
        if (!string.IsNullOrEmpty(_config.RequestPublicKey)) return;
        _config.RequestPublicKey = publicKey;
        JsonFileStore.Save(ConfigPath, _config);
        AppLog.Info("Получен ключ для запросов «добавить время» от родителя");
    }

    public void SetPassword(string password)
    {
        _config.PasswordHash = PasswordHasher.Hash(password);
        _config.RequestPublicKey = RequestSigner.DerivePublicKey(password);
    }

    // ---------- Лимиты ----------

    public int GetLimitMinutes(DateTime date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? _config.WeekendLimitMinutes
            : _config.WeekdayLimitMinutes;

    public bool IsCurrentUserMonitored()
    {
        var monitored = _config.MonitoredAccounts;
        return monitored.Count == 0 || monitored.Contains(CurrentUser, StringComparer.OrdinalIgnoreCase);
    }
}
