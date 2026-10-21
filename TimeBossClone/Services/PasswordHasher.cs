using System.Security.Cryptography;
using System.Text;

namespace TimeBossClone.Services;

/// <summary>
/// PBKDF2-SHA256 с солью. Раньше был голый SHA256 без соли — короткий пароль
/// (минимум 4 символа) из config.json подбирался за доли секунды.
/// Старые хэши поддерживаются и автоматически обновляются при успешном входе.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 200_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Хэш старого формата HEX(SHA256(password)).</summary>
    public static bool IsLegacy(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.Length == 64 && stored.All(Uri.IsHexDigit);

    public static bool Verify(string password, string stored, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length == 4 && parts[0] == Prefix && int.TryParse(parts[1], out int iterations))
        {
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
                    iterations, HashAlgorithmName.SHA256, expected.Length);
                bool ok = CryptographicOperations.FixedTimeEquals(actual, expected);
                needsUpgrade = ok && iterations < Iterations;
                return ok;
            }
            catch (FormatException) { return false; }
        }

        // Старый формат: HEX(SHA256(password))
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        bool legacyOk = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(legacy), Encoding.ASCII.GetBytes(stored.ToUpperInvariant()));
        needsUpgrade = legacyOk;
        return legacyOk;
    }
}
