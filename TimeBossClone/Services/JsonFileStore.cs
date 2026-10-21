using System.Text.Json;

namespace TimeBossClone.Services;

/// <summary>
/// Надёжное чтение/запись JSON: атомарная замена файла + резервная копия .bak.
/// Раньше обрыв питания во время File.WriteAllText оставлял битый JSON,
/// а битый JSON ронял программу при старте (исключение из конструктора) —
/// и родительский контроль просто переставал работать.
/// </summary>
public static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Load<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Options);
                if (value != null) return value;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Не удалось прочитать {candidate}", ex);
            }
        }
        return null;
    }

    public static bool Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Не удалось сохранить {path}", ex);
            return false;
        }
    }
}
