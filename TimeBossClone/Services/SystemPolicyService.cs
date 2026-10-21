using System.Security.Principal;
using Microsoft.Win32;

namespace TimeBossClone.Services;

public static class SystemPolicyService
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static void ApplyLockPolicies(bool blockTaskManager, bool blockRegistry)
    {
        SetPolicy("DisableTaskMgr", blockTaskManager ? 1 : null);
        SetPolicy("DisableRegistryTools", blockRegistry ? 1 : null);
    }

    public static void RemoveLockPolicies()
    {
        SetPolicy("DisableTaskMgr", null);
        SetPolicy("DisableRegistryTools", null);
    }

    private static void SetPolicy(string valueName, int? value)
    {
        try
        {
            if (value.HasValue)
            {
                using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
                key.SetValue(valueName, value.Value, RegistryValueKind.DWord);
            }
            else
            {
                // Не создаём ключ политик, если его нет, — просто нечего удалять.
                using var key = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { AppLog.Error($"Политика {valueName}", ex); }
    }

    /// <summary>
    /// Реальные имена локальных учётных записей (как Environment.UserName).
    /// Раньше брались имена папок C:\Users, которые часто не совпадают с именем
    /// пользователя (переименование, аккаунт Microsoft, «user.PC» и т.п.),
    /// из-за чего выбранная учётная запись не распознавалась.
    /// </summary>
    public static List<string> GetLocalUserAccounts()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.UserName };

        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
            foreach (var sidString in list?.GetSubKeyNames() ?? [])
            {
                if (!sidString.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue; // только обычные пользователи
                try
                {
                    var account = new SecurityIdentifier(sidString).Translate(typeof(NTAccount)).Value;
                    var name = account[(account.LastIndexOf('\\') + 1)..];
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
                catch (IdentityNotMappedException) { /* удалённая учётка */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Список учётных записей из реестра", ex);
            AddFromUsersFolder(result);
        }

        return result.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddFromUsersFolder(HashSet<string> result)
    {
        var systemDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Public", "Default", "Default User", "All Users" };
        var usersDir = Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\", "Users");
        try
        {
            foreach (var dir in Directory.GetDirectories(usersDir))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name) && !systemDirs.Contains(name)) result.Add(name);
            }
        }
        catch { /* оставляем хотя бы текущего пользователя */ }
    }
}
