using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Gateway;

/// <summary>
/// 会话令牌放在网关进程内存里（在线状态按技术架构第 3 节放对应进程内存，不另装库）。
/// 令牌本身是 32 字节随机数，进程一停就全失效，重启后要重新登录。
/// 这里只发；拿令牌换连接的校验在 P1-1a2，那一行再加。
/// </summary>
public sealed class TokenStore
{
    readonly ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new(StringComparer.Ordinal);

    public int Count => _expiresAt.Count;

    public (string Token, DateTimeOffset ExpiresAt) Issue(int ttlSeconds)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        _expiresAt[token] = expiresAt;
        return (token, expiresAt);
    }

    static string Base64Url(byte[] raw)
    {
        var text = Convert.ToBase64String(raw).TrimEnd('=');
        return text.Replace('+', '-').Replace('/', '_');
    }

    public override string ToString()
    {
        // 日志里只打数量，不打令牌。
        var sb = new StringBuilder();
        sb.Append("tokens=").Append(_expiresAt.Count);
        return sb.ToString();
    }
}
