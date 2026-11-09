using System.Security.Cryptography;
using System.Text;

namespace Gateway;

/// <summary>
/// 口令只以 PBKDF2-HMAC-SHA256 的哈希形式存在，每个账号一组随机盐。
/// 比对用固定时间比较，不提前返回；迭代次数跟着行存，以后调高不用回填老账号。
/// </summary>
public static class PasswordHasher
{
    public const int SaltBytes = 16;

    public const int HashBytes = 32;

    public static (byte[] Hash, byte[] Salt) Create(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations, HashBytes);
        return (hash, salt);
    }

    public static bool Verify(string password, byte[] hash, byte[] salt, int iterations)
    {
        if (hash.Length == 0 || salt.Length == 0)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations, hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, hash);
    }

    /// <summary>
    /// 账号不存在时也走一次同参数的计算，让耗时不随「账号在不在」变化。
    /// 返回值故意丢弃，只为拉平时间。
    /// </summary>
    public static void Burn(string password, int iterations)
    {
        _ = Derive(password, DummySalt, iterations, HashBytes);
    }

    static byte[] Derive(string password, byte[] salt, int iterations, int length)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            length);
    }

    static readonly byte[] DummySalt = Encoding.ASCII.GetBytes("mmo-gateway-dummy-salt");
}
