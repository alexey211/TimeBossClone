using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace TimeBossClone.Services;

/// <summary>
/// Подпись запросов «добавить время» между учётными записями.
///
/// Ключевая пара ECDSA P-256 детерминированно выводится из пароля родителя:
/// приватный ключ = PBKDF2(пароль). В конфиге каждой учётной записи хранится
/// только ПУБЛИЧНЫЙ ключ, поэтому ребёнок может проверить подпись, но не может
/// её подделать (для этого нужен сам пароль). Раньше любой пользователь мог
/// положить .req-файл в C:\Users\Public и начислить себе сколько угодно минут.
/// </summary>
public static class RequestSigner
{
    private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("TimeBossClone/add-time-requests/v1");
    private const int KeyIterations = 200_000;

    // Параметры кривой NIST P-256 (secp256r1)
    private static readonly BigInteger P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger A = P - 3;
    private static readonly BigInteger Gx = Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
    private static readonly BigInteger Gy = Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

    /// <summary>Публичный ключ (Base64 от X||Y), соответствующий паролю.</summary>
    public static string DerivePublicKey(string password)
    {
        var p = DeriveParameters(password);
        return Convert.ToBase64String([.. p.Q.X!, .. p.Q.Y!]);
    }

    public static string Sign(string password, string payload)
    {
        using var ecdsa = ECDsa.Create(DeriveParameters(password));
        return Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
    }

    public static bool Verify(string? publicKeyBase64, string payload, string? signatureBase64)
    {
        if (string.IsNullOrEmpty(publicKeyBase64) || string.IsNullOrEmpty(signatureBase64)) return false;
        try
        {
            var pub = Convert.FromBase64String(publicKeyBase64);
            if (pub.Length != 64) return false;
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = pub[..32], Y = pub[32..] }
            });
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload),
                Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    // ---------- Привязка ключа к старому (SHA256) хэшу пароля ----------
    //
    // Учётки, где после обновления родитель ещё ни разу не входил в «Настройки», имеют
    // старый хэш HEX(SHA256(пароль)) и НЕ имеют RequestPublicKey — из-за этого все запросы
    // «Другой польз.» отклонялись. Теперь отправитель прикладывает к запросу свой публичный
    // ключ и доказательство HMAC(SHA256-хэш пароля, ключ). Получатель проверяет его по
    // своему хэшу и один раз сохраняет ключ. Уровень защиты не ниже прежнего: старый
    // несолёный хэш и так позволял подобрать пароль; после первого входа родителя хэш
    // обновляется до PBKDF2 и этот путь закрывается.

    private static readonly byte[] KeyProofLabel = Encoding.UTF8.GetBytes("TimeBossClone/key-binding/v1|");

    public static string CreateLegacyKeyProof(string password, string publicKeyBase64)
    {
        var legacyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        return ComputeKeyProof(legacyHash, publicKeyBase64);
    }

    public static bool VerifyLegacyKeyProof(string storedLegacyHash, string? publicKeyBase64, string? proofBase64)
    {
        if (string.IsNullOrEmpty(publicKeyBase64) || string.IsNullOrEmpty(proofBase64)) return false;
        try
        {
            var expected = Convert.FromBase64String(ComputeKeyProof(storedLegacyHash.ToUpperInvariant(), publicKeyBase64));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(proofBase64));
        }
        catch (FormatException) { return false; }
    }

    private static string ComputeKeyProof(string legacyHashHexUpper, string publicKeyBase64)
    {
        byte[] data = [.. KeyProofLabel, .. Encoding.UTF8.GetBytes(publicKeyBase64)];
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(legacyHashHexUpper), data));
    }

    private static ECParameters DeriveParameters(string password)
    {
        // Выводим скаляр d ∈ [1, n-1]. Вероятность повтора цикла ~2^-32.
        BigInteger d;
        int counter = 0;
        do
        {
            byte[] salt = [.. KeySalt, (byte)counter++];
            var raw = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
                KeyIterations, HashAlgorithmName.SHA256, 32);
            d = new BigInteger(raw, isUnsigned: true, isBigEndian: true);
        } while (d.IsZero || d >= N);

        // Публичную точку считаем сами: импорт ECParameters только с D
        // поддерживается не на всех версиях Windows/CNG.
        var (qx, qy) = Multiply(d, Gx, Gy);

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = ToFixed(d),
            Q = new ECPoint { X = ToFixed(qx), Y = ToFixed(qy) }
        };
    }

    // --- Минимальная арифметика P-256 (аффинные координаты; нужна раз на вход в админку) ---

    private static (BigInteger X, BigInteger Y) Multiply(BigInteger k, BigInteger x, BigInteger y)
    {
        (BigInteger X, BigInteger Y)? result = null;
        (BigInteger X, BigInteger Y) addend = (x, y);
        while (k > 0)
        {
            if (!k.IsEven) result = result is null ? addend : Add(result.Value, addend);
            addend = Double(addend);
            k >>= 1;
        }
        return result ?? throw new CryptographicException("Некорректный скаляр");
    }

    private static (BigInteger, BigInteger) Add((BigInteger X, BigInteger Y) p1, (BigInteger X, BigInteger Y) p2)
    {
        if (p1.X == p2.X) return Double(p1); // p1 == -p2 при корректном d невозможно
        var l = Mod((p2.Y - p1.Y) * Inv(p2.X - p1.X));
        var x3 = Mod(l * l - p1.X - p2.X);
        return (x3, Mod(l * (p1.X - x3) - p1.Y));
    }

    private static (BigInteger, BigInteger) Double((BigInteger X, BigInteger Y) p)
    {
        var l = Mod((3 * p.X * p.X + A) * Inv(2 * p.Y));
        var x3 = Mod(l * l - 2 * p.X);
        return (x3, Mod(l * (p.X - x3) - p.Y));
    }

    private static BigInteger Mod(BigInteger v) { var r = v % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger Inv(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

    private static byte[] ToFixed(BigInteger v)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32) return bytes;
        var res = new byte[32];
        bytes.CopyTo(res, 32 - bytes.Length);
        return res;
    }

    private static BigInteger Hex(string s) => BigInteger.Parse("0" + s, NumberStyles.HexNumber);
}
