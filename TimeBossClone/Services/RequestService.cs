using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace TimeBossClone.Services;

/// <summary>Ответ получателя на запрос (файл &lt;Id&gt;.ack рядом с запросами).</summary>
public sealed record RequestAck(bool Applied, string Message);

/// <summary>
/// Передача «добавить время» другой учётной записи через общую папку C:\Users\Public.
/// Каждый запрос подписан ключом, выведенным из пароля родителя (см. RequestSigner),
/// привязан к пользователю и дате и имеет уникальный Id — повтор и подделка отбрасываются.
///
/// Получатель пишет ответ (.ack): применено или почему отклонено. Раньше отправитель
/// всегда видел «Запрос отправлен», даже если получатель молча его отбрасывал.
/// </summary>
public static class RequestService
{
    public const int MaxMinutesPerRequest = 480;
    private const long MaxRequestFileSize = 8192;

    private static readonly string RequestsDir = Path.Combine(
        Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
        "TimeBossClone", "requests");

    private sealed class AddTimeRequest
    {
        public int Version { get; set; } = 1;
        public string Target { get; set; } = string.Empty;
        public int Minutes { get; set; }
        public string Date { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;

        /// <summary>Публичный ключ отправителя — нужен получателю, у которого ключа ещё нет.</summary>
        public string? PublicKey { get; set; }

        /// <summary>HMAC(старый SHA256-хэш пароля, PublicKey) — см. RequestSigner.</summary>
        public string? KeyProof { get; set; }

        public string Payload() => $"v{Version}|{Target.ToLowerInvariant()}|{Minutes}|{Date}|{Id}";
    }

    private sealed class AckFile
    {
        public string Id { get; set; } = string.Empty;
        public bool Applied { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <returns>Id запроса — по нему отправитель ждёт ответ через <see cref="TryReadAck"/>.</returns>
    public static string SendAddTime(string targetUser, int minutes, string parentPassword)
    {
        if (minutes is <= 0 or > MaxMinutesPerRequest)
            throw new ArgumentOutOfRangeException(nameof(minutes));

        EnsureDirectory();
        CleanupStaleFiles();

        var publicKey = RequestSigner.DerivePublicKey(parentPassword);
        var request = new AddTimeRequest
        {
            Target = targetUser,
            Minutes = minutes,
            Date = ConfigManager.DateKey(DateTime.Today),
            Id = Guid.NewGuid().ToString("N"),
            PublicKey = publicKey,
            KeyProof = RequestSigner.CreateLegacyKeyProof(parentPassword, publicKey)
        };
        request.Signature = RequestSigner.Sign(parentPassword, request.Payload());

        // Пишем во временный файл и переименовываем: получатель не увидит недописанный .req.
        var path = Path.Combine(RequestsDir, $"{request.Id}.req");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(request));
        GrantModify(tmp);
        File.Move(tmp, path);
        return request.Id;
    }

    /// <summary>Прочитать и удалить ответ получателя. null — ответа пока нет.</summary>
    public static RequestAck? TryReadAck(string requestId)
    {
        var path = AckPath(requestId);
        try
        {
            if (!File.Exists(path)) return null;
            var ack = JsonSerializer.Deserialize<AckFile>(File.ReadAllText(path));
            TryDelete(path);
            return ack == null ? null : new RequestAck(ack.Applied, ack.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // ещё пишется — прочитаем на следующем опросе
        }
    }

    /// <summary>Запрос так и не забрали (TimeBoss у адресата не запущен) — удалить его.</summary>
    public static bool CancelIfPending(string requestId)
    {
        var path = Path.Combine(RequestsDir, $"{requestId}.req");
        if (!File.Exists(path)) return false;
        TryDelete(path);
        return !File.Exists(path);
    }

    /// <summary>
    /// Применяет запросы, адресованные текущему пользователю. Id применённых запросов
    /// добавляются в <paramref name="consumedIds"/>, поэтому даже если файл не удалось
    /// удалить (чужой владелец), минуты не начисляются повторно.
    /// </summary>
    public static int ConsumeAddedMinutes(ConfigManager config, ICollection<string> consumedIds)
    {
        if (!Directory.Exists(RequestsDir)) return 0;

        var currentUser = config.CurrentUser;
        var today = ConfigManager.DateKey(DateTime.Today);
        int total = 0;

        string[] files;
        try { files = Directory.GetFiles(RequestsDir, "*.req"); }
        catch (Exception ex) { AppLog.Error("Чтение папки запросов", ex); return 0; }

        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > MaxRequestFileSize) continue;

                AddTimeRequest? req;
                try { req = JsonSerializer.Deserialize<AddTimeRequest>(File.ReadAllText(file)); }
                catch (JsonException) { continue; } // старый формат или мусор — не наше

                if (req == null || !req.Target.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(req.Id) && consumedIds.Contains(req.Id))
                {
                    TryDelete(file); // уже применён, файл просто не удалился в прошлый раз
                    continue;
                }

                string? reject = Validate(req, today, config);
                if (reject == null)
                {
                    consumedIds.Add(req.Id);
                    total += req.Minutes;
                    AppLog.Info($"Применён запрос +{req.Minutes} мин ({req.Id})");
                    WriteAck(req.Id, true, $"Начислено +{req.Minutes} мин пользователю {currentUser}.");
                }
                else
                {
                    AppLog.Info($"Отклонён запрос {Path.GetFileName(file)}: {reject}");
                    if (!string.IsNullOrEmpty(req.Id)) WriteAck(req.Id, false, reject);
                }

                TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл ещё пишется или нет доступа — попробуем в следующий раз.
            }
        }
        return total;
    }

    /// <returns>null — запрос корректен, иначе причина отказа (показывается родителю).</returns>
    private static string? Validate(AddTimeRequest req, string today, ConfigManager config)
    {
        if (req.Version != 1 || string.IsNullOrEmpty(req.Id))
            return "неизвестный формат запроса (обновите TimeBoss в обеих учётках)";
        if (req.Date != today)
            return $"запрос на другую дату ({req.Date})";
        if (req.Minutes is <= 0 or > MaxMinutesPerRequest)
            return "недопустимое количество минут";

        var publicKey = config.Config.RequestPublicKey;
        bool provisioning = false;

        if (string.IsNullOrEmpty(publicKey))
        {
            // Главная причина бага: у учётки со старым хэшем пароля не было ключа,
            // и ВСЕ запросы отклонялись. Принимаем ключ из запроса, если отправитель
            // доказал знание пароля, совпадающего с сохранённым здесь.
            var hash = config.Config.PasswordHash;
            if (!PasswordHasher.IsLegacy(hash))
                return "в этой учётке не настроен ключ — войдите в ней в «Настройки (родитель)»";
            if (!RequestSigner.VerifyLegacyKeyProof(hash, req.PublicKey, req.KeyProof))
                return "пароль родителя в этой учётке отличается от вашего";
            publicKey = req.PublicKey;
            provisioning = true;
        }

        if (!RequestSigner.Verify(publicKey, req.Payload(), req.Signature))
            return "неверная подпись — пароль родителя в этой учётке отличается от вашего";

        if (provisioning) config.ProvisionRequestKey(publicKey!);
        return null;
    }

    private static string AckPath(string requestId) => Path.Combine(RequestsDir, $"{requestId}.ack");

    private static void WriteAck(string requestId, bool applied, string message)
    {
        // Id приходит из чужого файла — не даём записать что-то вне папки.
        if (requestId.Length > 64 || !requestId.All(char.IsAsciiLetterOrDigit)) return;
        try
        {
            var path = AckPath(requestId);
            File.WriteAllText(path, JsonSerializer.Serialize(new AckFile { Id = requestId, Applied = applied, Message = message }));
            GrantModify(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Запись ответа на запрос", ex);
        }
    }

    private static void EnsureDirectory()
    {
        if (Directory.Exists(RequestsDir)) return;
        Directory.CreateDirectory(RequestsDir);
        try
        {
            // Чтобы получатель мог удалять обработанные файлы, созданные другой учётной записью.
            var dir = new DirectoryInfo(RequestsDir);
            var security = dir.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch (Exception ex) { AppLog.Error("Настройка прав папки запросов", ex); }
    }

    private static void GrantModify(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var security = fi.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify, AccessControlType.Allow));
            fi.SetAccessControl(security);
        }
        catch (Exception ex) { AppLog.Error("Настройка прав файла запроса", ex); }
    }

    /// <summary>Удаляем старые необработанные запросы и непрочитанные ответы.</summary>
    private static void CleanupStaleFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(RequestsDir))
            {
                var ext = Path.GetExtension(file);
                if (ext is ".req" or ".ack" or ".tmp" && File.GetLastWriteTime(file) < DateTime.Today)
                    TryDelete(file);
            }
        }
        catch { /* не критично */ }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
